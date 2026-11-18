using Cewka.Audio.Dsp;

namespace Cewka.App.Models;

/// <summary>Rodzaj korektora, który gra: dziesięć suwaków albo pasma parametryczne.</summary>
public enum EqualiserMode
{
    Graphic,
    Parametric,
}

/// <summary>Zapisane pasmo korektora parametrycznego.</summary>
public sealed class ParametricBandSettings
{
    public ParametricFilterType Type { get; set; } = ParametricFilterType.Peak;

    /// <summary>Częstotliwość w hercach, 20–20 000.</summary>
    public double Frequency { get; set; } = 1000;

    /// <summary>Wzmocnienie w decybelach, −24…+24.</summary>
    public double Gain { get; set; }

    /// <summary>Dobroć, 0,1–20.</summary>
    public double Q { get; set; } = 1.41;

    public bool Enabled { get; set; } = true;

    public ParametricBandSettings Clone() => new() { Type = Type, Frequency = Frequency, Gain = Gain, Q = Q, Enabled = Enabled };

    /// <summary>Wartości spoza zakresów (plik poprawiany ręcznie) sprowadzone do dozwolonych.</summary>
    public void Sanitise()
    {
        var band = new ParametricBand(0, Type, Frequency, Gain, Q).Clamped();
        Type = band.Type;
        Frequency = band.Frequency;
        Gain = band.Gain;
        Q = band.Q;
    }
}

/// <summary>
/// Ustawienia korektora i efektów zapamiętane dla jednego urządzenia wyjściowego. Ogranicznik
/// i normalizacja do profilu nie należą: chronią wyjście i poziom, a nie brzmienie słuchawek.
/// </summary>
public sealed class SoundProfile
{
    /// <summary>Najwięcej profili w pliku; najdawniej używany wypada.</summary>
    public const int MaxProfiles = 32;

    /// <summary>Nazwa urządzenia, jak podaje ją system.</summary>
    public string Device { get; set; } = string.Empty;

    public bool EqualiserEnabled { get; set; } = true;

    public EqualiserMode EqualiserMode { get; set; } = EqualiserMode.Graphic;

    public double Preamp { get; set; }

    public double[] EqualiserGains { get; set; } = new double[10];

    public double ParametricPreamp { get; set; }

    public List<ParametricBandSettings> ParametricBands { get; set; } = [];

    public bool CrossfeedEnabled { get; set; }

    public double CrossfeedStrength { get; set; } = 0.5;

    public bool LoudnessEnabled { get; set; }

    public double LoudnessStrength { get; set; } = 1.0;

    public bool VirtualBassEnabled { get; set; }

    public double VirtualBassStrength { get; set; } = 0.5;

    public bool DynamicRangeEnabled { get; set; }

    public double DynamicRangeStrength { get; set; } = 0.5;

    public bool StereoWidthEnabled { get; set; }

    public double StereoWidthStrength { get; set; } = 0.5;

    /// <summary>Bieżące wartości z ustawień jako profil urządzenia.</summary>
    public static SoundProfile Capture(string device, AppSettings s) => new()
    {
        Device = device,
        EqualiserEnabled = s.EqualiserEnabled,
        EqualiserMode = s.EqualiserMode,
        Preamp = s.Preamp,
        EqualiserGains = (double[])s.EqualiserGains.Clone(),
        ParametricPreamp = s.ParametricPreamp,
        ParametricBands = [.. s.ParametricBands.Select(band => band.Clone())],
        CrossfeedEnabled = s.CrossfeedEnabled,
        CrossfeedStrength = s.CrossfeedStrength,
        LoudnessEnabled = s.LoudnessEnabled,
        LoudnessStrength = s.LoudnessStrength,
        VirtualBassEnabled = s.VirtualBassEnabled,
        VirtualBassStrength = s.VirtualBassStrength,
        DynamicRangeEnabled = s.DynamicRangeEnabled,
        DynamicRangeStrength = s.DynamicRangeStrength,
        StereoWidthEnabled = s.StereoWidthEnabled,
        StereoWidthStrength = s.StereoWidthStrength,
    };

    public SoundProfile Clone()
    {
        var copy = (SoundProfile)MemberwiseClone();
        copy.EqualiserGains = (double[])EqualiserGains.Clone();
        copy.ParametricBands = [.. ParametricBands.Select(band => band.Clone())];
        return copy;
    }

    /// <summary>Naprawia wartości, które ręcznie poprawiony plik mógł zepsuć.</summary>
    public void Sanitise()
    {
        Device ??= string.Empty;
        if (!Enum.IsDefined(EqualiserMode)) EqualiserMode = EqualiserMode.Graphic;

        Preamp = Settings.ClampFinite(Preamp, -12, 12);
        EqualiserGains = Settings.TenGains(EqualiserGains);
        ParametricPreamp = Settings.ClampFinite(ParametricPreamp, ParametricEqualiser.MinPreamp, ParametricEqualiser.MaxPreamp);
        ParametricBands = Settings.Bands(ParametricBands);

        CrossfeedStrength = Settings.ClampFinite(CrossfeedStrength, 0, 1);
        LoudnessStrength = Settings.ClampFinite(LoudnessStrength, 0, 1);
        VirtualBassStrength = Settings.ClampFinite(VirtualBassStrength, 0, 1);
        DynamicRangeStrength = Settings.ClampFinite(DynamicRangeStrength, 0, 1);
        StereoWidthStrength = Settings.ClampFinite(StereoWidthStrength, 0, 1);
    }

    /// <summary>Wspólne poprawki wartości dźwięku dla ustawień i profili.</summary>
    internal static class Settings
    {
        public static double ClampFinite(double value, double minimum, double maximum) =>
            double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : 0;

        /// <summary>Dokładnie dziesięć wzmocnień w zakresie ±12 dB; brakujące są zerami.</summary>
        public static double[] TenGains(double[]? gains)
        {
            var fixedGains = new double[10];
            for (var i = 0; i < 10; i++)
                fixedGains[i] = gains is not null && i < gains.Length ? ClampFinite(gains[i], -12, 12) : 0;
            return fixedGains;
        }

        /// <summary>Najwyżej 20 pasm, bez pustych wpisów, każde w zakresach.</summary>
        public static List<ParametricBandSettings> Bands(List<ParametricBandSettings>? bands)
        {
            var result = (bands ?? []).Where(band => band is not null).Take(ParametricEqualiser.MaxBands).ToList();
            foreach (var band in result) band.Sanitise();
            return result;
        }
    }
}
