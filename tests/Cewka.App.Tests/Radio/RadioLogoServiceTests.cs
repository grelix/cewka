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
}
