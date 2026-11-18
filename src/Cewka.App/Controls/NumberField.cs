using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Cewka.App.Localisation;
using Cewka.Audio.Dsp;

namespace Cewka.App.Controls;

/// <summary>Wielkość w polu liczbowym pasma korektora: zakres, krok strzałek i zapis.</summary>
public enum NumberFieldKind
{
    /// <summary>Herce, 20–20 000; strzałka przesuwa o 1/12 oktawy.</summary>
    Frequency,

    /// <summary>Decybele, −24…+24; strzałka o 0,5 dB.</summary>
    Gain,

    /// <summary>Dobroć, 0,1–20; strzałka o 0,05.</summary>
    Q,
}

/// <summary>
/// Pole liczbowe pasma korektora parametrycznego. Wartość zatwierdza Enter albo wyjście z pola,
/// strzałki w górę i w dół zmieniają ją o krok, wartość spoza zakresu jest przycinana, a tekst,
/// który nie jest liczbą, wraca do poprzedniej wartości. Liczby zapisane według języka interfejsu;
/// przyjmowana jest kropka i przecinek, z jednostką albo bez.
/// </summary>
public sealed class NumberField : TextBox
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<NumberField, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<NumberFieldKind> KindProperty =
        AvaloniaProperty.Register<NumberField, NumberFieldKind>(nameof(Kind));

    /// <summary>Twarda spacja między liczbą a jednostką.</summary>
    private const char NoBreakSpace = (char)0xA0;

    /// <summary>Minus typograficzny, jak w opisach suwaków korektora graficznego.</summary>
    private const char Minus = (char)0x2212;

    private static readonly double SemitoneRatio = Math.Pow(2, 1.0 / 12);

    private readonly PropertyChangedEventHandler _languageWatcher;
    private bool _showUnit = true;

    public NumberField()
    {
        _languageWatcher = (_, _) => ShowValue();
        ShowValue();
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public NumberFieldKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>Zapis wartości w polu, np. „1000 Hz”, „−2,5 dB”, „1,41”.</summary>
    /// <param name="unit">Z jednostką; w wąskim polu sama liczba, a jednostkę podaje nazwa pola dla czytnika.</param>
    public static string Format(NumberFieldKind kind, double value, NumberFormatInfo numbers, bool unit = true)
    {
        var text = kind switch
        {
            NumberFieldKind.Frequency => value.ToString(value < 100 ? "0.#" : "0", numbers) + (unit ? NoBreakSpace + "Hz" : string.Empty),
            NumberFieldKind.Gain => (value > 0 ? "+" : string.Empty) + value.ToString("0.0", numbers) + (unit ? NoBreakSpace + "dB" : string.Empty),
            _ => value.ToString("0.00", numbers),
        };

        return text.Replace('-', Minus);
    }

    /// <summary>Odczyt wpisanego tekstu: kropka albo przecinek, minus zwykły albo typograficzny, jednostka nieobowiązkowa.</summary>
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var cleaned = text.Trim().Replace(NoBreakSpace, ' ').Replace(Minus, '-').Replace(',', '.');
        foreach (var unit in (string[])["hz", "db"])
            if (cleaned.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
                cleaned = cleaned[..^unit.Length];

        return double.TryParse(cleaned.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }

    public static double Clamp(NumberFieldKind kind, double value) => kind switch
    {
        NumberFieldKind.Frequency => ParametricEqualiser.ClampFrequency(value),
        NumberFieldKind.Gain => ParametricEqualiser.ClampGain(value),
        _ => ParametricEqualiser.ClampQ(value),
    };

    /// <summary>Wartość po jednym kroku strzałki w górę (<paramref name="direction"/> 1) albo w dół (−1).</summary>
    public static double Step(NumberFieldKind kind, double value, int direction) => Clamp(kind, kind switch
    {
        NumberFieldKind.Frequency => value * Math.Pow(SemitoneRatio, direction),
        NumberFieldKind.Gain => value + 0.5 * direction,
        _ => value + 0.05 * direction,
    });

    /// <summary>Tekst, który pole pokazuje dla bieżącej wartości.</summary>
    private string Shown => Format(Kind, Value, Strings.Current.Numbers, _showUnit);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Strings.Current.PropertyChanged += _languageWatcher;
        ShowValue();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Strings.Current.PropertyChanged -= _languageWatcher;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == KindProperty) ShowValue();
    }

    /// <summary>Jednostka zostaje, gdy mieści się najszerszy zapis tej wielkości; inaczej pole pokazuje samą liczbę.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        var widest = Format(Kind, Kind == NumberFieldKind.Frequency ? 20000 : -24, Strings.Current.Numbers);
        var text = new FormattedText(widest, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight), FontSize, null);
        var fits = text.Width + Padding.Left + Padding.Right + BorderThickness.Left + BorderThickness.Right + 2 <= e.NewSize.Width;

        if (fits == _showUnit) return;
        _showUnit = fits;
        ShowValue();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                SelectAll();
                e.Handled = true;
                return;
            case Key.Up:
            case Key.Down:
                Commit();
                Value = Step(Kind, Value, e.Key == Key.Up ? 1 : -1);
                SelectAll();
                e.Handled = true;
                return;
            case Key.Escape when Text != Shown:
                ShowValue();
                SelectAll();
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);

        // Klawisze pisania nie docierają do okna: tam Spacja, litery i strzałki sterują odtwarzaniem.
        // Tabulator, Esc i skróty z modyfikatorem idą dalej.
        if (!e.Handled && e.Key is not (Key.Tab or Key.Escape)
            && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
            e.Handled = true;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        Commit();
        base.OnLostFocus(e);
    }

    /// <summary>Przyjmuje wpisany tekst albo przywraca poprzednią wartość.</summary>
    public void Commit()
    {
        // Wpis równy wyświetlanej wartości nie zmienia wartości: inaczej samo przejście tabulatorem
        // przez pole zaokrąglałoby częstotliwość ustawioną strzałkami.
        if (TryParse(Text, out var parsed) && Text?.Trim() != Shown) Value = Clamp(Kind, parsed);

        ShowValue();
    }

    private void ShowValue() => Text = Shown;
}
