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
/// </summary>
public sealed class RenderLoop
{
    private readonly Visual _owner;
    private readonly Action<TimeSpan> _tick;
    private bool _running;
    private bool _armed;

    public RenderLoop(Visual owner, Action<TimeSpan> tick)
    {
        _owner = owner;
        _tick = tick;
    }

    public bool IsRunning => _running;

    public void Start()
    {
        if (_running) return;
        _running = true;
        Arm();
    }

    public void Stop() => _running = false;

    private void Arm()
    {
        if (!_running || _armed) return;

        var topLevel = TopLevel.GetTopLevel(_owner);
        if (topLevel is null)
        {
            // Not attached to a window yet; the next attach will start the loop.
            _running = false;
            return;
        }

        _armed = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan timestamp)
    {
        _armed = false;
        if (!_running) return;

        _tick(timestamp);
        Arm();
    }
}
