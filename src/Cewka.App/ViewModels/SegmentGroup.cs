using System.Collections.ObjectModel;
using Cewka.App.Localisation;

namespace Cewka.App.ViewModels;

/// <summary>
/// One choice within a <see cref="SegmentGroup"/>.
/// <para>
/// The caption is kept as a key rather than as finished text: switching language has to
/// relabel the buttons already on screen, and a string copied once would stay in whatever
/// language it was copied from.
/// </para>
/// </summary>
public sealed class SegmentOption(string key, object value, bool literal = false) : ObservableObject
{
    public string Key => key;

    public object Value => value;

    /// <summary>
    /// Literal captions exist for one case: the names of the languages themselves. „Polski"
    /// and „English" are written the same way whatever language the rest of the window is in.
    /// </summary>
    public string Label => literal ? key : Strings.Current[key];

    private bool _isSelected;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Called after a language change to relabel the button.</summary>
    internal void RefreshLabel() => Raise(nameof(Label));
}

/// <summary>
/// A row of mutually exclusive buttons, in the manner of the limiter and normalisation
/// switches in the main window. Used wherever a setting has a handful of values and a drop-down
/// list would hide them behind an extra click.
/// </summary>
public sealed class SegmentGroup
{
    private readonly Action<object> _apply;

    public SegmentGroup(Action<object> apply, params (string Key, object Value)[] options)
        : this(apply, false, options)
    {
    }

    public SegmentGroup(Action<object> apply, bool literalLabels, params (string Key, object Value)[] options)
    {
        _apply = apply;
        Options = new ObservableCollection<SegmentOption>(
            options.Select(option => new SegmentOption(option.Key, option.Value, literalLabels)));
    }

    /// <summary>For a group whose captions are not all of the same kind — see the languages.</summary>
    public SegmentGroup(Action<object> apply, IEnumerable<SegmentOption> options)
    {
        _apply = apply;
        Options = new ObservableCollection<SegmentOption>(options);
    }

    public ObservableCollection<SegmentOption> Options { get; }

    /// <summary>Applies a choice made by the user.</summary>
    public void Choose(SegmentOption option)
    {
        if (option.IsSelected) return;

        Mark(option.Value);
        _apply(option.Value);
    }

    /// <summary>Shows a value as chosen without applying it; used when the window opens.</summary>
    public void Mark(object value)
    {
        foreach (var option in Options) option.IsSelected = Equals(option.Value, value);
    }

    internal void RefreshLabels()
    {
        foreach (var option in Options) option.RefreshLabel();
    }
}
