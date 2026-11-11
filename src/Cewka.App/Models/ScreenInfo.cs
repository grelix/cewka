using Avalonia;
using Avalonia.Platform;

namespace Cewka.App.Models;

/// <summary>
/// Ekran w postaci niezależnej od platformy: dane do wyboru ekranu sceny i do zapamiętania marginesu.
/// </summary>
/// <param name="Key">Klucz ekranu, patrz <see cref="SceneScreens.KeyOf"/>.</param>
/// <param name="Name">Nazwa ekranu podana przez system; może być pusta.</param>
/// <param name="Bounds">Cały ekran w pikselach.</param>
/// <param name="WorkingArea">Obszar roboczy w pikselach, bez pasków systemowych.</param>
/// <param name="Scaling">Skala interfejsu ekranu.</param>
/// <param name="IsPrimary">Ekran główny systemu.</param>
public readonly record struct ScreenInfo(string Key, string Name, PixelRect Bounds, PixelRect WorkingArea, double Scaling, bool IsPrimary)
{
    /// <summary>Rozdzielczość w postaci „1920×1080”.</summary>
    public string Resolution => $"{Bounds.Width}×{Bounds.Height}";

    public static ScreenInfo From(Screen screen) =>
        new(SceneScreens.KeyOf(screen.DisplayName, screen.Bounds), screen.DisplayName?.Trim() ?? string.Empty,
            screen.Bounds, screen.WorkingArea, screen.Scaling, screen.IsPrimary);
}

/// <summary>Czyste funkcje kluczy ekranów.</summary>
public static class SceneScreens
{
    /// <summary>
    /// Klucz ekranu: nazwa i rozdzielczość. Położenie ekranu w układzie pulpitu w kluczu nie występuje,
    /// bo zmienia się przy każdym przestawieniu monitorów, a margines należy do samego ekranu. Jednakowe
    /// monitory rozróżnia dopiero <see cref="Distinguish"/>.
    /// </summary>
    public static string KeyOf(string? displayName, PixelRect bounds) =>
        $"{displayName?.Trim()}|{bounds.Width}x{bounds.Height}";

    /// <summary>Znak przed numerem jednakowego monitora w kluczu, np. „DELL|2560x1440#2”.</summary>
    private const char Twin = '#';

    /// <summary>
    /// Jednakowe monitory mają tę samą nazwę i rozdzielczość, więc i klucz. Dostają numer według położenia:
    /// od lewej, potem od góry. Pierwszy zachowuje klucz bez numeru, żeby wybór i margines zapisane przy
    /// jednym takim monitorze dalej działały. Numer w nazwie odróżnia je na liście w ustawieniach.
    /// </summary>
    public static ScreenInfo[] Distinguish(IEnumerable<ScreenInfo> screens)
    {
        var list = screens.ToArray();
        var twins = Enumerable.Range(0, list.Length).GroupBy(i => list[i].Key).Where(group => group.Count() > 1);
        foreach (var group in twins)
        {
            var number = 1;
            foreach (var i in group.OrderBy(i => list[i].Bounds.X).ThenBy(i => list[i].Bounds.Y).ToArray())
            {
                var screen = list[i];
                list[i] = screen with
                {
                    Key = number == 1 ? screen.Key : $"{screen.Key}{Twin}{number}",
                    Name = screen.Name.Length == 0 ? screen.Name : $"{screen.Name} {number}",
                };
                number++;
            }
        }

        return list;
    }

    /// <summary>
    /// Klucz modelu: klucz bez numeru jednakowego monitora. Platforma nie podaje stałego identyfikatora
    /// monitora, więc po odłączeniu jednego z jednakowych nie wiadomo, który został; są zamienne.
    /// </summary>
    public static string Model(string key)
    {
        var twin = key.LastIndexOf(Twin);
        return twin > key.LastIndexOf('|') ? key[..twin] : key;
    }

    /// <summary>Ekran z listy o danym kluczu albo nic.</summary>
    public static ScreenInfo? WithKey(IReadOnlyList<ScreenInfo> screens, string key)
    {
        foreach (var screen in screens)
            if (screen.Key == key) return screen;

        return null;
    }

