using Microsoft.Extensions.Configuration;

namespace DocQaMs.Web.Tests;

public sealed class LoopbackGuardTests
{
    [Fact]
    public void AnEmptyConfiguration_IsClean() =>
        // Kestrel's default is http://localhost:5000.
        Assert.Empty(LoopbackGuard.NonLoopbackConfigured(Config()));

    [Fact]
    public void LoopbackUrls_AreClean() =>
        Assert.Empty(LoopbackGuard.NonLoopbackConfigured(Config(("urls", "http://localhost:5266;http://[::1]:5267"))));

    [Fact]
    public void ANonLoopbackUrl_IsReportedBeforeAnythingBinds() =>
        Assert.Equal(
            ["http://0.0.0.0:5266"],
            LoopbackGuard.NonLoopbackConfigured(Config(("urls", "http://localhost:5266; http://0.0.0.0:5266"))));

    [Theory]
    [InlineData("http_ports")]
    [InlineData("https_ports")]
    public void PortOnlySettings_AreReported_BecauseTheyBindEveryInterface(string key) =>
        Assert.Contains("*:8080", Assert.Single(LoopbackGuard.NonLoopbackConfigured(Config((key, "8080")))), StringComparison.Ordinal);

    [Fact]
    public void ANonLoopbackKestrelEndpoint_IsReported() =>
        Assert.Equal(
            ["http://*:5000"],
            LoopbackGuard.NonLoopbackConfigured(Config(
                ("Kestrel:Endpoints:Public:Url", "http://*:5000"),
                ("Kestrel:Endpoints:Local:Url", "http://localhost:5001"))));

    [Theory]
    [InlineData("http://localhost:5266")]
    [InlineData("http://LOCALHOST:5266")]
    [InlineData("http://127.0.0.1:5266")]
    [InlineData("http://[::1]:5266")]
    public void LoopbackAddresses_AreAllowed(string address) =>
        Assert.True(LoopbackGuard.IsLoopback(address));

    [Theory]
    [InlineData("http://0.0.0.0:5266")]
    [InlineData("http://[::]:5266")]
    [InlineData("http://*:5266")]
    [InlineData("http://+:5266")]
    [InlineData("http://192.168.1.20:5266")]
    [InlineData("http://docqa.example.com:5266")]
    [InlineData("not an address")]
    public void AnyOtherAddress_IsRefused(string address) =>
        Assert.False(LoopbackGuard.IsLoopback(address));

    [Fact]
    public void NonLoopback_ListsOnlyTheExposedAddresses() =>
        Assert.Equal(
            ["http://0.0.0.0:5266"],
            LoopbackGuard.NonLoopback(["http://localhost:5266", "http://0.0.0.0:5266", "http://[::1]:5266"]));

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
}
