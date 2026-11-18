using Cewka.Audio.Decoding;
using Cewka.Audio.Metadata;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>
/// Plik od słuchacza jest niezaufany. Uszkodzony albo spreparowany może się nie otworzyć albo
/// urwać w połowie, ale nie może zawiesić dekodera, zająć pamięci bez miary ani grać bez końca.
/// Losowanie ma stałe ziarno, więc porażka wskazuje rundę, którą da się powtórzyć.
/// </summary>
public sealed class DecoderFuzzTests
{
    private const int Seed = 20261112;
    private const int Rounds = 400;

    private const int OutputRate = 48000;
    private const int OutputChannels = 2;

    /// <summary>Ziarna trwają pół sekundy; zapas na wolną maszynę jest wielokrotny.</summary>
    private static readonly TimeSpan CaseLimit = TimeSpan.FromSeconds(30);

    /// <summary>Plik kilku kilobajtów nie ma z czego zagrać dziesięciu minut.</summary>
    private const long MaxFrames = OutputRate * 600L;

    /// <summary>Pamięć zarządzana zajęta przez jedno otwarcie i odczyt.</summary>
    private const long MaxAllocated = 192L * 1024 * 1024;

    [Theory]
    [InlineData("wav", AudioFileFormat.Wav)]
    [InlineData("mp3", AudioFileFormat.Mp3)]
    [InlineData("flac", AudioFileFormat.Flac)]
    [InlineData("ogg", AudioFileFormat.OggVorbis)]
    [InlineData("opus", AudioFileFormat.Opus)]
    [InlineData("wv", AudioFileFormat.WavPack)]
    [InlineData("aiff", AudioFileFormat.Aiff)]
    public async Task UszkodzonyPlikNieZawieszaDekoderaAniNieZajmujePamieci(string kind, AudioFileFormat format)
    {
        var seed = TestAudioFiles.Seed(kind);
        var random = new Random(Seed);

        // Ziarno bez zmian musi grać: inaczej test nie sprawdzałby niczego.
        Assert.True(Exercise(seed, format, [new Step(null, 1000)]).Frames > OutputRate / 4, "ziarno nie gra");

        for (var round = 0; round < Rounds; round++)
        {
            var data = Mutate(random, seed, ogg: kind is "ogg" or "opus");
            var plan = Plan(random);

            var task = Task.Run(() => Exercise(data, format, plan));

            Assert.True(
                await Task.WhenAny(task, Task.Delay(CaseLimit)) == task,
                $"{kind}, runda {round}: dekoder nie skończył w {CaseLimit.TotalSeconds:0} s");

            var (frames, allocated) = await task;
            Assert.True(frames <= MaxFrames, $"{kind}, runda {round}: dekoder gra bez końca");
            Assert.True(allocated < MaxAllocated, $"{kind}, runda {round}: dekoder zajął {allocated / (1024 * 1024)} MB");
        }
    }

