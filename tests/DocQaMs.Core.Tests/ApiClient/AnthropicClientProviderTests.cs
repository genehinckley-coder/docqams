using System.Collections;
using System.Net;
using Anthropic;
using Anthropic.Models.Models;
using DocQaMs.Core.ApiClient;
using DocQaMs.Core.Configuration;
using DocQaMs.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace DocQaMs.Core.Tests.ApiClient;

/// <summary>
/// Offline: every request is answered by <see cref="StubHandler"/>. The live checks of the request
/// shape are plan item 1's.
/// </summary>
/// <remarks>
/// In a non-parallel collection, because one test sets process-wide ANTHROPIC_* environment
/// variables that every client construction could read.
/// </remarks>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AnthropicClientProviderTests
{
    // Every ANTHROPIC_* variable named in the SDK 12.47.0 assembly.
    private static readonly string[] AnthropicVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_PROFILE",
        "ANTHROPIC_WORKSPACE_ID", "ANTHROPIC_ORGANIZATION_ID", "ANTHROPIC_IDENTITY_TOKEN",
        "ANTHROPIC_IDENTITY_TOKEN_FILE", "ANTHROPIC_FEDERATION_RULE_ID", "ANTHROPIC_SERVICE_ACCOUNT_ID",
        "ANTHROPIC_WEBHOOK_SIGNING_KEY",
    ];

    private const string Key = "sk-ant-api03-kkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkkk";

    private const string ModelJson =
        """{"type":"model","id":"claude-opus-5","display_name":"Claude Opus 5","created_at":"2026-01-01T00:00:00Z"}""";

    private static readonly Dictionary<EffortLevel, TimeSpan> Timeouts = new()
    {
        [EffortLevel.Low] = TimeSpan.FromMinutes(20),
        [EffortLevel.Medium] = TimeSpan.FromMinutes(21),
        [EffortLevel.High] = TimeSpan.FromMinutes(22),
        [EffortLevel.XHigh] = TimeSpan.FromMinutes(30),
        [EffortLevel.Max] = TimeSpan.FromMinutes(60),
    };

    [Fact]
    public async Task EveryEffortView_HasItsOwnTimeout_AndSendsThroughTheSharedHandlers()
    {
        var stub = new StubHandler(_ => StubHandler.Json(ModelJson));
        using AnthropicClientProvider provider = AnthropicClientProvider.Create(Key, Timeouts, [stub]);

        foreach (EffortLevel level in CacheAffecting.EffortLevels)
        {
            IAnthropicClient view = provider.ForEffort(level);
            Assert.Equal(Timeouts[level], view.Timeout);
            await Retrieve(view);
        }

        await Retrieve(provider.Client);

        // Building the views must not have changed the root client's timeout.
        Assert.Null(provider.Client.Timeout);
        Assert.Equal(CacheAffecting.EffortLevels.Count + 1, stub.Requests.Count);
    }

    [Fact]
    public async Task Requests_GoToTheFixedHost_WithTheKeyAsXApiKey_AndNoAuthorizationHeader()
    {
        var stub = new StubHandler(_ => StubHandler.Json(ModelJson));
        using AnthropicClientProvider provider = AnthropicClientProvider.Create(Key, Timeouts, [stub]);

        await Retrieve(provider.ForEffort(EffortLevel.High));

        CapturedRequest request = Assert.Single(stub.Requests);
        Assert.Equal("https", request.Uri?.Scheme);
        Assert.Equal("api.anthropic.com", request.Uri?.Host);
        Assert.Equal(Key, request.ApiKey);
        Assert.False(request.HasAuthorization);
    }

    [Fact]
    public async Task AmbientAnthropicVariables_ChangeNeitherTheHostNorTheCredentialsNorTheHeaders()
    {
        Dictionary<string, string?> saved = AnthropicVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (string name in AnthropicVariables)
            {
                Environment.SetEnvironmentVariable(name, name == "ANTHROPIC_BASE_URL" ? "https://junk.invalid" : "junk-value");
            }

            var stub = new StubHandler(_ => StubHandler.Json(ModelJson));
            using AnthropicClientProvider provider = AnthropicClientProvider.Create(Key, Timeouts, [stub]);

            await Retrieve(provider.Client);
            await Retrieve(provider.ForEffort(EffortLevel.Max));

            Assert.Equal(2, stub.Requests.Count);
            Assert.All(stub.Requests, request =>
            {
                Assert.Equal("api.anthropic.com", request.Uri?.Host);
                Assert.Equal(Key, request.ApiKey);
                Assert.False(request.HasAuthorization);
                Assert.DoesNotContain(request.Headers, header => header.Value.Contains("junk", StringComparison.Ordinal));
            });
        }
        finally
        {
            foreach ((string name, string? value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void Creating_TheClient_LeavesTheEnvironmentUntouched()
    {
        // Putting the key in an environment variable would hand it to every child process.
        Dictionary<string, string?> before = Snapshot();

        using AnthropicClientProvider provider = AnthropicClientProvider.Create(
            Key, Timeouts, [new StubHandler(_ => StubHandler.Json(ModelJson))]);

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task ARetryableFailure_IsLoggedWithoutTheKey_AndRetried()
    {
        var logger = new ListLogger<RetryLoggingHandler>();
        var stub = new StubHandler(number => number == 1
            ? StubHandler.Json("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", HttpStatusCode.ServiceUnavailable)
            : StubHandler.Json(ModelJson));
        using AnthropicClientProvider provider = AnthropicClientProvider.Create(
            Key, Timeouts, [new RetryLoggingHandler(logger), stub]);

        await Retrieve(provider.Client);

        Assert.Equal(2, stub.Requests.Count);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("503", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("kkkk", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASuccessfulRequest_LogsNothing()
    {
        var logger = new ListLogger<RetryLoggingHandler>();
        using AnthropicClientProvider provider = AnthropicClientProvider.Create(
            Key, Timeouts, [new RetryLoggingHandler(logger), new StubHandler(_ => StubHandler.Json(ModelJson))]);

        await Retrieve(provider.Client);

        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(409, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(529, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void RetryableStatuses_FollowTheSdksDocumentation(int status, bool retryable) =>
        Assert.Equal(retryable, RetryLoggingHandler.IsRetryableStatus(status));

    [Fact]
    public void AMissingTimeout_IsRefused()
    {
        Dictionary<EffortLevel, TimeSpan> timeouts = new(Timeouts);
        timeouts.Remove(EffortLevel.XHigh);

        var ex = Assert.Throws<ArgumentException>(() => AnthropicClientProvider.Create(Key, timeouts, []));

        Assert.Contains("XHigh", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForEffort_RejectsAnUndefinedLevel()
    {
        using AnthropicClientProvider provider = AnthropicClientProvider.Create(Key, Timeouts, []);

        Assert.Throws<ArgumentOutOfRangeException>(() => provider.ForEffort((EffortLevel)99));
    }

    private static Task<ModelInfo> Retrieve(IAnthropicClient client) =>
        client.Models.Retrieve(ModelIds.ClaudeOpus5, new ModelRetrieveParams(), TestContext.Current.CancellationToken);

    private static Dictionary<string, string?> Snapshot() =>
        Environment.GetEnvironmentVariables()
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);
}
