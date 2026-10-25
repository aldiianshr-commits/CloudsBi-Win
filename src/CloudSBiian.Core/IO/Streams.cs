namespace CloudSBiian.Core.Abstractions;

/// <summary>Wraps an HTTP content stream, limits it to a length and disposes the response with it.</summary>
public sealed class ResponseStream : Stream
{
    private readonly Stream _inner;
    private readonly IDisposable _owner;
    private long _remaining;

    public ResponseStream(Stream inner, IDisposable owner, long? length)
    {
        _inner = inner;
        _owner = owner;
        _remaining = length ?? long.MaxValue;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var n = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        _remaining -= n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var n = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        _remaining -= n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _owner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Read-only pass-through stream that reports cumulative bytes read.</summary>
public sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly IProgress<long>? _progress;
    private long _total;

    public ProgressStream(Stream inner, IProgress<long>? progress)
    {
        _inner = inner;
        _progress = progress;
    }

    public long BytesRead => _total;

    public override bool CanRead => true;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = _inner.Read(buffer, offset, count);
        Advance(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Advance(n);
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Advance(int n)
    {
        if (n > 0)
        {
            _total += n;
            _progress?.Report(_total);
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public static class StreamExtensions
{
    /// <summary>Reads until the buffer is full or the stream ends. Returns bytes read.</summary>
    public static async Task<int> ReadFullAsync(this Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
