namespace Cewka.Audio.Dsp;

/// <summary>
/// A second-order section in transposed direct form II.
/// <para>
/// That form is the right choice here because it keeps its state in the same order of
/// magnitude as the signal itself, whereas direct form I accumulates the sum of several
/// terms and loses precision faster in 32-bit arithmetic.
/// </para>
/// </summary>
public struct Biquad
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _s1, _s2;

    /// <summary>Pass-through until configured.</summary>
    public static Biquad Identity()
    {
        var filter = new Biquad();
        filter.SetCoefficients(1, 0, 0, 1, 0, 0);
        return filter;
    }

    public void SetCoefficients(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    /// <summary>
    /// Peaking EQ from the Audio EQ Cookbook: a bell centred on <paramref name="frequency"/>
    /// that leaves everything outside its bandwidth untouched.
    /// </summary>
    public void SetPeaking(double frequency, double q, double gainDb, double sampleRate)
    {
        // A bell of zero gain is exactly a pass-through; computing it wastes precision.
        if (Math.Abs(gainDb) < 1e-6)
        {
            SetCoefficients(1, 0, 0, 1, 0, 0);
            return;
        }

        // Keep the centre away from Nyquist: at 44.1 kHz the 16 kHz band sits close enough
        // that the bilinear transform would warp it badly.
        var nyquist = sampleRate / 2;
        frequency = Math.Min(frequency, nyquist * 0.92);

        var a = Math.Pow(10, gainDb / 40);
        var w0 = 2 * Math.PI * frequency / sampleRate;
        var cosW0 = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * q);

        SetCoefficients(
            b0: 1 + alpha * a,
            b1: -2 * cosW0,
            b2: 1 - alpha * a,
            a0: 1 + alpha / a,
            a1: -2 * cosW0,
            a2: 1 - alpha / a);
    }

    /// <summary>
    /// First stage of the K-weighting filter from ITU-R BS.1770-4: a high shelf standing in
    /// for the acoustic effect of the head.
    /// <para>
    /// Deliberately not expressed through <see cref="SetHighShelf"/>. The cookbook form
    /// normalises the numerator differently, which leaves a constant offset of a few
    /// hundredths of a decibel — harmless in an equaliser, wrong in a meter whose whole
    /// purpose is to agree with other meters. This derivation reproduces the coefficient
    /// table in the specification exactly, at any sample rate.
    /// </para>
    /// </summary>
    public void SetKWeightingShelf(double sampleRate)
    {
        const double frequency = 1681.974450955533;
        const double gainDb = 3.999843853973347;
        const double q = 0.7071752369554196;
        const double vbExponent = 0.4996667741545416;

        var k = Math.Tan(Math.PI * frequency / sampleRate);
        var vh = Math.Pow(10.0, gainDb / 20.0);
        var vb = Math.Pow(vh, vbExponent);
        var a0 = 1.0 + k / q + k * k;

        _b0 = (vh + vb * k / q + k * k) / a0;
        _b1 = 2.0 * (k * k - vh) / a0;
        _b2 = (vh - vb * k / q + k * k) / a0;
        _a1 = 2.0 * (k * k - 1.0) / a0;
        _a2 = (1.0 - k / q + k * k) / a0;
    }

    /// <summary>
    /// Second stage of the K-weighting filter: the RLB high pass. Its numerator is exactly
    /// 1, −2, 1 — unnormalised, unlike the cookbook high pass.
    /// </summary>
    public void SetKWeightingHighPass(double sampleRate)
    {
        const double frequency = 38.13547087602444;
        const double q = 0.5003270373238773;

        var k = Math.Tan(Math.PI * frequency / sampleRate);
        var denominator = 1.0 + k / q + k * k;

        _b0 = 1.0;
        _b1 = -2.0;
        _b2 = 1.0;
        _a1 = 2.0 * (k * k - 1.0) / denominator;
        _a2 = (1.0 - k / q + k * k) / denominator;
    }

    /// <summary>High-shelf in the Audio EQ Cookbook form. Not used by the loudness meter.</summary>
    public void SetHighShelf(double frequency, double q, double gainDb, double sampleRate)
    {
        var a = Math.Pow(10, gainDb / 40);
        var w0 = 2 * Math.PI * frequency / sampleRate;
        var cosW0 = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * q);
        var twoSqrtAAlpha = 2 * Math.Sqrt(a) * alpha;

        SetCoefficients(
            b0: a * ((a + 1) + (a - 1) * cosW0 + twoSqrtAAlpha),
            b1: -2 * a * ((a - 1) + (a + 1) * cosW0),
            b2: a * ((a + 1) + (a - 1) * cosW0 - twoSqrtAAlpha),
            a0: (a + 1) - (a - 1) * cosW0 + twoSqrtAAlpha,
            a1: 2 * ((a - 1) - (a + 1) * cosW0),
            a2: (a + 1) - (a - 1) * cosW0 - twoSqrtAAlpha);
    }

    /// <summary>High-pass, used by the second stage of the K-weighting filter.</summary>
    public void SetHighPass(double frequency, double q, double sampleRate)
    {
        var w0 = 2 * Math.PI * frequency / sampleRate;
        var cosW0 = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2 * q);

        SetCoefficients(
            b0: (1 + cosW0) / 2,
            b1: -(1 + cosW0),
            b2: (1 + cosW0) / 2,
            a0: 1 + alpha,
            a1: -2 * cosW0,
            a2: 1 - alpha);
    }

    /// <summary>Normalised coefficients, exposed so tests can check them against published tables.</summary>
    public readonly (double B0, double B1, double B2, double A1, double A2) Coefficients =>
        (_b0, _b1, _b2, _a1, _a2);

    public float Process(float input)
    {
        var output = _b0 * input + _s1;
        _s1 = _b1 * input - _a1 * output + _s2;
        _s2 = _b2 * input - _a2 * output;
        return (float)output;
    }

    public double ProcessDouble(double input)
    {
        var output = _b0 * input + _s1;
        _s1 = _b1 * input - _a1 * output + _s2;
        _s2 = _b2 * input - _a2 * output;
        return output;
    }

    public void Reset()
    {
        _s1 = 0;
        _s2 = 0;
    }
}
