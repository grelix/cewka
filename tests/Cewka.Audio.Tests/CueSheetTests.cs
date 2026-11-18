using System.Text;
using Cewka.Audio.Cue;
using Cewka.Audio.Metadata;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Odczyt arkuszy CUE: polecenia, kodowania, granice i podział pliku na utwory.</summary>
public sealed class CueSheetTests
{
    /// <summary>Arkusz w postaci, w jakiej zapisują go programy do zgrywania płyt.</summary>
    internal const string Rip = """
        REM GENRE "Jazz"
        REM DATE 1959
        REM DISCID 6E0A5A09
        REM COMMENT "ExactAudioCopy v1.6"
        REM DISCNUMBER 2
        REM REPLAYGAIN_ALBUM_GAIN -7.25 dB
        REM REPLAYGAIN_ALBUM_PEAK 0.988000
        PERFORMER "Miles Davis"
        TITLE "Kind of Blue"
        FILE "Kind of Blue.wav" WAVE
          TRACK 01 AUDIO
            TITLE "So What"
            REM REPLAYGAIN_TRACK_GAIN -6.50 dB
            REM REPLAYGAIN_TRACK_PEAK 0.950000
            INDEX 01 00:00:00
          TRACK 02 AUDIO
            TITLE "Freddie Freeloader"
            PERFORMER "Miles Davis Sextet"
            INDEX 00 09:22:10
            INDEX 01 09:24:00
          TRACK 03 AUDIO
            TITLE "Blue in Green"
            INDEX 01 19:10:74
        """;

    private static CueSheet Parse(string text, Encoding? encoding = null) =>
        CueSheet.Parse((encoding ?? new UTF8Encoding(false)).GetBytes(text));

    private static long Frames(int minutes, int seconds, int frames) => (minutes * 60L + seconds) * 75 + frames;

