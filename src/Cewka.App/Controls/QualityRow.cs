using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Cewka.App.Controls;

/// <summary>
/// Wiersz z etykietą jakości źródła za tekstem: pierwsze dziecko (wiersz wykonawcy albo „Na żywo”)
/// stoi na początku, drugie (etykieta jakości) zaraz za nim, z odstępem i na wspólnej linii bazowej.
/// <para>
/// Gdy wiersz się nie mieści, ustępuje pierwsze dziecko: tekst z przycinaniem skraca się wielokropkiem,
/// a etykieta zostaje w całości. Etykieta nigdy nie jest ucinana ani zawijana. Znika w całości dopiero
/// wtedy, gdy obok niej pierwsze dziecko nie miałoby choćby <see cref="LeadMinEm"/> em swojego pisma
/// (tekst bez przycinania: całej swojej szerokości) albo gdy sama nie mieści się w wierszu.
/// </para>
/// </summary>
public sealed class QualityRow : Panel
{
    /// <summary>Odstęp przed etykietą jako część rozmiaru jej pisma.</summary>
    public const double GapPerEm = 0.7;

    /// <summary>Najmniejsza szerokość przycinanego tekstu obok etykiety: kilka liter i wielokropek.</summary>
    public const double LeadMinEm = 3;

    private double _leadWidth;
    private double _gap;
    private double _top;

    /// <summary>Etykieta stoi w wierszu; fałsz, gdy jest ukryta albo nie zmieściła się.</summary>
    public bool ShowsLabel { get; private set; }

    private Control? Lead => Children.Count > 0 ? Children[0] : null;

    private Control? Label => Children.Count > 1 ? Children[1] : null;

    protected override void ChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        base.ChildrenChanged(sender, e);

        if (e.OldItems is not null)
            foreach (var child in e.OldItems.OfType<Control>())
                child.PropertyChanged -= OnChildPropertyChanged;

        if (e.NewItems is not null)
            foreach (var child in e.NewItems.OfType<Control>())
                child.PropertyChanged += OnChildPropertyChanged;

        // Etykieta, która się nie zmieściła, dostaje pusty prostokąt; bez przycięcia jej tekst
        // rysowałby się na wierszu wykonawcy.
        if (Label is { } label) label.ClipToBounds = true;
    }

    /// <summary>
    /// Przycinany tekst jest mierzony w zawężonej szerokości, więc dłuższy tekst nie zmienia jego
    /// żądanego rozmiaru i układ by go nie przeliczył; zmiana tekstu lub pisma liczy wiersz od nowa.
    /// </summary>
    private void OnChildPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBlock.TextProperty || e.Property == TextBlock.FontSizeProperty
            || e.Property == TextBlock.FontWeightProperty || e.Property == TextBlock.FontFamilyProperty
            || e.Property == TextBlock.LetterSpacingProperty || e.Property == TextBlock.TextTrimmingProperty)
            InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lead = Lead;
        var label = Label;
        var width = availableSize.Width;
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);

        lead?.Measure(unbounded);
        label?.Measure(unbounded);

        var leadOn = lead is { IsVisible: true };
        var labelOn = label is { IsVisible: true };
        var leadNatural = leadOn ? lead!.DesiredSize.Width : 0;
        var labelWidth = labelOn ? label!.DesiredSize.Width : 0;
        var gap = leadOn && labelOn ? GapPerEm * FontSizeOf(label!) : 0;
        var leadMin = leadOn ? Math.Min(leadNatural, MinWidthOf(lead!, leadNatural)) : 0;

        // Pół piksela zapasu na zaokrąglenie układu.
        ShowsLabel = labelOn && leadMin + gap + labelWidth <= width + 0.5;
        if (!ShowsLabel)
        {
            gap = 0;
            labelWidth = 0;
        }

        _gap = gap;
        _leadWidth = leadOn ? Math.Max(0, Math.Min(leadNatural, width - gap - labelWidth)) : 0;

        // Tylko przycinany tekst dostaje zawężoną szerokość; reszta zostaje w naturalnej.
        if (leadOn && IsTrimmable(lead!) && _leadWidth < leadNatural)
            lead!.Measure(new Size(_leadWidth, availableSize.Height));

        // Wspólna linia bazowa: góra wiersza wypada tam, gdzie wyżej sięga któreś z dzieci.
        var leadBase = leadOn ? BaselineOf(lead!) : 0;
        var labelBase = ShowsLabel ? BaselineOf(label!) : 0;
        _top = Math.Max(leadBase, labelBase);

        var below = Math.Max(
            leadOn ? lead!.DesiredSize.Height - leadBase : 0,
            ShowsLabel ? label!.DesiredSize.Height - labelBase : 0);

        return new Size(_leadWidth + gap + labelWidth, _top + below);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var lead = Lead;
        var label = Label;

        if (lead is not null)
        {
            lead.Arrange(lead.IsVisible
                ? new Rect(0, _top - BaselineOf(lead), _leadWidth, lead.DesiredSize.Height)
                : default);
        }

        if (label is not null)
        {
            label.Arrange(ShowsLabel
                ? new Rect(_leadWidth + _gap, _top - BaselineOf(label), label.DesiredSize.Width, label.DesiredSize.Height)
                : default);
        }

        for (var i = 2; i < Children.Count; i++) Children[i].Arrange(default);

        return finalSize;
    }

    private static bool IsTrimmable(Control control) =>
        control is TextBlock { TextTrimming: var trimming } && trimming != TextTrimming.None;

    private static double MinWidthOf(Control lead, double natural) =>
        IsTrimmable(lead) ? LeadMinEm * FontSizeOf(lead) : natural;

    private static double FontSizeOf(Control control) => control switch
    {
        TextBlock text => text.FontSize,
        Panel panel => panel.Children.OfType<TextBlock>().FirstOrDefault(t => t.IsVisible)?.FontSize ?? 0,
        _ => 0,
    };

    /// <summary>
    /// Odległość linii bazowej od górnej krawędzi dziecka. Panel (kropka z napisem „Na żywo”) ma ją
    /// po pierwszym widocznym napisie, który w poziomym panelu stoi przy górnej krawędzi.
    /// </summary>
    private static double BaselineOf(Control control)
    {
        var text = control as TextBlock
                   ?? (control as Panel)?.Children.OfType<TextBlock>().FirstOrDefault(t => t.IsVisible);

        if (text?.TextLayout is { TextLines.Count: > 0 } layout)
            return text.Margin.Top + text.Padding.Top + layout.TextLines[0].Baseline;

        return control.DesiredSize.Height;
    }
}
