using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Radio;
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

/// <summary>Pozycja listy ekranów sceny; <see cref="Key"/> równy <c>null</c> to ekran okna.</summary>
public sealed class ScreenOption(string label, string? key) : ObservableObject
{
    public string Label { get; } = label;

    public string? Key => key;

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
/// Stan okna ustawień. Każda zmiana działa i zapisuje się od razu, bez „Zastosuj” i „Anuluj”.
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly MainViewModel _player;
    private readonly SceneSettingsHost _scene;
    private readonly PropertyChangedEventHandler _languageWatcher;

    private string _associationStatus = string.Empty;

    /// <param name="scene">Ekrany i polecenia sceny z okna głównego; bez niego ustawienia sceny dotyczą nieznanego ekranu.</param>
    public SettingsViewModel(SettingsStore settings, MainViewModel player, SceneSettingsHost? scene = null)
    {
        _settings = settings;
        _player = player;
        _scene = scene ?? new SceneSettingsHost();

        Appearance = new SettingsSection("SectionAppearance");

        // Język osobno, zaraz po wyglądzie: lista ma kilkanaście pozycji i będzie rosła, a przy
        // takiej długości zajmowała w zakładce wyglądu więcej miejsca niż wszystkie ustawienia
        // wyglądu razem. Nazwa własności nie brzmi „Language", bo tak nazywa się już pasek
        // wyboru języka w tej samej klasie.
        LanguageSection = new SettingsSection("SectionLanguage");
        Integration = new SettingsSection("SectionSystem");

        Audio = new SettingsSection("SectionAudioOutput");
        Processing = new SettingsSection("SectionEqualiser");
        Playback = new SettingsSection("SectionPlayback");

        SceneFullScreen = new SettingsSection("SectionFullScreen");
        SceneVisuals = new SettingsSection("SectionSceneVisuals");
        SceneTelevision = new SettingsSection("SectionSceneTv");

        RadioSection = new SettingsSection("SectionRadioStations");
        RadioLogos = new SettingsSection("SectionRadioLogos");

        About = new SettingsSection("SectionAboutInfo");
        Licences = new SettingsSection("SectionLicences");

        // Dotychczasowe zakładki są podkategoriami kategorii głównych, jak w ustawieniach macOS i Windows.
        Categories =
        [
            new SettingsCategory("SectionGeneral", Appearance, LanguageSection, Integration),
            new SettingsCategory("SectionAudio", Audio, Processing, Playback),
            new SettingsCategory("SectionScene", SceneFullScreen, SceneVisuals, SceneTelevision),
            new SettingsCategory("SectionRadio", RadioSection, RadioLogos),
            new SettingsCategory("SectionAbout", About, Licences),
        ];
        Sections = [.. Categories.SelectMany(category => category.Sections)];
        Appearance.IsSelected = true;
        Search = new SettingsSearch(Sections) { Entries = ModelSearchEntries };

        Theme = new SegmentGroup(
            value => ApplyTheme((ThemeMode)value),
            ("ThemeSystem", ThemeMode.System),
            ("ThemeLight", ThemeMode.Light),
            ("ThemeDark", ThemeMode.Dark));

        // Nazwy języków zapisane są dosłownie — „Polski" brzmi tak samo niezależnie od tego,
        // w jakim języku jest reszta okna. Tłumaczy się tylko pozycja „zgodnie z systemem".
        var languages = new List<SegmentOption> { new("LanguageAuto", "auto") };
        languages.AddRange(Strings.Available
            .Select(language => new SegmentOption(language.Name, language.Code, literal: true)
            {
                Icon = LanguageFlags.For(language.Code),
            }));

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

        QueueRevealChoice = new SegmentGroup(
            value =>
            {
                _player.QueueReveal = (QueueReveal)value;
                Raise(nameof(QueueHoverOptionsEnabled));
            },
            ("QueueRevealHover", QueueReveal.Hover),
            ("QueueRevealClick", QueueReveal.Click));

        // Czasy całkowite są tylko liczbą z jednostką, jak krok przewijania; pół sekundy ma przecinek
        // dziesiętny języka, więc idzie ze słownika.
        QueueHideDelayChoice = new SegmentGroup(
            value => _player.QueueHideDelay = (QueueHideDelay)value,
            [
                new SegmentOption("QueueHideHalf", QueueHideDelay.HalfSecond),
                new SegmentOption("1 s", QueueHideDelay.OneSecond, literal: true),
                new SegmentOption("2 s", QueueHideDelay.TwoSeconds, literal: true),
                new SegmentOption("4 s", QueueHideDelay.FourSeconds, literal: true),
                new SegmentOption("QueueHideNever", QueueHideDelay.Never),
            ]);

        QueueRevealDelayChoice = new SegmentGroup(
            value => _player.QueueRevealDelay = (QueueRevealDelay)value,
            ("QueueDelayShort", QueueRevealDelay.Short),
            ("QueueDelayNormal", QueueRevealDelay.Normal),
            ("QueueDelayLong", QueueRevealDelay.Long));

        // Lista z CoilCover.Fixed, żeby nowa para nie ominęła ustawień. Klucz językowy to nazwa
        // pozycji wyliczenia; test sprawdza, że jest w każdym pliku języka.
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

        // Siedem wizualizacji nie mieści się w jednym pasku, więc są dwa paski jednego wyboru.
        void ChooseVisual(object value)
        {
            _player.SceneVisual = (SceneVisual)value;
            SceneVisualChoice!.Mark(value);
            SceneVisualReactiveChoice!.Mark(value);
        }

        SceneVisualChoice = new SegmentGroup(
            ChooseVisual,
            ("SceneVisualDisc", SceneVisual.Disc),
            ("SceneVisualCover", SceneVisual.Cover),
            ("SceneVisualNight", SceneVisual.Night),
            ("SceneVisualHorizon", SceneVisual.Horizon));

        // Zorza ma kartę tylko przy wykrytym GPU; bez niego karta jest ukryta (RefreshAuroraOption).
        SceneVisualReactiveChoice = new SegmentGroup(
            ChooseVisual,
            [
                new SegmentOption("SceneVisualSpectrum", SceneVisual.Spectrum),
                new SegmentOption("SceneVisualWaveform", SceneVisual.Waveform),
            ]);
        RefreshAuroraOption();

        SpectrumShapeChoice = new SegmentGroup(
            value => _player.SceneSpectrumShape = (SpectrumShape)value,
            ("SpectrumShapeBars", SpectrumShape.Bars),
            ("SpectrumShapeCurve", SpectrumShape.Curve));

        WaveformKindChoice = new SegmentGroup(
            value => _player.SceneWaveformKind = (WaveformKind)value,
            ("WaveKindLine", WaveformKind.Line),
            ("WaveKindStereo", WaveformKind.Stereo));

        DiscRingChoice = new SegmentGroup(
            value => _player.SceneDiscRing = (bool)value,
            ("DiscRingOff", false),
            ("DiscRingOn", true));

        SceneTextSizeChoice = new SegmentGroup(
            value => _player.SceneTextSize = (SceneTextSize)value,
            ("SceneTextSizeNormal", SceneTextSize.Normal),
            ("SceneTextSizeLarge", SceneTextSize.Large),
            ("SceneTextSizeExtraLarge", SceneTextSize.ExtraLarge));

        SceneTextPositionChoice = new SegmentGroup(
            value => _player.SceneTextPosition = (SceneTextPosition)value,
            ("SceneTextTop", SceneTextPosition.Top),
            ("SceneTextMiddle", SceneTextPosition.Middle),
            ("SceneTextBottom", SceneTextPosition.Bottom));

        SceneTextAlignChoice = new SegmentGroup(
            value => _player.SceneTextCentred = (bool)value,
            ("SceneTextAlignLeft", false),
            ("SceneTextAlignCentre", true));

        SceneTextInsetChoice = new SegmentGroup(
            value => _player.SceneTextInset = (SceneTextInset)value,
            ("SceneTextInsetNormal", SceneTextInset.Normal),
            ("SceneTextInsetWide", SceneTextInset.Wide));

        SceneArtSizeChoice = new SegmentGroup(
            value => _player.SceneArtSize = (SceneArtSize)value,
            ("SceneArtSmall", SceneArtSize.Small),
            ("SceneArtNormal", SceneArtSize.Normal),
            ("SceneArtLarge", SceneArtSize.Large));

        DiscSpinChoice = new SegmentGroup(
            value => _player.DiscSpin = (DiscSpin)value,
            ("DiscSpinSlow", DiscSpin.Slow),
            ("DiscSpinNormal", DiscSpin.Normal),
            ("DiscSpinFast", DiscSpin.Fast),
            ("DiscSpinStill", DiscSpin.Still));

        BackdropMotionChoice = new SegmentGroup(
            value => _player.BackdropMotion = (BackdropMotion)value,
            ("MotionSlow", BackdropMotion.Slow),
            ("MotionNormal", BackdropMotion.Normal));

        SpectrumResponseChoice = new SegmentGroup(
            value => _player.SpectrumResponse = (SpectrumResponse)value,
            ("SpectrumSmooth", SpectrumResponse.Smooth),
            ("SpectrumNormal", SpectrumResponse.Normal),
            ("SpectrumFast", SpectrumResponse.Fast));

        // Płyta jest w cyklu zawsze, więc nie ma przełącznika.
        SceneCycleOptions =
        [
            .. new[] { SceneVisual.Cover, SceneVisual.Night, SceneVisual.Spectrum, SceneVisual.Waveform, SceneVisual.Horizon, SceneVisual.Aurora }
                .Select(visual => new SceneCycleOption(visual, _player)),
        ];

        SceneEffectOptions =
        [
            new SceneEffectOption("Blobs", () => _player.SceneBlobsOn, on => _player.SceneBlobsOn = on,
                () => _player.SceneBlobsStrength, value => _player.SceneBlobsStrength = value),
            new SceneEffectOption("Glow", () => _player.SceneGlowOn, on => _player.SceneGlowOn = on,
                () => _player.SceneGlowStrength, value => _player.SceneGlowStrength = value),
            new SceneEffectOption("Tint", () => _player.SceneTintOn, on => _player.SceneTintOn = on,
                () => _player.SceneTintStrength, value => _player.SceneTintStrength = value),
            new SceneEffectOption("Particles", () => _player.SceneParticlesOn, on => _player.SceneParticlesOn = on,
                () => _player.SceneParticlesStrength, value => _player.SceneParticlesStrength = value),
        ];

        SceneMarginChoice = new SegmentGroup(
            value => _player.SetSceneMargin(_scene.ScreenKey(), (SceneMargin)value),
            ("SceneMarginNone", SceneMargin.None),
            ("SceneMarginSmall", SceneMargin.Small),
            ("SceneMarginLarge", SceneMargin.Large));

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
            ("−23 LUFS", LoudnessTarget.Broadcast),
            ("−18 LUFS", LoudnessTarget.Reference),
            ("−14 LUFS", LoudnessTarget.Streaming));

