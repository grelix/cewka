using System.Text.Json;
using Cewka.Audio.Dsp;
using Cewka.Audio.Playback;
using Xunit;

namespace Cewka.Audio.Tests;

/// <summary>Wzmocnienie normalizacji z tagów i z własnej analizy, z ochroną szczytów.</summary>
public sealed class LoudnessServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cewka-testy-" + Guid.NewGuid().ToString("N"));

    public LoudnessServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>
    /// Tag +12 dB przy celu −14 LUFS dałby +16 dB, a szczyt 0,9 zostawia niespełna 1 dB zapasu:
    /// wzmocnienie zostaje ścięte tak, żeby szczyt wypadł na progu limitera.
    /// </summary>
    [Fact]
    public void TaggedGainIsLimitedByThePeak()
    {
        var gain = LoudnessService.FromReplayGain(12, peak: 0.9, targetLufs: -14);

        var ceiling = -20 * Math.Log10(0.9) - LoudnessService.PeakHeadroomDb;
        Assert.Equal(ceiling, gain.GainDecibels, precision: 6);
        Assert.True(gain.GainDecibels <= -0.08);

        // Utwór gra ciszej od celu o tyle, o ile ścięto wzmocnienie.
        Assert.Equal(4 - (16 - ceiling), gain.ProgrammeLevelDecibels, precision: 6);
    }

    /// <summary>Muzyka poważna −30 LUFS ze szczytem −1 dBFS nie dostaje dodatniego wzmocnienia.</summary>
    [Fact]
    public void MeasuredGainIsLimitedByThePeak()
    {
        var gain = LoudnessService.FromMeasurement(-30, peak: Math.Pow(10, -1.0 / 20), targetLufs: -18);

        Assert.InRange(gain.GainDecibels, -1e-9, 1e-9);
        Assert.Equal(-12, gain.ProgrammeLevelDecibels, precision: 6);
    }

    /// <summary>Ściszanie nie potrzebuje ochrony szczytów i zostaje bez zmian.</summary>
    [Fact]
    public void NegativeGainIsNotTouched()
    {
        var gain = LoudnessService.FromMeasurement(-9, peak: 1.0, targetLufs: -18);

        Assert.Equal(-9, gain.GainDecibels, precision: 6);
        Assert.Equal(0, gain.ProgrammeLevelDecibels, precision: 6);
    }

    /// <summary>Bez znanego szczytu wzmocnienie z tagu zostaje, a ochronę przejmuje limiter.</summary>
    [Fact]
    public void UnknownPeakLeavesTheGain()
    {
        var gain = LoudnessService.FromReplayGain(6, peak: null, targetLufs: -18);
        Assert.Equal(6, gain.GainDecibels, precision: 6);
    }

    [Fact]
    public void AnalyserReportsTheSamplePeak()
    {
        var analyser = new LoudnessAnalyser(48000, 2);
        var buffer = Signals.Sine(1000, 0.5, 48000, 48000);
        buffer[1001] = -0.75f;
        analyser.Add(buffer, 48000);

        Assert.Equal(0.75, analyser.SamplePeak, precision: 6);
    }

    /// <summary>Wpis z pomiaru szczytu ogranicza wzmocnienie utworu z pamięci podręcznej.</summary>
    [Fact]
    public void CachedMeasurementIsLimitedByItsPeak()
    {
        var file = CreateFile("dynamic.flac");
        using var service = ServiceWithCache(file, integratedLufs: -30, samplePeak: 0.5);

        var gain = service.GetTrackGain(new QueueEntry { Path = file });

        Assert.NotNull(gain);
        Assert.Equal(-20 * Math.Log10(0.5) - 1, gain!.Value.GainDecibels, precision: 6);
    }

    /// <summary>Wpis sprzed pomiaru szczytu nie podbija utworu, dopóki analiza go nie uzupełni.</summary>
    [Fact]
    public void CachedRecordWithoutPeakIsNotBoosted()
    {
        var file = CreateFile("old.flac");
        using var service = ServiceWithCache(file, integratedLufs: -30, samplePeak: null);

        var gain = service.GetTrackGain(new QueueEntry { Path = file });

        Assert.NotNull(gain);
        Assert.Equal(0, gain!.Value.GainDecibels, precision: 6);
        Assert.Equal(-12, gain.Value.ProgrammeLevelDecibels, precision: 6);
    }

    // ---------- pomocnicze ----------

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    private LoudnessService ServiceWithCache(string file, double integratedLufs, double? samplePeak)
    {
        var info = new FileInfo(file);
        var record = new Dictionary<string, object?>
        {
            ["path"] = file,
            ["length"] = info.Length,
            ["modifiedTicks"] = info.LastWriteTimeUtc.Ticks,
            ["integratedLufs"] = integratedLufs,
        };
        if (samplePeak is not null) record["samplePeak"] = samplePeak;

        var cache = Path.Combine(_directory, "loudness.json");
        File.WriteAllText(cache, JsonSerializer.Serialize(new[] { record }));
        return new LoudnessService(cache) { TargetLufs = -18 };
    }
}
