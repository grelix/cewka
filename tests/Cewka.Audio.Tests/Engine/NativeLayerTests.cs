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
}
