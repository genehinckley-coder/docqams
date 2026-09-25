using DocQaMs.Core.Answering;
using DocQaMs.Core.Chat;
using DocQaMs.Core.Configuration;

namespace DocQaMs.Core.Tests.Answering;

/// <summary>
/// Every failure the provider's chat client reports, and every one that can still arrive untranslated, maps to
/// the message the user reads. The messages are the prototype's, so they are compared exactly.
/// </summary>
public sealed class AnswerFailuresTests
{
    public static TheoryData<ChatProviderException, AnswerFailureKind, string> ProviderFailures => new()
    {
        {
            new ChatProviderException(ChatProviderFailure.Authentication, "401", statusCode: 401, errorType: "authentication_error"),
            AnswerFailureKind.Authentication,
            "The API rejected the key (401). Check the key file named by DocQaMs:ApiKeyPath."
        },
        {
            new ChatProviderException(ChatProviderFailure.Forbidden, "403", statusCode: 403, errorType: "permission_error"),
            AnswerFailureKind.Authentication,
            "The key is not permitted to make this request (403)."
        },
        {
            new ChatProviderException(ChatProviderFailure.RateLimited, "429", statusCode: 429, errorType: "rate_limit_error"),
            AnswerFailureKind.RateLimited,
            "Rate limited or out of quota (429). Wait a minute, then ask again."
        },
        {
            new ChatProviderException(ChatProviderFailure.CreditExhausted, "400", statusCode: 400, errorType: "billing_error"),
            AnswerFailureKind.CreditExhausted,
            "The API account is out of credit. Add credit in the Claude Console, then ask again."
        },
        {
            new ChatProviderException(ChatProviderFailure.ApiError, "529", statusCode: 529, errorType: "overloaded_error"),
            AnswerFailureKind.ApiError,
            "The API returned an error (529, OverloadedError). Ask again; if it persists, see the log."
        },
        {
            new ChatProviderException(ChatProviderFailure.ApiError, "529", statusCode: 529, errorType: "OverloadedError"),
            AnswerFailureKind.ApiError,
            "The API returned an error (529, OverloadedError). Ask again; if it persists, see the log."
        },
        {
            new ChatProviderException(ChatProviderFailure.ApiError, "500", statusCode: 500),
            AnswerFailureKind.ApiError,
            "The API returned an error (500). Ask again; if it persists, see the log."
        },
        {
            new ChatProviderException(ChatProviderFailure.ApiError, "no status", errorType: "api_error"),
            AnswerFailureKind.ApiError,
            "The API returned an error (ApiError). Ask again; if it persists, see the log."
        },
        {
            new ChatProviderException(ChatProviderFailure.ApiError, "nothing known"),
            AnswerFailureKind.ApiError,
            "The API returned an error. Ask again; if it persists, see the log."
        },
        {
            new ChatProviderException(ChatProviderFailure.StreamError, "error event", errorType: "overloaded_error"),
            AnswerFailureKind.ApiError,
            "The API reported an error partway through the answer (OverloadedError). Ask again."
        },
        {
            new ChatProviderException(ChatProviderFailure.StreamError, "error event", errorType: "OverloadedError"),
            AnswerFailureKind.ApiError,
            "The API reported an error partway through the answer (OverloadedError). Ask again."
        },
        {
            new ChatProviderException(ChatProviderFailure.StreamError, "error event"),
            AnswerFailureKind.ApiError,
            "The API reported an error partway through the answer. Ask again."
        },
        {
            new ChatProviderException(ChatProviderFailure.StreamEndedEarly, "no message_stop"),
            AnswerFailureKind.StreamEndedEarly,
            "The answer stream ended before the answer was complete. Ask again."
        },
        {
            new ChatProviderException(ChatProviderFailure.Connection, "no route"),
            AnswerFailureKind.Connection,
            "Network error: the connection to the API failed. Check the connection, then ask again."
        },
    };

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public void EachProviderFailure_MapsToItsMessage(ChatProviderException exception, AnswerFailureKind kind, string message)
    {
        AnswerFailedException failure = AnswerFailures.Map(exception, EffortLevel.High, timedOutAfter: null);

        Assert.Equal(kind, failure.Kind);
        Assert.Equal(message, failure.Message);
        Assert.Same(exception, failure.InnerException);
    }

