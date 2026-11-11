namespace Cewka.RadioProbe;

internal sealed class ByteBudgetExhaustedException() : IOException("Wyczerpany limit danych")
{
    /// <summary>Klient HTTP owija wyjątki odczytu w swoje.</summary>
    public static bool IsIn(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ByteBudgetExhaustedException)
                return true;
        }

        return false;
    }
}

/// <summary>Limit bajtów jednego adresu, łącznie z przekierowaniami, listami i uzgadnianiem TLS.</summary>
internal sealed class ByteBudget(long limit)
{
    private long _used;

    public long Used => Interlocked.Read(ref _used);
    public long Remaining => Math.Max(0, limit - Used);

    public void Add(int bytes) => Interlocked.Add(ref _used, bytes);
}

/// <summary>Licznik na samym gnieździe: żadna warstwa wyżej nie przeczyta ponad limit.</summary>
internal sealed class MeteredStream(Stream inner, ByteBudget budget) : Stream
{
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
        var count = Clamp(buffer.Length);
        var read = await inner.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        budget.Add(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer[..Clamp(buffer.Length)]);
        budget.Add(read);
        return read;
    }

    /// <summary>Odczyt zerowy służy klientowi do czekania na dane i nie zużywa limitu.</summary>
    private int Clamp(int requested)
    {
        if (requested == 0)
            return 0;

        var remaining = budget.Remaining;
        if (remaining == 0)
            throw new ByteBudgetExhaustedException();
        return (int)Math.Min(requested, remaining);
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
