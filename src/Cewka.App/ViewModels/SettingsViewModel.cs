using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.Audio.Decoding;
using Cewka.Audio.Devices;
using Cewka.Platform;

namespace Cewka.App.ViewModels;

/// <summary>
/// One entry in the output device list. <see cref="Name"/> is <c>null</c> for the entry that
/// follows whatever the system considers default.
/// </summary>
public sealed class DeviceOption(string label, string? name) : ObservableObject
{
    public string Label => label;

    public string? Name => name;

    private bool _isSelected;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// Jedna para barw domyślnej okładki, razem z próbką pokazującą przejście.
/// </summary>
public sealed class PaletteOption(string labelKey, PlaceholderPalette value) : ObservableObject
{
    private bool _isSelected;
    private IBrush _preview = Brushes.Transparent;

    public string LabelKey => labelKey;

    public string Label => Strings.Current[labelKey];

    public PlaceholderPalette Value => value;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>Przejście barw takie, jakie wyjdzie na okładce w obecnym motywie.</summary>
    public IBrush Preview { get => _preview; private set => Set(ref _preview, value); }

    public void RefreshPreview(bool darkTheme)
    {
        var colours = CoilCover.RampColours(value, darkTheme);

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };

        // Barwy rozłożone równomiernie, bez zakładania, ile ich jest: para ma trzy punkty,
        // a pozycja „losowo" — po jednym z każdej pary.
        for (var i = 0; i < colours.Length; i++)
        {
            var offset = colours.Length == 1 ? 0 : (double)i / (colours.Length - 1);
            brush.GradientStops.Add(new GradientStop(colours[i], offset));
        }

        Preview = brush;
    }

    public void RefreshLabel() => Raise(nameof(Label));
}

