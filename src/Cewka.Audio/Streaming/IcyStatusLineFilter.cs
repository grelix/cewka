using System.Text;

namespace Cewka.Audio.Streaming;

/// <summary>
/// Zamienia wiersz „ICY 200 OK” na „HTTP/1.0 200 OK”. Klient HTTP odrzuca wiersz statusu ICY,
/// a reszta odpowiedzi SHOUTcast jest zwykłym HTTP. Czyta najwyżej 4 bajty i wychodzi przy
/// pierwszej niezgodności, więc porcja jednobajtowa ani krótka odpowiedź go nie zawiesi.
/// </summary>
internal sealed class IcyStatusLineFilter(Stream inner, ConnectionTrace? trace) : Stream
{
    private static readonly byte[] Icy = "ICY "u8.ToArray();
    private static readonly byte[] Http10 = "HTTP/1.0 "u8.ToArray();

    private readonly byte[] _prefix = new byte[Icy.Length];
    private int _prefixLength;
    private bool _decided;
    private byte[]? _pending;
    private int _pendingOffset;

    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Klient HTTP czeka na dane odczytem zerowym; nie może on rozstrzygać o wierszu statusu.
        if (buffer.IsEmpty)
            return HasPending ? 0 : await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

        while (!_decided)
        {
            var read = await inner.ReadAsync(_prefix.AsMemory(_prefixLength), cancellationToken).ConfigureAwait(false);
            _prefixLength += read;
            Decide(read == 0);
        }

        return HasPending
            ? TakePending(buffer.Span)
            : await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return HasPending ? 0 : inner.Read(buffer);

        while (!_decided)
        {
            var read = inner.Read(_prefix.AsSpan(_prefixLength));
            _prefixLength += read;
            Decide(read == 0);
        }

        return HasPending ? TakePending(buffer) : inner.Read(buffer);
    }

    private bool HasPending => _pending is not null;

    private void Decide(bool ended)
    {
        var matches = _prefix.AsSpan(0, _prefixLength).SequenceEqual(Icy.AsSpan(0, _prefixLength));
        if (matches && _prefixLength < Icy.Length && !ended)
            return;

        _decided = true;
        if (matches && _prefixLength == Icy.Length)
        {
            _pending = Http10;
            if (trace is not null)
                trace.SawIcyStatusLine = true;
        }
        else if (_prefixLength > 0)
        {
            _pending = _prefix.AsSpan(0, _prefixLength).ToArray();
        }
    }

    private int TakePending(Span<byte> destination)
    {
        var available = _pending.AsSpan(_pendingOffset);
        var count = Math.Min(available.Length, destination.Length);
        available[..count].CopyTo(destination);
        _pendingOffset += count;
        if (_pendingOffset == _pending!.Length)
            _pending = null;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(buffer, cancellationToken);

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
