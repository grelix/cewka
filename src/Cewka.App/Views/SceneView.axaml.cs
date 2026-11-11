using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cewka.App.Controls;
using Cewka.App.Localisation;
using Cewka.App.Models;
using Cewka.App.Services;
using Cewka.App.ViewModels;

namespace Cewka.App.Views;

/// <summary>
/// Scena: plansza „Teraz odtwarzane” na wierzchu okna. Rozmyta okładka, plamy z jej barw
/// i przyciemnienia na cały ekran, nad nimi kręcąca się płyta, tytuł, wykonawca, album i pasek
/// postępu widoczne zawsze oraz rząd przycisków i wyjście, które gasną same.
/// <para>
/// Kadr ma 1920 × 1080 jednostek i jest skalowany równomiernie o min(W/1920, H/1080), wyśrodkowany,
/// więc wszystkie wymiary w XAML są jednostkami wzoru, a tekst i wektory rysują się ostro w każdej
/// rozdzielczości. Przycisk wyjścia ma tę samą skalę, ale trzyma się narożnika ekranu.
/// </para>
/// </summary>
public partial class SceneView : UserControl
{
    /// <summary>Szerokość kadru w jednostkach wzoru.</summary>
    public const double FrameWidth = 1920;

    /// <summary>Wysokość kadru w jednostkach wzoru.</summary>
    public const double FrameHeight = 1080;

    private static readonly Cursor HiddenCursor = new(StandardCursorType.None);

    private readonly SceneChrome _chrome;
    private readonly DiscSpinner _spinner;
    private readonly DispatcherTimer _timer = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly ScaleTransform _exitScale = new(1, 1);
    private readonly Control[] _chromeParts;
    private readonly Control[] _fadingParts;
    private readonly Button[] _rowButtons;
    private readonly Control[][] _focusRows;

    /// <summary>Podpisy rzędu przycisków, z oboma stanami Odtwórz/Wstrzymaj.</summary>
    private static readonly string[] RowCaptions =
        ["SceneShuffle", "ScenePrevious", "ScenePlay", "ScenePause", "SceneStop", "SceneNext", "SceneRepeat"];

    /// <summary>Najmniejszy luz podpisu z każdej strony przycisku rzędu.</summary>
    private const double CaptionSlack = 4;

    private MainViewModel? _viewModel;
    private int _coverRequest;

    /// <summary>Ostatni element warstwy, który dostał fokus, z klawiatury albo myszy; klawisz, który odsłoni warstwę, do niego wraca.</summary>
    private IInputElement? _lastFocused;

    /// <summary>Położenie przechodzi właśnie między paskiem a modelem widoku; zmiana nie wraca tam, skąd przyszła.</summary>
    private bool _syncingSeek;

    /// <summary>Bieżące kliknięcie (z dalszymi naciśnięciami dwukliku) tylko odsłoniło warstwę albo kursor.</summary>
    private bool _swallowingClicks;

    /// <summary>Scena zamrożona do zrzutu: bez przejść.</summary>
    private bool _frozen;
    private bool? _gpuDetected;

    /// <summary>Pierwsze naciśnięcie bieżącego kliknięcia było lewym w pustej części sceny, poza warstwą.</summary>
    private bool _pressedEmpty;

    /// <summary>Ostatnie położenie myszy na scenie, z którego liczy się najechanie na warstwę.</summary>
    private Point? _mouse;

    /// <summary>Ostatnie naciśnięcie przyszło z dotyku albo pióra: części przyjmują wskaźnik przy widocznej warstwie.</summary>
    private bool _touch;

    /// <summary>Kursor paska postępu (dłoń); przy ukrytym kursorze pasek dziedziczy ukryty kursor sceny.</summary>
    private readonly Cursor? _seekCursor;

    public SceneView()
    {
        InitializeComponent();
        _seekCursor = Seek.Cursor;

        // Rozmiary pisma ze stylów, zanim wielkość tekstu zacznie je mnożyć.
        _scalableText =
        [
            new(SceneTitle), new(SceneArtist), new(SceneAlbum),
            new(NextCardLabel), new(NextCardTitle), new(NextCardArtist),
            new(NightTitle), new(NightArtist), new(NightTime),
        ];

        // Warstwa sterowania: pasek postępu przyjmuje fokus i trzyma warstwę pod kursorem, ale
        // gaśnie z niego tylko uchwyt; rząd przycisków i wyjście gasną całe.
        _chromeParts = [Seek, TransportRow, ExitButton, RestartButton];
        _fadingParts = [TransportRow, ExitButton, RestartButton];

        _rowButtons = [ShuffleButton, PreviousButton, PlayButton, NextButton, RepeatButton];

        // Rzędy warstwy od góry: zamknięcie, pasek postępu, transport, odtworzenie od początku.
        _focusRows = [[ExitButton], [Seek], _rowButtons, [RestartButton]];

        Clock = () => _stopwatch.Elapsed;
        _chrome = new SceneChrome(() => Clock());
        _chrome.Changed += ApplyChrome;
        _chrome.DeadlineChanged += ScheduleTimer;
        _spinner = new DiscSpinner(Disc);
        Stage.LayoutTransform = _scale;
        ExitStage.LayoutTransform = _exitScale;
        EdgeHintStage.LayoutTransform = _exitScale;

        GpuProbe.Detected += OnGpuDetected;
        _captionTimer.Tick += (_, _) => HideVisualCaption();
        _driftTimer.Tick += (_, _) => AdvanceDrift();
        _artFront = CoverArtA;
        _artBack = CoverArtB;
        CoverArtHost.RenderTransform = new TransformGroup { Children = { _coverScale, _driftScale, _driftMove } };

        // Wielkość płyty i logo stacji: skala wokół środka, więc kolumna napisów stoi w miejscu.
        DiscHost.RenderTransformOrigin = RelativePoint.Center;
        DiscHost.RenderTransform = _discScale;
        RadioStageHost.RenderTransformOrigin = RelativePoint.Center;
        RadioStageHost.RenderTransform = _radioScale;
        _glowTimer.Tick += (_, _) => StepGlow();
        _nightTick.Tick += (_, _) => AdvanceNight();
        _nightFade.Tick += (_, _) => FinishNightMove();
        _oledTimer.Tick += (_, _) => AdvanceOled();
        _dimTimer.Tick += (_, _) => AdvanceDim();
        Stage.RenderTransform = _oledMove;
        SceneBody.RenderTransform = _bodyMove;
        NightBlock.RenderTransform = _nightMove;
        QueueRow.RenderTransform = _rowMove;

        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _chrome.Update();
            ScheduleTimer();
        };

        AddHandler(PointerMovedEvent, OnScenePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnScenePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnScenePointerWheel, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Najechanie zmienia się także bez ruchu wskaźnika: część zaczyna przyjmować wskaźnik po
        // kółku albo kliknięciu, a rząd przesuwa się pod kursorem po zmianie tytułu. Avalonia
        // przelicza je przy następnej klatce i zgłasza samo wejście albo zejście z części.
        foreach (var part in _chromeParts)
        {
            part.PointerEntered += (_, _) => UpdatePointerOver();
            part.PointerExited += (_, _) => UpdatePointerOver();
        }

        // Ruch poza oknem nie trafia do sceny: ostatnie położenie nad warstwą trzymałoby ją bez końca.
        // Przy puszczeniu przycisku Avalonia czyści najechanie całego łańcucha i zaraz je przywraca,
        // choć wskaźnik nie wyszedł; wyjście przy wskaźniku przechwyconym w scenie nic więc nie
        // zmienia, a wejście zapamiętuje położenie od nowa.
        PointerExited += (_, e) =>
        {
            if (e.Pointer.Type != PointerType.Mouse || e.Pointer.Captured is Visual captured
                && (captured == this || this.IsVisualAncestorOf(captured)))
                return;

            _mouse = null;
            UpdatePointerOver();
        };

        PointerEntered += (_, e) =>
        {
            if (!IsOpen || e.Pointer.Type != PointerType.Mouse) return;

            _mouse = e.GetPosition(this);
            UpdatePointerOver();
        };

        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is IInputElement element && IsInLayer(element)) _lastFocused = element;

            // Kliknięcie myszą przenosi fokus, ale strzałki dalej nie chodzą po warstwie.
            if (e.NavigationMethod == NavigationMethod.Pointer) _chrome.KeyboardFocusLeft();
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        Seek.PropertyChanged += OnSeekValueChanged;

        // Regulacja strzałkami dotyczy tylko paska z fokusem: strzałka, Tab albo klik obok ją kończą.
        Seek.LostFocus += (_, _) =>
        {
            if (FocusedElement() is Visual focused && Seek.IsVisualAncestorOf(focused)) return;
            _chrome.SetAdjusting(false);
        };

