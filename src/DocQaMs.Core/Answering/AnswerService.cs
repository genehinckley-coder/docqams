using System.Runtime.CompilerServices;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocQaMs.Core.Answering;

/// <summary>
/// Streams one answer through the provider's chat client, in the prototype's request shape at the selected
/// model and level.
/// </summary>
/// <remarks>
/// <para>
/// The stream is enumerated once: each update is turned into answer events for the UI and aggregated into
/// the final response in the same pass (<see cref="AnswerStreamReader"/>).
/// </para>
/// <para>
/// Each level's timeout is enforced here, on the token passed to the chat client, whatever the client
/// itself enforces: a body that stalls after the response headers must still be interrupted (plan item 1).
/// </para>
/// </remarks>
public sealed class AnswerService : IAnswerService
{
    /// <summary>
    /// The share of the level's timeout after which a cancellation nobody asked for is taken as the chat
    /// client's own timeout. The margin allows for timer resolution.
    /// </summary>
    private const double ClientTimeoutFraction = 0.95;

    private readonly Func<IChatClientProvider> _clientProvider;
    private readonly IOptions<DocQaOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AnswerService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="clientProvider">
    /// Supplies the chat client provider when an answer is requested, not before. The provider is built on
    /// first use, so a missing or malformed key file surfaces as a failed answer, which the UI can show.
    /// </param>
    /// <param name="options">The options: <c>max_tokens</c>, timeout and prices.</param>
    /// <param name="timeProvider">The clock, for the timeout and the elapsed time.</param>
    /// <param name="logger">Receives every failure, with its full exception. Never a request body.</param>
    public AnswerService(
        Func<IChatClientProvider> clientProvider,
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
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        DocQaOptions options = _options.Value;
        TimeSpan timeout = options.RequestTimeouts[settings.Effort];
        ModelPrices prices = options.Prices[settings.ModelId];

        // Built before anything is sent: an invalid conversation is a programming error, not a failed answer.
        ChatRequest request = MessageRequestBuilder.Create(
            settings.ModelId, settings.Effort, options.MaxTokens[settings.Effort], CacheAffecting.SystemPrompt, messages);
        IChatClient client = ClientFor(settings.Effort);

        using var timeoutSource = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        long started = _timeProvider.GetTimestamp();
        var reader = new AnswerStreamReader(prices);

        IAsyncEnumerator<ChatResponseUpdate> stream = client
            .GetStreamingResponseAsync(request.Messages, request.Options, linked.Token)
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
                    if (await stream.MoveNextAsync())
                    {
                        events = reader.Read(stream.Current);
                    }
                    else
                    {
                        // The provider's client ends the stream normally once the whole message has arrived,
                        // even if closing the response then fails, so what was paid for is kept.
                        events = [.. reader.Finish(), new AnswerEvent.Completed(Complete(reader, settings, options, started))];
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
                        ex, settings.Effort, TimedOutAfter(ex, timeoutSource, timeout, started));
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
    /// The timeout that ended the request, or <see langword="null"/> when no timeout did.
    /// </summary>
    /// <remarks>
    /// Either this service's own timeout fired, or the chat client's did. The client's whole-request timeout
    /// equals the level's (see <see cref="IChatClientProvider.ForEffort"/>), and it surfaces as a bare
    /// <see cref="OperationCanceledException"/> with the caller's token not cancelled, so it is recognized by
    /// how long the request ran.
    /// </remarks>
    private TimeSpan? TimedOutAfter(
        Exception exception, CancellationTokenSource timeoutSource, TimeSpan timeout, long started)
    {
        if (timeoutSource.IsCancellationRequested)
        {
            return timeout;
        }

        return exception is OperationCanceledException
            && _timeProvider.GetElapsedTime(started) >= timeout * ClientTimeoutFraction
                ? timeout
                : null;
    }

    private IChatClient ClientFor(EffortLevel effort)
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

    private AnswerResult Complete(AnswerStreamReader reader, AnswerSettings settings, DocQaOptions options, long started)
    {
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
        ChatResponse response = reader.Response();

        // The provider reports usage on every response; without it, nothing is known to have been billed.
        TokenUsage usage = TokenUsage.From(response.Usage ?? new UsageDetails());
        TurnCost cost = TurnCost.For(usage, options.Prices[settings.ModelId]);
        StopWarning? warning = StopWarning.For(response, settings.Effort, options.MaxTokens);

        _logger.LogInformation(
            "Answer completed on {Model} at {Effort} in {Seconds:0.0} s, stop reason {StopReason}: input {Input}, " +
            "1-hour write {Write1h}, 5-minute write {Write5m}, read {Read}, output {Output}, cost {Cost:0.0000} USD.",
            settings.ModelId, settings.Effort, elapsed.TotalSeconds, StopWarning.StopReasonOf(response), usage.Input,
            usage.CacheWrite1h, usage.CacheWrite5m, usage.CacheRead, usage.Output, cost.Total);

        return new AnswerResult(settings, response, reader.Sources(response), usage, cost, elapsed, warning);
    }

    private void LogStopped(AnswerSettings settings) =>
        _logger.LogInformation("Answer stopped on {Model} at {Effort}.", settings.ModelId, settings.Effort);
}
