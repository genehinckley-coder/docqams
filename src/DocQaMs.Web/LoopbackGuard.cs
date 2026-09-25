using System.Net;

namespace DocQaMs.Web;

/// <summary>
/// Refuses any listening address other than loopback.
/// </summary>
/// <remarks>
/// The app has no authentication, and anyone who can reach its port spends money on the API key.
/// Loopback binding is therefore its only access control. The configuration is checked before the
/// server opens a socket, so an <c>ASPNETCORE_URLS</c> or <c>--urls</c> override cannot expose it even
/// briefly; the addresses actually bound are checked again after start-up, as a backstop.
/// </remarks>
internal static class LoopbackGuard
{
    /// <summary>Returns the addresses in <paramref name="addresses"/> that are not loopback.</summary>
    public static IReadOnlyList<string> NonLoopback(IEnumerable<string> addresses) =>
        addresses.Where(address => !IsLoopback(address)).ToList();

    /// <summary>
    /// Returns every non-loopback address that <paramref name="configuration"/> asks the server to bind.
    /// </summary>
    /// <remarks>
    /// Covers the keys the host reads: <c>urls</c> (<c>ASPNETCORE_URLS</c>, <c>DOTNET_URLS</c>,
    /// <c>--urls</c>), <c>http_ports</c> and <c>https_ports</c> (which always bind every interface),
    /// and each <c>Kestrel:Endpoints:*:Url</c>. Every source is checked, even one the host would
    /// ignore in favor of another, so the check errs toward refusing.
    /// </remarks>
    public static IReadOnlyList<string> NonLoopbackConfigured(IConfiguration configuration)
    {
        List<string> exposed = [.. SplitList(configuration["urls"]).Where(url => !IsLoopback(url))];

        foreach ((string key, string scheme) in (ReadOnlySpan<(string, string)>)[("http_ports", "http"), ("https_ports", "https")])
        {
            exposed.AddRange(SplitList(configuration[key]).Select(port => $"{scheme}://*:{port} (from {key})"));
        }

        foreach (IConfigurationSection endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { } url && !IsLoopback(url))
            {
                exposed.Add(url);
            }
        }

        return exposed;
    }

    private static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Whether <paramref name="address"/>, such as <c>http://localhost:5266</c>, listens on loopback
    /// only. Wildcards such as <c>*</c>, <c>+</c>, <c>0.0.0.0</c> and <c>[::]</c> do not.
    /// </summary>
    public static bool IsLoopback(string address)
    {
        BindingAddress binding;
        try
        {
            binding = BindingAddress.Parse(address);
        }
        catch (FormatException)
        {
            return false;
        }

        if (binding.IsUnixPipe || binding.IsNamedPipe)
        {
            return false;
        }

        if (string.Equals(binding.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(binding.Host.Trim('[', ']'), out IPAddress? ip) && IPAddress.IsLoopback(ip);
    }
}
