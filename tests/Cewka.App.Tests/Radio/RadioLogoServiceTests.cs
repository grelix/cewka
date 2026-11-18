using System.Buffers.Binary;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Cewka.App.Radio;
using Cewka.Audio.Streaming;
using Xunit;

namespace Cewka.App.Tests.Radio;

/// <summary>Obrazy testowe generowane w kodzie; to nie są znaki żadnej stacji.</summary>
internal static class TestLogos
{
    public static byte[] Png(int width, int height, uint argb = 0xFF3366CC)
    {
        using var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var frame = bitmap.Lock())
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var colour = (x + y) % 9 < 4 ? argb : 0xFFFFFFFF;
                    System.Runtime.InteropServices.Marshal.WriteInt32(frame.Address, (y * frame.RowBytes) + (x * 4), (int)colour);
                }
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    /// <summary>Poprawny nagłówek PNG z podanymi wymiarami i bez danych obrazu: dekoder nigdy go nie dostaje.</summary>
    public static byte[] PngHeader(int width, int height)
    {
        var data = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), 13);
        "IHDR"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(20), height);
        data[24] = 8;
        data[25] = 6;
        return data;
    }

    public static byte[] Truncated(byte[] png) => png[..(png.Length / 2)];

    public static byte[] IcoWith(byte[] image, bool png)
    {
        var data = new byte[22 + image.Length];
        data[2] = 1;
        data[4] = 1;
        data[6] = 32;
        data[7] = 32;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), image.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), 22);
        image.CopyTo(data, 22);
        if (!png) data[22] = 40;
        return data;
    }
}

[Collection(UiCollection.Name)]
public sealed class RadioLogoServiceTests
{
    private const string Url = "https://logo.example.org/a.png";

    private static RadioStation Station(params string[] logos) => new()
    {
        Id = "pl-test",
        Name = "Test",
        Country = "PL",
        Logos = [.. logos],
    };

    private sealed class Server(Func<Uri, ImageFetchResult> reply)
    {
        public int Calls;
        public readonly List<Uri> Requested = [];

        public Task<ImageFetchResult> Fetch(Uri url, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            lock (Requested) Requested.Add(url);
            return Task.FromResult(reply(url));
        }
    }

