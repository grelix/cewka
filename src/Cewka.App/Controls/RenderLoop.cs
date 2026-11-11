using Avalonia;
using Avalonia.Controls;

namespace Cewka.App.Controls;

/// <summary>
/// Wywołanie na każdą klatkę kompozytora, bo <c>Animation</c> z Avalonii nie da się wstrzymać
/// i wznowić w tym samym miejscu. <see cref="TopLevel.RequestAnimationFrame"/> działa raz, więc
/// pętla zamawia kolejną klatkę sama.
/// <para>
/// Przy zminimalizowanym oknie albo niewidocznym właścicielu (np. okno przykryte sceną) pętla
/// czeka i rusza sama, gdy znów jest co pokazać. Tak samo czeka pętla w poddrzewie wstrzymanym
/// przez <see cref="IsSuspendedProperty"/>.
/// </para>
/// </summary>
public sealed class RenderLoop
{
    private readonly Visual _owner;
    private readonly Action<TimeSpan> _tick;
    private readonly Action? _shown;
    private bool _running;
    private bool _armed;
    private Window? _parkedOn;
    private TopLevel? _waitingForVisibility;
    private bool _waitingForAttach;

    /// <param name="shown">
    /// Wołane, gdy pętla rusza po czekaniu na odsłonięcie albo przywrócenie okna, przed pierwszą
    /// klatką: ruch, którego nikt nie widział, można wtedy od razu dokończyć.
    /// </param>
    public RenderLoop(Visual owner, Action<TimeSpan> tick, Action? shown = null)
    {
        _owner = owner;
        _tick = tick;
        _shown = shown;
    }

    /// <summary>
    /// Wstrzymuje pętle całego poddrzewa, choć jest widoczne: treść okna pod przenikającą sceną
    /// stoi, zanim scena ją zakryje.
    /// </summary>
    public static readonly AttachedProperty<bool> IsSuspendedProperty =
        AvaloniaProperty.RegisterAttached<RenderLoop, Visual, bool>("IsSuspended", inherits: true);

    public static bool GetIsSuspended(Visual visual) => visual.GetValue(IsSuspendedProperty);

    public static void SetIsSuspended(Visual visual, bool value) => visual.SetValue(IsSuspendedProperty, value);

    /// <summary>Każde zamówienie klatki, z właścicielem pętli; testy liczą nim pracę w pauzie.</summary>
    internal static event Action<Visual>? FrameRequested;

    public bool IsRunning => _running;

    /// <summary>Pętla działa, ale czeka na dołączenie do okna, przywrócenie okna albo odsłonięcie właściciela.</summary>
    public bool IsParked => _parkedOn is not null || _waitingForVisibility is not null || _waitingForAttach;

    /// <summary>Właściciela widać i nie jest wstrzymany; niewidocznego nikt nie ogląda w ruchu.</summary>
    public bool IsOwnerShown => _owner.IsEffectivelyVisible && !GetIsSuspended(_owner);

    public void Start()
    {
        if (_running) return;
        _running = true;
        Arm();
    }

    public void Stop()
    {
        _running = false;
        Unpark();
    }

    private void Arm()
    {
        if (!_running || _armed || IsParked) return;

        var topLevel = TopLevel.GetTopLevel(_owner);
        if (topLevel is null)
        {
            // Jeszcze poza oknem: np. zawartość ukrytej sceny, którą LayoutTransformControl dołącza
            // dopiero przy pierwszym układzie. Pętla rusza sama po dołączeniu.
            _waitingForAttach = true;
            _owner.AttachedToVisualTree += OnOwnerAttached;
            return;
        }

        if (topLevel is Window { WindowState: WindowState.Minimized } window)
        {
            _parkedOn = window;
            window.PropertyChanged += OnWindowPropertyChanged;
            return;
        }

        // Odsłonięcie zmienia układ, więc na powrót wystarczy czekać na przebieg układu, a nie
        // zamawiać klatek, których nikt nie zobaczy. Zdjęcie wstrzymania układu nie zmienia.
        if (!IsOwnerShown)
        {
            _waitingForVisibility = topLevel;
            topLevel.LayoutUpdated += OnLayoutUpdated;
            _owner.PropertyChanged += OnOwnerPropertyChanged;
            return;
        }

        _armed = true;
        FrameRequested?.Invoke(_owner);
        FrameHub.For(topLevel).Add(this);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;
        if (_parkedOn is not { } window || window.WindowState == WindowState.Minimized) return;

        Resume();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsOwnerShown) return;

        Resume();
    }

    private void OnOwnerAttached(object? sender, VisualTreeAttachmentEventArgs e) => Resume();

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsSuspendedProperty || !IsOwnerShown) return;

        Resume();
    }

    private void Resume()
    {
        Unpark();
        _shown?.Invoke();
        Arm();
    }

    private void Unpark()
    {
        if (_parkedOn is not null)
        {
            _parkedOn.PropertyChanged -= OnWindowPropertyChanged;
            _parkedOn = null;
        }

        if (_waitingForVisibility is not null)
        {
            _waitingForVisibility.LayoutUpdated -= OnLayoutUpdated;
            _owner.PropertyChanged -= OnOwnerPropertyChanged;
            _waitingForVisibility = null;
        }

        if (_waitingForAttach)
        {
            _owner.AttachedToVisualTree -= OnOwnerAttached;
            _waitingForAttach = false;
        }
    }

    internal void Fire(TimeSpan timestamp)
    {
        _armed = false;
        if (!_running) return;

        _tick(timestamp);
        Arm();
    }

    /// <summary>
    /// Jedno zamówienie klatki na okno dla wszystkich pętli: tło, płyta, wizualizacje i fala
    /// dostają ten sam znacznik czasu w jednym wywołaniu, zamiast osobnych zamówień. Listy są
    /// wielokrotnego użytku, więc klatka niczego nie alokuje.
    /// </summary>
    internal sealed class FrameHub
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TopLevel, FrameHub> Hubs = new();

        private readonly TopLevel _topLevel;
        private readonly Action<TimeSpan> _onFrame;
        private List<RenderLoop> _pending = [];
        private List<RenderLoop> _batch = [];
        private bool _requested;

        private FrameHub(TopLevel topLevel)
        {
            _topLevel = topLevel;
            _onFrame = Run;
        }

        /// <summary>Ile razy zamówiono klatkę okna; testy porównują to z liczbą zamówień pętli.</summary>
        internal int Requests { get; private set; }

        internal static FrameHub For(TopLevel topLevel) => Hubs.GetValue(topLevel, static t => new FrameHub(t));

        internal void Add(RenderLoop loop)
        {
            _pending.Add(loop);
            if (_requested) return;

            _requested = true;
            Requests++;
            _topLevel.RequestAnimationFrame(_onFrame);
        }

        private void Run(TimeSpan timestamp)
        {
            _requested = false;

            // Pętle ponownie zamawiające klatkę trafiają do nowej listy i do następnej klatki.
            (_pending, _batch) = (_batch, _pending);
            try
            {
                for (var i = 0; i < _batch.Count; i++) _batch[i].Fire(timestamp);
            }
            finally
            {
                _batch.Clear();
            }
        }
    }
}
