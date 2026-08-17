using System.Runtime.InteropServices;
using System.Text;
using Cewka.Audio.Interop;

namespace Cewka.Audio.Devices;

/// <summary>
/// An open playback device. The <see cref="Render"/> delegate is invoked from the audio
/// thread and must fill the buffer with interleaved samples.
/// <para>
/// Everything that runs inside <see cref="Render"/> shares the constraints of any realtime
/// audio callback: no allocation, no locks that another thread can hold for long, no
/// blocking. Breaking those rules does not throw — it produces dropouts.
/// </para>
/// </summary>
public sealed unsafe class AudioDevice : IDisposable
{
    /// <summary>Fills one period of audio. The span length is frames × channels.</summary>
    public delegate void RenderCallback(Span<float> buffer);

    private GCHandle _self;
    private nint _handle;
    private bool _disposed;

    /// <param name="sampleRate">Requested rate; 0 asks for the device's own.</param>
    /// <param name="channels">Requested channel count.</param>
    /// <param name="deviceIndex">Index from <see cref="AudioDeviceList.Enumerate"/>, or -1 for the default.</param>
    /// <param name="periodSizeInFrames">Buffer size hint; 0 lets miniaudio decide.</param>
    public AudioDevice(int sampleRate, int channels, int deviceIndex = -1, int periodSizeInFrames = 0)
    {
        _self = GCHandle.Alloc(this);

        var result = NativeAudio.DeviceCreate(
            deviceIndex, (uint)sampleRate, (uint)channels, (uint)periodSizeInFrames,
            &OnRender, (void*)GCHandle.ToIntPtr(_self), out _handle);

        if (result != NativeAudio.Success)
        {
            _self.Free();
            throw new AudioException($"Nie udało się otworzyć urządzenia dźwiękowego — {NativeAudio.Describe(result)}.");
        }

        SampleRate = (int)NativeAudio.DeviceSampleRate(_handle);
        Channels = (int)NativeAudio.DeviceChannels(_handle);
        PeriodSizeInFrames = (int)NativeAudio.DevicePeriod(_handle);
        Name = ReadName();
    }

    /// <summary>Rate the device actually runs at, which may differ from the request.</summary>
    public int SampleRate { get; }

    /// <summary>Channel count the device actually runs at.</summary>
    public int Channels { get; }

    /// <summary>
    /// Rozmiar okresu, jaki urządzenie przyjęło. Żądany rozmiar jest tylko podpowiedzią —
    /// sterownik może go zmienić — więc opóźnienie wolno podawać wyłącznie z tego odczytu.
    /// </summary>
    public int PeriodSizeInFrames { get; }

    public string Name { get; }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Called from the audio thread. Assign before <see cref="Start"/>; leaving it null
    /// produces silence rather than an error.
    /// </summary>
    public RenderCallback? Render { get; set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;

        NativeAudio.ThrowIfFailed(NativeAudio.DeviceStart(_handle), "Uruchomienie urządzenia");
        IsRunning = true;
    }

    public void Stop()
    {
        if (_disposed || !IsRunning) return;

        NativeAudio.ThrowIfFailed(NativeAudio.DeviceStop(_handle), "Zatrzymanie urządzenia");
        IsRunning = false;
    }

    [UnmanagedCallersOnly]
    private static void OnRender(void* user, float* frames, uint frameCount)
    {
        try
        {
            var self = (AudioDevice?)GCHandle.FromIntPtr((nint)user).Target;
            if (self is null) return;

            var buffer = new Span<float>(frames, checked((int)(frameCount * self.Channels)));

            var render = self.Render;
            if (render is null) buffer.Clear();
            else render(buffer);
        }
        catch
        {
            // An exception here would tear down the process from a native thread.
            // Silence is the only sane outcome.
            new Span<float>(frames, checked((int)frameCount)).Clear();
        }
    }

    private string ReadName()
    {
        var buffer = stackalloc byte[512];
        var length = NativeAudio.DeviceName(_handle, buffer, 512);
        return length > 0 ? Encoding.UTF8.GetString(buffer, length) : "Urządzenie domyślne";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop first: destroying a running device would let the callback fire against
        // a freed handle.
        if (IsRunning)
        {
            NativeAudio.DeviceStop(_handle);
            IsRunning = false;
        }

        Render = null;

        if (_handle != nint.Zero)
        {
            NativeAudio.DeviceDestroy(_handle);
            _handle = nint.Zero;
        }

        if (_self.IsAllocated) _self.Free();
    }
}