    private static ImageFetchResult Ok(byte[] bytes) => new(bytes, ImageFetchProblem.None, 200);

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    }

    private static RadioLogoService Create(Server server, Clock clock, string? directory = null) =>
        new(directory ?? TestEnvironment.NewDirectory(), server.Fetch, () => clock.Now);

    [Fact]
    public void PoprawnyObraz_DajeDwaRozmiaryIZapisujeJeNaDysku() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(40, 30)));
        using var service = Create(server, new Clock());

        using var logo = await service.GetAsync(Station(Url));

        Assert.NotNull(logo);
        Assert.Equal(RadioLogoService.ListSide, logo.List.PixelSize.Width);
        Assert.Equal(RadioLogoService.StageSide, logo.Stage.PixelSize.Width);
        Assert.True(File.Exists(logo.StagePath));
        Assert.True(service.SizeBytes() > 0);
    });

    [Fact]
    public void ObrazPionowy_MaWysokoscRownaBokowi() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(20, 60)));
        using var service = Create(server, new Clock());

        using var logo = await service.GetAsync(Station(Url));

        Assert.NotNull(logo);
        Assert.Equal(RadioLogoService.StageSide, logo.Stage.PixelSize.Height);
        Assert.True(logo.Stage.PixelSize.Width < logo.Stage.PixelSize.Height);
    });

    [Fact]
    public void DrugieZapytanie_IdzieZPamieciPodrecznejBezPolaczenia() => Ui.RunAsync(async () =>
    {
        var directory = TestEnvironment.NewDirectory();
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        var clock = new Clock();
        using (var first = Create(server, clock, directory))
            (await first.GetAsync(Station(Url)))?.Dispose();

        using var second = Create(server, clock, directory);
        Assert.NotNull(second.TryGetCached(Station(Url)));
        using var logo = await second.GetAsync(Station(Url));

        Assert.NotNull(logo);
        Assert.Equal(1, server.Calls);
    });

    [Fact]
    public void WpisStarszyNiz7Dni_JestPobieranyOdNowa() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        var clock = new Clock();
        using var service = Create(server, clock);
        (await service.GetAsync(Station(Url)))?.Dispose();

        clock.Now += TimeSpan.FromDays(6);
        (await service.GetAsync(Station(Url)))?.Dispose();
        Assert.Equal(1, server.Calls);

        clock.Now += TimeSpan.FromDays(2);
        Assert.Null(service.TryGetCached(Station(Url)));
        (await service.GetAsync(Station(Url)))?.Dispose();
        Assert.Equal(2, server.Calls);
    });

    [Fact]
    public void Svg_DajeObrazZastepczyIJestZapamietanyJakoPorazka() => Ui.RunAsync(async () =>
    {
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"/>");
        var server = new Server(_ => Ok(svg));
        using var service = Create(server, new Clock());

        Assert.Null(await service.GetAsync(Station(Url)));
        Assert.Null(await service.GetAsync(Station(Url)));

        Assert.Equal(1, server.Calls);
    });

    [Theory]
    [InlineData("uszkodzony")]
    [InlineData("za-duzy-wymiar")]
    [InlineData("smieci")]
    [InlineData("pusty")]
    public void ObrazUszkodzonyAlboZaDuzy_DajeObrazZastepczyBezWyjatku(string kind) => Ui.RunAsync(async () =>
    {
        var bytes = kind switch
        {
            "uszkodzony" => TestLogos.PngHeader(100, 100),
            "za-duzy-wymiar" => TestLogos.PngHeader(5000, 5000),
            "smieci" => [.. Enumerable.Range(0, 400).Select(i => (byte)(i * 31))],
            _ => [],
        };
        var server = new Server(_ => Ok(bytes));
        using var service = Create(server, new Clock());

        Assert.Null(await service.GetAsync(Station(Url)));
    });

    [Fact]
    public void ObrazUrwanyWPolowie_NieRzucaWyjatku() => Ui.RunAsync(async () =>
    {
        // Dekoder bywa łaskawy dla ucinanych plików: wynik to obraz albo zastępczy, ale nigdy wyjątek.
        var server = new Server(_ => Ok(TestLogos.Truncated(TestLogos.Png(300, 300))));
        using var service = Create(server, new Clock());

        (await service.GetAsync(Station(Url)))?.Dispose();

        Assert.Equal(1, server.Calls);
    });

    [Fact]
    public void BrakSieci_PonawiaPoDobie_ABrakObrazuPoTygodniu() => Ui.RunAsync(async () =>
    {
        var clock = new Clock();
        var down = true;
        var server = new Server(_ => down
            ? new ImageFetchResult(null, ImageFetchProblem.Unavailable)
            : Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, clock);

        Assert.Null(await service.GetAsync(Station(Url)));
        Assert.Null(await service.GetAsync(Station(Url)));
        Assert.Equal(1, server.Calls);

        clock.Now += TimeSpan.FromHours(25);
        down = false;
        using var logo = await service.GetAsync(Station(Url));
        Assert.NotNull(logo);
        Assert.Equal(2, server.Calls);
    });

    /// <summary>
    /// Serwer, który ograniczył tempo pobierania, oddaje logo kilka minut później. Zapamiętanie takiej
    /// odpowiedzi jak braku obrazu zostawiało stację bez logo na tydzień.
    /// </summary>
    [Fact]
    public void SerwerOgraniczylTempo_LogoWracaPoKwadransie() => Ui.RunAsync(async () =>
    {
        var clock = new Clock();
        var throttled = true;
        var server = new Server(_ => throttled
            ? new ImageFetchResult(null, ImageFetchProblem.Throttled, 429)
            : Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, clock);

        Assert.Null(await service.GetAsync(Station(Url)));

        // Zaraz potem nie pyta ponownie: serwer właśnie prosił o mniej żądań.
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Null(await service.GetAsync(Station(Url)));
        Assert.Equal(1, server.Calls);

        clock.Now += TimeSpan.FromMinutes(11);
        throttled = false;
        using var logo = await service.GetAsync(Station(Url));

        Assert.NotNull(logo);
        Assert.Equal(2, server.Calls);
    });

    private const string Primary = "https://logo.example.org/";
    private const string Source = "https://nadawca.example.net/";

    private static RadioStation StationWithFallback(string name) => new()
    {
        Id = "pl-" + name,
        Name = name,
        Country = "PL",
        Logos = [Primary + name + ".png", Source + name + ".png"],
    };

    /// <summary>
    /// Serwer, który nie odpowiada, jest pomijany przez kilka minut. Bez tego każde logo z listy czekałoby
    /// na przekroczenie czasu, zanim program sięgnie po adres zapasowy.
    /// </summary>
    [Fact]
    public void SerwerNieOdpowiada_NastepneLogoIdzieOdRazuDoAdresuZapasowego() => Ui.RunAsync(async () =>
    {
        var clock = new Clock();
        var server = new Server(url => url.Host == "logo.example.org"
            ? new ImageFetchResult(null, ImageFetchProblem.Unavailable)
            : Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, clock);

        using var first = await service.GetAsync(StationWithFallback("a"));
        using var second = await service.GetAsync(StationWithFallback("b"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(
            [Primary + "a.png", Source + "a.png", Source + "b.png"],
            server.Requested.Select(u => u.AbsoluteUri));
    });

    [Fact]
    public void SerwerNieOdpowiada_PoPrzerwieJestPytanyPonownie() => Ui.RunAsync(async () =>
    {
        var clock = new Clock();
        var down = true;
        var server = new Server(url => down && url.Host == "logo.example.org"
            ? new ImageFetchResult(null, ImageFetchProblem.Unavailable)
            : Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, clock);

        (await service.GetAsync(StationWithFallback("a")))?.Dispose();

        // Przerwa trwa pięć minut: po czterech serwer nadal nie dostaje żądania, choć już działa.
        down = false;
        clock.Now += TimeSpan.FromMinutes(4);
        (await service.GetAsync(StationWithFallback("b")))?.Dispose();
        Assert.DoesNotContain(server.Requested, u => u.AbsoluteUri == Primary + "b.png");

        clock.Now += TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1);
        using var logo = await service.GetAsync(StationWithFallback("c"));

        Assert.NotNull(logo);
        Assert.Equal(Primary + "c.png", server.Requested[^1].AbsoluteUri);
    });

    /// <summary>
    /// Pobranie, które czekało w kolejce serwera, nie idzie do niego, jeśli w tym czasie serwer
    /// poprosił o przerwę; od razu przechodzi do adresu zapasowego.
    /// </summary>
    [Fact]
    public void PobranieCzekajaceWKolejceNieIdzieDoSerweraWPrzerwie() => Ui.RunAsync(async () =>
    {
        var clock = new Clock();
        var first = new TaskCompletionSource<ImageFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<ImageFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new List<string>();

        Task<ImageFetchResult> Fetch(Uri url, CancellationToken ct)
        {
            lock (requested) requested.Add(url.AbsoluteUri);
            return url.AbsoluteUri == Primary + "a.png" ? first.Task
                : url.AbsoluteUri == Primary + "b.png" ? second.Task
                : Task.FromResult(Ok(TestLogos.Png(64, 64)));
        }

        using var service = new RadioLogoService(TestEnvironment.NewDirectory(), Fetch, () => clock.Now);
        var a = service.GetAsync(StationWithFallback("a"));
        var b = service.GetAsync(StationWithFallback("b"));
        await WaitUntilAsync(() =>
        {
            lock (requested) return requested.Contains(Primary + "a.png") && requested.Contains(Primary + "b.png");
        });

        // Trzecie pobranie stoi w kolejce serwera za dwoma, które trwają.
        var c = service.GetAsync(StationWithFallback("c"));
        await WaitUntilAsync(() => service.FetchesStarted == 3);

        first.SetResult(new ImageFetchResult(null, ImageFetchProblem.Throttled, 429, TimeSpan.FromMinutes(10)));
        using var logo = await c;
        second.SetResult(Ok(TestLogos.Png(64, 64)));
        (await a)?.Dispose();
        (await b)?.Dispose();

        Assert.NotNull(logo);
        lock (requested)
        {
            Assert.DoesNotContain(Primary + "c.png", requested);
            Assert.Contains(Source + "c.png", requested);
        }
    });

    /// <summary>
    /// Serwer, który prosi o przerwę, dostaje ją dla wszystkich swoich adresów i na tyle, ile podał,
    /// a nie tylko dla adresu, na którym odmówił.
    /// </summary>
    [Fact]
    public void ZaDuzoZadan_CalySerwerCzekaTyleIlePodal() => Ui.RunAsync(async () =>
    {
        var clock = new Clock();
        var throttled = true;
        var server = new Server(url => throttled && url.Host == "logo.example.org"
            ? new ImageFetchResult(null, ImageFetchProblem.Throttled, 429, TimeSpan.FromMinutes(40))
            : Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, clock);

        (await service.GetAsync(StationWithFallback("a")))?.Dispose();

        clock.Now += TimeSpan.FromMinutes(39);
        (await service.GetAsync(StationWithFallback("b")))?.Dispose();
        Assert.DoesNotContain(server.Requested, u => u.AbsoluteUri == Primary + "b.png");

        clock.Now += TimeSpan.FromMinutes(2);
        throttled = false;
        (await service.GetAsync(StationWithFallback("c")))?.Dispose();
        Assert.Contains(server.Requested, u => u.AbsoluteUri == Primary + "c.png");
    });

    /// <summary>
    /// Z jednego serwera idą dwa pobrania naraz, więc po odpowiedzi z długą przerwą może jeszcze przyjść
    /// porażka drugiego pobrania, która sama dałaby przerwę krótką. Obowiązuje dłuższa: serwer o nią prosił.
    /// </summary>
    [Fact]
    public void DluzszaPrzerwaNieJestSkracanaPrzezPozniejszaPorazke() => Ui.RunAsync(async () =>
    {
        var clock = new Clock();
        var first = new TaskCompletionSource<ImageFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<ImageFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new List<string>();

        Task<ImageFetchResult> Fetch(Uri url, CancellationToken ct)
        {
            lock (requested) requested.Add(url.AbsoluteUri);
            return url.AbsoluteUri == Primary + "a.png" ? first.Task
                : url.AbsoluteUri == Primary + "b.png" ? second.Task
                : Task.FromResult(Ok(TestLogos.Png(64, 64)));
        }

        using var service = new RadioLogoService(TestEnvironment.NewDirectory(), Fetch, () => clock.Now);
        var a = service.GetAsync(StationWithFallback("a"));
        var b = service.GetAsync(StationWithFallback("b"));
        await WaitUntilAsync(() =>
        {
            lock (requested) return requested.Contains(Primary + "a.png") && requested.Contains(Primary + "b.png");
        });

        first.SetResult(new ImageFetchResult(null, ImageFetchProblem.Throttled, 429, TimeSpan.FromHours(1)));
        (await a)?.Dispose();
        second.SetResult(new ImageFetchResult(null, ImageFetchProblem.Unavailable));
        (await b)?.Dispose();

        clock.Now += RadioLogoService.HostDownFor + TimeSpan.FromMinutes(1);
        (await service.GetAsync(StationWithFallback("c")))?.Dispose();

        lock (requested) Assert.DoesNotContain(Primary + "c.png", requested);
    });

    [Theory]
    [InlineData(5, 60)]
    [InlineData(600, 600)]
    [InlineData(7 * 24 * 3600, 24 * 3600)]
    public void PrzerwaPodanaPrzezSerwer_MaGranice(int requestedSeconds, int expectedSeconds)
    {
        var result = new ImageFetchResult(null, ImageFetchProblem.Throttled, 429, TimeSpan.FromSeconds(requestedSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RadioLogoService.PauseFor(result));
    }

    [Fact]
    public void PrzerwaBezPodanegoCzasu_ZalezyOdPrzyczyny()
    {
        Assert.Equal(RadioLogoService.ThrottledFor, RadioLogoService.PauseFor(new ImageFetchResult(null, ImageFetchProblem.Throttled, 429)));
        Assert.Equal(RadioLogoService.HostDownFor, RadioLogoService.PauseFor(new ImageFetchResult(null, ImageFetchProblem.Unavailable)));
        Assert.Null(RadioLogoService.PauseFor(new ImageFetchResult(null, ImageFetchProblem.Missing, 404)));
        Assert.Null(RadioLogoService.PauseFor(new ImageFetchResult(null, ImageFetchProblem.TooLarge)));
    }

    /// <summary>Czas podany przez serwer obowiązuje także przy błędzie 5xx, w tych samych granicach.</summary>
    [Fact]
    public void BladSerweraZPodanymCzasem_DajePrzerweWedlugSerwera()
    {
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            RadioLogoService.PauseFor(new ImageFetchResult(null, ImageFetchProblem.Unavailable, 503, TimeSpan.FromSeconds(10))));
        Assert.Equal(
            TimeSpan.FromMinutes(30),
            RadioLogoService.PauseFor(new ImageFetchResult(null, ImageFetchProblem.Unavailable, 503, TimeSpan.FromMinutes(30))));
    }

    /// <summary>Brak pliku na serwerze nie zatrzymuje pozostałych logo z tego serwera.</summary>
    [Fact]
    public void BrakJednegoPliku_NieWstrzymujeSerwera() => Ui.RunAsync(async () =>
    {
        var clock = new Clock();
        var server = new Server(url => url.AbsoluteUri == Primary + "a.png"
            ? new ImageFetchResult(null, ImageFetchProblem.Missing, 404)
            : Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, clock);

        (await service.GetAsync(StationWithFallback("a")))?.Dispose();
        (await service.GetAsync(StationWithFallback("b")))?.Dispose();

        Assert.Contains(server.Requested, u => u.AbsoluteUri == Primary + "b.png");
    });

    /// <summary>
    /// Serwery plików ograniczają liczbę połączeń od jednego klienta (Wikimedia: najwyżej dwa naraz),
    /// więc z jednego serwera idą najwyżej dwa pobrania jednocześnie.
    /// </summary>
    [Fact]
    public void ZJednegoSerweraNajwyzejDwaPobraniaNaraz() => Ui.RunAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, peak = 0, started = 0;

        async Task<ImageFetchResult> Fetch(Uri url, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            Interlocked.Increment(ref started);
            await release.Task.WaitAsync(ct);
            Interlocked.Decrement(ref running);
            return Ok(TestLogos.Png(64, 64));
        }

        using var service = new RadioLogoService(TestEnvironment.NewDirectory(), Fetch, () => DateTimeOffset.UtcNow);
        var tasks = Enumerable.Range(0, 6)
            .Select(i => service.GetListAsync(Station($"https://logo.example.org/{i}.png")))
            .ToList();

        // Dwa pobrania stoją; trzecie nie może ruszyć, dopóki żadne z nich się nie skończy.
        await WaitUntilAsync(() => Volatile.Read(ref started) >= 2);
        await Task.Delay(300);
        Assert.Equal(2, Volatile.Read(ref started));

        release.SetResult();
        foreach (var task in tasks) (await task)?.Dispose();

        Assert.Equal(2, peak);
        Assert.Equal(6, started);
    });

    /// <summary>
    /// Trzecie pobranie z zajętego serwera czeka na swój serwer, nie zajmując miejsca we wspólnej
    /// kolejce: pobranie z innego serwera rusza od razu, choć wspólna kolejka ma trzy miejsca.
    /// </summary>
    [Fact]
    public void PobranieCzekajaceNaSwojSerwerNieZajmujeMiejscaInnym() => Ui.RunAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new List<string>();

        async Task<ImageFetchResult> Fetch(Uri url, CancellationToken ct)
        {
            lock (started) started.Add(url.Host);
            await release.Task.WaitAsync(ct);
            return Ok(TestLogos.Png(64, 64));
        }

        using var service = new RadioLogoService(TestEnvironment.NewDirectory(), Fetch, () => DateTimeOffset.UtcNow);
        var tasks = Enumerable.Range(0, 3)
            .Select(i => service.GetListAsync(Station($"https://a.example.org/{i}.png")))
            .ToList();

        // Dwa pobrania z pierwszego serwera trwają, trzecie doszło do kolejki.
        await WaitUntilAsync(() =>
        {
            lock (started) return started.Count == 2;
        });
        await WaitUntilAsync(() => service.FetchesStarted == 3);
        await Task.Delay(100);

        tasks.Add(service.GetListAsync(Station("https://b.example.org/logo.png")));
        await WaitUntilAsync(() =>
        {
            lock (started) return started.Contains("b.example.org");
        });

        release.SetResult();
        foreach (var task in tasks) (await task)?.Dispose();
    });

    /// <summary>Przerwane pobrania oddają miejsca w obu kolejkach: po anulowaniu rusza komplet nowych.</summary>
    [Fact]
    public void AnulowanieZwalniaObieKolejki() => Ui.RunAsync(async () =>
    {
        var started = 0;

        async Task<ImageFetchResult> Fetch(Uri url, CancellationToken ct)
        {
            Interlocked.Increment(ref started);
            await Task.Delay(Timeout.Infinite, ct);
            return Ok(TestLogos.Png(64, 64));
        }

        using var service = new RadioLogoService(TestEnvironment.NewDirectory(), Fetch, () => DateTimeOffset.UtcNow);

        // Trzy pobrania zajmują wspólną kolejkę (dwa z serwera a, jedno z b); czwarte ma miejsce
        // na serwerze b i czeka na wspólną kolejkę.
        var before = new[] { "https://a.example.org/1.png", "https://a.example.org/2.png", "https://b.example.org/1.png" }
            .Select(url => service.GetListAsync(Station(url)))
            .ToList();
        await WaitUntilAsync(() => Volatile.Read(ref started) == 3);
        before.Add(service.GetListAsync(Station("https://b.example.org/2.png")));
        await WaitUntilAsync(() => service.FetchesStarted == 4);
        await Task.Delay(100);

        service.CancelPending();
        foreach (var task in before)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        Volatile.Write(ref started, 0);
        var after = new[] { "https://b.example.org/3.png", "https://b.example.org/4.png", "https://a.example.org/3.png" }
            .Select(url => service.GetListAsync(Station(url)))
            .ToList();

        await WaitUntilAsync(() => Volatile.Read(ref started) == 3);

        service.CancelPending();
        foreach (var task in after)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "warunek nie został spełniony w 20 s");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void ObrazNieznaleziony_NieJestPytanyPrzezTydzien() => Ui.RunAsync(async () =>
    {
        var clock = new Clock();
        var server = new Server(_ => new ImageFetchResult(null, ImageFetchProblem.Missing, 404));
        using var service = Create(server, clock);

        Assert.Null(await service.GetAsync(Station(Url)));
        clock.Now += TimeSpan.FromDays(6);
        Assert.Null(await service.GetAsync(Station(Url)));
        Assert.Equal(1, server.Calls);

        clock.Now += TimeSpan.FromDays(2);
        Assert.Null(await service.GetAsync(Station(Url)));
        Assert.Equal(2, server.Calls);
    });

    [Fact]
    public void AdresyInneNizHttps_NieWychodzaDoSieci() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, new Clock());

        Assert.Null(await service.GetAsync(Station("http://logo.example.org/a.png", "ftp://x/a.png", "nie-adres", "/lokalny.png")));
        Assert.Equal(0, server.Calls);
    });

    [Fact]
    public void PierwszyAdresNiedziala_DrugiJestProbowany() => Ui.RunAsync(async () =>
    {
        var server = new Server(u => u.AbsolutePath == "/zly.png"
            ? new ImageFetchResult(null, ImageFetchProblem.Missing, 404)
            : Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, new Clock());

        using var logo = await service.GetAsync(Station("https://a.example.org/zly.png", "https://b.example.org/dobry.png"));

        Assert.NotNull(logo);
        Assert.Equal(2, server.Calls);
    });

    /// <summary>
    /// Wiersz listy dostaje samo małe logo. Z pamięci podręcznej czyta się wtedy tylko mały plik:
    /// duży obraz to megabajt pamięci na każdy widoczny wiersz, a wiersz go nie pokazuje.
    /// </summary>
    [Fact]
    public void LogoWierszaCzytaZPamieciPodrecznejTylkoMalyObraz() => Ui.RunAsync(async () =>
    {
        var directory = TestEnvironment.NewDirectory();
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        var clock = new Clock();
        using (var first = Create(server, clock, directory))
            (await first.GetAsync(Station(Url)))?.Dispose();

        // Bez dużego obrazu pełne logo nie da się już odczytać, a małe dalej tak.
        foreach (var stage in Directory.GetFiles(directory, "*.stage.png")) File.Delete(stage);

        using var second = Create(server, clock, directory);
        using var list = await second.GetListAsync(Station(Url));

        Assert.NotNull(list);
        Assert.Equal(RadioLogoService.ListSide, list!.PixelSize.Width);
        Assert.Equal(1, server.Calls);
    });

    [Fact]
    public void LogoWierszaBezPamieciPodrecznejPobieraJeIZapisuje() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, new Clock());

        using var list = await service.GetListAsync(Station(Url));
        using var again = await service.GetListAsync(Station(Url));

        Assert.NotNull(list);
        Assert.NotNull(again);
        Assert.Equal(1, server.Calls);
        Assert.NotNull(service.TryGetCached(Station(Url)));
    });

    [Fact]
    public void LogoWierszaPoNiedawnejPorazcePrzechodziDoNastepnegoAdresuBezPolaczenia() => Ui.RunAsync(async () =>
    {
        var server = new Server(url => url.Host.StartsWith('a')
            ? new ImageFetchResult(null, ImageFetchProblem.Missing, 404)
            : Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, new Clock());
        var station = Station("https://a.example.org/zly.png", "https://b.example.org/dobry.png");

        using var first = await service.GetListAsync(station);
        using var second = await service.GetListAsync(station);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, server.Calls);
    });

    [Fact]
    public void RownoczesneZapytaniaOTenSamAdres_PobieraJedno() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, new Clock());

        var all = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.GetAsync(Station(Url))));

        Assert.All(all, logo => Assert.NotNull(logo));
        Assert.Equal(1, server.Calls);
    });

    [Fact]
    public void WyjatekPobierania_DajeObrazZastepczy() => Ui.RunAsync(async () =>
    {
        using var service = new RadioLogoService(
            TestEnvironment.NewDirectory(), (_, _) => throw new InvalidOperationException("atrapa"));

        Assert.Null(await service.GetAsync(Station(Url)));
    });

    [Fact]
    public void Ico_zPngWSrodku_DziałaABmpDajeObrazZastepczy() => Ui.RunAsync(async () =>
    {
        var png = TestLogos.Png(32, 32);
        var withPng = new Server(_ => Ok(TestLogos.IcoWith(png, png: true)));
        using (var service = Create(withPng, new Clock()))
        {
            using var logo = await service.GetAsync(Station(Url));
            Assert.NotNull(logo);
        }

        var withBmp = new Server(_ => Ok(TestLogos.IcoWith(png, png: false)));
        using var other = Create(withBmp, new Clock());
        Assert.Null(await other.GetAsync(Station(Url)));
    });

    [Fact]
    public void Wyczysc_UsuwaPlikiIPozwalaPobracOdNowa() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, new Clock());
        (await service.GetAsync(Station(Url)))?.Dispose();
        Assert.True(service.SizeBytes() > 0);

        service.Clear();

        Assert.Equal(0, service.SizeBytes());
        (await service.GetAsync(Station(Url)))?.Dispose();
        Assert.Equal(2, server.Calls);
    });

    [Fact]
    public void UszkodzonyPlikWPamieciPodrecznej_JestPobieranyOdNowa() => Ui.RunAsync(async () =>
    {
        var directory = TestEnvironment.NewDirectory();
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, new Clock(), directory);
        (await service.GetAsync(Station(Url)))?.Dispose();
        foreach (var file in Directory.GetFiles(directory, "*.png"))
            File.WriteAllBytes(file, [1, 2, 3]);

        using var logo = await service.GetAsync(Station(Url));

        Assert.NotNull(logo);
        Assert.Equal(2, server.Calls);
    });

    [Fact]
    public void Naglowki_WymiaryZJpegGifIWebP()
    {
        var jpeg = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0x01, 0x2C, 0x00, 0xC8, 3, 1, 0x22, 0,
        };
        Assert.Equal(LogoFormat.Jpeg, LogoSniffer.Detect(jpeg));
        Assert.Equal((200, 300), LogoSniffer.Dimensions(jpeg, LogoFormat.Jpeg));

        var gif = "GIF89a"u8.ToArray().Concat(new byte[] { 0x40, 0x01, 0xF0, 0x00, 0, 0 }).ToArray();
        Assert.Equal(LogoFormat.Gif, LogoSniffer.Detect(gif));
        Assert.Equal((320, 240), LogoSniffer.Dimensions(gif, LogoFormat.Gif));

        var webp = new byte[30];
        "RIFF"u8.CopyTo(webp);
        "WEBP"u8.CopyTo(webp.AsSpan(8));
        "VP8X"u8.CopyTo(webp.AsSpan(12));
        webp[24] = 99;
        webp[27] = 49;
        Assert.Equal(LogoFormat.WebP, LogoSniffer.Detect(webp));
        Assert.Equal((100, 50), LogoSniffer.Dimensions(webp, LogoFormat.WebP));
    }

    /// <summary>
    /// Obraz jest dobry, tylko nie daje się zapisać (brak miejsca, plik zajęty przez drugą kopię
    /// programu). Logo ma się pokazać z pamięci, a porażka nie może zostać zapamiętana na tydzień
    /// jako brak obrazu: po usunięciu przeszkody następne zapytanie pobiera i zapisuje.
    /// </summary>
    [Fact]
    public void BladZapisuNaDysk_NieJestZapamietywanyJakoBrakObrazu() => Ui.RunAsync(async () =>
    {
        var directory = TestEnvironment.NewDirectory();
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(new Uri(Url).AbsoluteUri))).ToLowerInvariant();

        // Katalog w miejscu pliku obrazu: zapis kończy się błędem, choć sam katalog pamięci jest w porządku.
        var obstacle = Path.Combine(directory, key + ".list.png");
        Directory.CreateDirectory(obstacle);

        var server = new Server(_ => Ok(TestLogos.Png(40, 30)));
        using var service = Create(server, new Clock(), directory);

        using (var first = await service.GetAsync(Station(Url)))
        {
            Assert.NotNull(first);
            Assert.Equal(RadioLogoService.ListSide, first.List.PixelSize.Width);
            Assert.Null(first.StagePath);
        }

        Assert.DoesNotContain(Directory.GetFiles(directory), file => file.EndsWith(".tmp", StringComparison.Ordinal));

        Directory.Delete(obstacle);

        using var second = await service.GetAsync(Station(Url));

        Assert.NotNull(second);
        Assert.Equal(2, server.Calls);
        Assert.True(File.Exists(second.StagePath));
    });

    private static bool Released(Bitmap bitmap)
    {
        try
        {
            _ = bitmap.PixelSize;
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Wiersz listy bierze tylko mały obraz. Duży (512 px, megabajt pamięci poza zasięgiem odśmiecacza)
    /// zdekodowany przy pobraniu nie może zostać bez właściciela: pierwsze przewinięcie listy kilkuset
    /// stacji zostawiało ich kilkaset.
    /// </summary>
    [Fact]
    public void LogoWierszaZSieciNieZostawiaDuzegoObrazuWPamieci() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, new Clock());
        RadioLogo? rendered = null;
        service.Rendered = logo => rendered = logo;

        using var list = await service.GetListAsync(Station(Url));

        Assert.NotNull(list);
        Assert.NotNull(rendered);
        Assert.True(Released(rendered.Stage), "duży obraz z pobrania został w pamięci");
        Assert.Equal(RadioLogoService.ListSide, list.PixelSize.Width);
    });

    /// <summary>
    /// Pytający o ten sam adres w trakcie jednego pobrania dostają własne obrazy: zwolnienie logo
    /// przez jednego nie odbiera go drugiemu.
    /// </summary>
    [Fact]
    public void RownoczesnePytaniaDostajaWlasneObrazy() => Ui.RunAsync(async () =>
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        using var service = new RadioLogoService(
            TestEnvironment.NewDirectory(),
            async (_, _) =>
            {
                Interlocked.Increment(ref calls);
                await gate.Task;
                return Ok(TestLogos.Png(64, 64));
            },
            () => new Clock().Now);

        var first = service.GetAsync(Station(Url));
        var second = service.GetAsync(Station(Url));
        var row = service.GetListAsync(Station(Url));
        gate.SetResult();

        using var a = await first;
        var b = await second;
        using var list = await row;

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(list);
        Assert.Equal(1, calls);

        b.Dispose();

        Assert.False(Released(a.Stage));
        Assert.False(Released(a.List));
        Assert.False(Released(list));
    });

    /// <summary>Katalog w miejscu pliku obrazu: zapis logo kończy się błędem, choć katalog pamięci jest w porządku.</summary>
    private static string DirectoryRefusingSave()
    {
        var directory = TestEnvironment.NewDirectory();
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(new Uri(Url).AbsoluteUri))).ToLowerInvariant();
        Directory.CreateDirectory(Path.Combine(directory, key + ".list.png"));
        return directory;
    }

    /// <summary>
    /// Pamięć podręczna, do której nie da się pisać, to stan trwały: każde przewinięcie listy pobiera
    /// logo od nowa. Duży obraz z dekodowania nie może wtedy zostawać w pamięci przy każdym wierszu.
    /// </summary>
    [Fact]
    public void BladZapisu_LogoWierszaNieZostawiaDuzegoObrazuWPamieci() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, new Clock(), DirectoryRefusingSave());
        RadioLogo? rendered = null;
        service.Rendered = logo => rendered = logo;

        using var list = await service.GetListAsync(Station(Url));

        Assert.NotNull(list);
        Assert.NotNull(rendered);
        Assert.True(Released(rendered.Stage), "duży obraz z pobrania został w pamięci");
        Assert.Equal(RadioLogoService.ListSide, list.PixelSize.Width);
    });

    [Fact]
    public void BladZapisu_RownoczesnePytaniaDostajaWlasneObrazy() => Ui.RunAsync(async () =>
    {
        var gate = new TaskCompletionSource();
        using var service = new RadioLogoService(
            DirectoryRefusingSave(),
            async (_, _) =>
            {
                await gate.Task;
                return Ok(TestLogos.Png(64, 64));
            },
            () => new Clock().Now);

        var first = service.GetAsync(Station(Url));
        var second = service.GetAsync(Station(Url));
        var row = service.GetListAsync(Station(Url));
        gate.SetResult();

        using var a = await first;
        var b = await second;
        using var list = await row;

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotNull(list);
        Assert.Null(a.StagePath);

        b.Dispose();

        Assert.False(Released(a.Stage));
        Assert.False(Released(a.List));
        Assert.False(Released(list));
    });

    /// <summary>
    /// Logo zapisane na dysku jest dekodowane raz: zadanie pobrania nie czyta go tylko po to, żeby
    /// je zwolnić, bo to zwykła droga przy każdym wyborze stacji.
    /// </summary>
    [Fact]
    public void LogoZPamieciPodrecznejNieUruchamiaPobrania() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        var directory = TestEnvironment.NewDirectory();
        using (var first = Create(server, new Clock(), directory))
        using (await first.GetAsync(Station(Url)))
        {
        }

        using var service = Create(server, new Clock(), directory);
        var rendered = 0;
        service.Rendered = _ => rendered++;

        using var logo = await service.GetAsync(Station(Url));

        Assert.NotNull(logo);
        Assert.NotNull(logo.StagePath);
        Assert.Equal(1, server.Calls);
        Assert.Equal(0, rendered);
        Assert.Equal(0, service.FetchesStarted);
    });

    /// <summary>
    /// Wpis usunięty między zapisem a odczytem (druga kopia programu, czyszczenie pamięci) nie
    /// odbiera logo temu, kto je właśnie pobrał.
    /// </summary>
    [Fact]
    public void WpisUsunietyZarazPoZapisie_LogoIMaloObrazSaZPobrania() => Ui.RunAsync(async () =>
    {
        var server = new Server(_ => Ok(TestLogos.Png(64, 64)));
        using var service = Create(server, new Clock());
        service.Rendered = _ => { };
        service.Stored = service.Clear;

        using var logo = await service.GetAsync(Station(Url));
        using var list = await service.GetListAsync(Station(Url));

        Assert.NotNull(logo);
        Assert.Null(logo.StagePath);
        Assert.NotNull(list);
    });
}
