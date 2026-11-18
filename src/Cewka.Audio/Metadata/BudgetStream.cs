namespace Cewka.Audio.Metadata;

/// <summary>
/// Strumień z limitem liczby operacji dla biblioteki znaczników. Uszkodzony plik potrafi wprowadzić
/// ją w pętlę bez końca (atom MP4 o zerowej długości czytany w kółko), a wątku w pętli nie da się
/// zatrzymać z zewnątrz. Każdy obrót takiej pętli sięga do strumienia, więc po wyczerpaniu limitu
/// strumień rzuca wyjątek i odczyt znaczników kończy się niczym.
/// </summary>
internal sealed class BudgetStream(Stream inner, long budget) : Stream
{
    private long _left = budget;

    /// <summary>
    /// Limit dla pliku o tej długości. Zwykły odczyt znaczników to setki operacji, ale w MP4
    /// biblioteka czyta tablicę próbek po jednej pozycji na operację, przy okładce dwa razy: przy
    /// małej przepływności to jedna operacja na kilkanaście bajtów pliku. Pętla bez końca
    /// zużywa ten limit w ułamku sekundy przy małym pliku i w kilkanaście sekund przy gigabajtowym.
    /// </summary>
    public static long BudgetFor(long length) => 2_000_000 + length / 4;

    public bool Exhausted => _left < 0;

    private void Spend()
    {
        if (--_left < 0) throw new IOException("Odczyt znaczników przekroczył limit operacji.");
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set { Spend(); inner.Position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count) { Spend(); return inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { Spend(); return inner.Read(buffer); }
    public override int ReadByte() { Spend(); return inner.ReadByte(); }
    public override long Seek(long offset, SeekOrigin origin) { Spend(); return inner.Seek(offset, origin); }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
