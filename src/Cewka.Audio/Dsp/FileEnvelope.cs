namespace Cewka.Audio.Dsp;

/// <summary>
/// Obwiednia całego pliku: szczyt i RMS w kilku tysiącach równych odcinków, zapisane w bajtach
/// (pierwiastek wartości względem pełnej skali, żeby ciche nagrania zachowały rozdzielczość).
/// Zajmuje około 4 KB na plik i trafia do pamięci podręcznej analiz głośności.
/// </summary>
public sealed class FileEnvelope
{
    /// <summary>Docelowa liczba punktów obwiedni.</summary>
    public const int TargetPoints = 2000;

    /// <summary>Pusta obwiednia nie istnieje; konstruktor wymaga równych, niepustych tablic.</summary>
    public FileEnvelope(byte[] peak, byte[] rms)
    {
        ArgumentNullException.ThrowIfNull(peak);
        ArgumentNullException.ThrowIfNull(rms);
        if (peak.Length == 0 || peak.Length != rms.Length)
            throw new ArgumentException("Tablice szczytu i RMS muszą mieć tę samą, niezerową długość.");

        Peak = peak;
        Rms = rms;
    }

    /// <summary>Szczyty odcinków, bajt = pierwiastek wartości względnej razy 255.</summary>
    public byte[] Peak { get; }

    /// <summary>RMS odcinków, kodowany jak <see cref="Peak"/>.</summary>
    public byte[] Rms { get; }

    public int Count => Peak.Length;

    /// <summary>Szczyt odcinka jako wartość liniowa 0 do 1.</summary>
    public float PeakAt(int index) => Decode(Peak[index]);

    /// <summary>RMS odcinka jako wartość liniowa 0 do 1.</summary>
    public float RmsAt(int index) => Decode(Rms[index]);

    internal static byte Encode(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return 0;
        return (byte)Math.Round(Math.Sqrt(Math.Min(1, value)) * 255);
    }

    internal static float Decode(byte value)
    {
        var x = value / 255f;
        return x * x;
    }
}

/// <summary>
/// Buduje <see cref="FileEnvelope"/> ze strumienia o nieznanej długości: odcinki startują od 256
/// ramek i dwukrotnie się wydłużają (sąsiednie łączą się parami), gdy jest ich więcej niż
/// <see cref="Capacity"/>, więc pamięć jest stała. Na koniec odcinki scalane są do najwyżej
/// <see cref="FileEnvelope.TargetPoints"/> punktów.
/// </summary>
public sealed class EnvelopeBuilder
{
    private const int Capacity = 4096;
    private const int InitialBlock = 256;

    private readonly float[] _peak = new float[Capacity];
    private readonly double[] _energy = new double[Capacity];
    private readonly long[] _samples = new long[Capacity];
    private int _bins;
    private int _block = InitialBlock;
    private int _blockFrames;
    private float _blockPeak;
    private double _blockEnergy;
    private long _blockSamples;

    /// <summary>Dopisuje ramki z bufora przeplatanego; próbki nieskończone liczą się jak cisza.</summary>
    public void Add(ReadOnlySpan<float> interleaved, int frames, int channels)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            for (var c = 0; c < channels; c++)
            {
                var v = interleaved[frame * channels + c];
                if (!float.IsFinite(v)) v = 0;
                var a = Math.Abs(v);
                if (a > _blockPeak) _blockPeak = a;
                _blockEnergy += (double)v * v;
            }

            _blockSamples += channels;
            if (++_blockFrames == _block) Close();
        }
    }

    private void Close()
    {
        if (_bins == Capacity) Halve();

        _peak[_bins] = _blockPeak;
        _energy[_bins] = _blockEnergy;
        _samples[_bins] = _blockSamples;
        _bins++;
        _blockFrames = 0;
        _blockPeak = 0;
        _blockEnergy = 0;
        _blockSamples = 0;
    }

    private void Halve()
    {
        var half = Capacity / 2;
        for (var i = 0; i < half; i++)
        {
            _peak[i] = Math.Max(_peak[2 * i], _peak[2 * i + 1]);
            _energy[i] = _energy[2 * i] + _energy[2 * i + 1];
            _samples[i] = _samples[2 * i] + _samples[2 * i + 1];
        }

        _bins = half;
        _block *= 2;
    }

    /// <summary>Gotowa obwiednia; null, gdy nie dopisano żadnej ramki.</summary>
    public FileEnvelope? Build()
    {
        // Niepełny ostatni odcinek też wchodzi do obwiedni.
        var bins = _bins + (_blockFrames > 0 ? 1 : 0);
        if (bins == 0) return null;

        var points = Math.Min(FileEnvelope.TargetPoints, bins);
        var peak = new byte[points];
        var rms = new byte[points];
        for (var p = 0; p < points; p++)
        {
            var from = (int)((long)p * bins / points);
            var to = (int)((long)(p + 1) * bins / points);
            float top = 0;
            double energy = 0;
            long samples = 0;
            for (var i = from; i < to; i++)
            {
                var closed = i < _bins;
                var bp = closed ? _peak[i] : _blockPeak;
                if (bp > top) top = bp;
                energy += closed ? _energy[i] : _blockEnergy;
                samples += closed ? _samples[i] : _blockSamples;
            }

            peak[p] = FileEnvelope.Encode(top);
            rms[p] = FileEnvelope.Encode(samples > 0 ? Math.Sqrt(energy / samples) : 0);
        }

        return new FileEnvelope(peak, rms);
    }
}
