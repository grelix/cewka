using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Cewka.App.Localisation;

/// <summary>
/// Skrót XAML dla tłumaczonego tekstu: <c>Text="{l:Translate Queue}"</c>. Zwraca wiązanie,
/// żeby zmiana języka odświeżała też teksty już widoczne na ekranie.
/// </summary>
public sealed class TranslateExtension : MarkupExtension
{
    public TranslateExtension()
    {
    }

    public TranslateExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = Strings.Current,
            Mode = BindingMode.OneWay,
        };
}
