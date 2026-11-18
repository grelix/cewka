using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.Audio.Dsp;

namespace Cewka.App.ViewModels;

/// <summary>Jedno pasmo korektora parametrycznego w liście panelu.</summary>
public sealed class ParametricBandViewModel : ObservableObject
{
    private static int _nextId;

    private ParametricFilterType _type;
    private double _frequency;
    private double _gain;
    private double _q;
    private bool _enabled;
    private int _number = 1;

    public ParametricBandViewModel(ParametricFilterType type, double frequency, double gain, double q, bool enabled = true)
    {
        // Identyfikator rozpoznaje pasmo w torze dźwięku także po usunięciu innego pasma z listy.
        Id = Interlocked.Increment(ref _nextId);

        var band = new ParametricBand(Id, type, frequency, gain, q, enabled).Clamped();
        _type = band.Type;
        _frequency = band.Frequency;
        _gain = band.Gain;
        _q = band.Q;
        _enabled = enabled;
    }

    public static ParametricBandViewModel From(ParametricBandSettings band) =>
        new(band.Type, band.Frequency, band.Gain, band.Q, band.Enabled);

    public int Id { get; }

    public ParametricFilterType Type
    {
        get => _type;
        set
        {
            if (!Enum.IsDefined(value) || !Set(ref _type, value)) return;
            Raise(nameof(IsPeak));
            Raise(nameof(IsLowShelf));
            Raise(nameof(IsHighShelf));
        }
    }

    public bool IsPeak => _type == ParametricFilterType.Peak;

    public bool IsLowShelf => _type == ParametricFilterType.LowShelf;

    public bool IsHighShelf => _type == ParametricFilterType.HighShelf;

    public double Frequency
    {
        get => _frequency;
        set => Set(ref _frequency, ParametricEqualiser.ClampFrequency(value));
    }

    public double Gain
    {
        get => _gain;
        set => Set(ref _gain, ParametricEqualiser.ClampGain(value));
    }

    public double Q
    {
        get => _q;
        set => Set(ref _q, ParametricEqualiser.ClampQ(value));
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            Raise(nameof(RowOpacity));
        }
    }

    /// <summary>Wyłączone pasmo przygasa jak wyłączony efekt.</summary>
    public double RowOpacity => _enabled ? 1.0 : 0.4;

    /// <summary>Numer na liście, od jedynki; nazwy dla czytnika ekranu go podają.</summary>
    public int Number
    {
        get => _number;
        set
        {
            if (!Set(ref _number, value)) return;
            RefreshLabels();
        }
    }

    public string Name => Format("EqualiserBandName");

    public string FilterName => Format("EqualiserBandFilterName");

    public string FrequencyName => Format("EqualiserBandFrequencyName");

    public string GainName => Format("EqualiserBandGainName");

    public string QName => Format("EqualiserBandQName");

    /// <summary>Nazwy przycisków wiersza dla czytnika: numer pasma przed nazwą polecenia, np. „Pasmo 3, Usuń pasmo”.</summary>
    public string RemoveName => WithBand("TooltipEqualiserRemoveBand");

    public string PeakName => WithBand("FilterPeak");

    public string LowShelfName => WithBand("FilterLowShelf");

    public string HighShelfName => WithBand("FilterHighShelf");
    public ParametricBand ToBand() => new(Id, _type, _frequency, _gain, _q, _enabled);

    public ParametricBandSettings ToSettings() =>
        new() { Type = _type, Frequency = _frequency, Gain = _gain, Q = _q, Enabled = _enabled };

    /// <summary>Po zmianie języka albo numeru.</summary>
    public void RefreshLabels()
    {
        Raise(nameof(Name));
        Raise(nameof(FilterName));
        Raise(nameof(FrequencyName));
        Raise(nameof(GainName));
        Raise(nameof(QName));
        Raise(nameof(RemoveName));
        Raise(nameof(PeakName));
        Raise(nameof(LowShelfName));
        Raise(nameof(HighShelfName));
    }

    private string WithBand(string key) => Name + ", " + Strings.Current[key];

    private string Format(string key) =>
        string.Format(Strings.Current.Numbers, Strings.Current[key], _number);
}