    /// <summary>
    /// Znaczniki i okładkę czyta osobna biblioteka, na innym wątku niż dekoder. Ma oddać cokolwiek
    /// albo nic, w skończonym czasie.
    /// </summary>
    [Theory]
    [InlineData("wav")]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("ogg")]
    [InlineData("opus")]
    [InlineData("m4a")]
    [InlineData("wv")]
    [InlineData("aiff")]
    public async Task UszkodzonyPlikNieZawieszaCzytnikaZnacznikow(string kind)
    {
        var seed = TestAudioFiles.Tagged(kind);
        var random = new Random(Seed + 1);

        var directory = Path.Combine(Path.GetTempPath(), "cewka-fuzz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "plik." + kind);

            for (var round = 0; round < Rounds; round++)
            {
                File.WriteAllBytes(path, Mutate(random, seed, ogg: kind is "ogg" or "opus", headerBias: true));

                var task = Task.Run(() =>
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();

                    var metadata = MetadataReader.Read(path);
                    var cover = MetadataReader.ReadCover(path);

                    Assert.False(string.IsNullOrEmpty(metadata.Title));
                    Assert.True(cover is null || cover.Length <= MetadataReader.MaxCoverBytes);

                    return GC.GetAllocatedBytesForCurrentThread() - before;
                });

                Assert.True(
                    await Task.WhenAny(task, Task.Delay(CaseLimit)) == task,
                    $"{kind}, runda {round}: czytnik znaczników nie skończył w {CaseLimit.TotalSeconds:0} s");

                var allocated = await task;
                Assert.True(allocated < MaxAllocated, $"{kind}, runda {round}: czytnik znaczników zajął {allocated / (1024 * 1024)} MB");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private readonly record struct Step(double? SeekTo, int Reads);

    private static Step[] Plan(Random random)
    {
        var steps = new Step[random.Next(1, 5)];

        for (var i = 0; i < steps.Length; i++)
        {
            // Cel przewinięcia jako ułamek długości; powyżej jedności wypada za końcem pliku.
            double? seek = random.Next(3) == 0 ? null : random.NextDouble() * 1.5;
            steps[i] = new Step(seek, random.Next(1, 12));
        }

        // Ostatni krok czyta do końca.
        steps[^1] = steps[^1] with { Reads = int.MaxValue };
        return steps;
    }

    /// <summary>Otwiera, przewija i czyta tak jak silnik. Zwraca liczbę oddanych ramek i zajętą pamięć.</summary>
    private static (long Frames, long Allocated) Exercise(byte[] data, AudioFileFormat format, Step[] plan)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        long frames = 0;

        try
        {
            using var decoder = DecoderFactory.Open(
                new MemoryStream(data), format, ownsStream: true, OutputRate, OutputChannels);

            Assert.Equal(OutputRate, decoder.SampleRate);
            Assert.Equal(OutputChannels, decoder.Channels);

            var buffer = new float[4096 * OutputChannels];

            foreach (var step in plan)
            {
                if (step.SeekTo is { } fraction && decoder.CanSeek)
                {
                    var length = decoder.TotalFrames > 0 ? decoder.TotalFrames : OutputRate;
                    decoder.Seek((long)(fraction * length));
                }

                for (var read = 0; read < step.Reads; read++)
                {
                    var got = decoder.Read(buffer);

                    Assert.InRange(got, 0, 4096);
                    Assert.True(decoder.Position >= 0, $"pozycja {decoder.Position}");

                    frames += got;

                    if (got < 4096 || frames > MaxFrames) break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not Xunit.Sdk.XunitException)
        {
            // Silnik kończy wtedy utwór: każdy inny wyjątek dekodera łapie wątek dekodujący.
        }

        return (frames, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static readonly uint[] Extremes =
        [0, 1, 0x7F, 0x80, 0xFF, 0x7FFF, 0x8000, 0xFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0x00FFFFFF, 0xFFFFFF00];

    private static byte[] Mutate(Random random, byte[] seed, bool ogg, bool headerBias = false)
    {
        var data = (byte[])seed.Clone();

        for (var edits = random.Next(1, 5); edits > 0; edits--)
        {
            // Początek pliku niesie nagłówki i znaczniki; tam błąd sięga najgłębiej.
            int At(int room) => Math.Max(0, random.Next(headerBias || random.Next(2) == 0 ? Math.Min(512, data.Length) : data.Length) - room);

            switch (random.Next(6))
            {
                case 0:
                    for (var flips = random.Next(1, 12); flips > 0; flips--)
                        data[At(0)] ^= (byte)(1 << random.Next(8));
                    break;

                case 1:
                    // Pole liczbowe o skrajnej wartości, w obu kolejnościach bajtów.
                    if (data.Length >= 4)
                    {
                        var value = Extremes[random.Next(Extremes.Length)];
                        var bytes = BitConverter.GetBytes(value);
                        if (random.Next(2) == 0) Array.Reverse(bytes);
                        bytes.AsSpan(0, random.Next(1, 5)).CopyTo(data.AsSpan(At(4)));
                    }
                    break;

                case 2:
                    data = data[..random.Next(data.Length + 1)];
                    break;

                case 3:
                    // Wycięty kawałek.
                    if (data.Length > 8)
                    {
                        var from = At(1);
                        var length = Math.Min(random.Next(1, 200), data.Length - from);
                        data = [.. data[..from], .. data[(from + length)..]];
                    }
                    break;

                case 4:
                    // Powtórzony kawałek.
                    if (data.Length > 8)
                    {
                        var from = At(1);
                        var length = Math.Min(random.Next(1, 400), data.Length - from);
                        data = [.. data[..(from + length)], .. data[from..]];
                    }
                    break;

                default:
                    if (data.Length > 0) random.NextBytes(data.AsSpan(At(16), Math.Min(random.Next(1, 16), data.Length)));
                    break;
            }

            if (data.Length == 0) break;
        }

        // Strona ze złą sumą kontrolną jest odrzucana w całości; z poprawioną uszkodzenie dochodzi do dekodera.
        if (ogg && random.Next(4) != 0) TestAudioFiles.OggWriter.FixChecksums(data);

        return data;
    }
}