        ApplyChrome();
    }

    /// <summary>Zegar liczników sceny; testy podstawiają własny.</summary>
    internal Func<TimeSpan> Clock { get; set; }

    internal SceneChrome Chrome => _chrome;

    internal DiscSpinner Spinner => _spinner;

    public bool IsOpen { get; private set; }

    internal bool IsCursorHidden => ReferenceEquals(Cursor, HiddenCursor);

    /// <summary>Prośba o wyjście ze sceny: przycisk, F11, Esc albo dwuklik w pustą część.</summary>
    internal event Action? ExitRequested;

    /// <summary>Przenikanie wejścia się skończyło: scena zakrywa okno w całości.</summary>
    internal event Action? Covered;

    // ================= Otwieranie i zamykanie =================

    /// <summary>Pokazuje scenę z przenikaniem, z warstwą sterowania ukrytą.</summary>
    internal void Open()
    {
        if (IsOpen) return;

        IsOpen = true;
        _lastFocused = null;
        _swallowingClicks = false;
        _pressedEmpty = false;
        _mouse = null;
        _touch = false;
        _lastInput = Clock();
        _dimmed = false;
        _oledSeconds = 0;
        _oledLast = null;
        _chrome.Reset(_viewModel?.IsPlaying == true);

        // Obrazy sceny przed odsłonięciem: ukryte plamy przyjmują nowe barwy od razu. Duży dekod
        // okładki ma wysokość ekranu, wiec rozmiar podawany jest przed otwarciem.
        if (_viewModel is not null)
        {
            if (ScreenBounds is { } target)
                _viewModel.SceneCoverSize = target.Height;
            else if (TopLevel.GetTopLevel(this)?.Screens?.ScreenFromVisual(this) is { } screen)
                _viewModel.SceneCoverSize = screen.Bounds.Height;

            _viewModel.IsSceneOpen = true;
        }

        Opacity = 0;
        IsVisible = true;
        Opacity = 1;

        ApplyVisual();
        ArmDim();
        UpdateDescription();
        UpdateSeek();
        UpdateSeekText();
        ApplyChrome();
        Focus();
        ReportCoverAfterFade();
    }

    /// <summary>
    /// Zgłasza <see cref="Covered"/> po czasie przenikania, liczonym w klatkach kompozytora jak
    /// samo przenikanie. Bez przenikania (narzędzie zrzutów) od razu.
    /// </summary>
    private void ReportCoverAfterFade()
    {
        var request = ++_coverRequest;
        var fade = Transitions?.OfType<DoubleTransition>().FirstOrDefault(t => t.Property == OpacityProperty)?.Duration
                   ?? TimeSpan.Zero;

        if (fade <= TimeSpan.Zero || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            Covered?.Invoke();
            return;
        }

        TimeSpan? start = null;

        void OnFrame(TimeSpan now)
        {
            // Wyjście albo ponowne wejście w trakcie przenikania unieważnia to zgłoszenie.
            if (request != _coverRequest || !IsOpen) return;

            start ??= now;
            if (now - start < fade)
            {
                topLevel.RequestAnimationFrame(OnFrame);
                return;
            }

            Covered?.Invoke();
        }

        topLevel.RequestAnimationFrame(OnFrame);
    }

    /// <summary>
    /// Płyta prosto, plamy w ustalonym punkcie i bez przenikań, dla narzędzia zrzutów: zrzut nie
    /// może złapać sceny, rzędu przycisków ani uchwytu paska w połowie przejścia.
    /// </summary>
    public void FreezeForCapture()
    {
        _frozen = true;
        _spinner.Freeze();
        Backdrop.FreezeForCapture();
        LiveDot.FreezeForCapture();

        Transitions = null;
        Seek.Transitions = null;
        foreach (var part in _fadingParts) part.Transitions = null;

        // Poświata i drobinki też stają, żeby zrzut był za każdym razem taki sam.
        ApplyLook();
    }

    internal void Close()
    {
        if (!IsOpen) return;

        // Przeciąganie paska nie może trwać w ukrytej scenie: puszczenie przewinęłoby utwór.
        Seek.EndDrag();
        CloseQueueRow(instant: true);

        IsOpen = false;
        _lastFocused = null;
        _coverRequest++;
        _timer.Stop();
        _dimTimer.Stop();
        _dimmed = false;
        EndEdgeFit();
        HideVisualCaption();
        _spinner.Stop();
        ApplyVisual();

        // Warstwa gaśnie od razu, bez przenikania: następne wejście nie może zacząć od części
        // warstwy widocznych przy wyjściu.
        WithoutFades(() => _chrome.Reset(_viewModel?.IsPlaying == true));
        Cursor = null;
        IsVisible = false;

        if (_viewModel is not null) _viewModel.IsSceneOpen = false;
    }

    /// <summary>
    /// Zmiana bez przenikania rzędu, wyjścia i uchwytu paska; trwające przenikanie kończy się od razu.
    /// Rząd i wyjście biorą przejścia ze stylu <c>.fades</c>, a pasek ma własne.
    /// </summary>
    private void WithoutFades(Action change)
    {
        // Po zamrożeniu do zrzutu przejść już nie ma i nie mogą wrócić.
        if (_frozen)
        {
            change();
            return;
        }

        var seekTransitions = Seek.Transitions;
        foreach (var part in _fadingParts) part.Transitions = null;
        Seek.Transitions = null;

        try
        {
            change();
        }
        finally
        {
            foreach (var part in _fadingParts) part.ClearValue(TransitionsProperty);
            Seek.Transitions = seekTransitions;
        }
    }

    // ================= Model widoku =================

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Strings.Current.PropertyChanged += OnLanguageChanged;
        UpdateTexts();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Strings.Current.PropertyChanged -= OnLanguageChanged;
        CloseQueueRow(instant: true);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Teksty składane w kodzie; etykiety z <c>{l:Translate}</c> odświeżają się same.</summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => UpdateTexts();

    private void UpdateTexts()
    {
        UpdatePlayLabel();
        UpdateDescription();
        UpdateSeekText();
        ApplyRadio();
        UpdateRowWidth();
        QueueRow.RefreshTexts();
        ToolTip.SetTip(ExitButton, $"{Strings.Current["SceneExit"]} · {Strings.Current["KeyEscape"]}");
        UpdateEdgeFit();
    }

    /// <summary>
    /// Wspólna szerokość przycisków rzędu: najmniejsza z planszy albo najdłuższy podpis w bieżącym
    /// języku z luzem po obu stronach, licząc oba stany Odtwórz/Wstrzymaj, więc zmiana stanu niczego
    /// nie przesuwa. Rząd wysuwa się w lewo o połowę nadwyżki nad polem ikony, a pole ikony
    /// pierwszego przycisku zostaje na linii kolumny. Wymiary i krój biorą się ze stylów, więc
    /// dopiero w drzewie okna.
    /// </summary>
    private void UpdateRowWidth()
    {
        if (VisualRoot is null) return;

        var typeface = new Typeface(PlayLabel.FontFamily, PlayLabel.FontStyle, PlayLabel.FontWeight, PlayLabel.FontStretch);
        var widest = 0.0;

        foreach (var key in RowCaptions)
        {
            using var layout = new TextLayout(Strings.Current[key], typeface, PlayLabel.FontSize, foreground: null,
                letterSpacing: PlayLabel.LetterSpacing);
            widest = Math.Max(widest, layout.WidthIncludingTrailingWhitespace);
        }

        var width = Math.Max(PlayButton.MinWidth, Math.Ceiling(widest + 2 * CaptionSlack));
        foreach (var button in _rowButtons) button.Width = width;

        TransportRow.Margin = new Thickness(-(width - ShuffleField.Width) / 2, 0, 0, 0);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Radio.PropertyChanged -= OnRadioChanged;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.Radio.PropertyChanged += OnRadioChanged;
            if (_gpuDetected is { } gpu) _viewModel.ReportGpu(gpu);
        }

        QueueRow.PlaceholderFactory = _viewModel is { } player ? player.RenderQueueRowPlaceholder : null;
        ApplyTextSize();
        ApplyScale(Bounds.Size);
        UpdateTexts();
    }

    /// <summary>
    /// Logo stacji zmienia się bez zmiany okładki pliku (przejście do innej stacji, pobranie logo po wejściu w scenę),
    /// więc mała okładka przy tytule odświeża się także na tę zmianę.
    /// </summary>
    private void OnRadioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadioViewModel.StageLogo)) UpdateMiniCover();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsPlaying):
                UpdatePlayLabel();
                if (!IsOpen) return;
                _chrome.PlayingChanged(_viewModel!.IsPlaying);
                UpdateSpinner();
                UpdateDrift();
                UpdateOled();
                UpdateDescription();
                ApplyLook();
                break;

            case nameof(MainViewModel.ReducedEffects):
                UpdateSpinner();
                UpdateDrift();
                UpdateOled();
                QueueRow.IsAnimated = QueueRowAnimated;
                ApplyLook();
                break;

            case nameof(MainViewModel.SceneOledCare):
                OnOledCareChanged();
                break;

            case nameof(MainViewModel.SceneTextSize):
                ApplyTextSize();
                PlaceZoneSides();
                break;

            case nameof(MainViewModel.SceneMargins):
                ApplyScale(Bounds.Size);
                UpdateEdgeFit();
                break;

            case nameof(MainViewModel.SceneCoverArt):
                UpdateArt();
                UpdateMiniCover();
                break;

            case nameof(MainViewModel.CurrentFilePath):
                OnTrackPathChanged();
                break;

            case nameof(MainViewModel.SceneVisual):
            case nameof(MainViewModel.ActiveSceneVisual):
                ApplyVisual();
                break;

            case nameof(MainViewModel.SceneEnvelope):
            case nameof(MainViewModel.SceneSpectrumShape):
            case nameof(MainViewModel.SceneWaveformKind):
            case nameof(MainViewModel.SceneDiscRing):
                UpdateAudio();
                break;

            case nameof(MainViewModel.SceneBlobs):
                UpdateAudioColours();
                ApplyLook();
                break;

            case nameof(MainViewModel.SceneTextPosition):
            case nameof(MainViewModel.SceneTextCentred):
            case nameof(MainViewModel.SceneTextInset):
                PlaceZoneSides();
                break;

            case nameof(MainViewModel.SceneArtSize):
            case nameof(MainViewModel.SceneBlobsStrength):
            case nameof(MainViewModel.SceneGlowOn):
            case nameof(MainViewModel.SceneGlowStrength):
            case nameof(MainViewModel.SceneTintOn):
            case nameof(MainViewModel.SceneTintStrength):
            case nameof(MainViewModel.SceneParticlesOn):
            case nameof(MainViewModel.SceneParticlesStrength):
            case nameof(MainViewModel.BackdropMotion):
            case nameof(MainViewModel.SpectrumResponse):
                ApplyLook();
                break;

            case nameof(MainViewModel.DiscSpin):
                UpdateSpinner();
                break;

            case nameof(MainViewModel.SceneBlobsOn):
                ApplyVisual();
                break;

            case nameof(MainViewModel.IsRadioMode):
                ApplyVisual();
                UpdatePlayLabel();
                UpdateDescription();
                if (IsQueueRowOpen) CloseQueueRow(instant: true);
                break;

            case nameof(MainViewModel.SceneLiveCaption):
                ApplyRadio();
                break;

            case nameof(MainViewModel.ShownTitle):
                UpdateDescription();
                // W radiu utworem jest kolejny tytuł stacji; w plikach zmianę zgłasza ścieżka.
                if (_viewModel!.IsRadioMode) OnTrackChanged();
                break;

            case nameof(MainViewModel.SceneLine):
                UpdateDescription();
                break;

            case nameof(MainViewModel.HasNextUp):
            case nameof(MainViewModel.NextUpTitle):
            case nameof(MainViewModel.NextUpArtist):
                UpdateNextCard();
                break;

            case nameof(MainViewModel.QueueEnded):
                UpdateQueueEnd();
                UpdateAudio();
                break;

            case nameof(MainViewModel.QueueRowRevision):
                RefreshQueueRow();
                break;

            case nameof(MainViewModel.Title):
            case nameof(MainViewModel.ArtistLine):
                UpdateDescription();
                break;

            // Ukryta scena nie przesuwa paska przy każdym odświeżeniu położenia ani nie liczy opisu
            // przy każdej sekundzie; wejście ustawia jedno i drugie od nowa.
            case nameof(MainViewModel.Progress):
                if (IsOpen) UpdateSeek();
                // Przewinięcie w pauzie: pętla stoi, więc Horyzont przesuwa głowicę jednym krokiem.
                if (IsOpen && !AudioStage.IsRunning && AudioStage.Mode == SceneAudioMode.Horizon) AudioStage.Step(1.0 / 30);
                break;

            case nameof(MainViewModel.Elapsed):
            case nameof(MainViewModel.Total):
                if (IsOpen) UpdateSeekText();
                break;
        }
    }

    /// <summary>Położenie z modelu widoku na pasku.</summary>
    private void UpdateSeek()
    {
        if (_viewModel is null || _syncingSeek) return;

        _syncingSeek = true;
        try { Seek.Value = _viewModel.Progress; }
        finally { _syncingSeek = false; }
    }

    /// <summary>
    /// Położenie ustawione wskaźnikiem, klawiaturą albo czytnikiem idzie do modelu widoku: w trakcie
    /// przeciągania tylko przesuwa czas, a przewinięcie następuje po zatwierdzeniu.
    /// </summary>
    private void OnSeekValueChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TrackSlider.ValueProperty || _syncingSeek || _viewModel is null) return;

        _syncingSeek = true;
        try { _viewModel.Progress = Seek.Value; }
        finally { _syncingSeek = false; }
    }

    /// <summary>Położenie słowami dla czytnika ekranu, np. „0:39 z 3:59”.</summary>
    private void UpdateSeekText()
    {
        if (_viewModel is null) return;
        Seek.ValueText = string.Format(Strings.Current["SeekPositionValue"], _viewModel.Elapsed, _viewModel.Total);
    }

    private void UpdateSpinner()
    {
        if (_viewModel is null) return;
        _spinner.Update(IsOpen && _viewModel.IsPlaying && !_viewModel.IsRadioMode && _viewModel.ActiveSceneVisual == SceneVisual.Disc,
            _viewModel.ReducedEffects, _viewModel.DiscSpinFactor);
        UpdateAudio();
    }

    /// <summary>Wybrana wizualizacja reaguje na dźwięk i jest dostępna: scena otwarta, ruch nieograniczony.</summary>
    private bool AudioActive => IsOpen && _viewModel is { } player && MainViewModel.IsStageVisual(player.ActiveSceneVisual);

    /// <summary>Stan GPU poznany przy pierwszym rysowaniu sceny; sonda znika, bo nie ma już nic do sprawdzenia.</summary>
    private void OnGpuDetected(bool gpu)
    {
        _gpuDetected = gpu;
        GpuProbe.IsVisible = false;
        _viewModel?.ReportGpu(gpu);
    }

    internal AuroraView AuroraView => AuroraStage;

    internal SceneAudioView AudioView => AudioStage;

    internal SceneAudioView RingView => DiscRingView;

    /// <summary>
    /// Ustawia wizualizacje oparte na danych dźwięku: tryb, barwy z palety, wariant i pętlę. Pętla
    /// chodzi tylko przy odtwarzaniu; w pauzie stoi, więc nie czyta danych i nie zamawia klatek.
    /// </summary>
    private void UpdateAudio()
    {
        if (_viewModel is not { } player) return;

        var visual = AudioActive ? player.ActiveSceneVisual : SceneVisual.Disc;
        var mode = visual switch
        {
            SceneVisual.Spectrum => SceneAudioMode.Spectrum,
            SceneVisual.Waveform => SceneAudioMode.Waveform,
            SceneVisual.Horizon => SceneAudioMode.Horizon,
            _ => SceneAudioMode.None,
        };

        var ring = IsOpen && !player.IsRadioMode && visual == SceneVisual.Disc && player.SceneDiscRing && !player.ReducedEffects;
        var playing = IsOpen && player.IsPlaying && !_frozen;

        // Zatrzymane słupki i linia zostałyby pod napisem o końcu kolejki; Horyzont ma pod nim miejsce.
        AudioStage.IsVisible = mode != SceneAudioMode.None
            && !(player.QueueEnded && mode is SceneAudioMode.Spectrum or SceneAudioMode.Waveform);
        AudioStage.Source = player.SceneAudioData;
        AudioStage.Shape = player.SceneSpectrumShape;
        AudioStage.Kind = player.SceneWaveformKind;
        AudioStage.Mode = mode;
        AudioStage.ProgressProvider ??= _progressProvider ??= () => _viewModel?.Progress ?? 0;
        AudioStage.Envelope = mode == SceneAudioMode.Horizon ? player.SceneEnvelope : null;
        AudioStage.CalmHead = player.ReducedEffects;
        PlaceAudioStage(mode);
        AudioStage.MonoChanged -= OnMonoChanged;
        AudioStage.MonoChanged += OnMonoChanged;
        MonoNote.IsVisible = mode == SceneAudioMode.Waveform && player.SceneWaveformKind == WaveformKind.Stereo && AudioStage.IsMono;

        DiscRingView.IsVisible = ring;
        DiscRingView.Source = player.SceneAudioData;
        DiscRingView.Mode = ring ? SceneAudioMode.Ring : SceneAudioMode.None;

        var aurora = visual == SceneVisual.Aurora;
        AuroraStage.IsVisible = aurora;
        AuroraStage.Source = player.SceneAudioData;
        AuroraStage.Throttled = false;

        UpdateAudioColours();
        AuroraStage.SetActive(playing && aurora);

        // Pauza: jedna klatka w barwach okładki, potem żadnych zamówień.
        if (aurora && !AuroraStage.IsRunning && AuroraStage.FramesRendered == 0) AuroraStage.Step(1.0 / 60);
        AudioStage.SetActive(playing && mode != SceneAudioMode.None);
        if (mode == SceneAudioMode.Horizon && !AudioStage.IsRunning) AudioStage.Step(1.0 / 30);
        DiscRingView.SetActive(playing && ring);
    }

    private Func<double>? _progressProvider;

    /// <summary>
    /// Fala i Widmo zajmują dolną część kadru przez szerokość treści, a Horyzont niski pas na około 70%
    /// wysokości kadru. Wszystkie stoją w tym samym miejscu po obu stronach ochrony OLED.
    /// </summary>
    private void PlaceAudioStage(SceneAudioMode mode)
    {
        var (top, height) = mode == SceneAudioMode.Horizon ? (HorizonTop, HorizonHeight) : (WideStageTop, WideStageHeight);

        AudioStage.Width = ContentRight - ContentLeft;
        AudioStage.Height = height;
        AudioStage.Margin = new Thickness(ContentLeft, top, 0, 0);
        MonoNote.Width = WideNoteWidth;
        MonoNote.Margin = new Thickness(ContentLeft, MonoNoteTop, 0, 0);
    }

    /// <summary>Nad wizualizacją przez szerokość treści tekst idzie na górę kadru, obok małej okładki, jeśli jest.</summary>
    private void PlaceStageText()
    {
        var wide = WideLayout;
        var player = _viewModel!;
        var inset = player.SceneTextInset == SceneTextInset.Wide ? TextInsetWide : 0;
        var left = (MiniCover.IsVisible ? ContentLeft + MiniCover.Width + MiniCoverGap : ContentLeft) + inset;
        var infoLeft = (Mirrored ? Across(InfoColumnLeft, InfoColumnWidth) : InfoColumnLeft) + inset;

        // Duża i największa wielkość tekstu nie mieszczą dwóch wierszy tytułu nad wizualizacją w całą szerokość.
        SceneTitle.MaxLines = wide && player.SceneTextSize is SceneTextSize.Large or SceneTextSize.ExtraLarge ? 1 : 2;

        // Wyrównanie napisów z ustawień: do lewej albo do środka kolumny.
        var align = player.SceneTextCentred ? TextAlignment.Center : TextAlignment.Left;
        SceneTitle.TextAlignment = align;
        SceneArtist.TextAlignment = align;
        SceneAlbum.TextAlignment = align;

        // Położenie z ustawień dotyczy kolumny obok płyty; nad wizualizacją w całą szerokość tekst stoi u góry.
        var bottom = !wide && player.SceneTextPosition == SceneTextPosition.Bottom;
        InfoColumn.VerticalAlignment = wide || player.SceneTextPosition == SceneTextPosition.Top
            ? Avalonia.Layout.VerticalAlignment.Top
            : bottom ? Avalonia.Layout.VerticalAlignment.Bottom : Avalonia.Layout.VerticalAlignment.Center;
        InfoColumn.Width = (wide ? ContentRight - left + inset : InfoColumnWidth) - 2 * inset;
        InfoColumn.Margin = wide
            ? new Thickness(left, WideTextTop, 0, 0)
            : new Thickness(infoLeft, InfoColumnTop, 0, bottom ? TextBottomMargin : 0);

        // Napis o końcu kolejki stoi pod kolumną, a przy kolumnie u dołu — nad nią.
        QueueEndBlock.VerticalAlignment = bottom ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Bottom;
        QueueEndBlock.Margin = bottom
            ? new Thickness(infoLeft, QueueEndBottom, 0, 0)
            : new Thickness(wide ? ContentLeft + inset : infoLeft, 0, 0, QueueEndBottom);
    }

    /// <summary>Wizualizacja w całą szerokość (Widmo, Fala, Horyzont): tekst stoi nad nią, nie obok.</summary>
    private bool WideLayout =>
        AudioActive && _viewModel!.ActiveSceneVisual is SceneVisual.Spectrum or SceneVisual.Waveform or SceneVisual.Horizon or SceneVisual.Aurora;

    private const double ContentLeft = 160;
    private const double ContentRight = 1760;
    private const double WideTextTop = 112;
    private const double MiniCoverGap = 40;
    private const double WideStageTop = 590;
    private const double WideStageHeight = 410;
    private const double HorizonTop = 656;
    private const double HorizonHeight = 200;
    private const double MonoNoteTop = 940;
    private const double WideNoteWidth = 560;
    private const double InfoColumnLeft = 920;
    private const double InfoColumnTop = 160;
    private const double InfoColumnWidth = 880;
    private const double QueueEndBottom = 64;
    private const double DiscLeft = 160;
    private const double DiscTop = 300;
    private const double DiscSize = 640;
    private const double CoverTop = 269;
    private const double NextCardEdge = 120;
    private const double NextCardBottom = 96;

    private void OnMonoChanged(bool mono) =>
        MonoNote.IsVisible = mono && AudioStage.Mode == SceneAudioMode.Waveform;

    private void UpdateAudioColours()
    {
        if (_viewModel?.SceneBlobs is not { Count: > 0 } palette) return;

        var low = palette[0];
        var high = palette.Count > 1 ? palette[1] : palette[0];
        AudioStage.SetColours(low, high);
        DiscRingView.SetColours(low, high);
        AuroraStage.SetColours(CoverPalette.Ribbon(low), CoverPalette.Ribbon(high));
    }

    /// <summary>Mała okładka przy tytule w Widmie; w radiu logo stacji, a bez logo nic.</summary>
    private void UpdateMiniCover()
    {
        if (_viewModel is not { } player) return;

        var source = player.IsRadioMode ? player.Radio.StageLogo : player.SceneCover;
        MiniCoverImage.Source = source;
        MiniCover.IsVisible = AudioActive && player.ActiveSceneVisual is SceneVisual.Spectrum or SceneVisual.Horizon or SceneVisual.Aurora && source is not null;
        PlaceStageText();
    }

    // ================= Wygląd: wielkość płyty i efekty tła =================

    private readonly ScaleTransform _discScale = new();
    private readonly ScaleTransform _radioScale = new();
    private readonly ScaleTransform _coverScale = new();

    /// <summary>Krok poświaty: wolne podążanie za poziomem, 15 razy na sekundę wystarcza.</summary>
    private readonly DispatcherTimer _glowTimer = new() { Interval = TimeSpan.FromSeconds(1.0 / 15) };

    /// <summary>Stała czasowa poświaty: zmiana jasności trwa około sekundy, więc nie ma błysków.</summary>
    private const double GlowSeconds = 0.8;

    /// <summary>Poświata tylko lekko oddycha z muzyką: do 12% krycia bazowego.</summary>
    private const double GlowSwing = 0.12;

    private const double TextInsetWide = 60;

    /// <summary>Dolny margines kolumny napisów przy położeniu u dołu.</summary>
    private const double TextBottomMargin = 120;

    private double _glowBase;

    /// <summary>Płyta albo Okładka (także logo stacji): układ, w którym działają efekty tła.</summary>
    private bool ArtLayout =>
        IsOpen && !NightActive && _viewModel?.ActiveSceneVisual is SceneVisual.Disc or SceneVisual.Cover;

    /// <summary>Mnożnik wielkości płyty, okładki i logo.</summary>
    internal static double ArtScale(SceneArtSize size) => size switch
    {
        SceneArtSize.Small => 0.85,
        SceneArtSize.Large => 1.1,
        _ => 1,
    };

    private static double StrengthFactor(SceneEffectStrength strength) => strength switch
    {
        SceneEffectStrength.Subtle => 0.6,
        SceneEffectStrength.Strong => 1.5,
        _ => 1,
    };

    /// <summary>
    /// Wielkość płyty i efekty tła według ustawień. Efekty działają w Płycie i Okładce, w barwach okładki
    /// (w radiu logo); przy ograniczonym ruchu i w pauzie stoją.
    /// </summary>
    private void ApplyLook()
    {
        if (_viewModel is not { } player) return;

        var scale = ArtScale(player.SceneArtSize);
        foreach (var transform in new[] { _discScale, _radioScale, _coverScale })
        {
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }

        var palette = player.SceneBlobs is { Count: > 0 } colours ? colours : null;
        var first = palette?[0] ?? Color.FromRgb(120, 140, 180);
        var second = palette is { Count: > 1 } ? palette[1] : first;
        var art = ArtLayout;
        var moving = player.IsPlaying && !player.ReducedEffects && !_frozen;

        // Plamy: w Płycie i Okładce dotychczasowa siła razy mnożnik z ustawień, w pozostałych układach
        // siła dotychczasowa (widoczność ustawia ApplyVisual).
        var blobs = art ? StrengthFactor(player.SceneBlobsStrength) : 1;
        Backdrop.MinimumStrength = 0.25 * blobs;
        Backdrop.MaximumStrength = 0.3 * blobs;

        // Tempo ruchu tła i opadanie widma z ustawień: plamy, przesuwanie okładki, drobinki,
        // słupki, pierścień i zorza.
        Backdrop.MotionScale = player.BackdropMotionFactor;
        Particles.MotionScale = player.BackdropMotionFactor;
        _driftScaleFactor = player.BackdropMotionFactor;
        AudioStage.Spectrum.FallScale = player.SpectrumFallFactor;
        DiscRingView.Spectrum.FallScale = player.SpectrumFallFactor;
        AuroraStage.Spectrum.FallScale = player.SpectrumFallFactor;

        // Zabarwienie: gradient od lewego górnego rogu i winieta w drugiej barwie, przyciemnionej.
        TintLayer.IsVisible = art && player.SceneTintOn;
        if (TintLayer.IsVisible)
        {
            var alpha = (byte)Math.Round(Math.Min(1, 0.22 * StrengthFactor(player.SceneTintStrength)) * 255);
            TintGradient.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(alpha, first.R, first.G, first.B), 0),
                    new GradientStop(Color.FromArgb(0, first.R, first.G, first.B), 0.7),
                },
            };
            var dark = Color.FromRgb((byte)(second.R * 0.4), (byte)(second.G * 0.4), (byte)(second.B * 0.4));
            TintVignette.Background = new RadialGradientBrush
            {
                RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, dark.R, dark.G, dark.B), 0.55),
                    new GradientStop(Color.FromArgb((byte)Math.Min(255, alpha * 1.4), dark.R, dark.G, dark.B), 1),
                },
            };
        }

        // Drobinki: liczba i krycie z siły; ruch tylko przy odtwarzaniu i pełnym ruchu.
        Particles.IsVisible = art && player.SceneParticlesOn;
        Particles.Count = player.SceneParticlesStrength switch
        {
            SceneEffectStrength.Subtle => 24,
            SceneEffectStrength.Strong => 64,
            _ => 40,
        };
        Particles.Strength = 0.35 * StrengthFactor(player.SceneParticlesStrength) * 0.9;
        Particles.IsActive = Particles.IsVisible && moving;

        // Poświata: miękkie światło za płytą, okładką albo logo.
        ArtGlow.IsVisible = art && player.SceneGlowOn;
        _glowBase = Math.Min(0.85, 0.5 * StrengthFactor(player.SceneGlowStrength));
        if (ArtGlow.IsVisible)
        {
            ArtGlow.Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(255, first.R, first.G, first.B), 0),
                    new GradientStop(Color.FromArgb(110, second.R, second.G, second.B), 0.45),
                    new GradientStop(Color.FromArgb(0, second.R, second.G, second.B), 1),
                },
            };
            PlaceGlow(scale);
            if (!moving) ArtGlow.Opacity = _glowBase;
        }

        if (ArtGlow.IsVisible && moving) _glowTimer.Start();
        else _glowTimer.Stop();
    }

    /// <summary>Poświata ma środek w środku płyty, okładki albo logo i rośnie razem z nimi.</summary>
    private void PlaceGlow(double scale)
    {
        var cover = _viewModel?.ActiveSceneVisual == SceneVisual.Cover && !(_viewModel?.IsRadioMode ?? false);
        var host = cover ? (Control)CoverArtHost : DiscHost;
        var width = cover ? CoverBoxWidth : DiscSize;
        var height = cover ? CoverBoxHeight : DiscSize;
        var centre = new Point(host.Margin.Left + width / 2, host.Margin.Top + height / 2);
        var size = Math.Max(width, height) * scale * 1.55;

        ArtGlow.Width = size;
        ArtGlow.Height = size;
        ArtGlow.Margin = new Thickness(centre.X - size / 2, centre.Y - size / 2, 0, 0);
    }

    /// <summary>Krok poświaty: jasność wolno idzie za poziomem dźwięku, w wąskim zakresie.</summary>
    private void StepGlow()
    {
        if (_viewModel is not { } player || !ArtGlow.IsVisible) return;

        var level = Math.Clamp(player.WaveLevel * 3, 0, 1);
        var target = _glowBase * (1 - GlowSwing + GlowSwing * level);
        var step = 1 - Math.Exp(-_glowTimer.Interval.TotalSeconds / GlowSeconds);
        ArtGlow.Opacity += (target - ArtGlow.Opacity) * step;
    }

    /// <summary>Wartość krycia poświaty, dla testów.</summary>
    internal double GlowOpacity => ArtGlow.Opacity;

    /// <summary>Zegar poświaty działa, dla testów.</summary>
    internal bool GlowRunning => _glowTimer.IsEnabled;

    // ================= Wizualizacje =================

    /// <summary>Co ile blok Nocy zmienia miejsce.</summary>
    internal static readonly TimeSpan NightMoveEvery = TimeSpan.FromMinutes(2);

    /// <summary>Czas przenikania bloku Nocy przy przenosinach; zgrany ze stylem w XAML.</summary>
    private static readonly TimeSpan NightFade = TimeSpan.FromMilliseconds(600);

    /// <summary>Krycie bloku Nocy: przygaszony, 40-55%.</summary>
    private const double NightOpacity = 0.5;

    /// <summary>Miejsca bloku Nocy w kadrze 1920 x 1080, poza kolumną sterowania.</summary>
    private static readonly Thickness[] NightSpots =
        [new(160, 120, 0, 0), new(1080, 100, 0, 0), new(160, 880, 0, 0), new(160, 480, 0, 0)];

    private readonly DispatcherTimer _nightTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _nightFade = new() { Interval = NightFade };
    private TimeSpan _nightNext;
    private int _nightSpot;

    // ================= Dopasowanie krawędzi =================

    private bool _edgeFit;

    /// <summary>Ramka dopasowania krawędzi jest pokazana, a strzałki zmieniają margines.</summary>
    internal bool IsEdgeFitting => _edgeFit;

    /// <summary>Pokazuje ramkę na granicy marginesu; strzałki zmieniają margines, Enter i Esc kończą tryb.</summary>
    internal void BeginEdgeFit()
    {
        if (!IsOpen) return;

        _edgeFit = true;
        ApplyScale(Bounds.Size);
        UpdateEdgeFit();
    }

    internal void EndEdgeFit()
    {
        if (!_edgeFit) return;

        _edgeFit = false;
        UpdateEdgeFit();
    }

    private void StepMargin(int direction)
    {
        if (_viewModel is not { } player) return;

        player.SetSceneMargin(ScreenKey, ScreenMargin.Step(player.GetSceneMargin(ScreenKey), direction));
    }

    private void UpdateEdgeFit()
    {
        EdgeFrame.IsVisible = _edgeFit;
        if (!_edgeFit) return;

        var key = CurrentMargin switch
        {
            SceneMargin.Small => "SceneMarginSmall",
            SceneMargin.Large => "SceneMarginLarge",
            _ => "SceneMarginNone",
        };

        EdgeFitValue.Text = string.Format(Strings.Current["SceneEdgeFitValue"], Strings.Current[key]);
    }

    // ================= Wielkość tekstu =================

    /// <summary>Napis z rozmiarem pisma i wysokością wiersza sprzed mnożenia.</summary>
    private readonly struct ScalableText(TextBlock block)
    {
        public TextBlock Block { get; } = block;

        public double Size { get; } = block.FontSize;

        public double Line { get; } = block.LineHeight;

        /// <summary>Ujemny margines, który oddaje układowi nadmiar wiersza nad pismem; rośnie razem z wierszem.</summary>
        public Thickness Margin { get; } = block.Margin;
    }

    private readonly ScalableText[] _scalableText;

    /// <summary>Szerokość karty „Następny” i bloku Nocy przy zwykłej wielkości; blok nie sięga prawej krawędzi.</summary>
    private const double NextCardWidth = 560;
    private const double NightBlockWidth = 640;
    private const double NightBlockMaxWidth = 780;

    private double TextFactor => _viewModel is null ? 1 : SceneText.Factor(_viewModel.SceneTextSize);

    /// <summary>
    /// Mnoży pismo kolumny tekstu, karty „Następny” i bloku Nocy. Karta i blok szerzeją razem z pismem,
    /// a w układach szerokich najwyższy poziom zamienia tytuł w jeden wiersz, żeby tekst nie wszedł
    /// na wizualizację.
    /// </summary>
    private void ApplyTextSize()
    {
        var factor = TextFactor;

        foreach (var text in _scalableText)
        {
            text.Block.FontSize = text.Size * factor;
            if (!double.IsNaN(text.Line)) text.Block.LineHeight = text.Line * factor;
            var margin = text.Margin;
            text.Block.Margin = new Thickness(margin.Left * factor, margin.Top * factor, margin.Right * factor, margin.Bottom * factor);
        }

        NextCard.MaxWidth = NextCardWidth * factor;
        NightBlock.Width = Math.Min(NightBlockWidth * factor, NightBlockMaxWidth);
        PlaceStageText();
    }

    // ================= Ochrona ekranu OLED =================

    private readonly DispatcherTimer _oledTimer = new() { Interval = OledCare.DriftStep };
    private readonly DispatcherTimer _dimTimer = new();
    private readonly TranslateTransform _oledMove = new();

    private readonly Transitions _dimTransitions =
        [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromSeconds(2) }];

    private double _oledSeconds;
    private TimeSpan? _oledLast;
    private int _oledJump;
    private double _oledX;
    private double _oledY;
    private TimeSpan _lastInput;
    private bool _dimmed;
    private bool _swapped;

    /// <summary>Przesunięcie całego kadru z ochrony OLED, w jednostkach płótna.</summary>
    internal (double X, double Y) OledOffset => (_oledX, _oledY);

    /// <summary>Timer dryfu chodzi; w pauzie i w trybie ograniczonym nie powinien.</summary>
    internal bool OledTimerRunning => _oledTimer.IsEnabled;

    /// <summary>Stałe napisy są przygaszone po bezczynności.</summary>
    internal bool IsDimmed => _dimmed;

    /// <summary>Płyta i tekst stoją po przeciwnych stronach niż zwykle.</summary>
    internal bool IsMirrored => Mirrored;

    /// <summary>Ochrona działa przy otwartej scenie i włączonym ustawieniu.</summary>
    private bool OledCareOn => IsOpen && _viewModel is { SceneOledCare: true };

    /// <summary>Dryf albo skok położenia: ustawienie włączone, a w Nocy zawsze.</summary>
    private bool OledMotionOn => OledCareOn || NightActive;

    private bool Mirrored => _swapped && OledCareOn && !NightActive && !WideLayout;

    private static double Across(double left, double width) => FrameWidth - left - width;

    private void SetOled(double x, double y)
    {
        _oledX = x;
        _oledY = y;
        _oledMove.X = x * Scale;
        _oledMove.Y = y * Scale;
    }

    /// <summary>
    /// Ustawia przesunięcie kadru i krok dryfu. W pauzie dryf stoi i nie ma timera; w trybie
    /// ograniczonym zamiast dryfu jest skok położenia przy zmianie utworu.
    /// </summary>
    private void UpdateOled()
    {
        if (!OledMotionOn || _viewModel is not { } player)
        {
            _oledTimer.Stop();
            _oledLast = null;
            SetOled(0, 0);
            return;
        }

        if (player.ReducedEffects)
        {
            _oledTimer.Stop();
            _oledLast = null;
            var (jumpX, jumpY) = OledCare.JumpAt(_oledJump);
            SetOled(jumpX, jumpY);
            return;
        }

        var (x, y) = OledCare.DriftAt(_oledSeconds);
        SetOled(x, y);

        if (player.IsPlaying && !_frozen)
        {
            _oledLast ??= Clock();
            _oledTimer.Start();
        }
        else
        {
            _oledTimer.Stop();
            _oledLast = null;
        }
    }

    /// <summary>Dolicza czas ruchu od poprzedniego kroku i przesuwa kadr.</summary>
    private void AdvanceOled()
    {
        if (!OledMotionOn || _viewModel is not { IsPlaying: true, ReducedEffects: false } || _frozen) return;

        var now = Clock();
        if (_oledLast is { } last) _oledSeconds += Math.Max(0, (now - last).TotalSeconds);
        _oledLast = now;

        var (x, y) = OledCare.DriftAt(_oledSeconds);
        SetOled(x, y);
    }

    /// <summary>Zmiana utworu: napisy wracają do pełnej jasności, układ zmienia stronę, a w trybie ograniczonym kadr skacze.</summary>
    private void OnTrackChanged()
    {
        if (!IsOpen || _viewModel is not { } player) return;

        NoteInput();

        if (OledMotionOn && player.ReducedEffects)
        {
            _oledJump++;
            UpdateOled();
        }

        // Przy widocznych przyciskach układ stoi, żeby „Następny” nie uciekał spod kursora.
        if (OledCareOn && !_chrome.IsShown)
        {
            _swapped = !_swapped;
            PlaceZoneSides();
        }
    }

    private void OnOledCareChanged()
    {
        UpdateOled();

        if (OledCareOn)
        {
            _lastInput = Clock();
            ArmDim();
        }
        else
        {
            _dimTimer.Stop();
            if (_dimmed)
            {
                _dimmed = false;
                ApplyDim(animate: false);
            }
        }

        PlaceZoneSides();
    }

    /// <summary>Płyta, okładka i kolumna tekstu zamieniają się stronami przy zmianie utworu bez widocznych przycisków; układy szerokie stoją.</summary>
    private void PlaceZoneSides()
    {
        var mirrored = Mirrored;
        var discLeft = mirrored ? Across(DiscLeft, DiscSize) : DiscLeft;

        DiscHost.Margin = new Thickness(discLeft, DiscTop, 0, 0);
        RadioStageHost.Margin = new Thickness(discLeft, DiscTop, 0, 0);
        CoverArtHost.Margin = new Thickness(mirrored ? Across(DiscLeft, CoverBoxWidth) : DiscLeft, CoverTop, 0, 0);
        CoverArtFrame.HorizontalAlignment = mirrored
            ? Avalonia.Layout.HorizontalAlignment.Right
            : Avalonia.Layout.HorizontalAlignment.Left;

        // Karta „Następny” przechodzi na stronę kolumny, żeby nie wchodzić na płytę; przy napisach
        // u dołu staje u góry, żeby nie wchodzić na kolumnę. Wyśrodkowana kolumna w największej wielkości
        // tekstu sięga dołem do karty, więc wtedy karta też staje u góry.
        var top = !WideLayout && _viewModel is { } look
            && (look.SceneTextPosition == SceneTextPosition.Bottom
                || look.SceneTextPosition == SceneTextPosition.Middle && look.SceneTextSize == SceneTextSize.ExtraLarge);
        NextCard.HorizontalAlignment = mirrored
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;
        NextCard.VerticalAlignment = top ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Bottom;
        NextCard.Margin = (mirrored, top) switch
        {
            (true, false) => new Thickness(NextCardEdge, 0, 0, NextCardBottom),
            (false, false) => new Thickness(0, 0, NextCardEdge, NextCardBottom),
            (true, true) => new Thickness(NextCardEdge, NextCardBottom, 0, 0),
            (false, true) => new Thickness(0, NextCardBottom, NextCardEdge, 0),
        };

        PlaceAudioStage(AudioStage.Mode);
        PlaceStageText();
        if (ArtGlow.IsVisible) PlaceGlow(ArtScale(_viewModel?.SceneArtSize ?? SceneArtSize.Normal));
    }

    // ----- Przygaszanie stałych napisów -----

    /// <summary>Wejście użytkownika (klawisz, mysz, dotyk) albo zmiana utworu: napisy wracają, odliczanie od nowa.</summary>
    private void NoteInput()
    {
        if (!IsOpen) return;

        _lastInput = Clock();

        if (_dimmed)
        {
            _dimmed = false;
            ApplyDim(animate: false);
        }

        ArmDim();
    }

    /// <summary>Jednorazowy timer do chwili przygaszenia; po przygaszeniu cisza do następnego wejścia.</summary>
    private void ArmDim()
    {
        if (_dimTimer.IsEnabled || _dimmed || !OledCareOn) return;

        _dimTimer.Interval = OledCare.DimAfter;
        _dimTimer.Start();
    }

    /// <summary>Przygasza napisy, gdy od ostatniego wejścia minęło dość czasu; inaczej czeka na resztę.</summary>
    internal void AdvanceDim()
    {
        _dimTimer.Stop();
        if (!OledCareOn || _dimmed) return;

        var left = OledCare.DimAfter - (Clock() - _lastInput);
        if (left > TimeSpan.Zero)
        {
            _dimTimer.Interval = left;
            _dimTimer.Start();
            return;
        }

        _dimmed = true;
        ApplyDim(animate: true);
    }

    private double NightRestOpacity => _dimmed ? NightOpacity * OledCare.DimFactor : NightOpacity;

    private void ApplyDim(bool animate)
    {
        ApplyTitleOpacity(animate);
        if (NightActive && !_nightFade.IsEnabled) NightBlock.Opacity = NightRestOpacity;
    }

    /// <summary>Tytuł jest niewidoczny w Nocy, przygaszony po bezczynności, a poza tym w pełni widoczny.</summary>
    private void ApplyTitleOpacity(bool animate)
    {
        TitleBlock.Transitions = animate && !_frozen && _viewModel is { ReducedEffects: false } ? _dimTransitions : null;
        TitleBlock.Opacity = NightActive ? 0 : _dimmed ? OledCare.DimFactor : 1;
    }

    // ================= Okładka =================

    /// <summary>Czas przenikania okładek przy zmianie utworu.</summary>
    private static readonly TimeSpan CoverFade = TimeSpan.FromMilliseconds(600);

    /// <summary>Skrócone przenikanie, gdy utwory zmieniają się szybciej niż co <see cref="CoverFastSwitch"/>.</summary>
    private static readonly TimeSpan CoverFadeFast = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan CoverFastSwitch = TimeSpan.FromSeconds(1);

    /// <summary>Największy obszar okładki w kadrze 1920 x 1080 (zgodny z hostem w XAML).</summary>
    private const double CoverBoxWidth = 720;

    private const double CoverBoxHeight = 702;

    private readonly DispatcherTimer _driftTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private readonly ScaleTransform _driftScale = new(1, 1);
    private readonly TranslateTransform _driftMove = new();
    private Image _artFront = null!;
    private Image _artBack = null!;
    private Bitmap? _artSmall;
    private TimeSpan _artChangedAt;
    private CoverDriftPath _driftPath;
    private double _driftSeconds;
    private TimeSpan? _driftLast;

    /// <summary>Czas ostatniego przenikania okładek: 600 ms, 300 ms przy szybkim przeskakiwaniu, zero w trybie ograniczonym.</summary>
    internal TimeSpan CoverFadeDuration { get; private set; }

    /// <summary>Warstwa okładki, która jest teraz na wierzchu.</summary>
    internal Image CoverArtShown => _artFront;

    internal double DriftScale => _driftScale.ScaleX;

    internal double DriftX => _driftMove.X;

    internal double DriftY => _driftMove.Y;

    internal CoverDriftPath DriftDirection => _driftPath;

    /// <summary>Wybrana wizualizacja jest widoczna: scena otwarta i Okładka.</summary>
    private bool CoverActive => IsOpen && _viewModel is { IsRadioMode: false, SceneVisual: SceneVisual.Cover };

    /// <summary>Okładka bez przenikania, ruch od początku cyklu; kierunek wynika ze ścieżki bieżącego pliku.</summary>
    private void ShowArtInstantly()
    {
        _artFront.Transitions = null;
        _artBack.Transitions = null;
        _artFront.Source = _viewModel?.SceneCoverArt;
        _artFront.Opacity = 1;
        _artBack.Source = null;
        _artBack.Opacity = 0;
        _artSmall = _viewModel?.SceneCover;
        _artChangedAt = Clock();
        SizeFrame(_artFront.Source);
        ResetDrift();
    }

    /// <summary>Wyjście z wizualizacji: warstwy nie trzymają obrazów, które model zaraz zwolni.</summary>
    private void ClearArt()
    {
        _artFront.Source = null;
        _artBack.Source = null;
        _artSmall = null;
    }

    /// <summary>Ramka okładki w proporcjach obrazu, w obszarze 720 x 702.</summary>
    private void SizeFrame(IImage? art)
    {
        var aspect = art is { Size: { Width: > 0, Height: > 0 } size } ? size.Width / size.Height : 1;
        var width = Math.Min(CoverBoxWidth, CoverBoxHeight * aspect);
        CoverArtFrame.Width = width;
        CoverArtFrame.Height = width / aspect;
    }

    /// <summary>Zmienił się obraz sceny: nowa okładka przenika, a ostrzejszy dekod tej samej podmienia się w miejscu.</summary>
    private void UpdateArt()
    {
        if (!CoverActive || _viewModel is null) return;

        var art = _viewModel.SceneCoverArt;
        if (ReferenceEquals(art, _artFront.Source)) return;

        var small = _viewModel.SceneCover;
        if (ReferenceEquals(small, _artSmall) && _artFront.Source is not null)
        {
            _artFront.Source = art;
            SizeFrame(art);
            return;
        }

        _artSmall = small;
        CrossfadeTo(art);
    }

    /// <summary>Numer ostatniego przenikania; starsze nie czyszczą już warstw.</summary>
    private int _artGeneration;

    /// <summary>Odłożone czyszczenie warstwy; test podstawia własny harmonogram.</summary>
    internal Action<Action, TimeSpan> RunArtLater { get; set; } = (action, delay) => DispatcherTimer.RunOnce(action, delay);

    private void CrossfadeTo(IImage? art)
    {
        var now = Clock();
        var duration = _viewModel!.ReducedEffects || _frozen ? TimeSpan.Zero
            : now - _artChangedAt < CoverFastSwitch ? CoverFadeFast
            : CoverFade;
        _artChangedAt = now;
        CoverFadeDuration = duration;

        var next = _artBack;
        var previous = _artFront;
        var transitions = duration > TimeSpan.Zero
            ? new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = duration } }
            : null;
        next.Transitions = transitions;
        previous.Transitions = transitions;

        next.Source = art;
        SizeFrame(art);
        previous.Opacity = 0;
        next.Opacity = 1;
        _artFront = next;
        _artBack = previous;

        // Stara warstwa po przenikaniu nie trzyma obrazu, który model zwolni. Czyści tylko ostatnie
        // przenikanie: przy szybkim przeskakiwaniu ta sama warstwa pokazuje już inną okładkę.
        var generation = ++_artGeneration;

        void Release()
        {
            if (generation == _artGeneration && ReferenceEquals(previous, _artBack)) previous.Source = null;
        }

        if (duration > TimeSpan.Zero) RunArtLater(Release, duration + TimeSpan.FromMilliseconds(100));
        else Release();
    }

    private void OnTrackPathChanged()
    {
        if (CoverActive) ResetDrift();
        if (_viewModel is { IsRadioMode: false }) OnTrackChanged();
    }

    // ----- Ruch okładki -----

    /// <summary>Ruch okładki trwa tylko w odtwarzaniu, w trybie pełnym i przy aktywnej wizualizacji.</summary>
    private bool DriftRuns => CoverActive && _viewModel!.IsPlaying && !_viewModel.ReducedEffects && !_frozen;

    private void ResetDrift()
    {
        _driftPath = CoverDrift.For(_viewModel?.CurrentFilePath);
        _driftSeconds = 0;
        _driftLast = DriftRuns ? Clock() : null;
        ApplyDrift();
    }

    private void UpdateDrift()
    {
        if (_viewModel is null) return;

        if (CoverActive && _viewModel.ReducedEffects && _driftSeconds != 0)
        {
            _driftSeconds = 0;
            ApplyDrift();
        }

        if (DriftRuns)
        {
            _driftLast ??= Clock();
            _driftTimer.Start();
        }
        else
        {
            _driftTimer.Stop();
            _driftLast = null;
        }
    }

    /// <summary>Dolicza czas ruchu od poprzedniego kroku i ustawia przekształcenie okładki.</summary>
    private void AdvanceDrift()
    {
        if (!DriftRuns) return;

        var now = Clock();
        if (_driftLast is { } last) _driftSeconds += Math.Max(0, (now - last).TotalSeconds) * _driftScaleFactor;
        _driftLast = now;
        ApplyDrift();
    }

    /// <summary>Mnożnik tempa przesuwania okładki z ustawień.</summary>
    private double _driftScaleFactor = 1;

    private void ApplyDrift()
    {
        var (scale, x, y) = CoverDrift.At(_driftPath, _driftSeconds);
        _driftScale.ScaleX = scale;
        _driftScale.ScaleY = scale;
        _driftMove.X = x * CoverArtFrame.Width;
        _driftMove.Y = y * CoverArtFrame.Height;
    }

    /// <summary>Wybrana wizualizacja jest widoczna: scena otwarta i Noc.</summary>
    private bool NightActive => IsOpen && _viewModel?.SceneVisual == SceneVisual.Night;

    /// <summary>
    /// Ustawia widoczność warstw wybranej wizualizacji. Ukryte tło i płyta nie liczą klatek;
    /// tytuł zostaje w układzie (niewidoczny), żeby warstwa sterowania stała w tym samym miejscu.
    /// </summary>
    private void ApplyVisual()
    {
        var night = NightActive;
        var wasNight = NightBlock.IsVisible;
        var cover = CoverActive;
        var wasCover = CoverArtHost.IsVisible;
        var radio = _viewModel?.IsRadioMode == true;

        NightVeil.IsVisible = night;
        NightBlock.IsVisible = night;
        // W radiu tło to plamy z barw logo; rozmyta okładka pliku nie należy do stacji.
        var aurora = IsOpen && _viewModel?.ActiveSceneVisual == SceneVisual.Aurora;
        CoverLayer.IsVisible = !night && !radio && !aurora;
        // Plamy w Płycie i Okładce wyłącza ustawienie efektów tła; w pozostałych układach są zawsze.
        Backdrop.IsVisible = !night && !aurora && (!ArtLayout || _viewModel?.SceneBlobsOn != false);
        DimAcross.IsVisible = !night;
        DimBottom.IsVisible = !night;
        var audio = AudioActive;
        DiscHost.IsVisible = !night && !cover && !radio && !audio;
        CoverArtHost.IsVisible = cover;
        RadioStageHost.IsVisible = radio && IsOpen && !night && !audio;
        ApplyRadio();
        ApplyTitleOpacity(animate: false);
        UpdateProgressBlock();

        if (cover && !wasCover) ShowArtInstantly();
        else if (!cover && wasCover) ClearArt();

        UpdateSpinner();
        UpdateDrift();
        UpdateOled();
        UpdateMiniCover();
        PlaceZoneSides();
        ApplyLook();

        if (night && !wasNight)
        {
            _nightSpot = 0;
            NightBlock.Margin = NightSpots[0];
            NightBlock.Opacity = NightRestOpacity;
            LiftNightBlock();
            _nightNext = Clock() + NightMoveEvery;
            _nightTick.Start();
        }
        else if (!night)
        {
            _nightTick.Stop();
            _nightFade.Stop();
        }

        UpdateNextCard();
    }

    /// <summary>Po upływie 2 minut zaczyna przenosiny bloku Nocy: w trybie ograniczonym od razu, bez przenikania.</summary>
    private void AdvanceNight()
    {
        if (!NightActive || _nightFade.IsEnabled || Clock() < _nightNext) return;

        _nightNext = Clock() + NightMoveEvery;

        if (_viewModel!.ReducedEffects || _frozen)
        {
            FinishNightMove();
            return;
        }

        NightBlock.Opacity = 0;
        _nightFade.Stop();
        _nightFade.Start();
    }

    /// <summary>Blok jest przezroczysty: zmienia miejsce i wraca do przygaszonej jasności.</summary>
    internal void FinishNightMove()
    {
        _nightFade.Stop();
        if (!NightActive) return;

        _nightSpot = (_nightSpot + 1) % NightSpots.Length;
        NightBlock.Margin = NightSpots[_nightSpot];
        NightBlock.Opacity = NightRestOpacity;
        LiftNightBlock();
    }

    /// <summary>
    /// Radio: zamiast osi czasu cienka kreska i napis „Na żywo” z kropką albo stan połączenia, bez Losowo i Powtarzaj
    /// (miejsce zostaje, żeby rząd się nie przesuwał). Pliki dostają wszystko z powrotem.
    /// </summary>
    private void ApplyRadio()
    {
        if (_viewModel is not { } player) return;

        var radio = player.IsRadioMode;
        Seek.IsEnabled = !radio;
        Seek.Opacity = radio ? 0 : 1;
        LiveLine.IsVisible = radio;
        TimesRow.IsVisible = !radio;
        LiveCaption.IsVisible = radio;
        LiveCaption.Text = radio ? player.SceneLiveCaption : string.Empty;
        LiveDot.IsVisible = radio && player.SceneCaptionIsLive;

        ShuffleButton.IsEnabled = !radio;
        ShuffleButton.Opacity = radio ? 0 : 1;
        RepeatButton.IsEnabled = !radio;
        RepeatButton.Opacity = radio ? 0 : 1;
    }

    private void UpdatePlayLabel()
    {
        var key = _viewModel?.IsPlaying != true ? "ScenePlay" : _viewModel.IsRadioMode ? "SceneStop" : "ScenePause";
        var label = Strings.Current[key];
        PlayLabel.Text = label;
        AutomationProperties.SetName(PlayButton, label);
    }

    /// <summary>Nazwa korzenia dla czytnika: tytuł, wykonawca i stan; zmiana jest ogłaszana grzecznie.</summary>
    private void UpdateDescription()
    {
        if (_viewModel is null) return;

        var state = Strings.Current[_viewModel.IsPlaying ? "SceneStatePlaying" : "SceneStatePaused"];
        var text = _viewModel.IsRadioMode
            ? (_viewModel.HasSceneLine ? $"{_viewModel.ShownTitle}, {_viewModel.SceneLine}, {state}" : $"{_viewModel.ShownTitle}, {state}")
            : _viewModel.HasArtistLine
            ? $"{_viewModel.Title}, {_viewModel.ArtistLine}, {state}"
            : $"{_viewModel.Title}, {state}";

        AutomationProperties.SetName(this, text);
    }

    // ================= Karta „Następny” =================

    /// <summary>Czas pojawiania się karty; w trybie ograniczonym i po zamrożeniu pojawia się skokowo.</summary>
    private static readonly TimeSpan NextCardFade = TimeSpan.FromMilliseconds(300);

    private readonly Transitions _nextCardTransitions =
        [new DoubleTransition { Property = OpacityProperty, Duration = NextCardFade }];

    /// <summary>Docelowa jasność karty: w Nocy taka sama jak bloku tekstu.</summary>
    internal double NextCardTargetOpacity => NightActive ? NightOpacity : 1;

    /// <summary>
    /// Pokazuje albo chowa kartę i ustawia jej jasność. Wołane tylko przy zmianie stanu modelu,
    /// wizualizacji albo trybu ruchu, więc w pauzie i bez zmiany nie zamawia klatek.
    /// </summary>
    private void UpdateNextCard()
    {
        var show = IsOpen && !IsQueueRowOpen && _viewModel is { HasNextUp: true };

        if (!show)
        {
            // Znika od razu, bez przenikania.
            NextCard.Transitions = null;
            NextCard.Opacity = 0;
            NextCard.IsVisible = false;
            return;
        }

        var instant = _viewModel!.ReducedEffects || _frozen;
        var title = _viewModel.NextUpTitle;
        AutomationProperties.SetName(NextCard, _viewModel.HasNextUpArtist
            ? $"{Strings.Current["SceneUpNext"]}: {title}, {_viewModel.NextUpArtist}"
            : $"{Strings.Current["SceneUpNext"]}: {title}");

        if (NextCard.IsVisible)
        {
            NextCard.Transitions = null;
            NextCard.Opacity = NextCardTargetOpacity;
            return;
        }

        NextCard.Transitions = instant ? null : _nextCardTransitions;
        NextCard.Opacity = 0;
        NextCard.IsVisible = true;
        NextCard.Opacity = NextCardTargetOpacity;
    }

    /// <summary>Po zniknięciu napisu o końcu kolejki fokus nie zostaje na niewidocznym przycisku.</summary>
    private void UpdateQueueEnd()
    {
        if (_viewModel is { QueueEnded: false } && FocusedElement() == RestartButton) Focus();
    }

    // ================= Rząd okładek kolejki =================

    /// <summary>O tyle unosi się płyta, kolumna i napis o końcu kolejki, żeby zmieścić rząd pod nimi.</summary>
    internal const double QueueRowLift = 230;

    /// <summary>Z tak daleka rząd wjeżdża na miejsce.</summary>
    private const double QueueRowEntry = 260;

    /// <summary>Blok Nocy stojący niżej niż ten pas nad dolną krawędzią kadru nachodziłby na rząd.</summary>
    private const double QueueRowBand = 300;

    internal static readonly TimeSpan QueueRowSlide = TimeSpan.FromMilliseconds(300);

    private readonly TranslateTransform _bodyMove = new();
    private readonly TranslateTransform _nightMove = new();
    private readonly TranslateTransform _rowMove = new();
    private readonly Transitions _bodyTransitions = SlideTransitions();
    private readonly Transitions _nightTransitions = SlideTransitions();
    private readonly Transitions _rowTransitions = SlideTransitions();

    private static Transitions SlideTransitions() =>
        [new DoubleTransition { Property = TranslateTransform.YProperty, Duration = QueueRowSlide, Easing = new Avalonia.Animation.Easings.CubicEaseOut() }];

    /// <summary>Rząd okładek kolejki jest otwarty. Zostaje otwarty do Q albo Esc, bez względu na warstwę sterowania.</summary>
    internal bool IsQueueRowOpen { get; private set; }

    internal TranslateTransform BodyMove => _bodyMove;

    internal TranslateTransform NightMove => _nightMove;

    internal TranslateTransform RowMove => _rowMove;

    private bool QueueRowAnimated => _viewModel is { ReducedEffects: false } && !_frozen;

    /// <summary>Otwiera rząd z fokusem na bieżącym wpisie. Pusta kolejka i źródło na żywo nie mają czego pokazać.</summary>
    private void OpenQueueRow()
    {
        if (IsQueueRowOpen || _viewModel is not { CanShowQueueRow: true } player) return;

        IsQueueRowOpen = true;
        player.QueueRowActive = true;

        var animated = QueueRowAnimated;
        _rowMove.Transitions = null;
        _rowMove.Y = animated ? QueueRowEntry : 0;

        var (items, current) = player.BuildQueueRow();
        QueueRow.IsAnimated = animated;
        QueueRow.Open(items, current);

        SetMove(_rowMove, _rowTransitions, 0, animated);
        SetMove(_bodyMove, _bodyTransitions, -QueueRowLift, animated);
        LiftNightBlock();

        _chrome.SetAdjusting(false);
        _chrome.SetQueueRowOpen(true);
        UpdateNextCard();
    }

    /// <summary>
    /// Zamyka rząd: okładki znikają od razu, a blok wraca płynnie. Fokus schodzi z okładek, zanim znikną,
    /// a obrazy zwalnia dopiero rząd, po odpięciu ich od okładek.
    /// </summary>
    private void CloseQueueRow(bool instant = false)
    {
        if (!IsQueueRowOpen) return;

        IsQueueRowOpen = false;
        if (_viewModel is not null) _viewModel.QueueRowActive = false;

        if (QueueRow.HasFocusedTile) Focus();
        QueueRow.Close();

        var animated = QueueRowAnimated && !instant;
        SetMove(_bodyMove, _bodyTransitions, 0, animated);
        SetMove(_nightMove, _nightTransitions, 0, animated);
        SetMove(_rowMove, _rowTransitions, 0, animated: false);

        _chrome.SetQueueRowOpen(false);
        UpdateNextCard();
    }

    private static void SetMove(TranslateTransform move, Transitions transitions, double y, bool animated)
    {
        move.Transitions = animated ? transitions : null;
        move.Y = y;
    }

    /// <summary>Blok Nocy unosi się tylko z dolnego miejsca; z pozostałych wyszedłby poza kadr, a rzędowi nie przeszkadza.</summary>
    private void LiftNightBlock()
    {
        var low = NightSpots[_nightSpot].Top >= FrameHeight - QueueRowBand;
        SetMove(_nightMove, _nightTransitions, IsQueueRowOpen && low ? -QueueRowLift : 0, QueueRowAnimated);
    }

    /// <summary>Kolejka, kolejność albo bieżący wpis się zmieniły; rząd bierze nową listę, a gdy nie ma już czego pokazać, zamyka się.</summary>
    private void RefreshQueueRow()
    {
        if (!IsQueueRowOpen || _viewModel is not { } player) return;

        if (!player.CanShowQueueRow)
        {
            CloseQueueRow();
            return;
        }

        var (items, current) = player.BuildQueueRow();
        QueueRow.SetItems(items, current);
    }

    private void OnQueueRowInvoked(CoverRowItem item) => _viewModel?.PlayQueueRowItem(item.Id);

    /// <summary>Klawisze otwartego rzędu; pozostałe działają jak zawsze.</summary>
    private bool DispatchQueueRowKey(KeyEventArgs e, MainViewModel player)
    {
        if (e.KeyModifiers != KeyModifiers.None) return false;

        switch (e.Key)
        {
            // Pierwszy Esc zamyka rząd, dopiero następny scenę.
            case Key.Escape or Key.Back or Key.Q:
                CloseQueueRow();
                return true;

            case Key.Left:
                QueueRow.MoveFocusBy(-1);
                return true;

            case Key.Right:
                QueueRow.MoveFocusBy(1);
                return true;

            case Key.Home:
                QueueRow.MoveFocusTo(0);
                return true;

            case Key.End:
                QueueRow.MoveFocusTo(QueueRow.Items.Count - 1);
                return true;

            case Key.Enter:
                if (QueueRow.FocusedItem is { } item) player.PlayQueueRowItem(item.Id);
                return true;

            // Fokus zostaje w rzędzie: warstwa przyjmuje tu tylko mysz.
            case Key.Up or Key.Down or Key.Tab:
                return true;

            // Okładka z fokusem to przycisk, a Spacja dalej przełącza odtwarzanie.
            case Key.Space:
                player.TogglePlay();
                return true;

            default:
                return false;
        }
    }

    // ================= Warstwa sterowania =================

    private void UpdateProgressBlock() => ProgressBlock.Opacity = NightActive && !_chrome.IsShown ? 0 : 1;

    private void ApplyChrome()
    {
        var shown = _chrome.IsShown;

        // Części przyjmują wskaźnik przy widocznym kursorze myszy, a po stuknięciu dotykiem albo
        // piórem przy widocznej warstwie. Przy ukrytym kursorze myszy nie przyjmują go: mysz nie
        // naciska niczego na ślepo, nie podświetla przycisków i nie dostaje dłoni ani podpowiedzi.
        var pointer = _chrome.IsCursorShown;
        var hit = pointer || shown && _touch;

        foreach (var part in _fadingParts)
        {
            part.Opacity = shown ? 1 : 0;
            part.IsHitTestVisible = hit;
        }

        // Pasek zostaje, gaśnie tylko jego uchwyt. W Nocy w spoczynku świeci tylko przygaszony blok tekstu.
        Seek.ThumbOpacity = shown ? 1 : 0;
        Seek.IsHitTestVisible = hit;
        UpdateProgressBlock();
        Seek.Classes.Set("adjusting", _chrome.IsAdjusting);
        QueueRow.IsHitTestVisible = hit;

        // Bez klatki Avalonia nie przelicza najechania po schowaniu kursora: dłoń nad paskiem, tło
        // najechania i podpowiedź zależą więc od stanu kursora, a nie od samego najechania.
        if (pointer) Seek.Cursor = _seekCursor;
        else Seek.ClearValue(CursorProperty);
        Root.Classes.Set("pointer", pointer);
        ToolTip.SetServiceEnabled(ExitButton, pointer);
        if (!pointer) ToolTip.SetIsOpen(ExitButton, false);

        if (!IsOpen) return;

        // Kursor znika razem z warstwą i po klawiszu, a wraca po ruchu myszy, kółku albo kliknięciu;
        // odsłonięcie klawiszem, pauzą i dotykiem go nie pokazuje.
        Cursor = _chrome.IsCursorShown ? null : HiddenCursor;
        UpdatePointerOver();

        // Fokus nie może zostać na niewidocznym elemencie warstwy (przycisku albo pasku): wraca na
        // korzeń sceny, a klawisz, który odsłoni warstwę, przywróci go na ten element.
        if (!shown && FocusedInLayer() is not null) Focus();

        ScheduleTimer();
    }

    private void ScheduleTimer()
    {
        _timer.Stop();
        if (!IsOpen || _chrome.NextDeadline is not { } at) return;

        var wait = at - Clock();
        _timer.Interval = wait > TimeSpan.FromMilliseconds(10) ? wait : TimeSpan.FromMilliseconds(10);
        _timer.Start();
    }

    /// <summary>Gasi to, czego czas minął; testy wołają to po przesunięciu zegara.</summary>
    internal void AdvanceTimers()
    {
        _chrome.Update();
        AdvanceNight();
        AdvanceDrift();
        AdvanceOled();
        AdvanceDim();
        if (VisualCaption.IsVisible && Clock() >= _captionUntil) HideVisualCaption();
    }

    // ================= Podpis wizualizacji =================

    /// <summary>Czas, przez który widać nazwę wybranej wizualizacji.</summary>
    internal static readonly TimeSpan CaptionDuration = TimeSpan.FromMilliseconds(1500);

    private readonly DispatcherTimer _captionTimer = new() { Interval = CaptionDuration };
    private TimeSpan _captionUntil;

    private void ShowVisualCaption(SceneVisual visual)
    {
        VisualCaptionText.Text = Strings.Current["SceneVisual" + visual];
        VisualCaption.IsVisible = true;
        _captionUntil = Clock() + CaptionDuration;

        _captionTimer.Stop();
        _captionTimer.Start();
    }

    private void HideVisualCaption()
    {
        _captionTimer.Stop();
        VisualCaption.IsVisible = false;
    }

    private bool IsFocusInLayer() => FocusedInLayer() is not null;

    /// <summary>Element z fokusem, jeśli leży w warstwie sterowania.</summary>
    private IInputElement? FocusedInLayer() => FocusedElement() is { } focused && IsInLayer(focused) ? focused : null;

    private bool IsInLayer(IInputElement element) =>
        element is Visual visual && _chromeParts.Any(part => part == visual || part.IsVisualAncestorOf(visual));

    // ================= Mysz =================

    private void OnScenePointerMoved(object? sender, PointerEventArgs e)
    {
        // Dotyk i pióro nie pokazują kursora myszy.
        if (!IsOpen || e.Pointer.Type != PointerType.Mouse) return;

        SetTouch(false);
        _mouse = e.GetPosition(this);
        _chrome.PointerMoved(e.GetPosition(Plane));
        // Drżenie myszy poniżej progu ruchu nie jest obsługą: kursor pozostaje ukryty.
        if (_chrome.IsCursorShown) NoteInput();
        UpdatePointerOver();
    }

    /// <summary>Kółko działa jak ruch myszy: odsłania warstwę i kursor, licznik od nowa.</summary>
    private void OnScenePointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!IsOpen) return;

        NoteInput();
        SetTouch(false);
        _mouse = e.GetPosition(this);
        _chrome.RevealByPointer();
        UpdatePointerOver();
    }

    /// <summary>
    /// Najechanie na warstwę z ostatniego położenia wskaźnika i bieżącego układu, a nie z najechania
    /// Avalonii: to przelicza się dopiero przy ruchu albo klatce, a w pauzie scena klatek nie rysuje.
    /// Ukryta warstwa nie ma najechania.
    /// </summary>
    private void UpdatePointerOver()
    {
        if (!IsOpen) return;
        _chrome.SetPointerOver(_chrome.IsShown && _mouse is { } point && IsInLayer(point));
    }

    private void OnScenePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsOpen) return;

        NoteInput();
        var position = e.GetPosition(this);
        var inLayer = IsInLayer(position);
        var first = e.ClickCount < 2;
        var left = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        bool swallowed;

        // Pierwszy klik przy ukrytej warstwie albo ukrytym kursorze myszy tylko je odsłania, a pierwsze
        // stuknięcie dotykiem albo piórem tylko odsłania warstwę. Dalsze naciśnięcia tego samego
        // dwukliku nie naciskają niczego w warstwie.
        if (e.Pointer.Type == PointerType.Mouse)
        {
            SetTouch(false);
            _mouse = position;
            swallowed = _chrome.PointerPressed();
        }
        else
        {
            swallowed = _chrome.TouchPressed();
            SetTouch(true);

            // Stuknięcie przy warstwie odsłoniętej klawiszem albo pauzą trafiło obok części, które
            // nie przyjmowały jeszcze wskaźnika: dostaje je część pod palcem.
            if (!swallowed && inLayer && !(e.Source is IInputElement hitPart && IsInLayer(hitPart)) && Redirect(e, position))
                return;
        }

        swallowed |= !first && _swallowingClicks && inLayer;
        // Licznik kliknięć nie patrzy na przycisk: prawy, a po nim lewy daje drugiemu 2.
        if (first)
        {
            _swallowingClicks = swallowed;
            _pressedEmpty = left && !inLayer;
        }

        UpdatePointerOver();

        // Dwuklik zamyka scenę, gdy oba lewe naciśnięcia padły w pustą część: między nimi rząd mógł
        // zjechać spod kursora albo pod niego wjechać.
        if (left && e.ClickCount == 2 && _pressedEmpty && !inLayer)
        {
            e.Handled = true;
            ExitRequested?.Invoke();
            return;
        }

        if (swallowed)
        {
            e.Handled = true;
            return;
        }

        // Klik myszą w element, który ma fokus z klawiatury: fokus na ten sam element niczego nie
        // zmienia, więc pierścień by został. Fokus wraca na chwilę na korzeń, a klik daje go elementowi
        // od nowa. Dotyk i pióro zostawiają fokus bez zmian.
        if (left && e.Pointer.Type == PointerType.Mouse
            && FocusedInLayer() is Control { } focused && focused.Classes.Contains(":focus-visible")
            && e.Source is Visual source && (focused == source || focused.IsVisualAncestorOf(source)))
            Focus();
    }

    /// <summary>Ostatnie naciśnięcie przyszło z dotyku albo pióra, nie z myszy.</summary>
    private void SetTouch(bool touch)
    {
        if (_touch == touch) return;

        _touch = touch;
        ApplyChrome();
    }

    /// <summary>
    /// Naciśnięcie od nowa w części warstwy pod wskaźnikiem, z przechwyceniem jak przy zwykłym
    /// naciśnięciu, żeby puszczenie trafiło w tę samą część.
    /// </summary>
    private bool Redirect(PointerPressedEventArgs e, Point position)
    {
        if (this.InputHitTest(position) is not Interactive target || !IsInLayer((IInputElement)target)
            || TopLevel.GetTopLevel(this) is not { } root)
            return false;

        e.Handled = true;
        e.Pointer.Capture((IInputElement)target);
        target.RaiseEvent(new PointerPressedEventArgs(target, e.Pointer, root, e.GetPosition(root), e.Timestamp,
            e.GetCurrentPoint(null).Properties, e.KeyModifiers, e.ClickCount));
        return true;
    }

    /// <summary>
    /// Punkt sceny leży w części warstwy: na pasku postępu, w rzędzie przycisków razem z odstępami
    /// albo na wyjściu. Liczy się z układu, także przy ukrytej warstwie.
    /// </summary>
    private bool IsInLayer(Point point) =>
        _chromeParts.Any(part => this.TranslatePoint(point, part) is { } local && new Rect(part.Bounds.Size).Contains(local))
        || IsQueueRowOpen && this.TranslatePoint(point, QueueRow) is { } row && new Rect(QueueRow.Bounds.Size).Contains(row);

    private void OnSeekScrubbing(object? sender, double value)
    {
        _chrome.SetDragging(true);
        _viewModel?.BeginScrub();
    }

    private void OnSeekCommitted(object? sender, double value)
    {
        _viewModel?.CommitScrub(value);
        _chrome.SetDragging(false);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    private void OnTogglePlay(object? sender, RoutedEventArgs e) => _viewModel?.TogglePlay();

    private void OnPrevious(object? sender, RoutedEventArgs e) => _viewModel?.Previous();

    private void OnNext(object? sender, RoutedEventArgs e) => _viewModel?.Next();

    private void OnRepeat(object? sender, RoutedEventArgs e) => _viewModel?.CycleRepeat();

    private void OnRestart(object? sender, RoutedEventArgs e) => _viewModel?.RestartQueue();

    // ================= Układ =================

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyScale(e.NewSize);
    }

    /// <summary>
    /// Kadr 1920 × 1080 przeskalowany równomiernie tak, żeby zmieścił się na ekranie: na 16:9 wypełnia
    /// go cały, na ekranie szerszym stoi w środku, a na wyższym skala idzie od szerokości.
    /// </summary>
    private void ApplyScale(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;

        // Margines ekranu zmniejsza kadr, a wyjście i ramka dopasowania cofają się razem z nim od krawędzi.
        var margin = CurrentMargin;
        var inset = ScreenMargin.Fraction(margin);
        var scale = Math.Min(size.Width / FrameWidth, size.Height / FrameHeight) * ScreenMargin.ScaleFactor(margin);
        _scale.ScaleX = _scale.ScaleY = scale;
        _exitScale.ScaleX = _exitScale.ScaleY = scale;
        ExitStage.Margin = new Thickness(0, size.Height * inset, size.Width * inset, 0);
        EdgeFrame.Margin = new Thickness(size.Width * inset, size.Height * inset);
        Stage.InvalidateMeasure();
        ExitStage.InvalidateMeasure();
        SetOled(_oledX, _oledY);

        // Położenie wskaźnika liczy się w jednostkach kadru, więc po zmianie skali albo położenia
        // kadru stare nie nadaje się do progu ruchu.
        _chrome.ForgetPointer();
    }

    /// <summary>Klucz ekranu, na którym stoi scena; według niego zapamiętuje się margines.</summary>
    internal string? ScreenKey
    {
        get => _screenKey;
        set
        {
            if (_screenKey == value) return;

            _screenKey = value;
            ApplyScale(Bounds.Size);
        }
    }

    private string? _screenKey;

    /// <summary>Ekran, na który okno zaraz przejdzie; wysokość podaje rozmiar odczytu okładki.</summary>
    internal PixelRect? ScreenBounds { get; set; }

    private SceneMargin CurrentMargin => _viewModel?.GetSceneMargin(ScreenKey) ?? ScreenMargin.Default;

    /// <summary>Skala kadru: piksele ekranu (logiczne) na jednostkę wzoru.</summary>
    internal double Scale => _scale.ScaleY;

    // ================= Klawiatura =================

    /// <summary>
    /// Klawisze sceny, przed skrótami okna. Wynik <c>false</c> przepuszcza klawisz dalej: do
    /// przycisku z fokusem (Spacja, Enter, Tab w warstwie) albo do skrótów okna (Ctrl+O, Ctrl+,).
    /// </summary>
    internal bool HandleKey(KeyEventArgs e)
    {
        if (_viewModel is not { } player) return false;

        NoteInput();
        var handled = DispatchKey(e, player, out var toLayer);

        // Klawisz obsłużony przez scenę albo przekazany elementowi warstwy liczy licznik od nowa
        // i chowa kursor; sam modyfikator, skrót okna, klawisz spoza sceny i klawisz głośności nie.
        if (handled && e.Key is not (Key.VolumeUp or Key.VolumeDown or Key.VolumeMute) || toLayer)
            _chrome.KeyPressed();

        return handled;
    }

    /// <summary>Obsługa klawisza; <paramref name="toLayer"/> mówi, że klawisz idzie dalej do elementu warstwy.</summary>
    private bool DispatchKey(KeyEventArgs e, MainViewModel player, out bool toLayer)
    {
        var none = e.KeyModifiers == KeyModifiers.None;
        var control = e.KeyModifiers == KeyModifiers.Control;
        var keyboard = _chrome.IsShown && _chrome.HasKeyboardFocus && IsFocusInLayer();
        toLayer = false;

        // Tryb ramki jest najwyższą warstwą: strzałki zmieniają margines, Enter i Esc kończą tryb,
        // a scena zostaje otwarta.
        if (_edgeFit && none)
        {
            switch (e.Key)
            {
                case Key.Left or Key.Right:
                    StepMargin(e.Key == Key.Right ? 1 : -1);
                    return true;

                case Key.Enter or Key.Escape:
                    EndEdgeFit();
                    return true;
            }
        }

        if (IsQueueRowOpen && DispatchQueueRowKey(e, player)) return true;

        switch (e.Key)
        {
            case Key.F11 when none:
                ExitRequested?.Invoke();
                return true;

            // Esc i Backspace wychodzą ze sceny zawsze, także w regulacji paska i z fokusem w warstwie.
            case Key.Escape or Key.Back when none:
                ExitRequested?.Invoke();
                return true;

            case Key.Up when none:
                if (keyboard) MoveFocus(0, -1);
                else RevealWithFocus();
                return true;

            // Rząd wizualizacji dochodzi później; na razie strzałka w dół odsłania warstwę.
            case Key.Down when none:
                if (keyboard) MoveFocus(0, 1);
                else RevealWithFocus();
                return true;

            case Key.Left or Key.Right when none:
                var direction = e.Key == Key.Right ? 1 : -1;
                if (_chrome.IsAdjusting || !_chrome.IsShown) player.SeekRelative(direction * player.SeekStep);
                else if (keyboard) MoveFocus(direction, 0);
                else RevealWithFocus();
                return true;

            // Pasek bez regulacji nie przewija: Home i End dochodzą do niego dopiero po Enterze.
            case Key.Home or Key.End when none:
                toLayer = _chrome.IsAdjusting;
                return !toLayer;

            case Key.Right when control:
            case Key.PageDown when none:
                player.Next();
                return true;

            case Key.Left when control:
            case Key.PageUp when none:
                player.Previous();
                return true;

            case Key.OemPlus or Key.Add or Key.VolumeUp when none || e.KeyModifiers == KeyModifiers.Shift:
                player.AdjustVolume(0.05);
                return true;

            case Key.OemMinus or Key.Subtract or Key.VolumeDown when none:
                player.AdjustVolume(-0.05);
                return true;

            // Spacja z fokusem na przycisku warstwy go naciska; poza nim przełącza odtwarzanie.
            case Key.Space when none:
                toLayer = keyboard && FocusedElement() is Button;
                if (toLayer) return false;
                player.TogglePlay();
                return true;

            case Key.Enter when none:
                if (_chrome.IsAdjusting) _chrome.SetAdjusting(false);
                else if (keyboard && FocusedElement() == Seek) _chrome.SetAdjusting(true);
                else if (keyboard && FocusedElement() is Button)
                {
                    toLayer = true;
                    return false;
                }
                else RevealWithFocus();
                return true;

            case Key.Tab:
                toLayer = keyboard;
                if (toLayer) return false;
                RevealWithFocus();
                return true;

            // Jak M, S i R: wybór wizualizacji nie odsłania warstwy sterowania.
            case Key.V when none || e.KeyModifiers == KeyModifiers.Shift:
                player.CycleSceneVisual(e.KeyModifiers == KeyModifiers.Shift ? -1 : 1);
                ShowVisualCaption(player.SceneVisual);
                return true;

            case Key.M when none: player.ToggleMute(); return true;
            case Key.S when none:
                if (!player.IsRadioMode) player.ShuffleEnabled = !player.ShuffleEnabled;
                return true;

            case Key.R when none:
                if (!player.IsRadioMode) player.CycleRepeat();
                return true;

            case Key.Z when control:
                if (player.CanUndoQueue && !player.IsRadioMode) player.UndoQueue();
                return true;

            case Key.Q when none:
                OpenQueueRow();
                return true;

            // Pas dolny i kolejka okna nie przechodzą do sceny, a scena jest zawsze ciemna.
            case Key.L or Key.T when none:
                return true;

            // Przycisk naciska się Enterem i Spacją bez względu na modyfikatory; z modyfikatorem nie
            // dochodzą do niego.
            case Key.Enter or Key.Space:
                return true;

            default:
                return false;
        }
    }

    /// <summary>Spacja trafia do przycisku z fokusem z klawiatury w warstwie; inaczej puszczenie jest połykane.</summary>
    internal bool PassesSpaceToFocus => _chrome.HasKeyboardFocus && FocusedElement() is Button && IsFocusInLayer();

    /// <summary>
    /// Odsłania warstwę z fokusem z klawiatury: na elemencie warstwy, który ma fokus teraz (np. po
    /// kliknięciu), potem na tym, który miał go przy zgaszeniu warstwy, a gdy żaden nie może go
    /// przyjąć, na Odtwórz/Wstrzymaj.
    /// </summary>
    private void RevealWithFocus()
    {
        _chrome.RevealByKeyboard();

        foreach (var candidate in new[] { FocusedInLayer(), _lastFocused })
            if (candidate is { } element && IsInLayer(element) && FocusFromKeyboard(element)) return;

        FocusFromKeyboard(PlayButton);
    }

    /// <summary>
    /// Fokus z pierścieniem. Element, który ma już fokus z myszy, dostaje go od nowa przez korzeń
    /// sceny, bo ponowny fokus na tym samym elemencie nie pokazałby pierścienia.
    /// </summary>
    private bool FocusFromKeyboard(IInputElement element)
    {
        if (FocusedElement() == element) Focus();
        return element.Focus(NavigationMethod.Directional);
    }

    private IInputElement? FocusedElement() => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

    /// <summary>
    /// Ruch fokusu strzałkami po rzędach warstwy, bez zawijania. Przejście do rzędu transportu
    /// trafia na Odtwórz/Wstrzymaj.
    /// </summary>
    private void MoveFocus(int columns, int rows)
    {
        var grid = _focusRows;
        var focused = FocusedElement();

        for (var row = 0; row < grid.Length; row++)
        {
            var column = Array.IndexOf(grid[row], focused);
            if (column < 0) continue;

            Control target;
            if (rows != 0)
            {
                var next = Math.Clamp(row + rows, 0, grid.Length - 1);
                target = next == row ? grid[row][column]
                    : grid[next].Length == 1 ? grid[next][0]
                    : PlayButton;
            }
            else
            {
                target = grid[row][Math.Clamp(column + columns, 0, grid[row].Length - 1)];
            }

            target.Focus(NavigationMethod.Directional);
            return;
        }

        PlayButton.Focus(NavigationMethod.Directional);
    }
}
