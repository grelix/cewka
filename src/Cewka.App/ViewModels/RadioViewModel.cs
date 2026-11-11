using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.Radio;
using Cewka.Audio;
using Cewka.Audio.Playback;
using Cewka.Audio.Streaming;

namespace Cewka.App.ViewModels;

/// <summary>Pozycja listy wyboru kraju albo kategorii; <see cref="Key"/> null oznacza „wszystkie”.</summary>
public sealed record RadioChoice(string? Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Wiersz listy stacji: nazwa z krajem, kategoria i format, serce ulubionych, znacznik bieżącej.</summary>
public sealed class StationRowViewModel : ObservableObject
{
    private bool _isFavourite;
    private bool _isCurrent;

    private readonly Action<StationRowViewModel>? _requestLogo;
    private Avalonia.Media.Imaging.Bitmap? _logo;

    public StationRowViewModel(
        RadioStation station, string name, string detail, bool isFavourite, bool isCurrent,
        Action<StationRowViewModel>? requestLogo = null)
    {
        _requestLogo = requestLogo;
        Station = station;
        Name = name;
        Detail = detail;
        _isFavourite = isFavourite;
        _isCurrent = isCurrent;
    }

    public RadioStation Station { get; }

    /// <summary>Nazwa stacji z krajem w nawiasie (decyzja 11.3).</summary>
    public string Name { get; }

    public string Detail { get; }

    /// <summary>Logo stacji (64 px); null do czasu pobrania albo gdy go nie ma, wtedy stoi wspólna ikona radia.</summary>
    public Avalonia.Media.Imaging.Bitmap? Logo
    {
        get => _logo;
        set
        {
            if (!Set(ref _logo, value)) return;
            Raise(nameof(HasLogo));
            Raise(nameof(ShowsPlaceholder));
        }
    }

    public bool HasLogo => _logo is not null;

    public bool ShowsPlaceholder => _logo is null;

    /// <summary>Wiersz stał się widoczny: logo ma być pobrane, gdy tylko pobieranie jest dozwolone.</summary>
    public bool WantsLogo { get; private set; }

    internal bool LogoStarted { get; set; }

    public void RequestLogo()
    {
        WantsLogo = true;
        _requestLogo?.Invoke(this);
    }

    public bool IsFavourite
    {
        get => _isFavourite;
        set
        {
            if (!Set(ref _isFavourite, value)) return;
            Raise(nameof(FavouriteTooltip));
            Raise(nameof(AccessibleName));
            Raise(nameof(HeartOpacity));
        }
    }

    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    public string FavouriteTooltip => Strings.Current[IsFavourite ? "TooltipFavouriteRemove" : "TooltipFavouriteAdd"];

    /// <summary>Serce niewypełnione bywa przygaszone, wypełnione w pełnej barwie.</summary>
    public double HeartOpacity => IsFavourite ? 1 : 0.55;

    /// <summary>Nazwa dla czytnika ekranu: nazwa, kategoria, ulubiona.</summary>
    public string AccessibleName =>
        IsFavourite ? $"{Name}, {Detail}, {Strings.Current["RadioFavouriteState"]}" : $"{Name}, {Detail}";
}

/// <summary>
/// Tryb radia okna głównego: lista stacji (kraj, kategoria, wyszukiwanie, ulubione, ostatnio słuchane), stacja
/// bieżąca i to, co o niej wiadomo („teraz gra”, stan połączenia, błąd). Z silnikiem rozmawia przez
/// <see cref="PlaybackEngine.PlayLive"/> i jego zdarzenia; zdarzenia spóźnione, które dotyczą już innej stacji niż
/// <see cref="PlaybackEngine.CurrentLive"/>, są odrzucane.
/// </summary>
public sealed class RadioViewModel : ObservableObject, IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly SettingsStore _settings;
    private readonly string _stateFile;
    private readonly Func<RadioCatalog> _catalogFactory;
    private readonly Func<Action, bool> _tryPlayback;
    private readonly Action<string> _notify;
    private readonly Func<LiveCodec, bool> _isPlayable;
    private readonly RadioLogoService? _logos;

    internal RadioLogoService? Logos => _logos;
    private RadioLogo? _stageLogo;
    private RadioStation? _stageLogoStation;
    private int _stageGeneration;

    private RadioCatalog? _catalog;
    private RadioState _state = new();
    private List<RadioStation> _context = [];
    private (int Index, RadioStation Station)? _undo;

    private string _searchText = string.Empty;
    private string _view = RadioListContext.ViewCountry;
    private RadioChoice? _country;
    private RadioChoice? _category;
    private StationRowViewModel? _selected;
    private RadioStation? _station;
    private string _trackTitle = string.Empty;
    private string _trackArtist = string.Empty;
    private LiveState? _liveState;
    private string _errorText = string.Empty;
    private bool _canRetry;
    private string _badge = string.Empty;
    private bool _adding;

    public RadioViewModel(
        PlaybackEngine engine,
        SettingsStore settings,
        string stateFile,
        Func<Action, bool> tryPlayback,
        Action<string> notify,
        Func<RadioCatalog>? catalogFactory = null,
        Func<LiveCodec, bool>? isPlayable = null,
        RadioLogoService? logos = null)
    {
        _logos = logos;
        _engine = engine;
        _settings = settings;
        _stateFile = stateFile;
        _tryPlayback = tryPlayback;
        _notify = notify;
        _catalogFactory = catalogFactory ?? RadioCatalog.LoadBuiltIn;
        _isPlayable = isPlayable ?? LiveSupport.IsPlayable;

        ViewGroup = new SegmentGroup(
            value => SetView((string)value),
            (Key: "RadioViewCountry", Value: RadioListContext.ViewCountry),
            (Key: "RadioViewFavourites", Value: RadioListContext.ViewFavourites),
            (Key: "RadioViewRecent", Value: RadioListContext.ViewRecent));
        ViewGroup.Mark(_view);

        _engine.LiveStateChanged += OnEngineState;
        _engine.LiveTitleChanged += OnEngineTitle;
        _engine.LiveFailed += OnEngineFailed;
        _engine.LiveInfoChanged += OnEngineInfo;
    }

    public ObservableCollection<StationRowViewModel> Rows { get; } = [];

    public SegmentGroup ViewGroup { get; }

    public IReadOnlyList<RadioChoice> Countries { get; private set; } = [];

    public IReadOnlyList<RadioChoice> Categories { get; private set; } = [];

    /// <summary>Region systemu (kod kraju) do wyboru kraju startowego; testy podstawiają własny.</summary>
    internal static Func<string?> RegionProvider { get; set; } = SystemRegion;

    private static string? SystemRegion()
    {
        try
        {
            var region = System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName;
            return region.Length == 2 && char.IsLetter(region[0]) ? region : null;
        }
        catch (Exception e) when (e is ArgumentException or System.Globalization.CultureNotFoundException)
        {
            return null;
        }
    }

    public bool IsLoaded => _catalog is not null;

    public RadioState State => _state;

    /// <summary>Stacja wybrana: gra, czeka na „Odtwórz” albo skończyła się błędem.</summary>
    public RadioStation? CurrentStation => _station;

    public bool HasStation => _station is not null;

    // ---------- Ładowanie ----------

    /// <summary>Wczytuje listę wbudowaną i stan słuchacza przy pierwszym wejściu w tryb radia.</summary>
    public void Activate()
    {
        if (_catalog is null)
        {
            _catalog = _catalogFactory();
            _state = RadioStateStore.Load(_stateFile);
            _state.TrimRecent(Config.RadioRecentLimit);

            var saved = _settings.Current;
            _view = _state.Context.View is RadioListContext.ViewFavourites or RadioListContext.ViewRecent
                ? _state.Context.View
                : RadioListContext.ViewCountry;
            ViewGroup.Mark(_view);

            var code = _catalog.DefaultCountry(Strings.Current.CurrentCode, RegionProvider(), saved.RadioCountry);
            BuildChoices(code, saved.RadioCategory);

            if (_state.LastStationId is { } last)
                _station = _state.Find(last) ?? _catalog.Find(last);

            LoadStageLogo();
        }

        Rebuild();
        RaiseStation();
    }

    private void BuildChoices(string? countryCode, string? categoryKey)
    {
        var catalog = _catalog!;
        var countries = catalog.Countries(CountryName, catalog.DefaultCountry(Strings.Current.CurrentCode));
        Countries =
        [
            new RadioChoice(null, Strings.Current["RadioAllCountries"]),
            .. countries.Select(c => new RadioChoice(c.Code, $"{c.Name} ({c.Count})")),
        ];
        _country = Countries.FirstOrDefault(c => c.Key == countryCode) ?? Countries[0];

        RebuildCategoryChoices(categoryKey);
        Raise(nameof(Countries));
        Raise(nameof(SelectedCountry));
    }

    private void RebuildCategoryChoices(string? categoryKey)
    {
        var entries = _catalog!.Categories(_country?.Key);
        Categories =
        [
            new RadioChoice(null, Strings.Current["RadioAllCategories"]),
            .. entries.Select(e => new RadioChoice(e.Key, $"{CategoryName(e.Key)} ({e.Count})")),
        ];
        _category = Categories.FirstOrDefault(c => c.Key == categoryKey) ?? Categories[0];
        Raise(nameof(Categories));
        Raise(nameof(SelectedCategory));
    }

    /// <summary>Po zmianie języka nazwy krajów i kategorii, a z nimi kolejność krajów, powstają od nowa.</summary>
    public void RefreshLanguage()
    {
        if (_catalog is null) return;

        BuildChoices(_country?.Key, _category?.Key);
        Rebuild();
        RaiseStation();
    }

    internal static string CountryName(string code)
    {
        var text = Strings.Current["Country" + code];
        return text.StartsWith('[') ? code : text;
    }

    internal static string CategoryName(string key)
    {
        var text = Strings.Current["Category" + key];
        return text.StartsWith('[') ? key : text;
    }

    // ---------- Lista ----------

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value ?? string.Empty)) return;
            Rebuild();
        }
    }

    public string View => _view;

    public void SetView(string view)
    {
        if (_view == view) return;

        _view = view;
        ViewGroup.Mark(view);
        _state.Context.View = view;
        SaveState();
        Raise(nameof(View));
        Raise(nameof(ShowsFilters));
        Rebuild();
    }

    /// <summary>Kraj i kategoria dotyczą listy wbudowanej, nie ulubionych i ostatnio słuchanych.</summary>
    public bool ShowsFilters => _view == RadioListContext.ViewCountry;

    public RadioChoice? SelectedCountry
    {
        get => _country;
        set
        {
            if (value is null || value == _country) return;

            _country = value;
            _settings.Current.RadioCountry = value.Key;
            _settings.Touch();
            RebuildCategoryChoices(_category?.Key);
            Rebuild();
        }
    }

    public RadioChoice? SelectedCategory
    {
        get => _category;
        set
        {
            if (value is null || value == _category) return;

            _category = value;
            _settings.Current.RadioCategory = value.Key;
            _settings.Touch();
            Rebuild();
        }
    }

    public StationRowViewModel? SelectedRow { get => _selected; set => Set(ref _selected, value); }

    public string Summary => string.Format(Strings.Current["RadioResultCount"], Rows.Count);

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyText => Strings.Current[_view switch
    {
        RadioListContext.ViewFavourites when _searchText.Trim().Length == 0 => "RadioEmptyFavourites",
        RadioListContext.ViewRecent when _searchText.Trim().Length == 0 => "RadioEmptyRecent",
        _ => "RadioEmptyResults",
    }];

    /// <summary>Stacje bieżącego widoku w kolejności listy.</summary>
    public IReadOnlyList<RadioStation> Visible() => [.. Rows.Select(r => r.Station)];

    private void Rebuild()
    {
        if (_catalog is null) return;

        var stations = Query();
        var selectedId = _selected?.Station.Id;

        Rows.Clear();
        foreach (var station in stations)
            Rows.Add(CreateRow(station));

        SelectedRow = selectedId is null ? null : Rows.FirstOrDefault(r => r.Station.Id == selectedId);
        Raise(nameof(Summary));
        Raise(nameof(IsEmpty));
        Raise(nameof(EmptyText));
    }

    private IReadOnlyList<RadioStation> Query()
    {
        var catalog = _catalog!;
        var query = _searchText.Trim();

        switch (_view)
        {
            case RadioListContext.ViewFavourites:
                return Filter(_state.Favourites, query);
            case RadioListContext.ViewRecent:
                return Filter(_state.Recent, query);
        }

        if (query.Length == 0)
            return catalog.List(_country?.Key, _category?.Key);

        return RadioSearch.Run(catalog, new RadioSearchRequest(
            query,
            SelectedCountry: _country?.Key,
            DefaultCountry: catalog.DefaultCountry(Strings.Current.CurrentCode),
            Category: _category?.Key,
            CountryName: CountryName,
            CategoryName: CategoryName));
    }

    private IReadOnlyList<RadioStation> Filter(IEnumerable<RadioStation> source, string query)
    {
        var words = TextFolding.Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [.. source];

        return
        [
            .. source.Where(station =>
            {
                var text = TextFolding.Fold($"{station.Name} {station.City} {station.CityLatin} {CountryName(station.Country)}");
                return words.All(word => text.Contains(word, StringComparison.Ordinal));
            }),
        ];
    }

    private StationRowViewModel CreateRow(RadioStation station)
    {
        var stream = station.Streams.Count > 0
            ? station.Streams[Math.Clamp(station.Recommended, 0, station.Streams.Count - 1)]
            : null;
        var format = stream is null || stream.Codec == "unknown"
            ? string.Empty
            : stream.Kbps > 0 ? $"{stream.Codec.ToUpperInvariant()} {stream.Kbps}" : stream.Codec.ToUpperInvariant();
        var categories = string.Join(", ", station.Categories.Select(CategoryName));
        var detail = string.Join(" · ", new[] { categories, format }.Where(p => p.Length > 0));

        return new StationRowViewModel(
            station,
            _catalog!.DisplayName(station, CountryName),
            detail,
            _state.IsFavourite(station.Id),
            _station is not null && _station.Id == station.Id,
            RequestRowLogo);
    }

    private void RefreshMarks()
    {
        foreach (var row in Rows)
        {
            row.IsCurrent = _station is not null && _station.Id == row.Station.Id;
            row.IsFavourite = _state.IsFavourite(row.Station.Id);
        }
    }

    // ---------- Odtwarzanie ----------

    /// <summary>Włącza stację; false, gdy się nie da (lista blokad, brak adresu do zagrania, błąd urządzenia).</summary>
    public bool Play(RadioStation station, IReadOnlyList<RadioStation>? context = null)
    {
        if (!Config.RadioEnabled) return false;

        Activate();

        CancelPendingStep();
        _context = [.. context ?? Visible()];
        if (_context.All(s => s.Id != station.Id)) _context.Add(station);

        ResetNowPlaying();
        SetStation(station);

        if (_catalog!.IsWithdrawn(station.Id))
        {
            SetError(Strings.Current["RadioStationWithdrawn"], canRetry: false);
            return false;
        }

        if (RadioBlocklist.Current.CheckStation(station) != AddressVerdict.Allowed)
        {
            SetError(Strings.Current["RadioStationBlocked"], canRetry: false);
            return false;
        }

        var source = station.ToLiveSource(_isPlayable);
        if (source is null)
        {
            SetError(RadioFailureText.For(LiveFailureReason.CodecUnavailable), canRetry: false);
            return false;
        }

        if (!_tryPlayback(() => _engine.PlayLive(source)))
            return false;

        _liveState = LiveState.Connecting;
        AcknowledgePrivacy();
        _state.PushRecent(station, Config.RadioRecentLimit);
        SaveState();
        if (_view == RadioListContext.ViewRecent) Rebuild();
        RaiseStation();
        return true;
    }

    /// <summary>„Odtwórz” bez nowego wyboru: wznawia stację wybraną albo włącza zaznaczoną w liście.</summary>
    public bool PlaySelected()
    {
        Activate();

        if (_engine.CurrentLive is { } live && _station?.Id == live.StationId && !HasFailed)
        {
            ClearError();
            if (_tryPlayback(_engine.Play))
            {
                _liveState = LiveState.Connecting;
                RaiseStation();
                return true;
            }

            return false;
        }

        var target = _station ?? _selected?.Station;
        return target is not null && Play(target, _context.Count > 0 ? _context : null);
    }

    /// <summary>„Spróbuj ponownie” po błędzie: łączy ze stacją od nowa.</summary>
    public void Retry()
    {
        CancelPendingStep();
        if (_station is { } station) Play(station, _context);
    }

    /// <summary>Poprzednia (-1) albo następna (+1) stacja z listy, z której uruchomiono bieżącą; zawija się na końcach.</summary>
    public bool Step(int direction)
    {
        Activate();

        var list = _context.Count > 0 ? _context : [.. Visible()];
        if (list.Count == 0) return false;

        var index = _station is null ? -1 : list.FindIndex(s => s.Id == _station.Id);
        var next = index < 0
            ? (direction > 0 ? 0 : list.Count - 1)
            : (index + direction + list.Count) % list.Count;
        return Play(list[next], list);
    }

    /// <summary>Opóźnienie połączenia po Wstecz i Dalej: kilka szybkich naciśnięć łączy tylko z ostatnią stacją.</summary>
    public static readonly TimeSpan DefaultStepDelay = TimeSpan.FromMilliseconds(400);

    internal TimeSpan StepDelay { get; set; } = DefaultStepDelay;

    /// <summary>Wywołuje działanie po upływie czasu; testy podstawiają własny zegar.</summary>
    internal Func<TimeSpan, Action, IDisposable> Scheduler { get; set; } = StartTimer;

    /// <summary>Zgłaszane, gdy opóźnione połączenie zostało wykonane, żeby okno odczytało stan odtwarzania.</summary>
    public event Action? StepCompleted;

    private IDisposable? _stepTimer;
    private RadioStation? _pendingStep;

    private sealed class TimerHandle(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }

    private static IDisposable StartTimer(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new TimerHandle(timer);
    }

    public bool HasPendingStep => _pendingStep is not null;

    /// <summary>Porzuca krok Wstecz i Dalej, który czeka na połączenie: zmiana źródła, wyłączenie radia i Stop.</summary>
    public void CancelPendingStep()
    {
        _stepTimer?.Dispose();
        _stepTimer = null;
        _pendingStep = null;
    }

    /// <summary>
    /// Wstecz i Dalej: stacja zmienia się na ekranie od razu, a połączenie następuje dopiero po krótkiej
    /// pauzie (0,4 s) od ostatniego naciśnięcia, więc przewijanie listy nie otwiera połączeń z każdą mijaną stacją.
    /// </summary>
    public bool StepDelayed(int direction)
    {
        if (StepDelay <= TimeSpan.Zero) return Step(direction);

        Activate();

        var list = _context.Count > 0 ? _context : [.. Visible()];
        if (list.Count == 0) return false;

        var from = _pendingStep ?? _station;
        var index = from is null ? -1 : list.FindIndex(s => s.Id == from.Id);
        var next = list[index < 0
            ? (direction > 0 ? 0 : list.Count - 1)
            : (index + direction + list.Count) % list.Count];

        _stepTimer?.Dispose();
        _pendingStep = next;
        _context = [.. list];
        ResetNowPlaying();
        SetStation(next);

        _stepTimer = Scheduler(StepDelay, () =>
        {
            var target = _pendingStep;
            _stepTimer = null;
            _pendingStep = null;
            if (target is not null && Config.RadioEnabled && Play(target, _context)) StepCompleted?.Invoke();
        });
        return true;
    }

    private void SetStation(RadioStation station)
    {
        _station = station;
        _state.LastStationId = station.Id;
        RefreshMarks();
        LoadStageLogo();
        RaiseStation();
    }

    private void ResetNowPlaying()
    {
        _trackTitle = string.Empty;
        _trackArtist = string.Empty;
        _liveState = null;
        _badge = string.Empty;
        ClearError();
    }

    // ---------- Zdarzenia silnika ----------

    private void OnEngineState(LiveSource source, LiveState state) => Dispatcher.UIThread.Post(() => OnLiveState(source, state));

    private void OnEngineTitle(LiveSource source, string? title) => Dispatcher.UIThread.Post(() => OnLiveTitle(source, title));

    private void OnEngineFailed(LiveSource source, LiveFailure failure) => Dispatcher.UIThread.Post(() => OnLiveFailed(source, failure));

    private void OnEngineInfo(LiveSource source, LiveStreamInfo info) => Dispatcher.UIThread.Post(() => OnLiveInfo(source, info));

    /// <summary>Zdarzenie jest ważne tylko dla stacji, którą silnik ma teraz; spóźnione zdarzenia poprzedniej nie psują stanu nowej.</summary>
    private bool IsCurrentSource(LiveSource source) =>
        ReferenceEquals(source, _engine.CurrentLive) && _station is not null && _station.Id == source.StationId;

    internal void OnLiveState(LiveSource source, LiveState state)
    {
        if (!IsCurrentSource(source)) return;

        _liveState = state;
        if (state is LiveState.Playing or LiveState.Connecting) ClearError();
        RaiseStation();
    }

    internal void OnLiveTitle(LiveSource source, string? raw)
    {
        if (!IsCurrentSource(source)) return;

        var parsed = ParseTitle(raw, _station!.Name);
        _trackTitle = parsed?.Title ?? string.Empty;
        _trackArtist = parsed?.Artist ?? string.Empty;
        RaiseStation();
    }

    internal void OnLiveFailed(LiveSource source, LiveFailure failure)
    {
        if (!IsCurrentSource(source)) return;

        _liveState = LiveState.Failed;
        _trackTitle = string.Empty;
        _trackArtist = string.Empty;
        SetError(RadioFailureText.For(failure.Reason), RadioFailureText.CanRetry(failure.Reason));
        RaiseStation();
        Failed?.Invoke();
    }

    /// <summary>Stacja skończyła się błędem: nic już nie gra, więc przycisk główny wraca do „Odtwórz”.</summary>
    public event Action? Failed;

    /// <summary>Ostatni stan stacji to błąd; „Odtwórz” łączy wtedy od nowa.</summary>
    public bool HasFailed => _liveState == LiveState.Failed;

    internal void OnLiveInfo(LiveSource source, LiveStreamInfo info)
    {
        if (!IsCurrentSource(source)) return;

        RefreshBadge();
    }

    /// <summary>Plakietka z pomiaru: kodek z dekodera i przepływność z danych, nie z nagłówków stacji.</summary>
    public void RefreshBadge()
    {
        var codec = _engine.LiveCodec;
        var kbps = _engine.InstantaneousBitrate;
        var badge = codec == LiveCodec.Unknown
            ? string.Empty
            : kbps > 0 ? $"{codec.ToString().ToUpperInvariant()} {kbps}" : codec.ToString().ToUpperInvariant();
        if (badge == _badge) return;

        _badge = badge;
        Raise(nameof(Badge));
    }

    public string Badge => _badge;

    /// <summary>Podział „Wykonawca – Tytuł” tylko przy dokładnie dwóch częściach; puste, równe nazwie stacji i reklamy pomijane.</summary>
    internal static (string Title, string? Artist)? ParseTitle(string? raw, string stationName)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Contains("adw_ad='true'", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(text, stationName.Trim(), StringComparison.OrdinalIgnoreCase)) return null;

        var parts = text.Split([" - ", " – ", " — "], StringSplitOptions.None);
        return parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0
            ? (parts[1].Trim(), parts[0].Trim())
            : (text, null);
    }

    // ---------- Co widać w „Teraz gra” ----------

    private bool IsBusy => _liveState is LiveState.Connecting or LiveState.Buffering or LiveState.Rebuffering or LiveState.Reconnecting;

    /// <summary>Stan połączenia słowami; pusty, gdy stacja gra albo nic się nie dzieje.</summary>
    public string StateText => _liveState switch
    {
        LiveState.Connecting => Strings.Current["RadioStateConnecting"],
        LiveState.Buffering or LiveState.Rebuffering => Strings.Current["RadioStateBuffering"],
        LiveState.Reconnecting => Strings.Current["RadioStateReconnecting"],
        _ => string.Empty,
    };

    public string StationName => _station is null ? string.Empty : _catalog?.DisplayName(_station, CountryName) ?? _station.Name;

    /// <summary>Duży wiersz: tytuł z metadanych, a bez niego nazwa stacji; bez stacji „Nie wybrano stacji”.</summary>
    public string Heading => _station is null ? Strings.Current["RadioNoStation"]
        : _trackTitle.Length > 0 ? _trackTitle
        : StationName;

    /// <summary>
    /// Drugi wiersz: przy tytule wykonawca i stacja, bez tytułu tylko stan połączenia. Grająca stacja bez
    /// tytułu ma wiersz pusty: „Na żywo” stoi już pod kreską z kropką i nie powtarza się pod nazwą.
    /// </summary>
    public string Subline
    {
        get
        {
            if (_station is null) return string.Empty;
            if (_trackTitle.Length > 0) return string.Join(" · ", new[] { _trackArtist, StationName }.Where(p => p.Length > 0));
            return IsBusy ? StateText : string.Empty;
        }
    }

    /// <summary>
    /// Co pokazuje panel multimediów systemu: tytuł z metadanych albo nazwa stacji, wykonawca z metadanych albo
    /// nazwa stacji, album to nazwa stacji; nazwa stacji ma kraj w nawiasie (decyzja 11.3). Null bez stacji.
    /// </summary>
    public (string Title, string Artist, string Album)? PanelInfo => _station is null
        ? null
        : (_trackTitle.Length > 0 ? _trackTitle : StationName,
           _trackArtist.Length > 0 ? _trackArtist : StationName,
           StationName);

    /// <summary>Stacja przysłała tytuł utworu (po filtrach); bez niego Scena pokazuje samą stację.</summary>
    public bool HasTrackTitle => _trackTitle.Length > 0;

    /// <summary>
    /// Stacje rzędu Sceny: lista, z której uruchomiono stację (albo widoczna lista), i numer bieżącej stacji
    /// albo -1.
    /// </summary>
    internal (IReadOnlyList<RadioStation> List, int Current) SceneStations()
    {
        IReadOnlyList<RadioStation> list = _context.Count > 0 ? _context : Visible();
        var current = -1;
        if (_station is not null)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i].Id == _station.Id) { current = i; break; }
        }

        return (list, current);
    }

    internal string DisplayNameOf(RadioStation station) => _catalog?.DisplayName(station, CountryName) ?? station.Name;

    /// <summary>Wstecz i Dalej mają dokąd przejść: lista, z której uruchomiono stację, ma więcej niż jedną stację.</summary>
    public bool CanStep => (_context.Count > 0 ? _context.Count : Rows.Count) > 1;

    public string ErrorText => _errorText;

    public bool HasError => _errorText.Length > 0;

    public bool CanRetry => _canRetry && HasError;

    /// <summary>Napis „Na żywo” pod kreską ma sens, gdy jest stacja i nie ma błędu.</summary>
    public bool ShowsLiveCaption => HasStation && !HasError;

    private void SetError(string text, bool canRetry)
    {
        _errorText = text;
        _canRetry = canRetry;
        Raise(nameof(ErrorText));
        Raise(nameof(HasError));
        Raise(nameof(CanRetry));
        Raise(nameof(ShowsLiveCaption));
    }

    private void ClearError()
    {
        if (_errorText.Length == 0) return;

        SetError(string.Empty, canRetry: false);
    }

    private void RaiseStation()
    {
        Raise(nameof(CurrentStation));
        Raise(nameof(HasStation));
        Raise(nameof(ShowsLiveCaption));
        Raise(nameof(StationName));
        Raise(nameof(Heading));
        Raise(nameof(Subline));
        Raise(nameof(StateText));
    }

    // ---------- Ulubione, cofanie, własny adres, import i eksport ----------

    public bool CanUndo => _undo is not null;

    public void ToggleFavourite(RadioStation station)
    {
        Activate();

        if (_state.IsFavourite(station.Id))
        {
            _undo = _state.RemoveFavourite(station.Id);
            Raise(nameof(CanUndo));
        }
        else
        {
            _state.AddFavourite(station);
        }

        SaveState();
        AfterFavouritesChanged();
    }

    /// <summary>Ctrl+Z: przywraca ostatnio usuniętą ulubioną na dawne miejsce.</summary>
    public bool Undo()
    {
        if (_undo is not { } undo) return false;

        _undo = null;
        Raise(nameof(CanUndo));
        var restored = _state.RestoreFavourite(undo.Index, undo.Station);
        SaveState();
        AfterFavouritesChanged();
        return restored;
    }

    public bool MoveFavourite(RadioStation station, int delta)
    {
        var from = _state.Favourites.FindIndex(f => f.Id == station.Id);
        if (from < 0 || !_state.MoveFavourite(from, from + delta)) return false;

        SaveState();
        Rebuild();
        return true;
    }

    private void AfterFavouritesChanged()
    {
        if (_view == RadioListContext.ViewFavourites) Rebuild();
        else RefreshMarks();
    }

    public bool IsAdding { get => _adding; set => Set(ref _adding, value); }

    /// <summary>Stacja z adresu wpisanego przez słuchacza: trafia do ulubionych i od razu gra. Odmowa podaje powód w komunikacie.</summary>
    public bool AddCustom(string name, string url)
    {
        Activate();

        var (station, verdict) = RadioState.TryCreateTyped(name, url);
        if (station is null)
        {
            _notify(Strings.Current[verdict == AddressVerdict.Blocked ? "RadioAddressBlocked" : "RadioAddressInvalid"]);
            return false;
        }

        _state.AddFavourite(station);
        SaveState();
        IsAdding = false;
        SetView(RadioListContext.ViewFavourites);
        Rebuild();
        return Play(station, Visible());
    }

    /// <summary>Dopisuje do ulubionych stacje z tekstu M3U albo PLS; komunikat podaje liczby dopisanych i pominiętych.</summary>
    public int Import(string text)
    {
        Activate();

        var import = StationPlaylist.Import(text);
        var known = _state.Favourites.SelectMany(f => f.Streams.Select(x => x.Url)).ToHashSet(StringComparer.Ordinal);
        var added = import.Stations.Count(station =>
            !station.Streams.Any(x => known.Contains(x.Url)) && _state.AddFavourite(station));
        SaveState();
        AfterFavouritesChanged();

        _notify(added == 0 && import.Rejected.Count == 0
            ? Strings.Current["RadioImportEmpty"]
            : import.Rejected.Count == 0
                ? string.Format(Strings.Current["RadioImported"], added)
                : string.Format(Strings.Current["RadioImportedSkipped"], added, import.Rejected.Count));
        return added;
    }

    /// <summary>Ulubione jako rozszerzone M3U; nazwa z krajem, jak na liście.</summary>
    public string ExportText()
    {
        Activate();
        return StationPlaylist.Export(_state.Favourites, station => _catalog!.DisplayName(station, CountryName));
    }

    public bool HasFavourites => _state.Favourites.Count > 0;

    private void SaveState() => RadioStateStore.Save(_state, _stateFile);

    public void Dispose()
    {
        CancelPendingStep();
        _engine.LiveStateChanged -= OnEngineState;
        _engine.LiveTitleChanged -= OnEngineTitle;
        _engine.LiveFailed -= OnEngineFailed;
        _engine.LiveInfoChanged -= OnEngineInfo;
        _stageGeneration++;
    }

    // ---------- Logo ----------

    /// <summary>Zgłaszane, gdy panel multimediów systemu ma dostać nowe logo bieżącej stacji.</summary>
    public event Action? StageLogoChanged;

    private AppSettings Config => _settings.Current;

    /// <summary>
    /// Logo wolno pobierać, gdy radio i pobieranie są włączone, a słuchacz poznał zasady połączeń (baner zamknięty
    /// albo stacja już uruchomiona). Do tego czasu wszędzie stoi obraz zastępczy.
    /// </summary>
    public bool LogosAllowed => _logos is not null && Config.RadioEnabled && Config.RadioLogos && Config.RadioPrivacyAcknowledged;

    public bool ShowsPrivacyBanner => Config.RadioEnabled && !Config.RadioPrivacyAcknowledged;

    /// <summary>Logo bieżącej stacji w pełnym rozmiarze (512 px); null, gdy go nie ma, i wtedy stoi ikona radia.</summary>
    public Avalonia.Media.Imaging.Bitmap? StageLogo => _stageLogo?.Stage;

    public bool HasStageLogo => _stageLogo is not null;

    public bool ShowsStagePlaceholder => _stageLogo is null;

    /// <summary>Plik logo bieżącej stacji w pamięci podręcznej, dla panelu multimediów systemu.</summary>
    public string? StageLogoPath => _stageLogo?.StagePath;

    /// <summary>Zamknięcie banera albo uruchomienie stacji: od teraz wolno pobierać logo.</summary>
    public void AcknowledgePrivacy()
    {
        if (Config.RadioPrivacyAcknowledged) return;

        Config.RadioPrivacyAcknowledged = true;
        _settings.Touch();
        ApplyLogoPolicy();
    }

    /// <summary>Po zmianie ustawień radia: logo znika albo zaczyna być pobierane.</summary>
    public void ApplyLogoPolicy()
    {
        Raise(nameof(ShowsPrivacyBanner));
        Raise(nameof(LogosAllowed));

        if (!LogosAllowed)
        {
            _logos?.CancelPending();
            foreach (var row in Rows)
            {
                row.Logo = null;
                row.LogoStarted = false;
            }

            SetStageLogo(null);
            return;
        }

        foreach (var row in Rows.Where(r => r.WantsLogo))
            RequestRowLogo(row);
        LoadStageLogo();
    }

    private void RequestRowLogo(StationRowViewModel row)
    {
        if (!LogosAllowed || row.LogoStarted || row.Station.Logos.Count == 0) return;

        row.LogoStarted = true;
        _ = LoadRowLogoAsync(row);
    }

    private async Task LoadRowLogoAsync(StationRowViewModel row)
    {
        try
        {
            var logo = await _logos!.GetAsync(row.Station).ConfigureAwait(true);
            if (logo is not null && LogosAllowed)
                row.Logo = logo.List;
        }
        catch (OperationCanceledException)
        {
            row.LogoStarted = false;
        }
    }

    private void LoadStageLogo()
    {
        var generation = ++_stageGeneration;
        if (!LogosAllowed || _station is null || _station.Logos.Count == 0)
        {
            SetStageLogo(null);
            return;
        }

        // Logo poprzedniej stacji nie zostaje na ekranie ani w palecie tła do czasu pobrania nowego.
        if (!ReferenceEquals(_stageLogoStation, _station)) SetStageLogo(null);
        _stageLogoStation = _station;

        _ = LoadStageLogoAsync(_station, generation);
    }

    private async Task LoadStageLogoAsync(RadioStation station, int generation)
    {
        RadioLogo? logo = null;
        try
        {
            logo = await _logos!.GetAsync(station).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        if (generation != _stageGeneration || !LogosAllowed) return;

        SetStageLogo(logo);
    }

    private void SetStageLogo(RadioLogo? logo)
    {
        if (ReferenceEquals(_stageLogo, logo)) return;

        _stageLogo = logo;
        Raise(nameof(StageLogo));
        Raise(nameof(HasStageLogo));
        Raise(nameof(ShowsStagePlaceholder));
        Raise(nameof(StageLogoPath));
        StageLogoChanged?.Invoke();
    }

    /// <summary>Adres strony stacji, gdy jest poprawnym adresem http albo https; inaczej null.</summary>
    public static string? WebsiteUrl(RadioStation station) =>
        Uri.TryCreate(station.Website, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;

    /// <summary>„Otwórz stronę stacji”: przekazuje adres funkcji otwierającej; false, gdy stacja nie ma strony.</summary>
    public bool OpenWebsite(RadioStation station, Action<string> open)
    {
        if (WebsiteUrl(station) is not { } url) return false;

        open(url);
        return true;
    }

    /// <summary>Czyści listę ostatnio słuchanych stacji i zapisuje stan.</summary>
    public void ClearRecent()
    {
        Activate();
        _state.ClearRecent();
        SaveState();
        Raise(nameof(HasRecent));
        if (_view == RadioListContext.ViewRecent) Rebuild();
    }

    public bool HasRecent => _state.Recent.Count > 0;

    /// <summary>
    /// Długość listy ostatnich: 5, 10 albo 20. Krótsza od wczytanej listy skraca ją od razu; przed
    /// wejściem w radio skrócenie czeka na wczytanie stanu.
    /// </summary>
    public int RecentLimit
    {
        get => Config.RadioRecentLimit;
        set
        {
            value = RecentLimits.Nearest(value);
            if (Config.RadioRecentLimit == value) return;

            Config.RadioRecentLimit = value;
            _settings.Touch();
            Raise();

            if (_catalog is null || !_state.TrimRecent(value)) return;

            SaveState();
            if (_view == RadioListContext.ViewRecent) Rebuild();
        }
    }

    /// <summary>Usuwa pobrane logo z dysku i z widoku; kolejne wyświetlenie pobierze je od nowa.</summary>
    public void ClearLogoCache()
    {
        _logos?.Clear();
        foreach (var row in Rows)
        {
            row.Logo = null;
            row.LogoStarted = false;
        }

        SetStageLogo(null);
        ApplyLogoPolicy();
    }

    public long LogoCacheBytes => _logos?.SizeBytes() ?? 0;
}