    /// <summary>Ekran z listy o danych granicach albo nic.</summary>
    public static ScreenInfo? At(IReadOnlyList<ScreenInfo> screens, PixelRect bounds)
    {
        foreach (var screen in screens)
            if (screen.Bounds == bounds) return screen;

        return null;
    }

    /// <summary>
    /// Ekran sceny: zapisany, gdy jest podłączony, inaczej ekran okna. Bez zapisanego wyboru (ekran okna)
    /// i bez ekranu okna wynik jest pusty.
    /// </summary>
    public static ScreenInfo? Pick(IReadOnlyList<ScreenInfo> screens, string? saved, ScreenInfo? windowScreen)
    {
        if (saved is not null)
        {
            foreach (var screen in screens)
                if (screen.Key == saved) return screen;

            // Zapisany jeden z jednakowych monitorów, a został inny tego samego modelu.
            var model = Model(saved);
            if (model != saved)
            {
                foreach (var screen in screens)
                    if (Model(screen.Key) == model) return screen;
            }
        }

        return windowScreen;
    }

    /// <summary>Ekran, który został po odłączeniu: główny, a bez głównego pierwszy z listy.</summary>
    public static ScreenInfo? Home(IReadOnlyList<ScreenInfo> screens)
    {
        foreach (var screen in screens)
            if (screen.IsPrimary) return screen;

        return screens.Count > 0 ? screens[0] : null;
    }

    /// <summary>Lewy górny róg okna o rozmiarze <paramref name="size"/> wyśrodkowanego w obszarze roboczym.</summary>
    public static PixelPoint Centre(PixelRect area, PixelSize size) =>
        new(area.X + Math.Max(0, (area.Width - size.Width) / 2), area.Y + Math.Max(0, (area.Height - size.Height) / 2));

    /// <summary>Opis ekranu z samego klucza, np. dla ekranu zapisanego, a niepodłączonego: „HDMI-1 (1920×1080)”.</summary>
    public static string Describe(string key)
    {
        var separator = key.LastIndexOf('|');
        if (separator < 0) return key;

        var name = key[..separator];
        var resolution = key[(separator + 1)..];
        var twin = resolution.IndexOf(Twin);
        if (twin >= 0)
        {
            if (name.Length > 0) name = $"{name} {resolution[(twin + 1)..]}";
            resolution = resolution[..twin];
        }

        resolution = resolution.Replace('x', '×');
        return name.Length == 0 ? resolution : $"{name} ({resolution})";
    }
}

/// <summary>Margines ekranu sceny: kroki, ułamki i skala kadru.</summary>
public static class ScreenMargin
{
    /// <summary>
    /// Margines ekranu bez zapisanego wyboru. Poza testami stała: 2,5%. Testy układu ustawiają „Brak”,
    /// żeby kadr zajmował cały ekran.
    /// </summary>
    internal static SceneMargin Default { get; set; } = StandardDefault;

    /// <summary>Margines ekranu w programie: 2,5%.</summary>
    internal const SceneMargin StandardDefault = SceneMargin.Small;

    /// <summary>Margines z każdej strony jako ułamek szerokości i wysokości ekranu.</summary>
    public static double Fraction(SceneMargin margin) => margin switch
    {
        SceneMargin.Small => 0.025,
        SceneMargin.Large => 0.05,
        _ => 0,
    };

    /// <summary>Mnożnik skali kadru: margines odejmuje się z obu stron.</summary>
    public static double ScaleFactor(SceneMargin margin) => 1 - 2 * Fraction(margin);

    /// <summary>Sąsiedni margines w kierunku <paramref name="direction"/>; na końcach zostaje bez zmian.</summary>
    public static SceneMargin Step(SceneMargin margin, int direction) =>
        (SceneMargin)Math.Clamp((int)margin + Math.Sign(direction), (int)SceneMargin.None, (int)SceneMargin.Large);

    /// <summary>
    /// Zapisany margines ekranu o danym kluczu; dla jednakowego monitora bez własnego wpisu wpis modelu;
    /// inaczej domyślny.
    /// </summary>
    public static SceneMargin For(IReadOnlyDictionary<string, SceneMargin>? margins, string? screenKey)
    {
        if (margins is null) return Default;

        var key = screenKey ?? string.Empty;
        if (margins.TryGetValue(key, out var margin)) return margin;
        return margins.TryGetValue(SceneScreens.Model(key), out margin) ? margin : Default;
    }
}