    [Fact]
    public void EveryProviderFailureKind_HasItsOwnMessage()
    {
        // A kind added to the contract must not fall through to the generic message.
        Assert.All(Enum.GetValues<ChatProviderFailure>(), kind =>
        {
            AnswerFailedException failure = AnswerFailures.Map(new ChatProviderException(kind, "x"), EffortLevel.High, null);

            Assert.NotEqual(AnswerFailureKind.Unexpected, failure.Kind);
        });
    }

    public static TheoryData<Exception, AnswerFailureKind, string> UntranslatedFailures => new()
    {
        {
            new IOException("The connection was reset."),
            AnswerFailureKind.Connection,
            "Network error: the connection to the API failed. Check the connection, then ask again."
        },
        {
            new HttpRequestException("No such host is known."),
            AnswerFailureKind.Connection,
            "Network error: the connection to the API failed. Check the connection, then ask again."
        },
        {
            new NotSupportedException("A \"page_location\" citation is not supported."),
            AnswerFailureKind.Unexpected,
            "The response contained a citation or block this app does not handle. See the log."
        },
        {
            new TaskCanceledException("Cancelled by a handler."),
            AnswerFailureKind.Unexpected,
            "The request was cancelled, though neither Stop nor a timeout asked for it. Ask again; if it persists, see the log."
        },
        {
            new InvalidOperationException("Something odd."),
            AnswerFailureKind.Unexpected,
            "The answer failed unexpectedly. See the log."
        },
    };

    [Theory]
    [MemberData(nameof(UntranslatedFailures))]
    public void AnUntranslatedFailure_MapsByItsType(Exception exception, AnswerFailureKind kind, string message)
    {
        AnswerFailedException failure = AnswerFailures.Map(exception, EffortLevel.High, timedOutAfter: null);

        Assert.Equal(kind, failure.Kind);
        Assert.Equal(message, failure.Message);
        Assert.Same(exception, failure.InnerException);
    }

    [Theory]
    [InlineData(500, EffortLevel.High, "0.5 seconds, the limit for High effort")]
    [InlineData(90_000, EffortLevel.XHigh, "1.5 minutes, the limit for Extra high effort")]
    [InlineData(1_200_000, EffortLevel.Max, "20 minutes, the limit for Max effort")]
    public void OnceATimeoutFired_AnyFailureIsATimeout(int milliseconds, EffortLevel effort, string limit)
    {
        var exception = new ChatProviderException(ChatProviderFailure.Connection, "The stream was closed.");

        AnswerFailedException failure = AnswerFailures.Map(exception, effort, TimeSpan.FromMilliseconds(milliseconds));

        Assert.Equal(AnswerFailureKind.TimedOut, failure.Kind);
        Assert.Equal($"No complete answer within {limit}, so the request was ended. Ask again, or ask a narrower question.", failure.Message);
        Assert.Same(exception, failure.InnerException);
    }

    [Fact]
    public void AnAlreadyMappedFailure_IsKept()
    {
        var failed = new AnswerFailedException(AnswerFailureKind.Credentials, "Cannot load the API key: missing.", null);

        Assert.Same(failed, AnswerFailures.Map(failed, EffortLevel.High, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData("overloaded_error", "OverloadedError")]
    [InlineData("OverloadedError", "OverloadedError")]
    [InlineData("api_error", "ApiError")]
    [InlineData("invalid_request_error", "InvalidRequestError")]
    [InlineData("_leading__and_trailing_", "LeadingAndTrailing")]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void TheErrorType_ReadsInPascalCase(string? errorType, string? expected) =>
        Assert.Equal(expected, AnswerFailures.ErrorTypeName(errorType));
}
