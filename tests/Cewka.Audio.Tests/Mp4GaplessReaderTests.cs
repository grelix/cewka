using System.Buffers.Binary;
using System.Diagnostics;
using Cewka.Audio.Decoding;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Opóźnienie kodera i długość nagrania z atomów MP4, bez dekodera systemu.</summary>
public sealed class Mp4GaplessReaderTests
{
    private static Mp4Gapless? Read(byte[] file) => Mp4GaplessReader.Read(new MemoryStream(file));

    /// <summary>Lista edycji mp4mux zaczyna się od zera; długość z niej i z tablicy czasów obejmuje rozbieg.</summary>
    [Fact]
    public void ListaEdycjiOdZeraZostawiaRozbieg()
    {
        var info = Read(Mp4Samples.First);

        // Rozbieg i nagranie; dopełnienie ostatniej ramki mp4mux odciął w tablicy czasów.
        Assert.Equal(new Mp4Gapless(Mp4Samples.Rate, 0, Mp4Samples.Delay + Mp4Samples.FirstFrames, Mp4GaplessSource.EditList), info);
    }

    [Fact]
    public void BezListyEdycjiDlugoscPochodziZTablicyCzasow()
    {
        var file = Mp4Samples.First;
        var edts = file.AsSpan().IndexOf("edts"u8);
        "free"u8.CopyTo(file.AsSpan(edts));

        Assert.Equal(new Mp4Gapless(Mp4Samples.Rate, 0, Mp4Samples.Delay + Mp4Samples.FirstFrames, Mp4GaplessSource.SampleTable), Read(file));
    }

    [Fact]
    public void ZnacznikITunesPodajeRozbiegIDlugosc()
    {
        var file = Mp4Samples.WithSmpb(Mp4Samples.First, Mp4Samples.Delay, Mp4Samples.Padding(Mp4Samples.FirstFrames), Mp4Samples.FirstFrames);

        Assert.Equal(new Mp4Gapless(Mp4Samples.Rate, Mp4Samples.Delay, Mp4Samples.FirstFrames, Mp4GaplessSource.ITunSmpb), Read(file));
    }

    /// <summary>Długość edycji w skali filmu 1800/s jest zaokrąglona; dokładny jest koniec tablicy czasów.</summary>
    [Fact]
    public void ListaEdycjiPodajeRozbiegIDokladnaDlugosc()
    {
        var file = Mp4Samples.WithEditList(Mp4Samples.First, Mp4Samples.Delay, Mp4Samples.FirstFrames);

        Assert.Equal(new Mp4Gapless(Mp4Samples.Rate, Mp4Samples.Delay, Mp4Samples.FirstFrames, Mp4GaplessSource.EditList), Read(file));
    }

    [Fact]
    public void ZnacznikITunesMaPierwszenstwoPrzedListaEdycji()
    {
        var file = Mp4Samples.WithEditList(Mp4Samples.First, 2048, 60000);
        file = Mp4Samples.WithSmpb(file, Mp4Samples.Delay, Mp4Samples.Padding(Mp4Samples.FirstFrames), Mp4Samples.FirstFrames);

        Assert.Equal(Mp4GaplessSource.ITunSmpb, Read(file)!.Value.Source);
        Assert.Equal(Mp4Samples.Delay, Read(file)!.Value.Delay);
    }

