using System.Diagnostics;
using Cewka.Audio.Decoding;
using Cewka.Audio.Metadata;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Próby na prawdziwych potokach i urządzeniach, które istnieją tylko w Linuksie.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Potoki nazwane i urządzenia w folderach są tylko w systemie Linux.";
    }
}

/// <summary>
/// W Linuksie w folderze z muzyką może leżeć potok nazwany albo dowiązanie do urządzenia, także
/// z rozszerzeniem pliku dźwiękowego (na przykład po rozpakowaniu archiwum). Otwarcie potoku bez
/// piszącego nie wraca nigdy, a urządzenie oddaje dane bez końca, więc żaden wątek nie może po nie sięgnąć.
/// </summary>
public sealed class RegularFileTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cewka-pliki-" + Guid.NewGuid().ToString("N"));

    public RegularFileTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Fifo(string name)
    {
        var path = Path.Combine(_directory, name);

        using var process = Process.Start(new ProcessStartInfo("mkfifo", [path]) { UseShellExecute = false })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);

        return path;
    }

    private string LinkTo(string target, string name)
    {
        var path = Path.Combine(_directory, name);
        File.CreateSymbolicLink(path, target);
        return path;
    }

    private static async Task<T> InTime<T>(Func<T> work)
    {
        var task = Task.Run(work);
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(Limit)));
        return await task;
    }

    [Fact]
    public void ZwyklyPlikJestZwyklymPlikiem()
    {
        var path = Path.Combine(_directory, "a.wav");
        File.WriteAllBytes(path, TestAudioFiles.Seed("wav"));

        Assert.True(RegularFile.Is(path));
        Assert.Equal(AudioFileFormat.Wav, AudioFileFormatDetector.Detect(path));
    }

    /// <summary>Brak pliku ma zgłosić ten, kto spróbuje go otworzyć, zwykłą drogą.</summary>
    [Fact]
    public void BrakPlikuNieJestOdmowa()
    {
        Assert.True(RegularFile.Is(Path.Combine(_directory, "nie-ma.mp3")));
    }

    [LinuxFact]
    public async Task PotokNazwanyNieJestOtwierany()
    {
        var path = Fifo("potok.mp3");

        Assert.False(RegularFile.Is(path));
        Assert.Equal(AudioFileFormat.Unknown, await InTime(() => AudioFileFormatDetector.Detect(path)));
        Assert.Equal("potok", (await InTime(() => MetadataReader.Read(path))).Title);
        Assert.Null(await InTime(() => MetadataReader.ReadCover(path)));

        var ex = await InTime(() => Assert.Throws<AudioException>(() => DecoderFactory.Open(path, 48000, 2)));
        Assert.Equal(DecoderFailure.UnrecognisedFormat, ex.Reason);
    }

    [LinuxFact]
    public async Task DowiazanieDoUrzadzeniaNieJestOtwierane()
    {
        var path = LinkTo("/dev/zero", "zero.wav");

        Assert.False(RegularFile.Is(path));
        Assert.Equal(AudioFileFormat.Unknown, await InTime(() => AudioFileFormatDetector.Detect(path)));
        Assert.Equal("zero", (await InTime(() => MetadataReader.Read(path))).Title);

        await InTime(() => Assert.Throws<AudioException>(() => DecoderFactory.Open(path, 48000, 2)));
    }

    [LinuxFact]
    public async Task DowiazanieDoPotokuNieJestOtwierane()
    {
        var path = LinkTo(Fifo("prawdziwy"), "dowiazanie.flac");

        Assert.False(RegularFile.Is(path));
        Assert.Equal(AudioFileFormat.Unknown, await InTime(() => AudioFileFormatDetector.Detect(path)));
    }

    /// <summary>Dowiązanie do zwykłego pliku gra jak plik.</summary>
    [LinuxFact]
    public void DowiazanieDoPlikuJestPlikiem()
    {
        var target = Path.Combine(_directory, "cel.wav");
        File.WriteAllBytes(target, TestAudioFiles.Seed("wav"));

        var path = LinkTo(target, "dowiazanie.wav");

        Assert.True(RegularFile.Is(path));
        Assert.Equal(AudioFileFormat.Wav, AudioFileFormatDetector.Detect(path));
    }
}
