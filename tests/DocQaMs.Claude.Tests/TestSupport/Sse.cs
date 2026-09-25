using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DocQaMs.Claude.Tests.TestSupport;

/// <summary>Builds server-sent-event response bodies the way the Messages API streams them.</summary>
internal static class Sse
{
    /// <summary>One <c>event:</c>/<c>data:</c> pair per JSON event, named after its <c>type</c>.</summary>
    public static string Format(IEnumerable<string> jsonEvents)
    {
        var body = new StringBuilder();
        foreach (string json in jsonEvents)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            string type = document.RootElement.GetProperty("type").GetString()!;
            body.Append("event: ").Append(type).Append('\n').Append("data: ").Append(json).Append("\n\n");
        }

        return body.ToString();
    }

    public static byte[] Bytes(IEnumerable<string> jsonEvents) => Encoding.UTF8.GetBytes(Format(jsonEvents));

    public static HttpResponseMessage Response(IEnumerable<string> jsonEvents) => Response(new MemoryStream(Bytes(jsonEvents)));

    public static HttpResponseMessage Response(Stream body)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

/// <summary>
/// A response body that delivers <paramref name="prefix"/>, then stalls until the read is cancelled,
/// the stream is disposed, or <paramref name="cap"/> elapses (then it ends, so a test can never hang).
/// </summary>
internal sealed class StallingStream(byte[] prefix, TimeSpan cap) : ReadOnlyTestStream
{
    private readonly CancellationTokenSource _disposed = new();
    private readonly TaskCompletionSource<string> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _position;

    /// <summary>How the stall ended: <c>cancelled</c>, <c>disposed</c> or <c>cap</c>.</summary>
    public Task<string> Ended => _ended.Task;

    /// <summary>Completes when the body is disposed, whether or not a read was pending.</summary>
    public Task Disposed => _disposedSignal.Task;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < prefix.Length)
        {
            int count = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        try
        {
            await Task.Delay(cap, linked.Token);
            _ended.TrySetResult("cap");
            return 0;
        }
        catch (OperationCanceledException)
        {
            _ended.TrySetResult(_disposed.IsCancellationRequested ? "disposed" : "cancelled");
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed.Cancel();
            _disposedSignal.TrySetResult();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A response body that reads normally, then throws when it is disposed.</summary>
internal sealed class ThrowingOnDisposeStream(byte[] body) : MemoryStream(body)
{
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            throw new IOException("Closing the body failed (simulated).");
        }
    }
}

/// <summary>
/// A response body that delivers <paramref name="prefix"/>, then fails as a reset connection would.
/// </summary>
internal sealed class BreakingStream(byte[] prefix) : ReadOnlyTestStream
{
    private int _position;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= prefix.Length)
        {
            throw new IOException("The connection was reset (simulated).");
        }

        int count = Math.Min(buffer.Length, prefix.Length - _position);
        prefix.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return ValueTask.FromResult(count);
    }
}

/// <summary>The boilerplate of a forward-only, read-only stream; subclasses implement one ReadAsync.</summary>
internal abstract class ReadOnlyTestStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public abstract override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
