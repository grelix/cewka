using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace Cewka.AudioProbe;

/// <summary>
/// Produces a short Opus file. The music library used for testing has none, and leaving
/// that decoder unexercised would mean shipping a code path nobody had ever run.
/// </summary>
internal static class OpusSample
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int Seconds = 5;

    public static string Create()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cewka-test-{Guid.NewGuid():N}.opus");

        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = 96000;

        using var file = File.Create(path);
        var writer = new OpusOggWriteStream(encoder, file, null, SampleRate);

        // Dwa tony rozlozone miedzy kanaly, zeby bylo widac, czy nie zamienily sie miejscami.
        var samples = new float[SampleRate * Channels * Seconds];
        for (var i = 0; i < SampleRate * Seconds; i++)
        {
            var t = i / (double)SampleRate;
            samples[i * 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * t));
            samples[i * 2 + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 660 * t));
        }

        writer.WriteSamples(samples, 0, samples.Length);
        writer.Finish();

        return path;
    }
}
