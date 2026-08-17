using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Cewka.Snapshots;

/// <summary>
/// Zestawia świeżo wyrenderowane zrzuty z obrazami odniesienia i mówi, czy wygląd się zmienił.
///
/// <para>Zrzuty są powtarzalne — obrót płyty, dryf plam tła i faza fali są przy fotografowaniu
/// zatrzymywane — ale powtarzalne nie znaczy identyczne co do bajtu. Dlatego porównanie liczy
/// piksele różniące się bardziej niż o próg i podaje, jak duży obszar zajmują: przesunięcie
/// układu daje różnicę rozlaną po całym obrazie, a resztkowy ruch pojedynczego elementu mieści
/// się w małym prostokącie.</para>
///
/// <para>Porównywane są wybrane zrzuty, nie wszystkie. Komplet odniesienia dla kilkudziesięciu
/// obrazów rósłby w historii repozytorium przy każdej zmianie wyglądu, a te pięć pokrywa
/// układ okna głównego w obu motywach, stan zwinięty oraz dwie najbogatsze zakładki ustawień.</para>
/// </summary>
internal static class Odniesienie
{
    public static readonly string[] Sprawdzane =
    [
        "ciemny-1180",
        "jasny-1180",
        "ciemny-zwiniety",
        "ustawienia-wyglad-cala",
        "ustawienia-jezyk",
    ];

    /// <summary>
    /// O tyle może różnić się pojedynczy kanał barwy, żeby piksel nadal uchodził za ten sam.
    /// Poniżej tego progu leży zaokrąglanie rasteryzacji, a nie zmiana w programie.
    /// </summary>
    private const int ProgKanalu = 8;

    /// <summary>
    /// Ile pikseli może się różnić, zanim przebieg zostanie uznany za zmianę wyglądu.
    ///
    /// <para>Dwa przebiegi na tym samym kodzie dają dziś obrazy identyczne co do piksela, więc
    /// zapas nie jest tu potrzebny do niczego, co widać. Jest natomiast potrzebny na maszynę
    /// w chmurze, której obraz systemu bywa podmieniany: drobna zmiana w rasteryzacji nie
    /// powinna zatrzymywać budowy. Zanim wymuszono odświeżanie modelu widoku, wędrował uchwyt
    /// suwaka postępu i różnice sięgały 42 pikseli w prostokącie 14 na 14 — stąd rząd wielkości
    /// obu progów.</para>
    /// </summary>
    private const int BudzetPikseli = 200;

    /// <summary>
    /// Największy dopuszczalny bok prostokąta obejmującego wszystkie różnice. Osobne kryterium
    /// obok budżetu, bo dwieście pikseli rozsypanych po całym oknie znaczy co innego niż
    /// dwieście pikseli skupionych w jednym miejscu — pierwsze to przesunięty układ.
    /// </summary>
    private const int MaksymalnyBok = 24;

    /// <summary>
    /// Zwraca <c>true</c>, gdy wygląd zgadza się z odniesieniem. Brak pliku odniesienia nie jest
    /// błędem: obraz zostaje zapisany, a przebieg mówi wprost, że powstało nowe odniesienie —
    /// inaczej pierwsze uruchomienie zawsze kończyłoby się porażką.
    /// </summary>
    public static bool Porownaj(string katalogZrzutow, string katalogOdniesienia, string katalogRoznic)
    {
        Directory.CreateDirectory(katalogOdniesienia);

        var zgodne = true;
        var utworzone = 0;

        Console.WriteLine();
        Console.WriteLine($"Porownanie z odniesieniem: {katalogOdniesienia}");

        foreach (var nazwa in Sprawdzane)
        {
            var swiezy = Path.Combine(katalogZrzutow, nazwa + ".png");
            var wzorzec = Path.Combine(katalogOdniesienia, nazwa + ".png");

            if (!File.Exists(swiezy))
            {
                Console.Error.WriteLine($"  BLAD {nazwa}: nie ma swiezego zrzutu {swiezy}");
                zgodne = false;
                continue;
            }

            if (!File.Exists(wzorzec))
            {
                File.Copy(swiezy, wzorzec);
                Console.WriteLine($"  nowe {nazwa}: nie bylo odniesienia, zapisano biezacy obraz");
                utworzone++;
                continue;
            }

            zgodne &= PorownajJeden(nazwa, swiezy, wzorzec, katalogRoznic);
        }

        if (utworzone > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Powstalo {utworzone} nowych obrazow odniesienia. Nalezy je przejrzec " +
                              "i dodac do repozytorium — dopiero wtedy porownanie cokolwiek pilnuje.");
        }

