using Cewka.App.Radio;
using Xunit;
using static Cewka.App.Tests.Radio.RadioTestData;

namespace Cewka.App.Tests.Radio;

public class RadioStateStoreTests
{
    [Fact]
    public void BrakPlikuDajePustyStanDoZapisu()
    {
        var state = RadioStateStore.Load(NewTempFile());

        Assert.False(state.ReadOnly);
        Assert.Empty(state.Favourites);
        Assert.Equal(RadioListContext.ViewCountry, state.Context.View);
    }

    [Fact]
    public void ZapisIOdczytZachowujaUlubioneOstatnieKontekstIPelneKopieStacji()
    {
        var path = NewTempFile();
        var state = new RadioState();
        var station = Station("pl-a", "Stacja A", "PL", city: "Łódź", cityLatin: "Lodz", languages: ["pl"],
            streams: [Stream("https://a.example.com/x", "aac", 64, hls: true)]);
        station.Recommended = 0;
        station.Logos = ["https://a.example.com/logo.png"];
        state.AddFavourite(station);
        state.AddFavourite(RadioState.CreateCustom("Moje", "http://192.168.0.2:8000/live", "PL", typed: true));
        state.PushRecent(station);
        state.Context = new RadioListContext { View = RadioListContext.ViewAll, Country = "PL", Category = RadioCategories.Hits };

        Assert.True(RadioStateStore.Save(state, path));
        var loaded = RadioStateStore.Load(path);

        Assert.False(loaded.ReadOnly);
        Assert.Equal(["pl-a", state.Favourites[1].Id], loaded.Favourites.Select(f => f.Id));
        var first = loaded.Favourites[0];
        Assert.Equal("Łódź", first.City);
        Assert.Equal("aac", first.Streams[0].Codec);
        Assert.True(first.Streams[0].Hls);
        Assert.Equal(64, first.Streams[0].Kbps);
        Assert.Equal(["https://a.example.com/logo.png"], first.Logos);
        Assert.True(loaded.Favourites[1].AllowLocalAddress);
        Assert.Equal(StationOrigin.Typed, loaded.Favourites[1].Origin);
        Assert.Equal("pl-a", loaded.LastStationId);
        Assert.Equal("pl-a", loaded.Recent[0].Id);
        Assert.Equal(RadioListContext.ViewAll, loaded.Context.View);
        Assert.Equal("PL", loaded.Context.Country);
        Assert.Equal(RadioCategories.Hits, loaded.Context.Category);
        Assert.Equal(1, loaded.SchemaVersion);
    }

    [Fact]
    public void PlikZapisanyWUtf8BezZnacznikaKolejnosci()
    {
        var path = NewTempFile();
        RadioStateStore.Save(new RadioState(), path);

        var bytes = File.ReadAllBytes(path);

        Assert.NotEqual(0xEF, bytes[0]);
    }

