using Cewka.App.Localisation;
using Cewka.App.Models;

namespace Cewka.App.ViewModels;

/// <summary>Przełącznik wizualizacji w cyklu klawisza V w Ustawieniach (Scena › Wizualizacje i efekty).</summary>
public sealed class SceneCycleOption(SceneVisual visual, MainViewModel player) : ObservableObject
{
    public SceneVisual Visual => visual;

    public string Label => Strings.Current["SceneVisual" + visual];

    public bool IsOn
    {
        get => player.IsInCycle(visual);
        set
        {
            player.SetInCycle(visual, value);
            Raise();
        }
    }

    internal void Refresh()
    {
        Raise(nameof(Label));
        Raise(nameof(IsOn));
    }
}

/// <summary>Efekt tła sceny w Ustawieniach: przełącznik i siła (Słabe, Średnie, Silne).</summary>
public sealed class SceneEffectOption : ObservableObject
{
    private readonly Func<bool> _isOn;
    private readonly Action<bool> _setOn;

    public SceneEffectOption(string key, Func<bool> isOn, Action<bool> setOn,
        Func<SceneEffectStrength> strength, Action<SceneEffectStrength> setStrength)
    {
        Key = key;
        _isOn = isOn;
        _setOn = setOn;
        Strength = new SegmentGroup(
            value => setStrength((SceneEffectStrength)value),
            ("StrengthSubtle", SceneEffectStrength.Subtle),
            ("StrengthNormal", SceneEffectStrength.Normal),
            ("StrengthStrong", SceneEffectStrength.Strong));
        Strength.Mark(strength());
    }

    public string Key { get; }

    public string Label => Strings.Current["Effect" + Key];

    public string Hint => Strings.Current["HintEffect" + Key];

    public bool IsOn
    {
        get => _isOn();
        set
        {
            _setOn(value);
            Raise();
        }
    }

    public SegmentGroup Strength { get; }

    internal void Refresh()
    {
        Raise(nameof(Label));
        Raise(nameof(Hint));
        Raise(nameof(IsOn));
        Strength.RefreshLabels();
    }
}
