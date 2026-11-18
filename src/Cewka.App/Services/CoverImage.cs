using System.Buffers.Binary;
using Cewka.App.Radio;

namespace Cewka.App.Services;

/// <summary>
/// Pułap wymiarów okładki z pliku, sprawdzany przed dekodowaniem. Limit bajtów nie wystarcza:
/// jednolity obraz 30000×30000 mieści się w kilkuset kilobajtach, a dekoder zajmuje pamięć według
/// wymiarów. Dekodowanie do zadanej szerokości powiększa też obraz o skrajnych proporcjach.
/// </summary>
internal static class CoverImage
{
    public const int MaxSide = 8192;

    public const long MaxPixels = 64_000_000;

    /// <summary>Wymiary z nagłówka, gdy mieszczą się w pułapie; null, gdy są nieznane albo za duże.</summary>
    public static (int Width, int Height)? Size(byte[] encoded)
    {
        var format = LogoSniffer.Detect(encoded);

        var size = format is LogoFormat.Png or LogoFormat.Jpeg or LogoFormat.Gif or LogoFormat.WebP
            ? LogoSniffer.Dimensions(encoded, format)
            : BitmapSize(encoded);

        return size is { } known && Fits(known.Width, known.Height) ? known : null;
    }

    /// <summary>Wymiary z nagłówka BMP, który w znacznikach zdarza się rzadko, ale bywa.</summary>
    private static (int Width, int Height)? BitmapSize(ReadOnlySpan<byte> d)
    {
        if (d.Length < 26 || d[0] != 'B' || d[1] != 'M') return null;

        var width = BinaryPrimitives.ReadInt32LittleEndian(d[18..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(d[22..]);

        // Ujemna wysokość oznacza wiersze od góry; int.MinValue nie ma wartości bezwzględnej.
        return width > 0 && height != 0 && height != int.MinValue ? (width, Math.Abs(height)) : null;
    }

    /// <summary>Obraz w pułapie, który po zdekodowaniu do tej szerokości także się w nim mieści.</summary>
    public static bool FitsAtWidth(byte[] encoded, int width) =>
        Size(encoded) is { } size && Fits(width, Scaled(size.Height, width, size.Width));

    /// <summary>Obraz w pułapie, który po zdekodowaniu do tej wysokości także się w nim mieści.</summary>
    public static bool FitsAtHeight(byte[] encoded, int height) =>
        Size(encoded) is { } size && Fits(Scaled(size.Width, height, size.Height), height);

    private static bool Fits(long width, long height) =>
        width is > 0 and <= MaxSide && height is > 0 and <= MaxSide && width * height <= MaxPixels;

    private static long Scaled(int side, int target, int reference) =>
        (long)Math.Ceiling((double)side * target / reference);
}
