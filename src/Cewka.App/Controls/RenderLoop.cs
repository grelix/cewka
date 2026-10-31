using Avalonia;
using Avalonia.Controls;

namespace Cewka.App.Controls;

/// <summary>
/// A per-frame callback driven by the compositor.
/// <para>
/// Avalonia's <c>Animation</c> cannot be paused and resumed at the same position through
/// public API, and the decorative pieces here (the spinning disc, the waveform) need exactly
/// that. <see cref="TopLevel.RequestAnimationFrame"/> is a one-shot, so the loop re-arms
/// itself until stopped.
/// </para>
/// <para>
/// Przy zminimalizowanym oknie pętla nie prosi o klatki, tylko czeka na zmianę stanu okna
/// i rusza sama, gdy okno wraca na ekran.
/// </para>
/// </summary>
public sealed class RenderLoop
{
    private readonly Visual _owner;
    private readonly Action<TimeSpan> _tick;
    private bool _running;
    private bool _armed;
    private Window? _parkedOn;

    public RenderLoop(Visual owner, Action<TimeSpan> tick)
    {
        _owner = owner;
        _tick = tick;
    }

    public bool IsRunning => _running;

    /// <summary>Pętla działa, ale czeka na przywrócenie zminimalizowanego okna.</summary>
    public bool IsParked => _parkedOn is not null;

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
        if (!_running || _armed || _parkedOn is not null) return;

        var topLevel = TopLevel.GetTopLevel(_owner);
        if (topLevel is null)
        {
            // Not attached to a window yet; the next attach will start the loop.
            _running = false;
            return;
        }

        if (topLevel is Window { WindowState: WindowState.Minimized } window)
        {
            _parkedOn = window;
            window.PropertyChanged += OnWindowPropertyChanged;
            return;
        }

        _armed = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty) return;
        if (_parkedOn is not { } window || window.WindowState == WindowState.Minimized) return;

        Unpark();
        Arm();
    }

    private void Unpark()
    {
        if (_parkedOn is null) return;

        _parkedOn.PropertyChanged -= OnWindowPropertyChanged;
        _parkedOn = null;
    }

    private void OnFrame(TimeSpan timestamp)
    {
        _armed = false;
        if (!_running) return;

        _tick(timestamp);
        Arm();
    }
}
