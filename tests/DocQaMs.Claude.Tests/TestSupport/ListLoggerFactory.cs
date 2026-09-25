using Microsoft.Extensions.Logging;

namespace DocQaMs.Claude.Tests.TestSupport;

/// <summary>A logger factory whose loggers all write to one <see cref="ListLogger{T}"/>, for assertions.</summary>
internal sealed class ListLoggerFactory : ILoggerFactory
{
    private readonly ListLogger<ListLoggerFactory> _logger = new();

    public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries => _logger.Entries;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => _logger;

    public void Dispose()
    {
    }
}
