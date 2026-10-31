namespace Cewka.Audio.Decoding;

/// <summary>
/// Mierzy przepływność fragmentu, który jest właśnie dekodowany; wartość z tagu to średnia
/// z całego pliku.
///
/// <para>Pułapka: dekoder czyta plik porcjami po kilkadziesiąt kilobajtów (w MP3 128 kbps około
/// 49 kB, czyli trzy sekundy dźwięku), a między nimi nie sięga do pliku wcale. Okno czasowe
/// krótsze od porcji daje wartości od zera do kilkukrotności średniej. Dlatego przepływność to
/// bajty jednej porcji podzielone przez czas dźwięku, jaki z niej powstał.</para>
/// </summary>
public sealed class BitrateMeter
{
    /// <summary>
    /// Najkrótszy czas dźwięku między pomiarami. Odczyty wykonane tuż po sobie trafiają dzięki
    /// temu do jednej porcji.
    /// </summary>
    private const double MinimumSpanSeconds = 0.75;

    private long _frames;
    private int _sampleRate = 48000;

    private long _granuleBytes;
    private double _granuleStartSeconds = -1;

    /// <summary>Smoothed value in kilobits per second; 0 until enough has been measured.</summary>
    public int Kilobits { get; private set; }

    /// <summary>
    /// Wywoływane po ustaleniu formatu wyjścia. Zeruje też liczniki, bo przy otwarciu miniaudio
    /// przegląda cały plik MP3, żeby policzyć ramki, a te bajty nie należą do żadnego fragmentu.
    /// </summary>
    public void Configure(int sampleRate)
    {
        _sampleRate = Math.Max(1, sampleRate);
        Reset();
    }

    /// <summary>
    /// Wywoływane przez strumień liczący przy każdym odczycie; odczyt wyznacza granicę porcji.
    /// </summary>
    public void AddBytes(long bytes)
    {
        var seconds = _frames / (double)_sampleRate;

        if (_granuleStartSeconds < 0)
        {
            // Pierwszy odczyt: nie ma jeszcze z czym go porównać.
            _granuleStartSeconds = seconds;
            _granuleBytes = bytes;
            return;
        }

        var span = seconds - _granuleStartSeconds;
        if (span < MinimumSpanSeconds)
        {
            // Kolejny odczyt tej samej porcji.
            _granuleBytes += bytes;
            return;
        }

        var kilobits = (int)Math.Round(_granuleBytes * 8 / span / 1000.0);

        // Dwie porcje po połowie: wygładza granicę między nimi, nie gubiąc zmienności.
        Kilobits = Kilobits == 0 ? kilobits : (int)Math.Round(Kilobits * 0.5 + kilobits * 0.5);

        _granuleStartSeconds = seconds;
        _granuleBytes = bytes;
    }

    /// <summary>Called by the decoder after producing frames.</summary>
    public void AddFrames(long frames) => _frames += frames;

    /// <summary>Clears history after a seek, where the byte-to-time relationship breaks.</summary>
    public void Reset()
    {
        _frames = 0;
        _granuleBytes = 0;
        _granuleStartSeconds = -1;
        Kilobits = 0;
    }
}

/// <summary>
/// Passes a stream through unchanged while counting the bytes read from it. Wrapping the
/// file here means every decoder is measured the same way, whether it reads through the
/// native shim or through a managed library.
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly bool _ownsInner;
    private readonly BitrateMeter _meter;

    public CountingStream(Stream inner, BitrateMeter meter, bool ownsInner)
    {
        _inner = inner;
        _meter = meter;
        _ownsInner = ownsInner;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0) _meter.AddBytes(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        if (read > 0) _meter.AddBytes(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void Flush() => _inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsInner) _inner.Dispose();
        base.Dispose(disposing);
    }
}
