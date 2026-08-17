using System.Globalization;

namespace Cewka.App.ViewModels;

/// <summary>One equaliser band, or the preamp, which behaves identically.</summary>
public sealed class EqBandViewModel : ObservableObject
{
    private double _gain;

    public EqBandViewModel(string label, double gain)
    {
        Label = label;
        _gain = gain;
    }

    /// <summary>Frequency caption such as <c>125</c> or <c>16k</c>, or <c>PREAMP</c>.</summary>
    public string Label { get; }

    /// <summary>Gain in decibels.</summary>
    public double Gain
    {
        get => _gain;
        set
        {
            if (!Set(ref _gain, value)) return;
            Raise(nameof(GainText));
        }
    }

    /// <summary>
    /// Signed value with a true minus sign (U+2212) rather than a hyphen, matching the
    /// design and keeping the column visually aligned in the monospaced font.
    /// </summary>
    public string GainText
    {
        get
        {
            var rounded = Math.Round(_gain * 2, MidpointRounding.AwayFromZero) / 2;
            var sign = rounded > 0 ? "+" : rounded < 0 ? "−" : string.Empty;
            return sign + Math.Abs(rounded).ToString("F1", CultureInfo.InvariantCulture);
        }
    }
}
