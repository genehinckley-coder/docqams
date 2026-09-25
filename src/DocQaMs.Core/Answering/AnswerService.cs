using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Streams one answer through the Messages API, in the prototype's request shape at the selected model
/// and level.
/// </summary>
/// <remarks>
/// <para>
/// The stream is enumerated once: the SDK's aggregator passes every event through as it builds the
/// final message, so the same pass serves the UI and the final message (proved by plan item 1).
/// </para>
/// <para>
/// Each level's timeout is enforced here, on the token passed to the SDK. The SDK's own timeout bounds
/// only the wait for the response headers, and a body that stalls after them is never interrupted
/// (plan item 1).
/// </para>
/// </remarks>
public sealed class AnswerService : IAnswerService
{
    private readonly Func<IAnthropicClientProvider> _clientProvider;
    private readonly IOptions<DocQaOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AnswerService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="clientProvider">
    /// Supplies the client when an answer is requested, not before. The client is built on first use, so
    /// a missing or malformed key file surfaces as a failed answer, which the UI can show.
    /// </param>
    /// <param name="options">The options: <c>max_tokens</c>, timeout and prices.</param>
    /// <param name="timeProvider">The clock, for the timeout and the elapsed time.</param>
    /// <param name="logger">Receives every failure, with its full exception. Never a request body.</param>
    public AnswerService(
        Func<IAnthropicClientProvider> clientProvider,
        IOptions<DocQaOptions> options,
        TimeProvider timeProvider,
        ILogger<AnswerService> logger)
    {
        ArgumentNullException.ThrowIfNull(clientProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _clientProvider = clientProvider;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AnswerEvent> StreamAsync(
        AnswerSettings settings,
        IReadOnlyList<MessageParam> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        DocQaOptions options = _options.Value;
        TimeSpan timeout = options.RequestTimeouts[settings.Effort];
        ModelPrices prices = options.Prices[settings.ModelId];

        // Built before anything is sent: an invalid conversation is a programming error, not a failed answer.
        MessageCreateParams request = MessageRequestBuilder.Create(
            settings.ModelId, settings.Effort, options.MaxTokens[settings.Effort], CacheAffecting.SystemPrompt, messages);
        IAnthropicClient client = ClientFor(settings.Effort);

        using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        long started = _timeProvider.GetTimestamp();
        var aggregator = new MessageContentAggregator();
        var reader = new AnswerStreamReader(prices);

        IAsyncEnumerator<RawMessageStreamEvent> stream = aggregator
            .CollectAsync(client.Messages.CreateStreaming(request, linked.Token))
            .GetAsyncEnumerator(linked.Token);
        try
        {
            bool finished = false;
            while (!finished)
            {
                // A yield cannot sit inside a try with a catch, so each step is read here and yielded below.
                IReadOnlyList<AnswerEvent> events;
                try
                {
                    if (await NextAsync(stream, reader, settings))
                    {
                        events = reader.Read(stream.Current);
                    }
                    else
                    {
                        events = [new AnswerEvent.Completed(Complete(aggregator.Message(), reader, settings, options, started))];
                        finished = true;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    LogStopped(settings);
                    throw;
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    // Closing the stream on Stop can surface as a transport error; it is still a Stop.
                    LogStopped(settings);
                    throw new OperationCanceledException("The answer was stopped.", ex, cancellationToken);
                }
                catch (Exception ex)
                {
                    AnswerFailedException failure = AnswerFailures.Map(
                        ex, settings.Effort, TimedOutAfter(ex, timeoutSource, timeout, client, started));
                    _logger.LogError(
                        ex, "Answer failed ({Kind}) on {Model} at {Effort}.", failure.Kind, settings.ModelId, settings.Effort);
                    throw failure;
                }

                foreach (AnswerEvent answerEvent in events)
                {
                    yield return answerEvent;
                }
            }
        }
        finally
        {
            // Also runs when the caller abandons the enumeration: disposing closes the HTTP stream.
            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception ex)
            {
                // The outcome is already decided; a failure to close the stream must not replace it.
                _logger.LogWarning(ex, "Closing the answer stream failed on {Model} at {Effort}.", settings.ModelId, settings.Effort);
            }
        }
    }

    /// <summary>
    /// Moves to the next raw event. A failure after <c>message_stop</c> ends the stream instead of the answer.
    /// </summary>
    /// <remarks>
    /// The SDK closes the HTTP response inside the final <c>MoveNextAsync</c>, after <c>message_stop</c>
    /// has arrived. By then the aggregator holds the whole message, which has been paid for, so a failure
    /// closing the response, or a Stop landing then, must not discard it.
    /// </remarks>
    private async Task<bool> NextAsync(IAsyncEnumerator<RawMessageStreamEvent> stream, AnswerStreamReader reader, AnswerSettings settings)
    {
        try
        {
            return await stream.MoveNextAsync();
        }
        catch (Exception ex) when (reader.MessageStopped)
        {
            _logger.LogWarning(
                ex, "The answer arrived whole, but closing its stream failed on {Model} at {Effort}.", settings.ModelId, settings.Effort);
            return false;
        }
    }

    /// <summary>
    /// The timeout that ended the request, or <see langword="null"/> when no timeout did.
    /// </summary>
    /// <remarks>
    /// Either this service's own timeout fired, or the SDK's timeout on the response headers did. The SDK's
    /// throws a bare <see cref="TaskCanceledException"/>, with no inner exception or other marker (checked
    /// against 12.47.0), so it is recognized by how long the request ran. The 5% margin allows for timer
    /// resolution.
    /// </remarks>
    private TimeSpan? TimedOutAfter(
        Exception exception, CancellationTokenSource timeoutSource, TimeSpan timeout, IAnthropicClient client, long started)
    {
        if (timeoutSource.IsCancellationRequested)
        {
            return timeout;
        }

        return exception is OperationCanceledException
            && client.Timeout is { } headerTimeout
            && _timeProvider.GetElapsedTime(started) >= headerTimeout * 0.95
                ? headerTimeout
                : null;
    }

    private IAnthropicClient ClientFor(EffortLevel effort)
    {
        try
        {
            return _clientProvider().ForEffort(effort);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException)
        {
            // The key loader's messages name the file, never its contents.
            _logger.LogError(ex, "Cannot load the API key.");
            throw new AnswerFailedException(AnswerFailureKind.Credentials, $"Cannot load the API key: {ex.Message}", ex);
        }
    }

    private AnswerResult Complete(
        Message message, AnswerStreamReader reader, AnswerSettings settings, DocQaOptions options, long started)
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
        TokenUsage usage = TokenUsage.From(message.Usage);
        TurnCost cost = TurnCost.For(usage, options.Prices[settings.ModelId]);
        StopWarning? warning = StopWarning.For(message, reader.StopDetails, settings.Effort, options.MaxTokens);

        _logger.LogInformation(
            "Answer completed on {Model} at {Effort} in {Seconds:0.0} s, stop reason {StopReason}: input {Input}, " +
            "1-hour write {Write1h}, 5-minute write {Write5m}, read {Read}, output {Output}, cost {Cost:0.0000} USD.",
            settings.ModelId, settings.Effort, elapsed.TotalSeconds, message.StopReason?.Raw(), usage.Input,
            usage.CacheWrite1h, usage.CacheWrite5m, usage.CacheRead, usage.Output, cost.Total);

        return new AnswerResult(settings, message, reader.Sources(message), usage, cost, elapsed, warning);
    }

    private void LogStopped(AnswerSettings settings) =>
        _logger.LogInformation("Answer stopped on {Model} at {Effort}.", settings.ModelId, settings.Effort);
}