        // Etykiety kroku przewijania są liczbami z jednostką, jednakową we wszystkich językach,
        // więc nie przechodzą przez tłumaczenie.
        SeekStep = new SegmentGroup(
            value => _player.SeekStep = (int)value,
            AudioPreferences.SeekSteps.Select(step => new SegmentOption($"{step} s", step, literal: true)));

        RadioRecentLimitChoice = new SegmentGroup(
            value => _player.Radio.RecentLimit = (int)value,
            RecentLimits.Options.Select(limit => new SegmentOption(
                limit.ToString(System.Globalization.CultureInfo.InvariantCulture), limit, literal: true)));

        Devices = new ObservableCollection<DeviceOption>();
        RefreshDevices();
        RefreshScreens();
        MarkCurrentValues();
        RefreshAssociationStatus();

        // Pozycja „zgodnie z systemem" i etykiety segmentów muszą zmienić język razem z resztą.
        _languageWatcher = (_, _) => OnLanguageChanged();
        Strings.Current.PropertyChanged += _languageWatcher;

        // Próbki barw zależą od motywu, a motyw można przełączyć w tym samym oknie, o dwa
        // ustawienia wyżej. Bez tego próbki zostawałyby w barwach poprzedniego motywu.
        _themeWatcher = (_, _) => RefreshPalettePreviews();
        App.Theme.Changed += _themeWatcher;

