using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform;
using Cewka.Platform;

namespace Cewka.App.Localisation;

/// <summary>A language the interface can be shown in.</summary>
public sealed record LanguageInfo(string Code, string Name);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LanguageJsonContext : JsonSerializerContext;

/// <summary>
/// Every piece of text shown in the interface, looked up by key.
///
/// <para><b>Dlaczego indekser, a nie stałe.</b> Teksty pobierane są przez wiązanie
/// <c>{l:Translate Klucz}</c>, które obserwuje zmianę indeksatora. Dzięki temu zmiana języka
/// odświeża cały interfejs bez ponownego otwierania okna — a to jedyny sposób, żeby ustawienie
/// języka w oknie ustawień działało tak, jak użytkownik oczekuje.</para>
///
/// <para>Dodanie kolejnego języka sprowadza się do dołożenia jednego pliku JSON obok
/// <c>pl.json</c> i dopisania go do listy poniżej. Klucze nieprzetłumaczone wracają do
/// polskiego, więc częściowe tłumaczenie nie psuje interfejsu.</para>
/// </summary>
public sealed class Strings : INotifyPropertyChanged
{
    /// <summary>Language used when a key is missing anywhere else.</summary>
    public const string FallbackCode = "pl";

    /// <summary>
    /// Języki, w jakich program jest dostępny. Kolejność jest kolejnością na liście wyboru:
    /// polski i angielski na początku, dalej alfabetycznie po kodzie.
    /// </summary>
    private static readonly string[] Codes =
    [
        "pl", "en",
        "cs", "de", "el", "es", "fr", "hu", "id", "it", "nl", "pt", "ro", "ru", "tr", "uk", "vi",
    ];

    private Dictionary<string, string> _active = [];
    private Dictionary<string, string> _fallback = [];

    private Strings()
    {
        _fallback = Load(FallbackCode);
        _active = _fallback;
        CurrentCode = FallbackCode;
        Numbers = BuildNumbers();
    }

    /// <summary>The single instance bound to by the interface.</summary>
    public static Strings Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentCode { get; private set; }

    /// <summary>
    /// Languages available to choose from, for the settings window. Czytane dopiero przy
    /// pierwszym otwarciu okna ustawień: wymaga to przejrzenia wszystkich plików języków.
    /// </summary>
    public static IReadOnlyList<LanguageInfo> Available => LazyAvailable.Value;

    private static readonly Lazy<IReadOnlyList<LanguageInfo>> LazyAvailable = new(BuildAvailable);

    /// <summary>
    /// Zapis liczb w bieżącym języku. Program działa z niezmienną globalizacją, więc kultura
    /// zawsze stawiałaby kropkę dziesiętną, także w polskim interfejsie.
    /// </summary>
    public NumberFormatInfo Numbers { get; private set; }

    /// <summary>Missing keys are returned in square brackets so a gap is obvious, never blank.</summary>
    public string this[string key] =>
        _active.TryGetValue(key, out var text) ? text
        : _fallback.TryGetValue(key, out var backup) ? backup
        : $"[{key}]";

    /// <summary>
    /// Switches language. <c>null</c> or <c>"auto"</c> follows the operating system, falling
    /// back to Polish when the system language is not among those translated.
    /// </summary>
    public void SetLanguage(string? code)
    {
        var resolved = Resolve(code);
        if (resolved == CurrentCode) return;

        _active = resolved == FallbackCode ? _fallback : Load(resolved);
        CurrentCode = resolved;
        Numbers = BuildNumbers();

        // Pusta nazwa własności odświeża wszystkie wiązania, w tym indeksator.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private static string Resolve(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) && !code.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return Codes.Contains(code, StringComparer.OrdinalIgnoreCase) ? code.ToLowerInvariant() : FallbackCode;

        // Nie CultureInfo: przy InvariantGlobalization jest zawsze kulturą niezmienną, więc to
        // dopasowanie nie mogło się udać dla żadnego języka. Szczegóły w SystemLanguage.
        // Pierwszy z preferowanych języków systemu, który ma tłumaczenie.
        foreach (var system in SystemLanguage.Candidates())
            if (Codes.Contains(system, StringComparer.OrdinalIgnoreCase)) return system;

        return FallbackCode;
    }

    private NumberFormatInfo BuildNumbers()
    {
        var format = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        var separator = this["DecimalSeparator"];
        if (separator.Length == 1) format.NumberDecimalSeparator = separator;
        return format;
    }

    private static Dictionary<string, string> Load(string code)
    {
        try
        {
            var uri = new Uri($"avares://Cewka/Localisation/Languages/{code}.json");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);

            return JsonSerializer.Deserialize(reader.ReadToEnd(), LanguageJsonContext.Default.DictionaryStringString)
                   ?? [];
        }
        catch (Exception ex)
        {
            // Brak pliku języka nie może uniemożliwić uruchomienia aplikacji.
            Console.Error.WriteLine($"[cewka] nie udało się wczytać języka '{code}': {ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<LanguageInfo> BuildAvailable()
    {
        var list = new List<LanguageInfo>();

        foreach (var code in Codes)
        {
            var strings = Load(code);
            var name = strings.TryGetValue("_language", out var value) ? value : code.ToUpperInvariant();
            list.Add(new LanguageInfo(code, name));
        }

        return list;
    }
}
