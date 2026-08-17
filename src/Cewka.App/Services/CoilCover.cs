using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cewka.App.Models;

namespace Cewka.App.Services;

/// <summary>
/// Rysuje domyślną okładkę: cewkę, od której program wziął nazwę. Używana dla plików,
/// które nie mają własnej okładki.
///
/// <para><b>Dlaczego rysowana, a nie wczytywana z pliku.</b> Wcześniej były to dwa obrazy PNG,
/// po jednym na motyw, w jednej barwie. Pięć par barw znaczyłoby dziesięć plików ważących
/// razem blisko megabajt, a przejście barwy wzdłuż zwoju trzeba by i tak było wygenerować
/// programem. Rysowanie na miejscu daje wszystkie warianty za darmo, waży zero i pozwala
/// dobrać jasność barw do motywu, zamiast utrwalać ją w pliku.</para>
/// </summary>
public static class CoilCover
{
    /// <summary>Bok obrazu w pikselach. Tyle miały pliki, które to zastępuje.</summary>
    private const int Size = 640;

    /// <summary>Liczba zwojów. Tyle widać na okładce z poprzednich wydań.</summary>
    private const double Turns = 4.0;

    /// <summary>
    /// Na tyle odcinków dzielony jest zwój. Każdy dostaje własną barwę, więc odcinków musi być
    /// dość, żeby przejście było gładkie — przy zaokrąglonych końcach nie widać wtedy styków.
    /// </summary>
    private const int Segments = 320;

    /// <summary>Promień początkowy i końcowy zwoju, w częściach połowy boku.</summary>
    private const double InnerRadius = 0.10;
    private const double OuterRadius = 0.70;

    /// <summary>
    /// Barwy zwoju: środek, połowa drogi, koniec. Podane w jasności odpowiedniej dla motywu
    /// ciemnego; dla jasnego są przyciemniane.
    ///
    /// <para><b>Dlaczego trzy barwy, a nie dwie.</b> Przy dwóch barwa środkowa wypada z ich
    /// wymieszania, a to działa tylko wtedy, gdy obie leżą blisko siebie na kole barw. Para
    /// turkus i róż leży prawie po przeciwnych stronach: prosta między nimi — w sRGB czy
    /// w Oklabie, bez różnicy — przechodzi obok osi bezbarwnej i połowa zwoju wychodziła szara.
    /// Wybór drogi po kole barw też nie rozwiązuje sprawy, bo najkrótszy łuk z turkusu do różu
    /// wiedzie przez zieleń i żółć, których w tej parze nie ma. Środek podany wprost zamienia
    /// przypadek na decyzję: turkus dochodzi do różu przez fiolet, czyli tak, jak wygląda ta
    /// para wszędzie tam, gdzie się jej używa.</para>
    /// </summary>
    /// <summary>
    /// Pary o ustalonych barwach, bez pozycji „losowo". Kolejność jest kolejnością na liście
    /// w ustawieniach i zarazem zbiorem, z którego losuje <see cref="Resolve"/>.
    /// </summary>
    public static readonly PlaceholderPalette[] Fixed =
    [
        PlaceholderPalette.BlueViolet,
        PlaceholderPalette.Turquoise,
        PlaceholderPalette.Amber,
        PlaceholderPalette.Lime,
        PlaceholderPalette.Graphite,
    ];

    /// <summary>
    /// Zamienia wybór na konkretną parę. Przy ustawieniu losowym wskazuje jedną z pozostałych —
    /// za każdym wywołaniem inną, więc ten sam plik wczytany ponownie dostanie inne barwy.
    /// </summary>
    public static PlaceholderPalette Resolve(PlaceholderPalette palette) =>
        palette == PlaceholderPalette.Random
            ? Fixed[System.Random.Shared.Next(Fixed.Length)]
            : palette;

    private static (Color From, Color Mid, Color To) Ramp(PlaceholderPalette palette) => palette switch
    {
        PlaceholderPalette.Turquoise =>
            (Color.FromRgb(0x2E, 0xC4, 0xB6), Color.FromRgb(0x7C, 0x6B, 0xE0), Color.FromRgb(0xFF, 0x6B, 0x9D)),

        PlaceholderPalette.Amber =>
            (Color.FromRgb(0xFF, 0xB7, 0x03), Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xE5, 0x38, 0x3B)),