        // Motyw przełącza też przycisk w oknie głównym. Znacznik bierze tryb z menedżera motywu,
        // bo zgłoszenie przychodzi, zanim okno zapisze nowy tryb w ustawieniach.
        _modeWatcher = (_, _) => Theme.Mark(App.Theme.Mode);
        App.Theme.ModeChanged += _modeWatcher;

        // Okno ustawień jest niemodalne, a normalizację i limiter przełącza też nagłówek
        // korektora w oknie głównym. Jedno źródło prawdy: tutaj tylko odbicie stanu odtwarzacza.
        _playerWatcher = (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.NormalisationEnabled):
                case nameof(MainViewModel.LimiterEnabled):
                case nameof(MainViewModel.LatencyDescription):
                    Raise(e.PropertyName);
                    break;
                case nameof(MainViewModel.SceneGpuAvailable):
                    RefreshAuroraOption();
                    break;
                case nameof(MainViewModel.IsOutputOpen):
                case nameof(MainViewModel.ActiveDeviceName):
                    Raise(nameof(ActiveDeviceName));
                    break;
            }
        };
        _player.PropertyChanged += _playerWatcher;
    }

    private readonly EventHandler _themeWatcher;
    private readonly EventHandler _modeWatcher;
    private readonly PropertyChangedEventHandler _playerWatcher;

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

    /// <summary>Model odtwarzacza: korektor i efekty w Ustawieniach to ten sam panel co w oknie korektora.</summary>
    public MainViewModel Player => _player;

    /// <summary>Wyszukiwanie w ustawieniach: pole nad paskiem bocznym i lista wyników.</summary>
    public SettingsSearch Search { get; }

    /// <summary>
    /// Hasła z list, które okno tworzy dopiero po pokazaniu strony (języki, urządzenia, efekty i inne),
    /// więc nie da się ich odczytać z niewidocznej strony; okno dokłada do nich teksty stron.
    /// </summary>
    internal IEnumerable<SettingsSearchEntry> ModelSearchEntries()
    {
        // Celem jest sama pozycja listy: okno odnajduje jej kontrolkę dopiero po pokazaniu strony.
        foreach (var option in Language.Options) yield return new(LanguageSection, option.Label, string.Empty, option);
        foreach (var palette in Palettes) yield return new(Appearance, palette.Label, Strings.Current["SettingPlaceholder"], palette);
        foreach (var device in Devices) yield return new(Audio, device.Label, Strings.Current["SettingOutputDevice"], device);
        foreach (var effect in _player.SoundEffects) yield return new(Processing, effect.Label, effect.Description, effect);
        foreach (var option in SceneCycleOptions) yield return new(SceneVisuals, option.Label, Strings.Current["SceneCycleHeading"], option);
        foreach (var option in SceneEffectOptions) yield return new(SceneVisuals, option.Label, option.Hint, option);
        foreach (var screen in ScreenOptions) yield return new(SceneTelevision, screen.Label, Strings.Current["SettingSceneScreen"], screen);
        foreach (var row in Shortcuts) yield return new(About, row.Action, row.Keys, row);
    }

    /// <summary>Kategorie główne w kolejności paska bocznego.</summary>
    public IReadOnlyList<SettingsCategory> Categories { get; }

    /// <summary>Wszystkie podkategorie po kolei, bez podziału na kategorie.</summary>
    public IReadOnlyList<SettingsSection> Sections { get; }

    public SettingsSection Appearance { get; }
    public SettingsSection LanguageSection { get; }
    public SettingsSection Integration { get; }

    /// <summary>Wyjście dźwięku: urządzenie, opóźnienie i jakość przeliczania.</summary>
    public SettingsSection Audio { get; }

    /// <summary>Korektor i efekty: przetwarzanie, korektor i efekty dźwiękowe.</summary>
    public SettingsSection Processing { get; }

    public SettingsSection Playback { get; }
    public SettingsSection SceneFullScreen { get; }
    public SettingsSection SceneVisuals { get; }
    public SettingsSection SceneTelevision { get; }
    public SettingsSection RadioSection { get; }
    public SettingsSection RadioLogos { get; }
    public SettingsSection About { get; }
    public SettingsSection Licences { get; }

    /// <summary>
    /// Przechodzi do wskazanej zakładki. Wybór nie jest zapamiętywany między uruchomieniami.
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

    /// <summary>Wysuwanie nieprzypiętego panelu kolejki: po najechaniu albo po kliknięciu uchwytu.</summary>
    public SegmentGroup QueueRevealChoice { get; }

    /// <summary>Czas chowania wysuniętego panelu po zjechaniu kursorem.</summary>
    public SegmentGroup QueueHideDelayChoice { get; }

    /// <summary>Zwłoka przed wysunięciem panelu.</summary>
    public SegmentGroup QueueRevealDelayChoice { get; }

    /// <summary>Czasy wysuwania i chowania mają sens tylko przy wysuwaniu po najechaniu.</summary>
    public bool QueueHoverOptionsEnabled => _settings.Current.QueueReveal == QueueReveal.Hover;

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

    // ---------- Pełny ekran ----------

    /// <summary>Wizualizacja sceny. Nazwa różni się od typu, żeby rzutowanie nie było niejednoznaczne.</summary>
    public SegmentGroup SceneVisualChoice { get; }

    public SegmentGroup SceneVisualReactiveChoice { get; }

    private readonly SegmentOption _auroraOption = new("SceneVisualAurora", SceneVisual.Aurora);

    /// <summary>Dokłada kartę Zorzy, gdy GPU jest wykryty, a bez niego ją ukrywa. Zapisany wybór zostaje.</summary>
    private void RefreshAuroraOption()
    {
        var options = SceneVisualReactiveChoice.Options;
        var shown = options.Contains(_auroraOption);
        if (_player.SceneGpuAvailable == shown) return;

        if (shown) options.Remove(_auroraOption);
        else
        {
            _auroraOption.IsSelected = _player.SceneVisual == SceneVisual.Aurora;
            options.Add(_auroraOption);
        }
    }

    public SegmentGroup SpectrumShapeChoice { get; }

    public SegmentGroup WaveformKindChoice { get; }

    public SegmentGroup DiscRingChoice { get; }

    public SegmentGroup SceneTextSizeChoice { get; }

    public SegmentGroup SceneTextPositionChoice { get; }

    public SegmentGroup SceneTextAlignChoice { get; }

    public SegmentGroup SceneTextInsetChoice { get; }

    public SegmentGroup SceneArtSizeChoice { get; }

    public SegmentGroup DiscSpinChoice { get; }

    public SegmentGroup BackdropMotionChoice { get; }

    public SegmentGroup SpectrumResponseChoice { get; }

    /// <summary>Długość listy ostatnio słuchanych stacji.</summary>
    public SegmentGroup RadioRecentLimitChoice { get; }

    /// <summary>Pauza plików wycisza płynnie, a wznowienie podgłaśnia.</summary>
    public bool SoftPause
    {
        get => _player.SoftPause;
        set { _player.SoftPause = value; Raise(); }
    }

    /// <summary>Start w trybie radia od razu włącza ostatnią stację.</summary>
    public bool RadioPlayAtStart
    {
        get => _player.RadioPlayAtStart;
        set { _player.RadioPlayAtStart = value; Raise(); }
    }

    /// <summary>Wizualizacje w cyklu klawisza V, każda z przełącznikiem (bez Płyty).</summary>
    public IReadOnlyList<SceneCycleOption> SceneCycleOptions { get; }

    /// <summary>Efekty tła Płyty i Okładki w barwach okładki.</summary>
    public IReadOnlyList<SceneEffectOption> SceneEffectOptions { get; }

    /// <summary>Ekran sceny: ekran okna i ekrany podłączone, z nazwą i rozdzielczością.</summary>
    public ObservableCollection<ScreenOption> ScreenOptions { get; } = [];

    /// <summary>Wybiera ekran sceny; margines poniżej dotyczy od tej chwili wybranego ekranu.</summary>
    public void ChooseScreen(ScreenOption option)
    {
        if (option.IsSelected) return;

        foreach (var other in ScreenOptions) other.IsSelected = ReferenceEquals(other, option);

        _player.SceneScreen = option.Key;
        SceneMarginChoice.Mark(_player.GetSceneMargin(_scene.ScreenKey()));
        RefreshScreens();
    }

    /// <summary>Czyta listę ekranów od nowa; monitor mógł zostać podłączony w międzyczasie.</summary>
    public void RefreshScreens()
    {
        ScreenOptions.Clear();
        ScreenOptions.Add(new ScreenOption(Strings.Current["SceneScreenWindow"], null));

        var screens = _scene.Screens();
        for (var i = 0; i < screens.Count; i++)
        {
            var screen = screens[i];
            var name = screen.Name.Length == 0 ? string.Format(Strings.Current["SceneScreenUnnamed"], i + 1) : screen.Name;
            ScreenOptions.Add(new ScreenOption($"{name} ({screen.Resolution})", screen.Key));
        }

        // Zapisany, a niepodłączony ekran zostaje na liście z dopiskiem, żeby wybór nie przeskoczył po cichu.
        var chosen = _settings.Current.SceneScreen;
        if (chosen is not null && ScreenOptions.All(option => option.Key != chosen))
            ScreenOptions.Add(new ScreenOption($"{SceneScreens.Describe(chosen)} — {Strings.Current["DeviceMissing"]}", chosen));

        var selected = ScreenOptions.FirstOrDefault(option => option.Key == chosen) ?? ScreenOptions[0];
        foreach (var option in ScreenOptions) option.IsSelected = ReferenceEquals(option, selected);
    }

    /// <summary>Margines ekranu, na którym stanie scena.</summary>
    public SegmentGroup SceneMarginChoice { get; }

    /// <summary>Zamyka Ustawienia i otwiera scenę z ramką do dopasowania krawędzi.</summary>
    public void FitEdges() => _scene.FitEdges();

    public bool SceneShowNext
    {
        get => _player.SceneShowNext;
        set { _player.SceneShowNext = value; Raise(); }
    }

    public bool KeepScreenAwake
    {
        get => _player.KeepScreenAwake;
        set { _player.KeepScreenAwake = value; Raise(); }
    }

    public bool SceneOledCare
    {
        get => _player.SceneOledCare;
        set { _player.SceneOledCare = value; Raise(); }
    }

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

    // ---------- Radio ----------

    public bool RadioEnabled
    {
        get => _settings.Current.RadioEnabled;
        set
        {
            if (_settings.Current.RadioEnabled == value) return;

            _player.SetRadioEnabled(value);
            Raise();
            Raise(nameof(RadioOptionsEnabled));
        }
    }

    /// <summary>Pozostałe ustawienia radia nie mają sensu, gdy radio jest wyłączone.</summary>
    public bool RadioOptionsEnabled => _settings.Current.RadioEnabled;

    public bool RadioLogosEnabled
    {
        get => _settings.Current.RadioLogos;
        set
        {
            if (_settings.Current.RadioLogos == value) return;

            _settings.Current.RadioLogos = value;
            _settings.Touch();
            _player.Radio.ApplyLogoPolicy();
            Raise();
        }
    }

    public string RadioLogoCacheText => string.Format(Strings.Current["RadioLogoCacheSize"], FormatSize(_player.Radio.LogoCacheBytes));

    public void ClearRadioLogos()
    {
        _player.Radio.ClearLogoCache();
        Raise(nameof(RadioLogoCacheText));
    }

    public void ClearRadioRecent()
    {
        _player.Radio.ClearRecent();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

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
            FileAssociations.Register(
                AudioFileFormatDetector.SupportedExtensions,
                new AssociationTexts(Strings.Current["AssociationFileType"], Strings.Current["AssociationDescription"]));
        }
        catch (Exception ex)
        {
            ShowAssociationFailure(ex);
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
            ShowAssociationFailure(ex);
            return;
        }

        RefreshAssociationStatus();
    }

    /// <summary>Przyczyna zwykłymi słowami; tekst wyjątku idzie do dziennika.</summary>
    private void ShowAssociationFailure(Exception ex)
    {
        Console.Error.WriteLine($"[cewka] skojarzenia plików: {ex}");

        AssociationStatus = Strings.Current[ex switch
        {
            AssociationException { Reason: AssociationFailure.ExecutableUnknown } => "AssociationsFailedExecutable",
            AssociationException { Reason: AssociationFailure.RegistryWrite } => "AssociationsFailedRegistry",
            _ => "AssociationsFailed",
        }];
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

    /// <summary>Uwaga o tłumaczeniu maszynowym dotyczy każdego języka poza polskim, w którym powstał interfejs.</summary>
    public bool ShowTranslationNote => Strings.Current.CurrentCode != Strings.FallbackCode;

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
    /// <para>Zapisywana jest tylko data sprawdzenia. Powód niepowodzenia trafia do dziennika,
    /// a w oknie staje jedno zdanie.</para>
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

            if (!result.Succeeded)
            {
                Console.Error.WriteLine($"[cewka] sprawdzanie wersji: {result.Failure}");
                UpdateStatus = Strings.Current["UpdateFailed"];
                return;
            }

            _settings.Current.LastUpdateCheck = DateTimeOffset.UtcNow;
            _settings.Touch();

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
        ? $"{SystemCodecs.Name}: {Strings.Current["CodecsAvailable"]}"
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
        QueueRevealChoice.RefreshLabels();
        QueueHideDelayChoice.RefreshLabels();
        QueueRevealDelayChoice.RefreshLabels();
        Quality.RefreshLabels();
        Latency.RefreshLabels();
        Loudness.RefreshLabels();
        FileOpen.RefreshLabels();
        SceneTextSizeChoice.RefreshLabels();
        SceneTextPositionChoice.RefreshLabels();
        SceneTextAlignChoice.RefreshLabels();
        SceneTextInsetChoice.RefreshLabels();
        SceneArtSizeChoice.RefreshLabels();
        DiscSpinChoice.RefreshLabels();
        BackdropMotionChoice.RefreshLabels();
        SpectrumResponseChoice.RefreshLabels();
        foreach (var option in SceneCycleOptions) option.Refresh();
        foreach (var option in SceneEffectOptions) option.Refresh();
        SceneMarginChoice.RefreshLabels();

        foreach (var category in Categories) category.RefreshLabel();
        Search.RefreshLabels();
        foreach (var palette in Palettes) palette.RefreshLabel();

        RefreshAssociationStatus();
        RefreshDevices();
        RefreshScreens();

        Raise(nameof(VersionText));
        Raise(nameof(DecodersText));
        Raise(nameof(SystemCodecsText));
        Raise(nameof(LicenceText));
        Raise(nameof(ShowTranslationNote));
        Raise(nameof(LatencyDescription));
        Raise(nameof(ActiveDeviceName));
        Raise(nameof(Shortcuts));
    }

    /// <summary>
    /// Skróty klawiszowe okna głównego dla zakładki „O programie”. Nazwy klawiszy pochodzą ze
    /// słownika, tak samo jak w menu kolejki.
    /// </summary>
    public IReadOnlyList<ShortcutRow> Shortcuts
    {
        get
        {
            var s = Strings.Current;
            var ctrl = KeyNames.Ctrl;
            var alt = KeyNames.Alt;

            return
            [
                new(KeyNames.Space, s["ShortcutPlayPause"]),
                new("← →", s["ShortcutSeek"]),
                new($"{KeyNames.Chord(ctrl, "←")} {KeyNames.Chord(ctrl, "→")}", s["ShortcutTrack"]),
                new("↑ ↓", s["TooltipVolume"]),
                new("M", s["ShortcutMute"]),
                new("S", s["ShortcutShuffle"]),
                new("R", s["ShortcutRepeat"]),
                new("Q", s["ShortcutPanel"]),
                new("L", s["Queue"]),
                new("T", s["SettingTheme"]),
                new("F11", s["ShortcutFullScreen"]),
                new($"V {KeyNames.Chord(KeyNames.Shift, "V")}", s["ShortcutSceneVisual"]),
                new(KeyNames.Escape, s["ShortcutEscape"]),
                new("← →", s["ShortcutEdgeFitMargin"]),
                new($"{KeyNames.Enter} {KeyNames.Escape}", s["ShortcutEdgeFitEnd"]),
                new(KeyNames.Chord(ctrl, "O"), s["OpenFilesTitle"]),
                new(KeyNames.Chord(ctrl, KeyNames.Shift, "O"), s["OpenFolderTitle"]),
                new(KeyNames.Chord(ctrl, ","), s["Settings"]),
                new(KeyNames.Enter, s["ShortcutQueuePlay"]),
                new(KeyNames.Delete, s["ShortcutQueueRemove"]),
                new($"{KeyNames.Chord(alt, "↑")} {KeyNames.Chord(alt, "↓")}", s["ShortcutQueueMove"]),
                new(KeyNames.Chord(ctrl, "Z"), s["ShortcutUndo"]),
            ];
        }
    }

    private void MarkCurrentValues()
    {
        Theme.Mark(_settings.Current.Theme);
        Language.Mark(_settings.Current.Language);
        WindowControls.Mark(_settings.Current.WindowControls);
        Effects.Mark(_settings.Current.Effects);
        Colours.Mark(_settings.Current.ColourIntensity);
        QueueRevealChoice.Mark(_settings.Current.QueueReveal);
        QueueHideDelayChoice.Mark(_settings.Current.QueueHideDelay);
        QueueRevealDelayChoice.Mark(_settings.Current.QueueRevealDelay);
        Quality.Mark(_settings.Current.ResamplerQuality);
        Latency.Mark(_settings.Current.OutputLatency);
        Loudness.Mark(_settings.Current.LoudnessTarget);
        SeekStep.Mark(_settings.Current.SeekStep);
        FileOpen.Mark(_settings.Current.FileOpenAction);
        SceneVisualChoice.Mark(_settings.Current.SceneVisual);
        SceneVisualReactiveChoice.Mark(_settings.Current.SceneVisual);
        SpectrumShapeChoice.Mark(_settings.Current.SpectrumShape);
        WaveformKindChoice.Mark(_settings.Current.WaveformKind);
        DiscRingChoice.Mark(_settings.Current.DiscRing);
        SceneTextSizeChoice.Mark(_settings.Current.SceneTextSize);
        SceneTextPositionChoice.Mark(_settings.Current.SceneTextPosition);
        SceneTextAlignChoice.Mark(_settings.Current.SceneTextCentred);
        SceneTextInsetChoice.Mark(_settings.Current.SceneTextInset);
        SceneArtSizeChoice.Mark(_settings.Current.SceneArtSize);
        DiscSpinChoice.Mark(_settings.Current.DiscSpin);
        BackdropMotionChoice.Mark(_settings.Current.BackdropMotion);
        SpectrumResponseChoice.Mark(_settings.Current.SpectrumResponse);
        RadioRecentLimitChoice.Mark(_settings.Current.RadioRecentLimit);
        SceneMarginChoice.Mark(_player.GetSceneMargin(_scene.ScreenKey()));

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
        App.Theme.ModeChanged -= _modeWatcher;
        _player.PropertyChanged -= _playerWatcher;
    }
}

/// <summary>Jeden wiersz listy skrótów: klawisze i to, co robią.</summary>
public sealed record ShortcutRow(string Keys, string Action);
