using Anthropic.Models;
using DocQaMs.Core.Chat;

namespace DocQaMs.Claude.Tests.Claude;

/// <summary>
/// <see cref="ClaudeFailures"/> for the exceptions that are not API responses. The API's own failures are
/// covered end to end, through the SDK, by <see cref="ClaudeChatClientStreamingTests"/>.
/// </summary>
public sealed class ClaudeFailuresTests
{
    [Theory]
    [InlineData(ErrorType.OverloadedError, "overloaded_error")]
    [InlineData(ErrorType.BillingError, "billing_error")]
    [InlineData(ErrorType.InvalidRequestError, "invalid_request_error")]
    [InlineData(ErrorType.RateLimitError, "rate_limit_error")]
    public void TheErrorType_IsSpelledAsTheApiSpellsIt(ErrorType type, string wire) =>
        Assert.Equal(wire, ClaudeFailures.ToApiString(type));

    [Fact]
    public void ATransportFailure_IsAConnectionFailure_KeepingTheOriginal()
    {
        var io = new IOException("The connection was reset (simulated).");
        var http = new HttpRequestException("Connection refused (simulated).");

        ChatProviderException fromIo = Assert.IsType<ChatProviderException>(ClaudeFailures.Translate(io));
        ChatProviderException fromHttp = Assert.IsType<ChatProviderException>(ClaudeFailures.Translate(http));

        Assert.Equal(ChatProviderFailure.Connection, fromIo.Failure);
        Assert.Same(io, fromIo.InnerException);
        Assert.Equal(io.Message, fromIo.Message);
        Assert.Null(fromIo.StatusCode);
        Assert.Null(fromIo.ErrorType);
        Assert.Equal(ChatProviderFailure.Connection, fromHttp.Failure);
    }

    [Fact]
    public void Cancellation_AnAlreadyTranslatedFailure_AndBugs_AreNotTranslated()
    {
        Assert.Null(ClaudeFailures.Translate(new OperationCanceledException()));
        Assert.Null(ClaudeFailures.Translate(new TaskCanceledException()));
        Assert.Null(ClaudeFailures.Translate(new ChatProviderException(ChatProviderFailure.ApiError, "Already translated.")));
        Assert.Null(ClaudeFailures.Translate(new InvalidOperationException("A bug.")));
        Assert.Null(ClaudeFailures.Translate(new NotSupportedException("An unsupported request.")));
    }
}
