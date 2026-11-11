using SkiaSharp;

namespace Cewka.App.Controls;

/// <summary>Parametry jednej klatki Zorzy, gotowe do narysowania na wątku renderowania.</summary>
public readonly record struct AuroraFrame(
    float Time, float Low, float Mid, float High, float Level, SKColor Colour1, SKColor Colour2, float Brightness,
    float Ceiling = AuroraRenderer.DefaultCeiling);

/// <summary>
/// Bufor wewnętrzny Zorzy: powierzchnia o stałym rozmiarze (zwykle 960 x 540), do której rysuje shader,
/// i skalowanie jej na cel z filtrowaniem liniowym. Powierzchnia żyje między klatkami; powstaje od nowa
/// tylko przy zmianie rozmiaru albo kontekstu GPU. Używany wyłącznie z jednego wątku (renderowania).
/// </summary>
public sealed class AuroraBuffer
{
    /// <summary>Rozmiar bufora przy pełnej jakości.</summary>
    public const int Width = 960;

    public const int Height = 540;

    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear);

    private readonly AuroraRenderer _renderer = new();
    private SKSurface? _surface;
    private GRContext? _context;
    private int _width;
    private int _height;
    private int _rebuilds;

    /// <summary>Ile razy powstała powierzchnia; w ruchu nie rośnie.</summary>
    public int Rebuilds => _rebuilds;

    /// <summary>Rozmiar bufora dla mnożnika rozdzielczości z <see cref="Services.FrameBudget"/>.</summary>
    public static (int Width, int Height) SizeFor(double scale) =>
        (Math.Max(2, (int)Math.Round(Width * scale / 2) * 2), Math.Max(2, (int)Math.Round(Height * scale / 2) * 2));

    /// <summary>Rysuje klatkę w buforze i skaluje ją na (0, 0, szerokość celu, wysokość celu).</summary>
    public void Present(SKCanvas target, GRContext? context, in AuroraFrame frame, float targetWidth, float targetHeight,
        int bufferWidth, int bufferHeight)
    {
        Ensure(context, bufferWidth, bufferHeight);
        if (_surface is null) return;

        _renderer.Ceiling = frame.Ceiling;
        _renderer.Draw(_surface.Canvas, bufferWidth, bufferHeight, frame.Time, frame.Low, frame.Mid, frame.High,
            frame.Level, frame.Colour1, frame.Colour2, frame.Brightness);

        // Zdjęcie powierzchni to uchwyt na jej zawartość, bez kopiowania pikseli na GPU.
        using var image = _surface.Snapshot();
        var count = target.Save();
        target.ClipRect(new SKRect(0, 0, targetWidth, targetHeight));
        target.DrawImage(image, new SKRect(0, 0, bufferWidth, bufferHeight), new SKRect(0, 0, targetWidth, targetHeight), Linear, null);
        target.RestoreToCount(count);
    }

    private void Ensure(GRContext? context, int width, int height)
    {
        if (_surface is not null && _width == width && _height == height && ReferenceEquals(_context, context)) return;

        _surface?.Dispose();
        _surface = null;
        _context = context;
        _width = width;
        _height = height;
        _rebuilds++;

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        _surface = context is null ? SKSurface.Create(info) : SKSurface.Create(context, true, info);
    }
}
