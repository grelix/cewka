using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Cewka.App.Localisation;

/// <summary>
/// XAML shorthand for a translated string: <c>Text="{l:Translate Queue}"</c>.
/// <para>
/// It returns a binding rather than a plain string, so that switching language refreshes
/// every piece of text already on screen instead of only the parts rebuilt afterwards.
/// </para>
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
