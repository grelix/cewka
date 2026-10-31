namespace Cewka.Snapshots;

/// <summary>
/// Wytwarza materiał dźwiękowy dla zrzutów.
///
/// <para>W repozytorium nie ma plików muzycznych, więc materiał powstaje na miejscu i musi być
/// zawsze taki sam. Sygnał liczony jest z funkcji trygonometrycznych, bez losowania i zegara,
/// więc dwa wywołania dają plik identyczny co do bajtu.</para>
/// </summary>
internal static class Material
{
    private const int Czestotliwosc = 44100;
    private const short Kanaly = 1;
    private const short BitowNaProbke = 16;

    /// <summary>
    /// Dłuższy plik musi przekraczać miejsce, w którym fotografowany jest utwór (79 s), bo zrzuty
    /// przewijają się w głąb utworu; przewinięcie poza koniec pokazałoby pusty pasek postępu.
    /// </summary>
    private const int SekundDluga = 143;

    /// <summary>
    /// Krótszy plik różni się czasem trwania od dłuższego; na tej różnicy opiera się sprawdzian
    /// zastąpienia kolejki, bo tytuł i numer pozycji są poprawne nawet przy starym dekoderze.
    /// </summary>
    private const int SekundKrotka = 95;

    /// <summary>Zapisuje oba pliki i zwraca ich ścieżki w kolejności: dłuższy, krótszy.</summary>
    public static (string Dluga, string Krotka) Write(string katalog)
    {
        Directory.CreateDirectory(katalog);

        var dluga = Path.Combine(katalog, "probka-dluga.wav");
        var krotka = Path.Combine(katalog, "probka-krotka.wav");

        Zapisz(dluga, SekundDluga, "Material probny dlugi");
        Zapisz(krotka, SekundKrotka, "Material probny krotki");

        return (dluga, krotka);
    }

    private static void Zapisz(string sciezka, int sekundy, string tytul)
    {
        var probek = Czestotliwosc * sekundy;
        var dane = new byte[probek * Kanaly * (BitowNaProbke / 8)];

        for (var i = 0; i < probek; i++)
        {
            var t = (double)i / Czestotliwosc;

            // Obwiednia z dwóch powolnych przebiegów o niewspółmiernych okresach, żeby fala miała
            // powtarzalny kształt; sam ton dałby równy prostokąt.
            var obwiednia = 0.30 + 0.22 * Math.Sin(2 * Math.PI * t / 11.0)
                                 + 0.16 * Math.Sin(2 * Math.PI * t / 3.7);

            var ton = Math.Sin(2 * Math.PI * 220.0 * t)
                      + 0.40 * Math.Sin(2 * Math.PI * 440.0 * t)
                      + 0.20 * Math.Sin(2 * Math.PI * 660.0 * t);

            var wartosc = Math.Clamp(obwiednia * ton / 1.6, -1.0, 1.0);
            var probka = (short)Math.Round(wartosc * short.MaxValue);

            dane[i * 2] = (byte)(probka & 0xFF);
            dane[i * 2 + 1] = (byte)((probka >> 8) & 0xFF);
        }

        using var plik = File.Create(sciezka);
        using var pisarz = new BinaryWriter(plik);

        var info = BlokInfo(tytul);

        pisarz.Write("RIFF"u8);
        pisarz.Write(4 + (8 + 16) + info.Length + (8 + dane.Length));   // rozmiar reszty pliku
        pisarz.Write("WAVE"u8);

        pisarz.Write("fmt "u8);
        pisarz.Write(16);
        pisarz.Write((short)1);                                          // PCM bez kompresji
        pisarz.Write(Kanaly);
        pisarz.Write(Czestotliwosc);
        pisarz.Write(Czestotliwosc * Kanaly * BitowNaProbke / 8);        // bajtów na sekundę
        pisarz.Write((short)(Kanaly * BitowNaProbke / 8));               // bajtów na ramkę
        pisarz.Write(BitowNaProbke);

        pisarz.Write(info);

        pisarz.Write("data"u8);
        pisarz.Write(dane.Length);
        pisarz.Write(dane);
    }

    /// <summary>
    /// Blok LIST/INFO z tytułem i wykonawcą. Wyłącznie znaki ASCII: pola RIFF nie niosą
    /// informacji o kodowaniu, więc polskie znaki byłyby zdane na domysł czytającego.
    /// </summary>
    private static byte[] BlokInfo(string tytul)
    {
        using var bufor = new MemoryStream();
        using var pisarz = new BinaryWriter(bufor);

        pisarz.Write("LIST"u8);
        var miejsceNaRozmiar = bufor.Position;
        pisarz.Write(0);
        pisarz.Write("INFO"u8);

        Pole(pisarz, "INAM"u8, tytul);
        Pole(pisarz, "IART"u8, "Cewka");
        Pole(pisarz, "IPRD"u8, "Zrzuty odniesienia");

        pisarz.Flush();
        var rozmiar = (int)(bufor.Position - miejsceNaRozmiar - 4);

        bufor.Position = miejsceNaRozmiar;
        pisarz.Write(rozmiar);
        pisarz.Flush();

        return bufor.ToArray();
    }

    private static void Pole(BinaryWriter pisarz, ReadOnlySpan<byte> nazwa, string wartosc)
    {
        // Napis kończy się zerem, a każdy blok RIFF zaczyna się na parzystym bajcie — stąd
        // dopełnienie. Bez niego dalsza część bloku przesuwa się o bajt i staje nieczytelna.
        var tresc = System.Text.Encoding.ASCII.GetBytes(wartosc + '\0');
        var dopelnienie = tresc.Length % 2;

        pisarz.Write(nazwa);
        pisarz.Write(tresc.Length + dopelnienie);
        pisarz.Write(tresc);
        if (dopelnienie == 1) pisarz.Write((byte)0);
    }
}