    [Fact]
    public void ArkuszZgrywarkiDajeUtworyIZnaczniki()
    {
        var sheet = Parse(Rip);

        Assert.Equal("Kind of Blue", sheet.Title);
        Assert.Equal("Miles Davis", sheet.Performer);
        Assert.Equal("Jazz", sheet.Genre);
        Assert.Equal(1959, sheet.Year);
        Assert.Equal(2, sheet.Disc);
        Assert.Equal(-7.25, sheet.AlbumGain);
        Assert.Equal(0.988, sheet.AlbumPeak);

        var file = Assert.Single(sheet.Files);
        Assert.Equal("Kind of Blue.wav", file.Name);
        Assert.Equal([1, 2, 3], file.Tracks.Select(t => t.Number));
        Assert.Equal(Frames(9, 22, 10), file.Tracks[1].Pregap);

        var tracks = sheet.Tracks(@"C:\Muzyka\kind.cue", name => @"C:\Muzyka\" + name);

        Assert.Equal(3, tracks.Count);
        Assert.All(tracks, item => Assert.Equal(@"C:\Muzyka\Kind of Blue.wav", item.Path));

        var first = tracks[0].Sheet!;
        Assert.Equal(@"C:\Muzyka\kind.cue", first.SheetPath);
        Assert.Equal("So What", first.Title);
        Assert.Null(first.Performer);
        Assert.Equal("Kind of Blue", first.Album);
        Assert.Equal("Miles Davis", first.AlbumPerformer);
        Assert.Equal(-6.5, first.TrackGain);
        Assert.Equal(0.95, first.TrackPeak);
        Assert.Equal(-7.25, first.AlbumGain);
        Assert.Equal(0, first.Start);

        // Przerwa przed drugim utworem należy do końca pierwszego.
        Assert.Equal(Frames(9, 24, 0), first.End);
        Assert.Equal(Frames(9, 24, 0), tracks[1].Sheet!.Start);
        Assert.Equal("Miles Davis Sextet", tracks[1].Sheet!.Performer);
        Assert.Equal(Frames(19, 10, 74), tracks[1].Sheet!.End);

        // Ostatni gra do końca pliku.
        Assert.Equal(Frames(19, 10, 74), tracks[2].Sheet!.Start);
        Assert.Null(tracks[2].Sheet!.End);
    }

    [Fact]
    public void CudzysłowyOpcjonalneINazwaZeSpacjami()
    {
        var sheet = Parse("""
            performer Ktoś Bez Cudzysłowu
            title Płyta bez cudzysłowu
            FILE Nazwa ze spacjami.flac WAVE
            TRACK 1 AUDIO
            TITLE Pierwszy utwór
            INDEX 1 0:00:00
            """);

        Assert.Equal("Ktoś Bez Cudzysłowu", sheet.Performer);
        Assert.Equal("Płyta bez cudzysłowu", sheet.Title);
        Assert.Equal("Nazwa ze spacjami.flac", sheet.Files[0].Name);
        Assert.Equal("Pierwszy utwór", sheet.Files[0].Tracks[0].Title);
    }

    [Fact]
    public void KilkaPlikowKazdyKonczySieNaSwoimKoncu()
    {
        var sheet = Parse("""
            FILE "01.flac" WAVE
              TRACK 01 AUDIO
                INDEX 01 00:00:00
            FILE "02.flac" WAVE
              TRACK 02 AUDIO
                INDEX 00 00:00:00
                INDEX 01 00:01:00
              TRACK 03 AUDIO
                INDEX 01 03:00:00
            """);

        var tracks = sheet.Tracks("arkusz.cue", name => name);

        Assert.Equal(["01.flac", "02.flac", "02.flac"], tracks.Select(t => t.Path));
        Assert.Null(tracks[0].Sheet!.End);
        Assert.Equal(75, tracks[1].Sheet!.Start);
        Assert.Equal(Frames(3, 0, 0), tracks[1].Sheet!.End);
        Assert.Null(tracks[2].Sheet!.End);
    }

    /// <summary>Przerwa dopisana do poprzedniego pliku: INDEX 00 w jednym pliku, INDEX 01 w następnym.</summary>
    [Fact]
    public void SciezkaZaczetaWPoprzednimPlikuNalezyDoNastepnego()
    {
        var sheet = Parse("""
            FILE "01.wav" WAVE
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 00 04:20:30
            FILE "02.wav" WAVE
                INDEX 01 00:00:00
              TRACK 03 AUDIO
                INDEX 00 03:10:00
                INDEX 01 03:12:00
            """);

        var tracks = sheet.Tracks("a.cue", name => name);

        Assert.Equal([1, 2, 3], tracks.Select(t => t.Sheet!.Number));
        Assert.Equal(["01.wav", "02.wav", "02.wav"], tracks.Select(t => t.Path));
        Assert.Null(tracks[0].Sheet!.End);
        Assert.Equal(0, tracks[1].Sheet!.Start);
        Assert.Equal(Frames(3, 12, 0), tracks[1].Sheet!.End);
    }

    [Fact]
    public void SciezkiDanychIObrazyPlytyPomijane()
    {
        var sheet = Parse("""
            FILE "obraz.bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            FILE "dzwiek.wav" WAVE
              TRACK 02 AUDIO
                INDEX 01 00:00:00
              TRACK 03 MODE2/2352
                INDEX 01 05:00:00
              TRACK 04 AUDIO
                INDEX 01 06:00:00
            FILE "motorola.raw" MOTOROLA
              TRACK 05 AUDIO
                INDEX 01 00:00:00
            """);

        var tracks = sheet.Tracks("a.cue", name => name);

        Assert.Equal([2, 4], tracks.Select(t => t.Sheet!.Number));

        // Ścieżka danych wyznacza koniec poprzedniej.
        Assert.Equal(Frames(5, 0, 0), tracks[0].Sheet!.End);
        Assert.All(tracks, t => Assert.Equal("dzwiek.wav", t.Path));
    }

    [Fact]
    public void PlikBezIstniejacegoPlikuDzwiekowegoNieDajeUtworow()
    {
        var sheet = Parse(Rip);
        Assert.Empty(sheet.Tracks("a.cue", _ => null));
    }

    public static TheoryData<byte[], int?> Kodowania()
    {
        const string text = "TITLE \"Zażółć gęślą jaźń\"\nFILE \"ść.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:00\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        return new TheoryData<byte[], int?>
        {
            { [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)], null },
            { [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)], null },
            { [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)], null },
            { Encoding.UTF8.GetBytes(text), 1252 },
            { Encoding.GetEncoding(1250).GetBytes(text), 1250 },
        };
    }

    [Theory]
    [MemberData(nameof(Kodowania))]
    public void KodowanieZeZnacznikaPotemUtf8NaKoncuStronaKodowa(byte[] bytes, int? codePage)
    {
        var sheet = CueSheet.Parse(bytes, codePage);

        Assert.Equal("Zażółć gęślą jaźń", sheet.Title);
        Assert.Equal("ść.wav", sheet.Files[0].Name);
    }

    [Theory]
    [InlineData("TRACK 01 AUDIO\nINDEX 01 00:00:00", "ścieżka przed plikiem")]
    [InlineData("FILE a.wav WAVE\nTRACK 01 AUDIO\nINDEX 01 01:00:00\nTRACK 02 AUDIO\nINDEX 01 00:30:00", "czasy malejące")]
    [InlineData("FILE a.wav WAVE\nTRACK 01 AUDIO\nINDEX 01 01:00:00\nTRACK 02 AUDIO\nINDEX 01 01:00:00", "czasy równe")]
    [InlineData("FILE a.wav WAVE\nTRACK 01 AUDIO\nINDEX 01 01:00:00\nTRACK 02 AUDIO\nINDEX 00 00:59:00\nINDEX 01 02:00:00", "przerwa przed początkiem poprzedniego")]
    [InlineData("FILE a.wav WAVE\nTRACK 01 AUDIO\nINDEX 00 00:02:00\nINDEX 01 00:01:00", "INDEX 00 po INDEX 01")]
    [InlineData("FILE a.wav WAVE\nTRACK 01 AUDIO\nINDEX 01 00:60:00", "sekunda 60")]
    [InlineData("FILE a.wav WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:75", "klatka 75")]
    [InlineData("FILE a.wav WAVE\nTRACK 01 AUDIO\nINDEX 01 -1:00:00", "czas ujemny")]
    [InlineData("FILE a.wav WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00", "czas bez klatek")]
    [InlineData("FILE a.wav WAVE\nTRACK 01 AUDIO\nTITLE x", "ścieżka bez INDEX 01")]
    [InlineData("FILE a.wav WAVE\nTRACK 00 AUDIO\nINDEX 01 00:00:00", "numer zero")]
    [InlineData("FILE a.wav WAVE\nTRACK xx AUDIO\nINDEX 01 00:00:00", "numer nieliczbowy")]
    [InlineData("FILE \"a.wav WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:00", "niezamknięty cudzysłów")]
    [InlineData("INDEX 01 00:00:00", "indeks poza ścieżką")]
    public void ArkuszNiepoprawnyJestOdrzucany(string text, string why)
    {
        var failure = Record.Exception(() => Parse(text));
        Assert.True(failure is CueSheetException, $"{why}: {failure?.GetType().Name ?? "przyjęty"}");
    }

    [Fact]
    public void GraniceLiczbySciezekIRozmiaru()
    {
        var many = new StringBuilder("FILE a.wav WAVE\n");
        for (var i = 1; i <= 1000; i++) many.Append($"TRACK {i} AUDIO\nINDEX 01 {i / 60:00}:{i % 60:00}:00\n");
        Assert.Throws<CueSheetException>(() => Parse(many.ToString()));

        var fits = new StringBuilder("FILE a.wav WAVE\n");
        for (var i = 1; i <= 999; i++) fits.Append($"TRACK {i} AUDIO\nINDEX 01 {i / 60:00}:{i % 60:00}:00\n");
        Assert.Equal(999, Parse(fits.ToString()).Files[0].Tracks.Count);

        var big = new byte[CueSheet.MaxBytes + 1];
        big.AsSpan().Fill((byte)' ');
        Assert.Throws<CueSheetException>(() => CueSheet.Parse(big));
    }

    [Fact]
    public void DuzyPlikNieJestCzytanyDoPamieci()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cewka-{Guid.NewGuid():N}.cue");
        using (var stream = File.Create(path)) stream.SetLength(CueSheet.MaxBytes + 1);

        try
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<CueSheetException>(() => CueSheet.Load(path));
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < CueSheet.MaxBytes / 4);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(44100, 588)]
    [InlineData(48000, 640)]
    [InlineData(96000, 1280)]
    [InlineData(22050, 294)]
    public void KlatkaArkuszaToCalkowitaLiczbaProbek(int rate, long perFrame)
    {
        Assert.Equal(perFrame, SheetTrack.ToFrames(1, rate));
        Assert.Equal(perFrame * Frames(73, 59, 74), SheetTrack.ToFrames(Frames(73, 59, 74), rate));
    }

    /// <summary>Klatki z pliku kolejki albo klucza pamięci to liczby z zewnątrz: przeliczenie nie może się przepełnić.</summary>
    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(1000000000000000L)]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    public void PrzeliczenieKlatekNieDajeLiczbyUjemnej(long frames)
    {
        foreach (var rate in new[] { 8000, 44100, 192000, 768000, int.MaxValue })
        {
            var result = SheetTrack.ToFrames(frames, rate);
            Assert.InRange(result, 0, SheetTrack.ToFrames(CueSheet.MaxTime, rate));
        }

        var track = new SheetTrack { SheetPath = "a.cue", Number = 1, Start = 0, End = frames };
        Assert.True(track.Apply(new TrackMetadata { Path = "a.wav", Title = "a" }).Duration >= TimeSpan.Zero);
    }

    [Fact]
    public void ParserPrzyjmujeCzasyDoGranicy()
    {
        Assert.Equal(CueSheet.MaxTime, CueSheet.ParseTime("99999:59:74"));
        Assert.Throws<CueSheetException>(() => CueSheet.ParseTime("100000:00:00"));
    }

    [Fact]
    public void WycinekZeSkrajnymPoczatkiemNieGraOdPoczatkuPliku()
    {
        var file = TestAudioFiles.Wav(new float[4410 * 2], 2, 44100);
        using var slice = new Cewka.Audio.Decoding.SliceDecoder(
            Cewka.Audio.Decoding.DecoderFactory.Open(new MemoryStream(file), Cewka.Audio.Decoding.AudioFileFormat.Wav, true, 44100, 2),
            "a.wav", new SheetTrack { SheetPath = "a.cue", Number = 1, Start = 1000000000000000L });

        Assert.Equal(0, slice.Read(new float[2000]));
        Assert.Equal(0, slice.TotalFrames);

        slice.Seek(long.MaxValue);
        Assert.Equal(0, slice.Read(new float[2000]));
    }

    [Fact]
    public void KluczPamieciPodrecznejObejmujeZakres()
    {
        var track = new SheetTrack { SheetPath = "a.cue", Number = 2, Start = 100, End = 250 };
        var last = track with { Start = 250, End = null };

        var key = track.CacheKey(@"C:\a.flac");
        Assert.NotEqual(@"C:\a.flac", key);
        Assert.NotEqual(key, last.CacheKey(@"C:\a.flac"));

        Assert.True(SheetTrack.TryParseCacheKey(key, out var path, out var start, out var end));
        Assert.Equal((@"C:\a.flac", 100L, (long?)250), (path, start, end));

        Assert.True(SheetTrack.TryParseCacheKey(last.CacheKey("/m/a.flac"), out path, out start, out end));
        Assert.Equal(("/m/a.flac", 250L, (long?)null), (path, start, end));

        Assert.False(SheetTrack.TryParseCacheKey(@"C:\a.flac", out path, out _, out _));
        Assert.Equal(@"C:\a.flac", path);
        Assert.Equal(@"C:\a.flac", SheetTrack.FileOfCacheKey(key));
        Assert.False(SheetTrack.TryParseCacheKey("a\0x-1", out _, out _, out _));
        Assert.False(SheetTrack.TryParseCacheKey("a\05-3", out _, out _, out _));
        Assert.False(SheetTrack.TryParseCacheKey($"a\0{CueSheet.MaxTime + 1}-", out _, out _, out _));
        Assert.False(SheetTrack.TryParseCacheKey($"a\00-{CueSheet.MaxTime + 1}", out _, out _, out _));
    }

    [Fact]
    public void ZnacznikiArkuszaNadpisujaZnacznikiPliku()
    {
        var file = new TrackMetadata
        {
            Path = "a.flac", Title = "Cała płyta", Artist = "Z pliku", Album = "Album z pliku", AlbumArtist = "Wykonawca pliku",
            Year = 2001, TrackNumber = 1, Duration = TimeSpan.FromMinutes(30), SampleRate = 44100, BitDepth = 16, Codec = "FLAC",
            ReplayGainTrack = -3, ReplayGainTrackPeak = 0.5, ReplayGainAlbum = -4, ReplayGainAlbumPeak = 0.6,
        };

        var sheet = Parse(Rip).Tracks("a.cue", _ => "a.flac");

        var first = sheet[0].Sheet!.Apply(file);
        Assert.Equal("So What", first.Title);
        Assert.Equal("Miles Davis", first.Artist);
        Assert.Equal("Miles Davis", first.AlbumArtist);
        Assert.Equal("Kind of Blue", first.Album);
        Assert.Equal(1959, first.Year);
        Assert.Equal(1, first.TrackNumber);
        Assert.Equal(2, first.DiscNumber);
        Assert.Equal(-6.5, first.ReplayGainTrack);
        Assert.Equal(-7.25, first.ReplayGainAlbum);
        Assert.Equal(TimeSpan.FromSeconds(9 * 60 + 24), first.Duration);
        Assert.Equal("FLAC", first.Codec);
        Assert.Equal(16, first.BitDepth);

        // Bez wzmocnienia utworu w arkuszu: wzmocnienie pliku dotyczy całej płyty, a nie utworu.
        var second = sheet[1].Sheet!.Apply(file);
        Assert.Equal("Miles Davis Sextet", second.Artist);
        Assert.Null(second.ReplayGainTrack);

        // Ostatni do końca pliku.
        var third = sheet[2].Sheet!.Apply(file);
        Assert.Equal(30 * 60 - (19 * 60 + 10 + 74 / 75.0), third.Duration.TotalSeconds, 5);

        // Arkusz bez tytułów: nazwa z pliku i numer ścieżki, żeby utwory dało się rozróżnić.
        var bare = new SheetTrack { SheetPath = "a.cue", Number = 4, Start = 10, End = 20 }.Apply(file);
        Assert.Equal("Cała płyta (04)", bare.Title);
        Assert.Equal("Z pliku", bare.Artist);
        Assert.Equal("Album z pliku", bare.Album);
        Assert.Equal(-4, bare.ReplayGainAlbum);
    }

    /// <summary>Arkusz od kogokolwiek: odczyt kończy się arkuszem albo opisanym wyjątkiem, w skończonym czasie.</summary>
    [Fact]
    public void UszkodzonyArkuszKonczySieWynikiemAlboOpisanymBledem()
    {
        var random = new Random(20261118);
        var seed = Encoding.UTF8.GetBytes(Rip);
        var accepted = 0;

        for (var round = 0; round < 3000; round++)
        {
            var data = (byte[])seed.Clone();

            for (var edits = random.Next(1, 6); edits > 0; edits--)
            {
                switch (random.Next(4))
                {
                    case 0:
                        data[random.Next(data.Length)] = (byte)random.Next(256);
                        break;
                    case 1:
                        data[random.Next(data.Length)] = (byte)"\":\n 0123456789-\"".AsSpan()[random.Next(16)];
                        break;
                    case 2:
                    {
                        var from = random.Next(data.Length);
                        data = [.. data[..from], .. data[Math.Min(data.Length, from + random.Next(1, 40))..]];
                        break;
                    }
                    default:
                    {
                        var from = random.Next(data.Length);
                        var length = Math.Min(random.Next(1, 80), data.Length - from);
                        data = [.. data[..(from + length)], .. data[from..]];
                        break;
                    }
                }

                if (data.Length == 0) data = [(byte)'\n'];
            }

            try
            {
                var sheet = CueSheet.Parse(data);
                var tracks = sheet.Tracks("a.cue", name => name);

                foreach (var file in sheet.Files)
                for (var i = 1; i < file.Tracks.Count; i++)
                    Assert.True(file.Tracks[i].Start > file.Tracks[i - 1].Start, $"runda {round}: czasy nie rosną");

                Assert.All(tracks, t => Assert.True(t.Sheet!.Start >= 0 && (t.Sheet.End is null || t.Sheet.End > t.Sheet.Start)));
                accepted++;
            }
            catch (CueSheetException)
            {
            }
        }

        // Część mutacji trafia w komentarze i tytuły; odczyt nie może odrzucać wszystkiego.
        Assert.InRange(accepted, 100, 2999);
    }
}
