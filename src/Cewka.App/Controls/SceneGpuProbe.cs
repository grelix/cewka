using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;

namespace Cewka.App.Controls;

/// <summary>
/// Jednopikselowa sonda, która przy pierwszym rysowaniu sceny sprawdza, czy Skia ma kontekst GPU
/// (<c>GrContext</c> z dzierżawy). Niczego nie rysuje. Wynik trafia do zdarzenia na wątku interfejsu,
/// raz; potem właściciel chowa sondę.
/// </summary>
public sealed class SceneGpuProbe : Control
{
    private int _reported;

    public SceneGpuProbe()
    {
        IsHitTestVisible = false;
        Width = 1;
        Height = 1;
    }

    /// <summary>Zgłaszane raz na wątku interfejsu: true, gdy jest kontekst GPU.</summary>
    public event Action<bool>? Detected;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Volatile.Read(ref _reported) == 0) context.Custom(new ProbeOperation(this));
    }

    private void Report(bool gpu)
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0) return;

        Dispatcher.UIThread.Post(() => Detected?.Invoke(gpu));
    }

    private sealed class ProbeOperation(SceneGpuProbe owner) : ICustomDrawOperation
    {
        public Rect Bounds => new(0, 0, 1, 1);

        public void Dispose()
        {
        }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point point) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                owner.Report(false);
                return;
            }

            using var lease = feature.Lease();
            owner.Report(lease.GrContext is not null);
        }
    }
}