        PlaceholderPalette.Lime =>
            (Color.FromRgb(0xA3, 0xE6, 0x35), Color.FromRgb(0x3F, 0xBF, 0x6A), Color.FromRgb(0x05, 0x96, 0x69)),

        PlaceholderPalette.Graphite =>
            (Color.FromRgb(0x8B, 0x9A, 0xB4), Color.FromRgb(0xB0, 0x8C, 0x8B), Color.FromRgb(0xD9, 0x77, 0x57)),

        _ => (Color.FromRgb(0x4E, 0xA8, 0xDE), Color.FromRgb(0x64, 0x84, 0xE2), Color.FromRgb(0x7B, 0x5C, 0xD6)),
    };

    /// <summary>Barwy podłoża i środkowej kropki, po jednej parze na motyw.</summary>
    private static (Color Near, Color Far, Color Hole) Ground(bool darkTheme) => darkTheme
        ? (Color.FromRgb(0x1C, 0x1C, 0x20), Color.FromRgb(0x0B, 0x0B, 0x0D), Color.FromRgb(0xF5, 0xF5, 0xF8))
        : (Color.FromRgb(0xFA, 0xFA, 0xFC), Color.FromRgb(0xE2, 0xE2, 0xE7), Color.FromRgb(0x1A, 0x1A, 0x1E));

    /// <summary>
    /// Trzy barwy zwoju w postaci, w jakiej faktycznie zostaną narysowane dla danego motywu.
    /// Okno ustawień rysuje z nich próbkę — próbka pokazująca inne barwy niż te, które wyjdą,
    /// byłaby myląca akurat tam, gdzie wybiera się je wzrokiem.
    /// </summary>
    public static Color[] RampColours(PlaceholderPalette palette, bool darkTheme)
    {
        // Pozycja „losowo" nie ma własnych barw, a próbka losowana za każdym odświeżeniem
        // migotałaby w oknie ustawień. Zamiast tego pokazuje po jednej barwie z każdej pary,
        // czyli dokładnie to, z czego losuje.
        if (palette == PlaceholderPalette.Random)
        {
            return Fixed
                .Select(entry => ForTheme(Ramp(entry), darkTheme).Mid)
                .ToArray();
        }

        var ramp = ForTheme(Ramp(palette), darkTheme);
        return [ramp.From, ramp.Mid, ramp.To];
    }

    private static (Color From, Color Mid, Color To) ForTheme(
        (Color From, Color Mid, Color To) ramp, bool darkTheme) => darkTheme
        ? ramp
        : (ForLightTheme(ramp.From), ForLightTheme(ramp.Mid), ForLightTheme(ramp.To));

    /// <summary>
    /// Zwraca gotową okładkę. Wołane na wątku interfejsu — <see cref="RenderTargetBitmap"/>
    /// wymaga działającego środowiska graficznego.
    /// </summary>
    public static Bitmap Render(PlaceholderPalette palette, bool darkTheme)
    {
        var ramp = ForTheme(Ramp(Resolve(palette)), darkTheme);
        var ground = Ground(darkTheme);

        var target = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));

        using (var context = target.CreateDrawingContext())
        {
            DrawGround(context, ground.Near, ground.Far);
            DrawCoil(context, ramp);
            DrawHole(context, ground.Hole);
        }

        return target;
    }

    private static void DrawGround(DrawingContext context, Color near, Color far)
    {
        // Przejście po skosie, nie promieniste: promieniste tło z jaśniejszym środkiem
        // konkurowałoby ze zwojem, który też zaczyna się w środku.
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(near, 0),
                new GradientStop(far, 1),
            },
        };

        context.DrawRectangle(brush, null, new Rect(0, 0, Size, Size));
    }

    private static void DrawCoil(DrawingContext context, (Color From, Color Mid, Color To) ramp)
    {
        var centre = new Point(Size / 2.0, Size / 2.0);
        var half = Size / 2.0;
        var inner = half * InnerRadius;
        var outer = half * OuterRadius;

        var thickness = Size / 44.0;
        var totalAngle = Turns * 2 * Math.PI;

        var previous = PointOn(centre, inner, 0);

        for (var i = 1; i <= Segments; i++)
        {
            var t = (double)i / Segments;
            var angle = t * totalAngle;
            var radius = inner + (outer - inner) * t;
            var point = PointOn(centre, radius, angle);

            // Barwa odcinka brana z połowy jego długości — inaczej pierwszy odcinek miałby
            // barwę końcową poprzedniego, którego nie ma, i zwój zaczynałby się skokiem.
            var colour = Along(ramp, t - 0.5 / Segments);

            // Zaokrąglone końce zamiast styków na płasko: każdy odcinek dokłada wtedy pół
            // grubości z każdej strony i sąsiedzi zachodzą na siebie, nie zostawiając szczelin.
            var pen = new Pen(new SolidColorBrush(colour), thickness, lineCap: PenLineCap.Round);
            context.DrawLine(pen, previous, point);

            previous = point;
        }
    }

    private static void DrawHole(DrawingContext context, Color colour)
    {
        var centre = new Point(Size / 2.0, Size / 2.0);
        context.DrawEllipse(new SolidColorBrush(colour), null, centre, Size / 26.0, Size / 26.0);
    }

    private static Point PointOn(Point centre, double radius, double angle) => new(
        centre.X + radius * Math.Cos(angle),
        centre.Y + radius * Math.Sin(angle));

    /// <summary>
    /// Przyciemnia barwę na tyle, żeby czytała się na jasnym podłożu, zachowując jej odcień.
    /// Jasność ustawiana jest w Oklabie, więc ta sama operacja nie zmienia barwy na inną —
    /// w HSL obniżenie jasności żółtego daje brud, a błękitu prawie nic.
    /// </summary>
    private static Color ForLightTheme(Color colour)
    {
        var lab = Oklab.FromColor(colour);
        return Oklab.ToColor((Math.Min(lab.L, 0.62) * 0.78, lab.A, lab.B));
    }

    /// <summary>
    /// Barwa w danym miejscu zwoju: pierwsza połowa od początku do środka, druga od środka
    /// do końca.
    /// </summary>
    private static Color Along((Color From, Color Mid, Color To) ramp, double t)
    {
        t = Math.Clamp(t, 0, 1);

        return t < 0.5
            ? Mix(ramp.From, ramp.Mid, t * 2)
            : Mix(ramp.Mid, ramp.To, (t - 0.5) * 2);
    }

    /// <summary>
    /// Miesza dwie barwy w Oklabie, nie w sRGB.
    ///
    /// <para>Odcinki między podanymi punktami są krótkie, więc nie chodzi tu o omijanie osi
    /// bezbarwnej — tym zajmuje się środkowy punkt z <see cref="Ramp"/>. Oklab wybrano dlatego,
    /// że jego jasność odpowiada jasności widzianej: w sRGB przejście od jasnej limonki do
    /// ciemnego szmaragdu ciemnieje najpierw wolno, a potem gwałtownie, i widać, gdzie leży
    /// punkt podziału. W Oklabie ten sam zakres ciemnieje równomiernie i podziału nie widać.</para>
    /// </summary>
    private static Color Mix(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);

        var a = Oklab.FromColor(from);
        var b = Oklab.FromColor(to);

        return Oklab.ToColor((
            a.L + (b.L - a.L) * t,
            a.A + (b.A - a.A) * t,
            a.B + (b.B - a.B) * t));
    }

    /// <summary>
    /// Przeliczenie sRGB ↔ Oklab. Współczynniki z opisu przestrzeni Oklab (Björn Ottosson).
    /// </summary>
    private static class Oklab
    {
        public static (double L, double A, double B) FromColor(Color colour)
        {
            var r = ToLinear(colour.R / 255.0);
            var g = ToLinear(colour.G / 255.0);
            var b = ToLinear(colour.B / 255.0);

            var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
            var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
            var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);

            return (
                0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }

        public static Color ToColor((double L, double A, double B) lab)
        {
            var l = lab.L + 0.3963377774 * lab.A + 0.2158037573 * lab.B;
            var m = lab.L - 0.1055613458 * lab.A - 0.0638541728 * lab.B;
            var s = lab.L - 0.0894841775 * lab.A - 1.2914855480 * lab.B;

            l = l * l * l;
            m = m * m * m;
            s = s * s * s;

            var r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
            var g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
            var b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

            return Color.FromRgb(ToByte(r), ToByte(g), ToByte(b));
        }

        private static double ToLinear(double c) =>
            c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

        private static byte ToByte(double linear)
        {
            var srgb = linear <= 0.0031308
                ? linear * 12.92
                : 1.055 * Math.Pow(Math.Max(linear, 0), 1.0 / 2.4) - 0.055;

            return (byte)Math.Clamp(Math.Round(srgb * 255), 0, 255);
        }
    }
}