    /// <summary>
    /// Uszkodzonej treści nikt już nie odczyta: oryginał zostaje w kopii obok, a stan zapisuje się
    /// od nowa. Inaczej ulubione dodane po takim starcie przepadałyby przy każdym uruchomieniu.
    /// </summary>
    [Theory]
    [InlineData("{ to nie jest json")]
    [InlineData("null")]
    [InlineData("""{"schemaVersion":"jeden"}""")]
    public void PlikUszkodzonyZostajeWKopiiAStanZapisujeSieOdNowa(string broken)
    {
        var path = NewTempFile();
        File.WriteAllText(path, broken);

        var state = RadioStateStore.Load(path);

        Assert.False(state.ReadOnly);
        var copy = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".bad-*"));
        Assert.Equal(broken, File.ReadAllText(copy));

        state.AddFavourite(Station("a", "A", "PL"));
        Assert.True(RadioStateStore.Save(state, path));
        Assert.Equal(["a"], RadioStateStore.Load(path).Favourites.Select(f => f.Id));
    }

    /// <summary>Plik zablokowany przy odczycie ma zwykle poprawną treść; w tej sesji nie jest nadpisywany.</summary>
    [Fact]
    public void PlikZablokowanyDajeStanTylkoDoOdczytuAZapisGoNieNadpisuje()
    {
        var path = NewTempFile();
        var saved = new RadioState();
        saved.AddFavourite(Station("a", "A", "PL"));
        Assert.True(RadioStateStore.Save(saved, path));
        var before = File.ReadAllText(path);

        RadioState state;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            state = RadioStateStore.Load(path);

        Assert.True(state.ReadOnly);
        Assert.False(RadioStateStore.Save(state, path));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".bad-*"));
    }

    /// <summary>Plik nowszej wersji z pustymi polami: stan tylko do odczytu, ale bez pustych list.</summary>
    [Fact]
    public void PlikNowszejWersjiZPustymiPolamiNieWywracaOdczytu()
    {
        var path = NewTempFile();
        File.WriteAllText(path, """{"schemaVersion":2,"recent":null,"favourites":null,"context":null}""");

        var state = RadioStateStore.Load(path);

        Assert.True(state.ReadOnly);
        Assert.NotNull(state.Recent);
        Assert.NotNull(state.Favourites);
        Assert.NotNull(state.Context);
        Assert.NotNull(state.Context.View);
        state.TrimRecent(10);
    }

    /// <summary>Puste wartości wewnątrz zapisanej stacji nie mogą dojść do widoku listy.</summary>
    [Fact]
    public void PustePolaStacjiSaUzupelniane()
    {
        var path = NewTempFile();
        File.WriteAllText(path, """
            {"schemaVersion":1,"favourites":[
              {"id":"a","name":null,"country":null,"categories":[null,"hits"],"languages":[null],"logos":[null],
               "streams":[{"url":"http://a/","codec":null}]}],
             "context":{"view":"favourites"}}
            """);

        var station = Assert.Single(RadioStateStore.Load(path).Favourites);

        Assert.Equal("unknown", station.Streams[0].Codec);
        Assert.Equal("a", station.Name);
        Assert.Equal(string.Empty, station.Country);
        Assert.Equal(["hits"], station.Categories);
        Assert.Empty(station.Languages);
        Assert.Empty(station.Logos);
    }

    [Fact]
    public void PlikNowszejWersjiNieJestNadpisywanyPustymStanem()
    {
        var path = NewTempFile();
        const string newer = """{"schemaVersion":7,"favourites":[{"id":"x","name":"X","nowePole":true}]}""";
        File.WriteAllText(path, newer);

        var state = RadioStateStore.Load(path);

        Assert.True(state.ReadOnly);
        Assert.False(RadioStateStore.Save(state, path));
        Assert.Equal(newer, File.ReadAllText(path));
    }

    [Fact]
    public void NieznanePolaIPustaTrescPlikuNieWywracajaOdczytu()
    {
        var path = NewTempFile();
        File.WriteAllText(path, """{"schemaVersion":1,"inne":1,"favourites":null,"recent":[null,{"id":"","name":"x"}],"context":null}""");

        var state = RadioStateStore.Load(path);

        Assert.False(state.ReadOnly);
        Assert.Empty(state.Favourites);
        Assert.Empty(state.Recent);
        Assert.NotNull(state.Context);
    }

    [Fact]
    public void WpisyBezAdresowIPowtorzeniaSaPomijane()
    {
        var path = NewTempFile();
        File.WriteAllText(path, """
            {"schemaVersion":1,"favourites":[
              {"id":"a","name":"A","streams":[{"url":""},{"url":"https://a.example.com/x"}]},
              {"id":"a","name":"A2","streams":[{"url":"https://a.example.com/y"}]},
              {"id":"b","name":"B","streams":null}]}
            """);

        var state = RadioStateStore.Load(path);

        Assert.Equal(["a", "b"], state.Favourites.Select(f => f.Id));
        Assert.Equal(["https://a.example.com/x"], state.Favourites[0].Streams.Select(s => s.Url));
        Assert.Empty(state.Favourites[1].Streams);
    }

    /// <summary>Plik trzyma najdłuższą listę z ustawień; krótszą przycina model widoku po wczytaniu.</summary>
    [Fact]
    public void OstatnioSluchanychWPlikuJestNajwyzejDwadziescia()
    {
        var path = NewTempFile();
        var recent = string.Join(',', Enumerable.Range(0, 25).Select(i => $$"""{"id":"s{{i}}","name":"S{{i}}","streams":[{"url":"https://x/{{i}}"}]}"""));
        File.WriteAllText(path, $$"""{"schemaVersion":1,"recent":[{{recent}}]}""");

        Assert.Equal(20, RadioStateStore.Load(path).Recent.Count);
    }

    [Fact]
    public void ZapisTworzyKatalogIZastepujePoprzedniPlikCalosciowo()
    {
        var path = Path.Combine(Path.GetDirectoryName(NewTempFile())!, "glebiej", "radio.json");
        var state = new RadioState();
        state.AddFavourite(Station("a", "A", "PL"));
        Assert.True(RadioStateStore.Save(state, path));

        state.RemoveFavourite("a");
        Assert.True(RadioStateStore.Save(state, path));

        Assert.Empty(RadioStateStore.Load(path).Favourites);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    /// <summary>
    /// Gdy kopii uszkodzonego pliku nie da się zrobić (pełny dysk), oryginał jest jedynym egzemplarzem
    /// ulubionych, które da się jeszcze ręcznie odzyskać. Stan jest wtedy tylko do odczytu.
    /// </summary>
    [Fact]
    public void UszkodzonyPlikBezKopiiNieJestNadpisywany()
    {
        var path = NewTempFile();
        File.WriteAllText(path, "{ to nie jest json");

        var state = RadioStateStore.Load(path, _ => false);

        Assert.True(state.ReadOnly);

        state.AddFavourite(Station("a", "A", "PL"));
        Assert.False(RadioStateStore.Save(state, path));
        Assert.Equal("{ to nie jest json", File.ReadAllText(path));
    }

    /// <summary>
    /// Plik nowszej wersji programu, w którym zmienił się typ pola, nie daje się odczytać, ale dla
    /// tamtej wersji jest poprawny. Po powrocie do niej ulubione mają być na miejscu.
    /// </summary>
    [Fact]
    public void PlikNowszejWersjiNieDoOdczytaniaZostajeNietkniety()
    {
        var path = NewTempFile();
        const string newer = """{"schemaVersion":7,"favourites":"zapis, którego ta wersja nie zna"}""";
        File.WriteAllText(path, newer);

        var state = RadioStateStore.Load(path);

        Assert.True(state.ReadOnly);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".bad-*"));

        state.AddFavourite(Station("a", "A", "PL"));
        Assert.False(RadioStateStore.Save(state, path));
        Assert.Equal(newer, File.ReadAllText(path));
    }
}