/// <summary>
/// State behind the settings window.
///
/// <para><b>Zmiany działają od razu.</b> Okno nie ma przycisków „Zastosuj" ani „Anuluj": każda
/// zmiana jest widoczna lub słyszalna natychmiast i od razu zapisywana. Przy ustawieniach, które
/// ocenia się zmysłami — motyw, urządzenie wyjściowe, siła efektów — porównanie „przed
/// i po" jest jedynym sensownym sposobem wyboru, a okno z przyciskiem zatwierdzenia właśnie to
/// uniemożliwia.</para>
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly MainViewModel _player;
    private readonly PropertyChangedEventHandler _languageWatcher;

    private string _associationStatus = string.Empty;

    public SettingsViewModel(SettingsStore settings, MainViewModel player)
    {
        _settings = settings;
        _player = player;

        Appearance = new SettingsSection("SectionAppearance");

        // Język osobno, zaraz po wyglądzie: lista ma trzynaście pozycji i będzie rosła, a przy
        // takiej długości zajmowała w zakładce wyglądu więcej miejsca niż wszystkie ustawienia
        // wyglądu razem. Nazwa własności nie brzmi „Language", bo tak nazywa się już pasek
        // wyboru języka w tej samej klasie.
        LanguageSection = new SettingsSection("SectionLanguage");

        Audio = new SettingsSection("SectionAudio");

        // Efekty osobno od dźwięku: w zakładce dźwięku stoją ustawienia urządzenia i wierności
        // odtwarzania, a tu — rzeczy, które celowo zmieniają brzmienie. Mieszanie jednego
        // z drugim kazałoby szukać wierności wśród upiększeń.
        // Nazwa zakładki bierze się z tego samego klucza, co nagłówek pasa efektów w oknie
        // głównym. Osobny klucz oznaczałby siedemnaście identycznych napisów do utrzymania.
        EffectsSection = new SettingsSection("Effects");

        Playback = new SettingsSection("SectionPlayback");
        Integration = new SettingsSection("SectionSystem");
        About = new SettingsSection("SectionAbout");

        Sections = [Appearance, LanguageSection, Audio, EffectsSection, Playback, Integration, About];
        Appearance.IsSelected = true;

        Theme = new SegmentGroup(
            value => ApplyTheme((ThemeMode)value),
            ("ThemeSystem", ThemeMode.System),
            ("ThemeLight", ThemeMode.Light),
            ("ThemeDark", ThemeMode.Dark));

        // Nazwy języków zapisane są dosłownie — „Polski" brzmi tak samo niezależnie od tego,
        // w jakim języku jest reszta okna. Tłumaczy się tylko pozycja „zgodnie z systemem".
        var languages = new List<SegmentOption> { new("LanguageAuto", "auto") };
        languages.AddRange(Strings.Available
            .Select(language => new SegmentOption(language.Name, language.Code, literal: true)));

        Language = new SegmentGroup(value => ApplyLanguage((string)value), languages);

        WindowControls = new SegmentGroup(
            value => ApplyWindowControls((WindowControlsPosition)value),
            ("ControlsRight", WindowControlsPosition.Right),
            ("ControlsLeft", WindowControlsPosition.Left),
            ("ControlsMacOs", WindowControlsPosition.MacOs));

        Effects = new SegmentGroup(
            value => _player.SetEffects((EffectsMode)value),
            ("EffectsAuto", EffectsMode.Auto),
            ("EffectsFull", EffectsMode.Full),
            ("EffectsReduced", EffectsMode.Reduced));

        Colours = new SegmentGroup(
            value => _player.SetColourIntensity((ColourIntensity)value),
            ("ColoursSubtle", ColourIntensity.Subtle),
            ("ColoursRecommended", ColourIntensity.Recommended),
            ("ColoursIntense", ColourIntensity.Intense));

        // Lista wyprowadzona wprost z CoilCover.Fixed, a nie wypisana drugi raz. Kiedy była
        // wypisana, dołożenie sześciu par w 0.8.0 zmieniło rysowanie okładek, ale do tej listy
        // nie dotarło — i nowe pary po prostu nie pojawiły się w ustawieniach. Klucz językowy
        // składa się z nazwy pozycji wyliczenia; pilnuje tego test, który sprawdza, czy każdy
        // taki klucz istnieje w każdym pliku językowym.
        Palettes =
        [
            ..CoilCover.Fixed.Select(value => new PaletteOption("Palette" + value, value)),
            new PaletteOption("PaletteRandom", PlaceholderPalette.Random),
        ];

        RefreshPalettePreviews();

        FileOpen = new SegmentGroup(
            value => ApplyFileOpenAction((FileOpenAction)value),
            ("FileOpenAppend", FileOpenAction.Append),
            ("FileOpenAppendPlay", FileOpenAction.AppendAndPlay),
            ("FileOpenReplace", FileOpenAction.ReplaceAndPlay));

        Quality = new SegmentGroup(
            value => _player.SetResamplerQuality((ResamplerQuality)value),
            ("QualityOff", ResamplerQuality.Off),
            ("QualityStandard", ResamplerQuality.Standard),
            ("QualityHigh", ResamplerQuality.High));

        Latency = new SegmentGroup(
            value =>
            {
                _player.SetOutputLatency((OutputLatency)value);
                Raise(nameof(LatencyDescription));
            },
            ("LatencyLow", OutputLatency.Low),
            ("LatencyBalanced", OutputLatency.Balanced),
            ("LatencySafe", OutputLatency.Safe));

        // Etykiety to poziomy w LUFS — zapis jednakowy w każdym języku, więc nie przechodzą
        // przez tłumaczenie; ich znaczenie wyjaśnia podpowiedź pod paskiem.
        //
        // Nazwa własności celowo krótsza niż nazwa typu: „LoudnessTarget” byłoby jednocześnie
        // nazwą wyliczenia i nazwą własności, a rzutowanie stałoby się niejednoznaczne.
        Loudness = new SegmentGroup(
            value => _player.SetLoudnessTarget((LoudnessTarget)value),
            literalLabels: true,
            ("-23 LUFS", LoudnessTarget.Broadcast),
            ("-18 LUFS", LoudnessTarget.Reference),
            ("-14 LUFS", LoudnessTarget.Streaming));

        // Etykiety kroku przewijania są liczbami z jednostką, jednakową we wszystkich językach,
        // więc nie przechodzą przez tłumaczenie.
        SeekStep = new SegmentGroup(
            value => _player.SeekStep = (int)value,
            AudioPreferences.SeekSteps.Select(step => new SegmentOption($"{step} s", step, literal: true)));

        Devices = new ObservableCollection<DeviceOption>();
        RefreshDevices();
        MarkCurrentValues();
        RefreshAssociationStatus();

        // Pozycja „zgodnie z systemem" i etykiety segmentów muszą zmienić język razem z resztą.
        _languageWatcher = (_, _) => OnLanguageChanged();
        Strings.Current.PropertyChanged += _languageWatcher;

        // Próbki barw zależą od motywu, a motyw można przełączyć w tym samym oknie, o dwa
        // ustawienia wyżej. Bez tego próbki zostawałyby w barwach poprzedniego motywu.
        _themeWatcher = (_, _) => RefreshPalettePreviews();
        App.Theme.Changed += _themeWatcher;
    }

    private readonly EventHandler _themeWatcher;

    private void RefreshPalettePreviews()
    {
        foreach (var option in Palettes) option.RefreshPreview(App.Theme.IsDark);
    }

    private void ApplyFileOpenAction(FileOpenAction action)
    {
        if (_settings.Current.FileOpenAction == action) return;

        _settings.Current.FileOpenAction = action;
        _settings.Touch();
    }

    // ---------- Zakładki ----------

    public IReadOnlyList<SettingsSection> Sections { get; }

    public SettingsSection Appearance { get; }
    public SettingsSection LanguageSection { get; }
    public SettingsSection Audio { get; }
    public SettingsSection EffectsSection { get; }
    public SettingsSection Playback { get; }

    /// <summary>
    /// Te same obiekty, którymi steruje okno główne. Dzięki temu suwak przesunięty w jednym
    /// miejscu widać od razu w drugim, bez żadnego uzgadniania między oknami.
    /// </summary>
    public IReadOnlyList<EffectViewModel> SoundEffects => _player.SoundEffects;
    public SettingsSection Integration { get; }
    public SettingsSection About { get; }

    /// <summary>
    /// Przechodzi do wskazanej zakładki. Wybór nie jest zapamiętywany między uruchomieniami:
    /// zapamiętywanie kosztowałoby kolejne pole w ustawieniach, a rozwiązywałoby problem,
    /// którego nie ma — okno otwiera się po to, żeby czegoś poszukać, a nie żeby wrócić.
    /// </summary>
    public void SelectSection(SettingsSection section)
    {
        if (section.IsSelected) return;

        foreach (var candidate in Sections) candidate.IsSelected = ReferenceEquals(candidate, section);
    }

    // ---------- Wygląd ----------

    public SegmentGroup Theme { get; }

    public SegmentGroup Language { get; }

    public SegmentGroup WindowControls { get; }

    public SegmentGroup Effects { get; }

    public SegmentGroup Colours { get; }

    public IReadOnlyList<PaletteOption> Palettes { get; }

    /// <summary>Whether the codec, bit depth, bitrate and rate are shown beside the record.</summary>
    public bool ShowFormatBadge
    {
        get => _settings.Current.ShowFormatBadge;
        set { _player.SetShowFormatBadge(value); Raise(); }
    }

    private void ApplyTheme(ThemeMode mode)
    {
        App.Theme.SetMode(mode);
        _settings.Current.Theme = mode;
        _settings.Touch();
    }

    private void ApplyLanguage(string code)
    {
        _player.SetLanguage(code);
        RefreshAssociationStatus();
    }

    /// <summary>Where the window buttons sit; the settings window follows it as well.</summary>
    public WindowControlsPosition CurrentWindowControls => _settings.Current.WindowControls;

    private void ApplyWindowControls(WindowControlsPosition position)
    {
        _player.SetWindowControls(position);
        Raise(nameof(CurrentWindowControls));
    }

    // ---------- Dźwięk ----------

    public ObservableCollection<DeviceOption> Devices { get; }

    /// <summary>
    /// Device actually feeding the speakers. Worth showing separately from the list: choosing
    /// „system default" says nothing about which box the sound is coming out of.
    /// </summary>
    public string ActiveDeviceName => _player.IsOutputOpen
        ? $"{Strings.Current["DeviceActive"]} {_player.ActiveDeviceName}"
        : Strings.Current["DeviceIdle"];

    public void ChooseDevice(DeviceOption device)
    {
        if (device.IsSelected) return;

        foreach (var option in Devices) option.IsSelected = ReferenceEquals(option, device);

        _player.SetOutputDevice(device.Name);
        Raise(nameof(ActiveDeviceName));

        // Inne urządzenie to inny sterownik, a więc i inny przyjęty rozmiar okresu.
        Raise(nameof(LatencyDescription));
    }

    /// <summary>Re-reads the device list; a headset may have been plugged in meanwhile.</summary>
    public void RefreshDevices()
    {
        Devices.Clear();
        Devices.Add(new DeviceOption(Strings.Current["DeviceSystemDefault"], null));

        try
        {
            foreach (var device in AudioDeviceList.Enumerate())
                Devices.Add(new DeviceOption(device.Name, device.Name));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] odczyt listy urządzeń: {ex.Message}");
        }

        var chosen = _settings.Current.OutputDevice;

        // Urządzenie zapamiętane, ale nieobecne, zostaje na liście z dopiskiem — inaczej wybór
        // po cichu przeskoczyłby na domyślne i nie dałoby się tego zauważyć.
        if (chosen is not null && Devices.All(device => device.Name != chosen))
            Devices.Add(new DeviceOption($"{chosen} — {Strings.Current["DeviceMissing"]}", chosen));

        var selected = Devices.FirstOrDefault(device => device.Name == chosen) ?? Devices[0];
        foreach (var option in Devices) option.IsSelected = ReferenceEquals(option, selected);

        Raise(nameof(ActiveDeviceName));
    }

    public bool NormalisationEnabled
    {
        get => _player.NormalisationEnabled;
        set { _player.NormalisationEnabled = value; Raise(); }
    }

    public bool LimiterEnabled
    {
        get => _player.LimiterEnabled;
        set { _player.LimiterEnabled = value; Raise(); }
    }

    public SegmentGroup Quality { get; }

    public SegmentGroup Latency { get; }

    /// <summary>Rozmiar okresu przyjęty przez urządzenie — odczytany, nie zadeklarowany.</summary>
    public string LatencyDescription => _player.LatencyDescription;

    public SegmentGroup Loudness { get; }

    public bool AlwaysAnalyse
    {
        get => _player.AlwaysAnalyse;
        set { _player.AlwaysAnalyse = value; Raise(); }
    }

    // ---------- Odtwarzanie ----------

    public SegmentGroup SeekStep { get; }

    public SegmentGroup FileOpen { get; }

    public bool RestoreSessionEnabled
    {
        get => _player.RestoreSessionEnabled;
        set { _player.RestoreSessionEnabled = value; Raise(); }
    }

    // ---------- System ----------

    public bool MediaKeysEnabled
    {
        get => _settings.Current.MediaKeys;
        set
        {
            if (_settings.Current.MediaKeys == value) return;

            _settings.Current.MediaKeys = value;
            _settings.Touch();
            App.ApplyMediaKeysSetting();
            Raise();
        }
    }

    public bool MediaKeysSupported => MediaKeys.IsSupported;

    /// <summary>
    /// State of the desktop's media panel, which the user cannot otherwise verify.
    /// Windows dostarcza go przez nakładkę systemową, Linux przez MPRIS na szynie sesji.
    /// </summary>
    public string MediaPanelText => OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
        ? Strings.Current[App.MediaPanelActive ? "MediaPanelActive" : "MediaPanelInactive"]
        : Strings.Current["MediaPanelUnsupported"];

    public bool SingleInstanceEnabled
    {
        get => _settings.Current.SingleInstance;
        set
        {
            if (_settings.Current.SingleInstance == value) return;

            _settings.Current.SingleInstance = value;
            _settings.Touch();
            Raise();
        }
    }

    public bool AssociationsSupported => FileAssociations.IsSupported;

    public string AssociationStatus { get => _associationStatus; private set => Set(ref _associationStatus, value); }

    public bool CanRegisterAssociations => FileAssociations.IsSupported &&
                                           FileAssociations.Query() != AssociationState.Current;

    public bool CanRemoveAssociations => FileAssociations.IsSupported &&
                                         FileAssociations.Query() != AssociationState.Absent;

    public void RegisterAssociations()
    {
        try
        {
            FileAssociations.Register(AudioFileFormatDetector.SupportedExtensions);
        }
        catch (Exception ex)
        {
            AssociationStatus = $"{Strings.Current["AssociationsFailed"]} {ex.Message}";
            return;
        }

        RefreshAssociationStatus();
    }

    public void RemoveAssociations()
    {
        try
        {
            FileAssociations.Unregister(AudioFileFormatDetector.SupportedExtensions);
        }
        catch (Exception ex)
        {
            AssociationStatus = $"{Strings.Current["AssociationsFailed"]} {ex.Message}";
            return;
        }

        RefreshAssociationStatus();
    }

    private void RefreshAssociationStatus()
    {
        AssociationStatus = FileAssociations.Query() switch
        {
            AssociationState.Current => Strings.Current["AssociationsCurrent"],
            AssociationState.Stale => Strings.Current["AssociationsStale"],
            AssociationState.Absent => Strings.Current["AssociationsAbsent"],
            _ => Strings.Current["AssociationsUnsupported"],
        };

        Raise(nameof(CanRegisterAssociations));
        Raise(nameof(CanRemoveAssociations));
    }

    // ---------- Informacje ----------

    public string VersionText => $"{Strings.Current["AppName"]} {ReadVersion()}";

    /// <summary>
    /// Author of the program. Odczytywany z metadanych zestawu, a nie wpisany tutaj — nazwisko
    /// ma jedno źródło, wspólne z właściwościami pliku wykonywalnego.
    /// </summary>
    public string AuthorText => Read<AssemblyCompanyAttribute>(a => a.Company) ?? "—";

    public string CopyrightText => Read<AssemblyCopyrightAttribute>(a => a.Copyright) ?? string.Empty;

    // ---------- Repozytorium i sprawdzanie wersji ----------

    /// <summary>Adres repozytorium, odczytany raz z metadanych zestawu.</summary>
    public string RepositoryUrl => UpdateCheck.Repository;

    /// <summary>Adres pokazywany w oknie — bez schematu, bo ten niczego czytelnikowi nie mówi.</summary>
    public string RepositoryLabel => RepositoryUrl
        .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase);

    public string ReleasesUrl => UpdateCheck.ReleasesUrl(RepositoryUrl) ?? string.Empty;

    /// <summary>
    /// Strona zgłoszeń. Odsyłacz do niej stoi przy wyborze języka, bo tłumaczenia powstały
    /// maszynowo i to jedyne miejsce, w którym prośba o poprawkę ma naturalne umiejscowienie.
    /// </summary>
    public string IssuesUrl => WebLink.IsSafe(RepositoryUrl)
        ? RepositoryUrl.TrimEnd('/') + "/issues"
        : string.Empty;

    public bool HasRepository => WebLink.IsSafe(RepositoryUrl);

    /// <summary>Sprawdzanie ma sens tylko wtedy, gdy z adresu da się zbudować adres usługi.</summary>
    public bool CanCheckForUpdates => UpdateCheck.ApiUrl(RepositoryUrl) is not null;

    private string _updateStatus = string.Empty;
    private bool _checking;

    /// <summary>Wynik ostatniego sprawdzenia, przeznaczony do pokazania w oknie.</summary>
    public string UpdateStatus
    {
        get => _updateStatus;
        private set
        {
            if (!Set(ref _updateStatus, value)) return;
            Raise(nameof(HasUpdateStatus));
        }
    }

    public bool HasUpdateStatus => _updateStatus.Length > 0;

    /// <summary>Blokuje przycisk na czas jednego żądania, żeby nie dało się go zwielokrotnić.</summary>
    public bool NotChecking => !_checking;

    /// <summary>
    /// Czy program pyta o nowsze wydanie sam, przy uruchomieniu. Wyłączenie nie odbiera
    /// przycisku sprawdzenia na żądanie.
    /// </summary>
    public bool CheckForUpdatesEnabled
    {
        get => _settings.Current.CheckForUpdates;
        set
        {
            if (_settings.Current.CheckForUpdates == value) return;

            _settings.Current.CheckForUpdates = value;
            _settings.Touch();
            Raise();
        }
    }

    /// <summary>
    /// Sprawdza dostępność nowszego wydania i opisuje wynik słowami.
    ///
    /// <para>Wynik nie jest nigdzie zapisywany: zapisana zostaje wyłącznie data sprawdzenia,
    /// żeby sprawdzanie automatyczne nie powtarzało żądania częściej niż raz na dobę. Powód
    /// niepowodzenia trafia do dziennika, a w oknie staje jedno zdanie — użytkownikowi
    /// niepotrzebny jest numer błędu HTTP.</para>
    /// </summary>
    public async Task CheckForUpdatesNowAsync()
    {
        if (_checking || !CanCheckForUpdates) return;

        _checking = true;
        Raise(nameof(NotChecking));
        UpdateStatus = Strings.Current["UpdateChecking"];

        try
        {
            var result = await UpdateCheck.LatestAsync(RepositoryUrl, UpdateCheck.Current);

            _settings.Current.LastUpdateCheck = DateTimeOffset.UtcNow;
            _settings.Touch();

            if (!result.Succeeded)
            {
                Console.Error.WriteLine($"[cewka] sprawdzanie wersji: {result.Failure}");
                UpdateStatus = Strings.Current["UpdateFailed"];
                return;
            }

            var latest = result.Latest!;

            UpdateStatus = latest > UpdateCheck.Current
                ? string.Format(Strings.Current["UpdateAvailable"], latest.ToString(3))
                : Strings.Current["UpdateCurrent"];
        }
        finally
        {
            _checking = false;
            Raise(nameof(NotChecking));
        }
    }

    private static string? Read<T>(Func<T, string> select) where T : Attribute
    {
        var attribute = typeof(SettingsViewModel).Assembly.GetCustomAttribute<T>();
        var value = attribute is null ? null : select(attribute);

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public string AudioEngineText => $"miniaudio {AudioDeviceList.NativeVersion}";

    /// <summary>Which decoder handles what, so an unplayable file has a traceable explanation.</summary>
    public string DecodersText => Strings.Current["DecodersSummary"];

    public string SystemCodecsText => SystemCodecs.IsAvailable
        ? $"{SystemCodecs.Name} — {Strings.Current["CodecsAvailable"]}"
        : SystemCodecs.UnavailableReason ?? Strings.Current["CodecsMissing"];

    public bool SystemCodecsAvailable => SystemCodecs.IsAvailable;

    /// <summary>
    /// Licence notice preceded by the copyright line. Nota o prawach autorskich stoi tutaj,
    /// a nie przy nazwisku autora, bo tam powtarzałaby je słowo w słowo.
    /// </summary>
    public string LicenceText => CopyrightText.Length > 0
        ? $"{CopyrightText}. {Strings.Current["LicenceSummary"]}"
        : Strings.Current["LicenceSummary"];

    private static string ReadVersion()
    {
        var informational = typeof(SettingsViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
            return typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "—";

        // Kompilacja dokleja identyfikator zmiany po znaku plus; w oknie jest zbędny.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }

    private void OnLanguageChanged()
    {
        Theme.RefreshLabels();
        Language.RefreshLabels();
        WindowControls.RefreshLabels();
        Effects.RefreshLabels();
        Colours.RefreshLabels();
        Quality.RefreshLabels();
        Latency.RefreshLabels();
        Loudness.RefreshLabels();
        FileOpen.RefreshLabels();

        foreach (var section in Sections) section.RefreshLabel();
        foreach (var palette in Palettes) palette.RefreshLabel();

        RefreshAssociationStatus();

        Raise(nameof(VersionText));
        Raise(nameof(DecodersText));
        Raise(nameof(SystemCodecsText));
        Raise(nameof(LicenceText));
        Raise(nameof(LatencyDescription));
        Raise(nameof(ActiveDeviceName));
    }

    private void MarkCurrentValues()
    {
        Theme.Mark(_settings.Current.Theme);
        Language.Mark(_settings.Current.Language);
        WindowControls.Mark(_settings.Current.WindowControls);
        Effects.Mark(_settings.Current.Effects);
        Colours.Mark(_settings.Current.ColourIntensity);
        Quality.Mark(_settings.Current.ResamplerQuality);
        Latency.Mark(_settings.Current.OutputLatency);
        Loudness.Mark(_settings.Current.LoudnessTarget);
        SeekStep.Mark(_settings.Current.SeekStep);
        FileOpen.Mark(_settings.Current.FileOpenAction);

        foreach (var palette in Palettes)
            palette.IsSelected = palette.Value == _settings.Current.PlaceholderPalette;
    }

    /// <summary>Applies a colour pair for the default cover and moves the mark onto it.</summary>
    public void SelectPalette(PaletteOption option)
    {
        _player.SetPlaceholderPalette(option.Value);

        foreach (var palette in Palettes) palette.IsSelected = ReferenceEquals(palette, option);
    }

    public void Dispose()
    {
        Strings.Current.PropertyChanged -= _languageWatcher;
        App.Theme.Changed -= _themeWatcher;
    }
}