    [Theory]
    [InlineData(1024, 80000)] // dłuższy niż ścieżka
    [InlineData(1024, 0)]
    [InlineData(60 * 44100 + 1, 1000)] // rozbieg ponad minutę
    public void NiewiarygodnyZnacznikUstepujeLiscieEdycji(long delay, long length)
    {
        var file = Mp4Samples.WithEditList(Mp4Samples.First, Mp4Samples.Delay, Mp4Samples.FirstFrames);
        file = Mp4Samples.WithSmpb(file, delay, 0, length);

        Assert.Equal(Mp4GaplessSource.EditList, Read(file)!.Value.Source);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 00000000 00000400")]
    [InlineData(" 00000000 0000040G 00000290 0000000000011170")]
    [InlineData(" 00000000 000000400 00000290 0000000000011170")]
    [InlineData(" 00000000 00000400 00000290 FFFFFFFFFFFFFFFF")]
    public void ZnieksztalconyZnacznikJestPomijany(string text)
    {
        var file = Mp4Samples.WithSmpbText(Mp4Samples.First, text);

        // Zostaje lista edycji wzorca, od zera.
        Assert.Equal((Mp4GaplessSource.EditList, 0L), (Read(file)!.Value.Source, Read(file)!.Value.Delay));
    }

    [Fact]
    public void PlikInnyNizMp4NieMaOpisu()
    {
        Assert.Null(Read(TestAudioFiles.Wav(new float[2 * 4410], 2, 44100)));
        Assert.Null(Read(new byte[64]));
    }

    [Fact]
    public void AtomWiekszyNizRodzicJestOpisanymBledem()
    {
        var file = Mp4Samples.First;
        var moov = Mp4Samples.Find(file, 0, file.Length, "moov")!.Value;
        var trak = Mp4Samples.Find(file, moov.Start + 8, moov.End, "trak")!.Value;
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(trak.Start), 0x7FFF_0000);

        var ex = Assert.Throws<InvalidDataException>(() => Read(file));
        Assert.Contains("trak", ex.Message);
    }

    [Fact]
    public void TablicaCzasowDluzszaNizAtomJestOpisanymBledem()
    {
        var file = Mp4Samples.First;
        var stts = file.AsSpan().IndexOf("stts"u8);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(stts + 8), uint.MaxValue);

        var ex = Assert.Throws<InvalidDataException>(() => Read(file));
        Assert.Contains("tablica czasów", ex.Message);
    }

    /// <summary>Wiele milionów pustych atomów najwyższego poziomu nie zajmuje czytnika bez końca.</summary>
    [Fact]
    public void LiczbaAtomowMaGranice()
    {
        var file = new byte[16 + 8 * 100_000];
        Mp4Samples.First.AsSpan(0, 16).CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file, 16);
        for (var position = 16; position < file.Length; position += 8)
        {
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(position), 8);
            "free"u8.CopyTo(file.AsSpan(position + 4));
        }

        Assert.Throws<InvalidDataException>(() => Read(file));
    }

    /// <summary>
    /// Losowo uszkodzony wzorzec ze znacznikiem i listą edycji (ziarno stałe): czytnik kończy
    /// wynikiem albo opisanym błędem, nigdy innym wyjątkiem, i nie wychodzi poza granicę czasu.
    /// </summary>
    [Fact]
    public void UszkodzonePlikiKonczaSieWynikiemAlboOpisanymBledem()
    {
        var source = Mp4Samples.WithEditList(Mp4Samples.First, Mp4Samples.Delay, Mp4Samples.FirstFrames);
        source = Mp4Samples.WithSmpb(source, Mp4Samples.Delay, Mp4Samples.Padding(Mp4Samples.FirstFrames), Mp4Samples.FirstFrames);
        var moov = Mp4Samples.Find(source, 0, source.Length, "moov")!.Value;

        var random = new Random(20261118);
        var watch = Stopwatch.StartNew();
        var failures = new List<string>();

        for (var round = 0; round < 3000; round++)
        {
            var file = source.ToArray();
            var changes = random.Next(1, 6);
            for (var i = 0; i < changes; i++)
            {
                var at = random.Next(moov.Start, moov.End);
                file[at] = random.Next(4) == 0 ? (byte)0xFF : (byte)random.Next(256);
            }

            if (random.Next(5) == 0) file = file[..random.Next(8, file.Length)];

            try
            {
                if (Read(file) is { } info)
                {
                    Assert.InRange(info.Timescale, 1, 1_000_000);
                    Assert.InRange(info.Delay, 0, 60L * info.Timescale);
                    Assert.True(info.Length > 0, $"runda {round}: długość {info.Length}");
                }
            }
            catch (InvalidDataException) { }
            catch (EndOfStreamException) { failures.Add($"runda {round}: koniec strumienia"); }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                failures.Add($"runda {round}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures.Take(10)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"czas {watch.Elapsed}");
    }
}
