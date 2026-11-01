namespace PotatoDM.Core.Media;

/// <summary>Reads a bounded window of another stream, skipping a prefix first.</summary>
internal sealed class BoundedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private long _skip;
    private long _remaining;
    private long _totalRead;

    public BoundedReadStream(Stream inner, long skip, long limit, bool leaveOpen = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _skip = Math.Max(0, skip);
        _remaining = Math.Max(0, limit);
        _leaveOpen = leaveOpen;
    }

    public long TotalRead => Interlocked.Read(ref _totalRead);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadCore(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => ReadCore(buffer);

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        await ReadAsyncCore(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        ReadAsyncCore(buffer, cancellationToken);

    private int ReadCore(Span<byte> buffer)
    {
        while (_skip > 0)
        {
            var want = (int)Math.Min(buffer.Length, _skip);
            if (want == 0)
            {
                return 0;
            }

            var skipped = _inner.Read(buffer[..want]);
            if (skipped == 0)
            {
                return 0;
            }

            _skip -= skipped;
        }

        if (_remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > _remaining)
        {
            buffer = buffer[..(int)_remaining];
        }

        var read = _inner.Read(buffer);
        _remaining -= read;
        Interlocked.Add(ref _totalRead, read);
        return read;
    }

    private async ValueTask<int> ReadAsyncCore(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        while (_skip > 0)
        {
            var want = (int)Math.Min(buffer.Length, _skip);
            if (want == 0)
            {
                return 0;
            }

            var skipped = await _inner.ReadAsync(buffer[..want], cancellationToken)
                .ConfigureAwait(false);
            if (skipped == 0)
            {
                return 0;
            }

            _skip -= skipped;
        }

        if (_remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > _remaining)
        {
            buffer = buffer[..(int)_remaining];
        }

        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        Interlocked.Add(ref _totalRead, read);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
