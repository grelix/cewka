using Cewka.App.Services;
using Cewka.Platform;

namespace Cewka.App.ViewModels;

/// <summary>Adres projektu składnika. Tylko adres http albo https wolno otworzyć w przeglądarce.</summary>
public sealed record ProjectLink(string Address)
{
    public bool IsSafe => WebLink.IsSafe(Address);

    public bool IsNotSafe => !IsSafe;
}

/// <summary>Wiersz listy składników i jego szczegóły.</summary>
public sealed class LicenceItem(NoticeComponent component)
{
    public NoticeComponent Component => component;

    public string Name => component.Name;

    /// <summary>Skrócona nazwa licencji, np. „MIT”. Pusta, gdy sekcja not jej nie podaje.</summary>
    public string Licence => component.Licence;

    public bool HasLicence => component.Licence.Length > 0;

    /// <summary>Nazwa dla czytnika ekranu: „Nazwa, licencja”. Obie części są nazwami własnymi, bez tłumaczenia.</summary>
    public string AccessibleName => HasLicence ? $"{Name}, {Licence}" : Name;

    public IReadOnlyList<string> Packages => component.Packages;

    public bool HasPackages => component.Packages.Count > 0;

    public IReadOnlyList<ProjectLink> Projects { get; } =
        component.Projects.Select(address => new ProjectLink(address)).ToList();

    public bool HasProjects => component.Projects.Count > 0;

    public string Copyright => component.Copyright;

    public bool HasCopyright => component.Copyright.Length > 0;

    public string LicenceText => component.LicenceText;
}

/// <summary>
/// Model widoku „Licencje składników”: lista składników i, po wybraniu jednego, jego szczegóły
/// w tym samym miejscu. Nie zna przycisków ani klawiszy; widok woła <see cref="Open"/>
/// i <see cref="Back"/>.
/// </summary>
public sealed class LicencesViewModel : ObservableObject
{
    private LicenceItem? _current;

    /// <summary>Składniki z not dołączonych do programu.</summary>
    public LicencesViewModel() : this(ThirdPartyNotices.Load())
    {
    }

    public LicencesViewModel(IReadOnlyList<NoticeComponent> components)
    {
        Items = components.Select(component => new LicenceItem(component)).ToList();
    }

    public IReadOnlyList<LicenceItem> Items { get; }

    /// <summary>Brak składników: plik not nie dał się wczytać.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>Składnik, którego szczegóły są pokazane; <c>null</c>, gdy widoczna jest lista.</summary>
    public LicenceItem? Current => _current;

    /// <summary>Ostatnio otwarty składnik, także po powrocie do listy. Na jego wiersz wraca fokus.</summary>
    public LicenceItem? LastOpened { get; private set; }

    public bool IsDetailVisible => _current is not null;

    public bool IsListVisible => _current is null && Items.Count > 0;

    public void Open(LicenceItem item)
    {
        if (!Items.Contains(item)) return;

        LastOpened = item;
        SetCurrent(item);
    }

    /// <summary>Wraca z szczegółów do listy.</summary>
    public void Back() => SetCurrent(null);

    private void SetCurrent(LicenceItem? item)
    {
        if (ReferenceEquals(_current, item)) return;

        _current = item;
        Raise(nameof(Current));
        Raise(nameof(IsDetailVisible));
        Raise(nameof(IsListVisible));
    }
}