        return zgodne;
    }

    private static bool PorownajJeden(string nazwa, string swiezy, string wzorzec, string katalogRoznic)
    {
        var a = Wczytaj(wzorzec);
        var b = Wczytaj(swiezy);

        if (a.Szerokosc != b.Szerokosc || a.Wysokosc != b.Wysokosc)
        {
            Console.Error.WriteLine(
                $"  BLAD {nazwa}: rozmiar {b.Szerokosc}x{b.Wysokosc}, a odniesienie ma " +
                $"{a.Szerokosc}x{a.Wysokosc}");
            return false;
        }

        var rozne = 0;
        var najwiekszaRoznica = 0;
        int lewo = int.MaxValue, gora = int.MaxValue, prawo = -1, dol = -1;

        for (var y = 0; y < a.Wysokosc; y++)
        {
            var wiersz = y * a.Szerokosc * 4;

            for (var x = 0; x < a.Szerokosc; x++)
            {
                var p = wiersz + x * 4;

                // Kanał przezroczystości pomijany: zrzuty są nieprzezroczyste, a jego wartość
                // bywa różna w zależności od tego, czym obraz przeszedł przez kodek PNG.
                var roznica = Math.Max(
                    Math.Abs(a.Piksele[p] - b.Piksele[p]),
                    Math.Max(
                        Math.Abs(a.Piksele[p + 1] - b.Piksele[p + 1]),
                        Math.Abs(a.Piksele[p + 2] - b.Piksele[p + 2])));

                if (roznica > najwiekszaRoznica) najwiekszaRoznica = roznica;
                if (roznica <= ProgKanalu) continue;

                rozne++;
                if (x < lewo) lewo = x;
                if (x > prawo) prawo = x;
                if (y < gora) gora = y;
                if (y > dol) dol = y;
            }
        }

        if (rozne == 0)
        {
            Console.WriteLine($"  ok   {nazwa}: identyczny z odniesieniem");
            return true;
        }

        var szerokoscObszaru = prawo - lewo + 1;
        var wysokoscObszaru = dol - gora + 1;
        var bok = Math.Max(szerokoscObszaru, wysokoscObszaru);

        var opis = $"{rozne} px, obszar {szerokoscObszaru}x{wysokoscObszaru} od ({lewo},{gora}), " +
                   $"najwieksza roznica kanalu {najwiekszaRoznica}";

        if (rozne <= BudzetPikseli && bok <= MaksymalnyBok)
        {
            Console.WriteLine($"  ok   {nazwa}: {opis} — w granicach tolerancji");
            return true;
        }

        Console.Error.WriteLine($"  BLAD {nazwa}: wyglad sie zmienil — {opis}");
        ZapiszRoznice(Path.Combine(katalogRoznic, nazwa + "-roznica.png"), a, b);
        return false;
    }

    private readonly record struct Obraz(int Szerokosc, int Wysokosc, byte[] Piksele);

    private static Obraz Wczytaj(string sciezka)
    {
        using var bitmapa = new Bitmap(sciezka);

        var szerokosc = bitmapa.PixelSize.Width;
        var wysokosc = bitmapa.PixelSize.Height;
        var krok = szerokosc * 4;
        var piksele = new byte[krok * wysokosc];

        var uchwyt = GCHandle.Alloc(piksele, GCHandleType.Pinned);
        try
        {
            bitmapa.CopyPixels(
                new PixelRect(0, 0, szerokosc, wysokosc), uchwyt.AddrOfPinnedObject(), piksele.Length, krok);
        }
        finally
        {
            uchwyt.Free();
        }

        return new Obraz(szerokosc, wysokosc, piksele);
    }

    /// <summary>
    /// Obraz różnicowy: tło z przygaszonego odniesienia, żeby było wiadomo, na co się patrzy,
    /// a różnice na czerwono. Sam prostokąt współrzędnych w dzienniku nie mówi, co się stało.
    /// </summary>
    private static void ZapiszRoznice(string sciezka, Obraz wzorzec, Obraz swiezy)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sciezka)!);

        using var mapa = new WriteableBitmap(
            new PixelSize(wzorzec.Szerokosc, wzorzec.Wysokosc),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using (var kadr = mapa.Lock())
        {
            var krok = wzorzec.Szerokosc * 4;
            var wiersz = new byte[krok];

            for (var y = 0; y < wzorzec.Wysokosc; y++)
            {
                var poczatek = y * krok;

                for (var x = 0; x < wzorzec.Szerokosc; x++)
                {
                    var p = poczatek + x * 4;

                    var roznica = Math.Max(
                        Math.Abs(wzorzec.Piksele[p] - swiezy.Piksele[p]),
                        Math.Max(
                            Math.Abs(wzorzec.Piksele[p + 1] - swiezy.Piksele[p + 1]),
                            Math.Abs(wzorzec.Piksele[p + 2] - swiezy.Piksele[p + 2])));

                    var d = x * 4;

                    if (roznica > ProgKanalu)
                    {
                        wiersz[d] = 0;
                        wiersz[d + 1] = 0;
                        wiersz[d + 2] = 255;
                    }
                    else
                    {
                        wiersz[d] = (byte)(wzorzec.Piksele[p] / 3);
                        wiersz[d + 1] = (byte)(wzorzec.Piksele[p + 1] / 3);
                        wiersz[d + 2] = (byte)(wzorzec.Piksele[p + 2] / 3);
                    }

                    wiersz[d + 3] = 255;
                }

                Marshal.Copy(wiersz, 0, kadr.Address + y * kadr.RowBytes, krok);
            }
        }

        mapa.Save(sciezka, new PngBitmapEncoderOptions());
        Console.Error.WriteLine($"       obraz roznicowy: {sciezka}");
    }
}
