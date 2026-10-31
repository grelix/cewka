using Cewka.Audio.Decoding;
using Cewka.Audio.Devices;
using Xunit;

namespace Cewka.Audio.Tests.Engine;

/// <summary>Warstwa natywna: puste wyjście i dekoder miniaudio czytający strumień.</summary>
public class NativeLayerTests
{
    /// <summary>Puste wyjście pobiera próbki w tempie rzeczywistym, nie grając ich nigdzie.</summary>
    [Fact]
    public void NullOutputPullsAudioWithoutASoundCard()
    {
        Assert.True(AudioDevice.UseNullOutput);

        var periods = 0;
        using var device = new AudioDevice(0, 2);
        device.Render = buffer =>
        {
            buffer.Clear();
            Interlocked.Increment(ref periods);
        };

        device.Start();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref periods) < 3 && clock.ElapsedMilliseconds < 5000) Thread.Sleep(5);

        device.Stop();
        Assert.True(Volatile.Read(ref periods) >= 3);

        Assert.False(device.IsRunning);
    }

    /// <summary>Po pierwszym użyciu dźwięku zaplecza nie da się już przełączyć.</summary>
    [Fact]
    public void BackendCannotChangeOnceInUse()
    {
        _ = AudioDeviceList.Enumerate();

        Assert.Throws<InvalidOperationException>(() => AudioDevice.UseNullOutput = false);
        Assert.True(AudioDevice.UseNullOutput);
    }

    /// <summary>Urządzenia o zapisanej nazwie nie ma: otwiera się domyślne, z adnotacją.</summary>
    [Fact]
    public void MissingNamedDeviceFallsBackToDefault()
    {
        using var device = AudioDevice.Open("Urządzenie, którego nie ma", 0, 2);

        Assert.True(device.IsFallback);
        Assert.True(device.SampleRate > 0);
    }

    /// <summary>
    /// Przerwane nagranie: nagłówek WAV obiecuje 10 s, w pliku jest 1 s. Długość ma wynikać
    /// z pliku, nie z nagłówka, inaczej pasek postępu kończy się w 10% utworu.
    /// </summary>
    [Fact]
    public void TruncatedWavReportsTheLengthOfItsData()
    {
        const int rate = 48000;
        const int channels = 2;
        var stream = new MemoryStream();

        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var declared = rate * channels * 2 * 10;
            var actual = rate * channels * 2;

            writer.Write("RIFF"u8);
            writer.Write(36 + declared);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)channels);
            writer.Write(rate);
            writer.Write(rate * channels * 2);
            writer.Write((short)(channels * 2));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(declared);
            writer.Write(new byte[actual]);
        }

        stream.Position = 0;
        using var decoder = DecoderFactory.Open(stream, AudioFileFormat.Wav, ownsStream: true, rate, channels);

        Assert.Equal(rate, decoder.TotalFrames);
    }

    /// <summary>Przewinięcie dekodera miniaudio ustawia pozycję i czyta dalej od celu.</summary>
    [Fact]
    public void WavSeekLandsOnTheTarget()
    {
        const int rate = 48000;
        var stream = new MemoryStream();

        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var frames = rate * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + frames * 2);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(rate);
            writer.Write(rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(frames * 2);

            // Rampa: wartość próbki mówi, gdzie leży.
            for (var i = 0; i < frames; i++) writer.Write((short)(i / 4));
        }

        stream.Position = 0;
        using var decoder = DecoderFactory.Open(stream, AudioFileFormat.Wav, ownsStream: true, rate, 1);

        decoder.Seek(rate);
        Assert.Equal(rate, decoder.Position);

        var buffer = new float[16];
        Assert.Equal(16, decoder.Read(buffer));
        Assert.Equal(rate / 4 / 32768f, buffer[0], 1e-6f);
    }
}
