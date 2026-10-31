using Cewka.App.Localisation;

namespace Cewka.App.ViewModels;

/// <summary>
/// Jeden efekt dźwiękowy w interfejsie: przełącznik i suwak siły.
///
/// <para>Efekty różnią się tylko nazwą i celem wartości, więc okno rysuje je z listy.</para>
/// </summary>
public sealed class EffectViewModel : ObservableObject
{
    private bool _enabled;
    private double _strength;

    public EffectViewModel(string key, bool enabled, double strength)
    {
        Key = key;
        _enabled = enabled;
        _strength = Math.Clamp(strength, 0, 1);
    }

    /// <summary>
    /// Nazwa własna efektu, na przykład <c>Crossfeed</c>. Służy za trzon klucza językowego
    /// i za rozpoznanie efektu przez model widoku okna.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Etykieta czytana z pliku językowego przy każdym odczycie, a nie zapamiętana przy
    /// utworzeniu. Dzięki temu zmiana języka wymaga samego powiadomienia, bez odtwarzania listy.
    /// </summary>
    public string Label => Strings.Current["Effect" + Key];

    public string Description => Strings.Current["EffectDescription" + Key];

    /// <summary>Nazwa suwaka siły dla czytnika ekranu.</summary>
    public string StrengthName => string.Format(Strings.Current["EffectStrength"], Label);

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            Raise(nameof(RowOpacity));
        }
    }

    /// <summary>
    /// Siła działania w zakresie 0–1, czyli w postaci, jakiej oczekuje suwak.
    ///
    /// <para>Jedenaście położeń co dziesięć procent, jak <c>Step</c> suwaków; zaokrąglenie
    /// pilnuje wartości z innych źródeł, na przykład z ręcznie poprawionych ustawień.</para>
    /// </summary>
    public double Strength
    {
        get => _strength;
        set
        {
            var snapped = Math.Round(Math.Clamp(value, 0, 1) * Steps) / Steps;
            if (Set(ref _strength, snapped)) Raise(nameof(StrengthText));
        }
    }

    /// <summary>Liczba stopni skali. Dziesięć, więc jeden punkt odpowiada dziesięciu procentom.</summary>
    private const double Steps = 10;

    /// <summary>Siła w punktach skali — to, co widać obok suwaka.</summary>
    public string StrengthText => $"{Math.Round(_strength * Steps):0}";

    /// <summary>
    /// Wygaszenie wiersza przy wyłączonym efekcie. Ta sama zasada, co przy korektorze:
    /// suwak pozostaje widoczny i można go ustawić z góry, ale widać, że nic nie robi.
    /// </summary>
    public double RowOpacity => _enabled ? 1.0 : 0.42;

    /// <summary>Wywoływane po zmianie języka; sam napis czytany jest na bieżąco.</summary>
    public void RefreshLabels()
    {
        Raise(nameof(Label));
        Raise(nameof(Description));
        Raise(nameof(StrengthName));
    }
}
