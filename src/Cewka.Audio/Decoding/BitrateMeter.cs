namespace Cewka.Audio.Decoding;

/// <summary>
/// Measures the bitrate of the passage being decoded.
///
/// <para><b>Po co.</b> Plik o zmiennej przepływności ma inną gęstość danych w cichym przejściu
/// i w kulminacji. Wartość z tagu jest średnią z całości i nie mówi nic o tym, co gra w tej
/// chwili.</para>
///
/// <para><b>Dlaczego pomiar idzie od porcji, a nie z okna czasowego.</b> Dekoder nie czyta pliku
/// równomiernie: pobiera go porcjami po kilkadziesiąt kilobajtów i między nimi nie sięga do pliku
/// wcale. Zmierzone na pliku MP3 128 kbps: 394 z 400 wywołań dekodowania nie odczytały ani jednego
/// bajtu, a pozostałe pobrały po około 49 kB — czyli mniej więcej po trzy sekundy dźwięku.
/// Zużycie danych jest więc obserwowalne wyłącznie w takich granulach.</para>
///
/// <para>Okno czasowe krótsze od porcji daje bezwartościowy wynik: przy oknie półsekundowym
/// zmierzone wartości wahały się od 0 do 1117 kbps przy prawdziwej średniej 128. Wcześniejsza
/// wersja miernika ukrywała ten rozrzut, pomijając okna bez odczytu — i przez to zatrzymywała się
/// na wartości jedynego okna, które trafiło w porcję, zawyżonej o połowę. Stąd wrażenie, że
/// odczyt po kilku sekundach zamiera.</para>
///
/// <para>Przepływność liczona jest teraz z jednej porcji podzielonej przez czas dźwięku, jaki
/// z niej powstał. Rozdzielczość odczytu to długość porcji, czyli kilka sekund — i tyle da się
/// z tego materiału uczciwie odczytać.</para>
/// </summary>
public sealed class BitrateMeter
{
    /// <summary>
    /// Minimum audio time between two measurements. Sąsiadujące odczyty, które dekoder wykonuje
    /// jeden po drugim, trafiają dzięki temu do jednej porcji, zamiast dawać wartość policzoną
    /// z ułamka sekundy.
    /// </summary>
    private const double MinimumSpanSeconds = 0.75;

    private long _frames;
    private int _sampleRate = 48000;

    private long _granuleBytes;
    private double _granuleStartSeconds = -1;

    /// <summary>Smoothed value in kilobits per second; 0 until enough has been measured.</summary>
    public int Kilobits { get; private set; }

    /// <summary>
    /// Called once the output format is known. Also clears the counters: opening a file reads far
    /// more than it plays — miniaudio przeskanowuje cały plik MP3, żeby policzyć jego ramki —
    /// a te bajty nie należą do żadnego fragmentu nagrania.
    /// </summary>
    public void Configure(int sampleRate)
    {
        _sampleRate = Math.Max(1, sampleRate);
        Reset();
    }

    /// <summary>
    /// Called by the counting stream for every read. Tutaj powstaje pomiar: odczyt wyznacza
    /// granicę między jedną porcją danych skompresowanych a następną.
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
