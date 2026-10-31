using Cewka.App.Services;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>Okładka czytana w tle: spóźniony wynik nie może zastąpić okładki nowszego utworu.</summary>
public sealed class CoverLoaderTests
{
    private static readonly byte[] PierwszaOkladka = [1];
    private static readonly byte[] DrugaOkladka = [2];

    [Fact]
    public async Task SpoznionyOdczytNieNadpisujeOkladkiNastepnegoUtworu()
    {
        using var pierwszyGotowy = new ManualResetEventSlim();
        var loader = new CoverLoader<byte[]>(path => path == "pierwszy"
            ? Czekaj(pierwszyGotowy, PierwszaOkladka)
            : DrugaOkladka);
        var pokazane = new List<byte[]?>();

        var pierwszy = loader.LoadAsync("pierwszy", pokazane.Add);
        await loader.LoadAsync("drugi", pokazane.Add);

        pierwszyGotowy.Set();
        await pierwszy;

        Assert.Equal(new[] { DrugaOkladka }, pokazane);
    }

    [Fact]
    public async Task UniewaznienieOdrzucaTrwajacyOdczyt()
    {
        using var gotowy = new ManualResetEventSlim();
        var loader = new CoverLoader<byte[]>(_ => Czekaj(gotowy, PierwszaOkladka));
        var pokazane = new List<byte[]?>();

        var odczyt = loader.LoadAsync("utwor", pokazane.Add);
        loader.Cancel();

        gotowy.Set();
        await odczyt;

        Assert.Empty(pokazane);
    }

    [Fact]
    public async Task AktualnyOdczytPrzekazujeOkladkeIBrakOkladki()
    {
        var loader = new CoverLoader<byte[]>(path => path == "z-okladka" ? PierwszaOkladka : null);
        var pokazane = new List<byte[]?>();

        await loader.LoadAsync("z-okladka", pokazane.Add);
        await loader.LoadAsync("bez-okladki", pokazane.Add);

        Assert.Equal(new[] { PierwszaOkladka, null }, pokazane);
    }

    private static byte[] Czekaj(ManualResetEventSlim sygnal, byte[] wynik)
    {
        Assert.True(sygnal.Wait(TimeSpan.FromSeconds(10)), "odczyt nie został zwolniony");
        return wynik;
    }
}
