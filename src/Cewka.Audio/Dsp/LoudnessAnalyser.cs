namespace Cewka.Audio.Dsp;

/// <summary>
/// Integrated loudness according to ITU-R BS.1770-4, which is what EBU R128 and
/// ReplayGain 2.0 are built on.
///
/// <para><b>Jak to działa.</b> Sygnał przechodzi przez dwustopniowy filtr K (półka górna
/// plus filtr górnoprzepustowy), który przybliża sposób, w jaki ucho ocenia głośność.
/// Następnie liczona jest średnia kwadratu w blokach 400 ms zachodzących na siebie w 75%.
/// Bloki cichsze niż −70 LUFS są odrzucane bramką bezwzględną, a z pozostałych wyznaczana
/// jest bramka względna 10 LU poniżej ich średniej. Dopiero bloki przechodzące obie bramki
/// tworzą wynik. Dwustopniowe bramkowanie sprawia, że cisza między utworami i wyciszenia
/// nie zaniżają wyniku.</para>
/// </summary>
public sealed class LoudnessAnalyser
{
    /// <summary>Absolute gate from the specification.</summary>
    private const double AbsoluteThresholdLufs = -70.0;

    /// <summary>The relative gate sits this far below the ungated mean.</summary>
    private const double RelativeGateLu = 10.0;

    /// <summary>Offset that ties the measurement to the LKFS scale.</summary>
    private const double ScaleOffset = -0.691;

    private const double BlockSeconds = 0.400;
    private const double HopSeconds = 0.100;

    private readonly int _channels;
    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;

    private readonly int _blockFrames;
    private readonly int _hopFrames;
    private readonly double[] _accumulator;
    private readonly List<double> _blockEnergies = [];

    private int _framesInHop;
    private int _hopsCollected;
    private readonly Queue<double[]> _pendingHops = new();

    public LoudnessAnalyser(int sampleRate, int channels)
    {
        _channels = channels;
        _shelf = new Biquad[channels];
        _highPass = new Biquad[channels];

        for (var c = 0; c < channels; c++)
        {
            _shelf[c].SetKWeightingShelf(sampleRate);
            _highPass[c].SetKWeightingHighPass(sampleRate);
        }

        _blockFrames = (int)Math.Round(sampleRate * BlockSeconds);
        _hopFrames = (int)Math.Round(sampleRate * HopSeconds);
        _accumulator = new double[channels];
    }

    /// <summary>Number of 400 ms blocks measured so far.</summary>
    public int BlockCount => _blockEnergies.Count;

    /// <summary>Feeds interleaved samples. Can be called any number of times.</summary>
    public void Add(ReadOnlySpan<float> buffer, int frames)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * _channels;

            for (var c = 0; c < _channels; c++)
            {
                var filtered = _highPass[c].ProcessDouble(_shelf[c].ProcessDouble(buffer[baseIndex + c]));
                _accumulator[c] += filtered * filtered;
            }

            _framesInHop++;
            if (_framesInHop < _hopFrames) continue;

            CloseHop();
        }
    }

    /// <summary>
    /// Every 100 ms of squared samples becomes one hop; a 400 ms block is the sum of four
    /// consecutive hops, which is how the 75% overlap is realised without buffering audio.
    /// </summary>
    private void CloseHop()
    {
        var hop = new double[_channels];
        Array.Copy(_accumulator, hop, _channels);
        Array.Clear(_accumulator);
        _framesInHop = 0;

        _pendingHops.Enqueue(hop);
        _hopsCollected++;

        const int hopsPerBlock = 4;
        if (_pendingHops.Count < hopsPerBlock) return;

        var sum = new double[_channels];
        foreach (var pending in _pendingHops)
            for (var c = 0; c < _channels; c++) sum[c] += pending[c];

        // Weights G are 1.0 for left and right; surround channels would use 1.41.
        var meanSquareSum = 0.0;
        for (var c = 0; c < _channels; c++) meanSquareSum += sum[c] / _blockFrames;

        _blockEnergies.Add(meanSquareSum);
        _pendingHops.Dequeue();
    }

    /// <summary>
    /// Integrated loudness in LUFS, or null when the material is too short or entirely
    /// silent to measure — under a second there are not even four hops to form one block.
    /// </summary>
    public double? ComputeIntegratedLoudness()
    {
        if (_blockEnergies.Count == 0) return null;

        // Bramka bezwzgledna.
        var aboveAbsolute = new List<double>(_blockEnergies.Count);
        foreach (var energy in _blockEnergies)
        {
            if (energy <= 0) continue;
            if (ScaleOffset + 10 * Math.Log10(energy) > AbsoluteThresholdLufs) aboveAbsolute.Add(energy);
        }

        if (aboveAbsolute.Count == 0) return null;

        // Bramka wzgledna, wyznaczona ze sredniej bloków, ktore przeszly bramke bezwzgledna.
        var ungatedMean = aboveAbsolute.Sum() / aboveAbsolute.Count;
        var relativeThreshold = ScaleOffset + 10 * Math.Log10(ungatedMean) - RelativeGateLu;

        var gated = 0.0;
        var count = 0;
        foreach (var energy in aboveAbsolute)
        {
            if (ScaleOffset + 10 * Math.Log10(energy) <= relativeThreshold) continue;
            gated += energy;
            count++;
        }

        if (count == 0) return null;

        return ScaleOffset + 10 * Math.Log10(gated / count);
    }

    /// <summary>
    /// ReplayGain 2.0 track gain: the difference between the measurement and the −18 LUFS
    /// reference level the standard settles on.
    /// </summary>
    public static double ToReplayGain(double integratedLufs) => -18.0 - integratedLufs;
}
