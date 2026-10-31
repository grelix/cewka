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
public sealed unsafe class AudioDevice : IAudioOutput
{
    /// <summary>Fills one period of audio. The span length is frames × channels.</summary>
    public delegate void RenderCallback(Span<float> buffer);

    private GCHandle _self;
    private nint _handle;
    private bool _disposed;
    private volatile bool _running;

    /// <summary>
    /// Puste wyjście: urządzenie pobiera próbki w tempie rzeczywistym i niczego nie odtwarza.
    /// Dla testów i narzędzi, które nie mogą grać przez głośniki. Wybór obowiązuje dla całego
    /// procesu i trzeba go dokonać przed pierwszym wyliczeniem lub otwarciem urządzenia.
    /// Zmienna środowiskowa <c>CEWKA_NULL_AUDIO=1</c> włącza je bez zmiany kodu.
    /// </summary>
    /// <exception cref="InvalidOperationException">Urządzenia były już używane z innym zapleczem.</exception>
    public static bool UseNullOutput
    {
        get => NativeAudio.NullBackend;
        set => NativeAudio.SetNullBackend(value);
    }

    /// <param name="sampleRate">Requested rate; 0 asks for the device's own.</param>
    /// <param name="channels">Requested channel count.</param>
    /// <param name="deviceIndex">Index from <see cref="AudioDeviceList.Enumerate"/>, or -1 for the default.</param>
    /// <param name="periodSizeInFrames">Buffer size hint; 0 lets miniaudio decide.</param>
    public AudioDevice(int sampleRate, int channels, int deviceIndex = -1, int periodSizeInFrames = 0)
    {
        NativeAudio.EnsureBackend();

        _self = GCHandle.Alloc(this);

        var result = NativeAudio.DeviceCreate(
            deviceIndex, (uint)sampleRate, (uint)channels, (uint)periodSizeInFrames,
            &OnRender, (void*)GCHandle.ToIntPtr(_self), out _handle);

        if (result != NativeAudio.Success)
        {
            _self.Free();
            throw new AudioException($"Nie udało się otworzyć urządzenia dźwiękowego — {NativeAudio.Describe(result)}.");
        }

        NativeAudio.DeviceSetNotify(_handle, &OnNotify);

        SampleRate = (int)NativeAudio.DeviceSampleRate(_handle);
        Channels = (int)NativeAudio.DeviceChannels(_handle);
        PeriodSizeInFrames = (int)NativeAudio.DevicePeriod(_handle);
        Name = ReadName();
    }

    /// <summary>
    /// Otwiera urządzenie o podanej nazwie, a gdy go nie ma albo nazwa jest pusta, domyślne.
    ///
    /// <para>Pozycja na liście urządzeń nie jest trwała: odłączenie sprzętu przesuwa numery
    /// pozostałych, a każde wyliczenie odświeża listę po stronie natywnej. Numer wyznaczony
    /// przy wyborze w Ustawieniach mógłby więc wskazać inne urządzenie, dlatego wyznacza się
    /// go z nazwy tuż przed otwarciem, w tym samym przebiegu co odświeżenie listy.</para>
    /// </summary>
    public static AudioDevice Open(string? deviceName, int sampleRate, int channels, int periodSizeInFrames = 0)
    {
        var index = -1;
        var fallback = false;

        if (!string.IsNullOrEmpty(deviceName))
        {
            try
            {
                index = AudioDeviceList.Enumerate().FirstOrDefault(d => d.Name == deviceName)?.Index ?? -1;
            }
            catch (AudioException ex)
            {
                Console.Error.WriteLine($"[cewka] odczyt listy urządzeń: {ex.Message}");
            }

            fallback = index < 0;
        }

        return new AudioDevice(sampleRate, channels, index, periodSizeInFrames) { IsFallback = fallback };
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

    /// <summary>True, gdy żądanego urządzenia nie znaleziono i otwarto domyślne.</summary>
    public bool IsFallback { get; private init; }

    /// <summary>
    /// True od chwili uruchomienia do zatrzymania albo utraty urządzenia. Ustawiane przed
    /// startem natywnym, bo wywołanie zwrotne może ruszyć, zanim start wróci.
    /// </summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Urządzenie przestało pracować bez polecenia: odłączono je albo sterownik unieważnił
    /// strumień. Zgłaszane z puli wątków.
    /// </summary>
    public event Action? Lost;

    /// <summary>
    /// Called from the audio thread. Assign before <see cref="Start"/>; leaving it null
    /// produces silence rather than an error.
    /// </summary>
    public RenderCallback? Render { get; set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;

        _running = true;

        var result = NativeAudio.DeviceStart(_handle);
        if (result != NativeAudio.Success)
        {
            _running = false;
            NativeAudio.ThrowIfFailed(result, "Uruchomienie urządzenia");
        }
    }

    public void Stop()
    {
        if (_disposed) return;

        // Po utracie urządzenia flaga już stoi, ale natywne zatrzymanie i tak trzeba wykonać:
        // dopiero ono przestawia urządzenie tak, żeby dało się je uruchomić ponownie.
        var wasRunning = _running;
        _running = false;

        var result = NativeAudio.DeviceStop(_handle);
        if (wasRunning) NativeAudio.ThrowIfFailed(result, "Zatrzymanie urządzenia");
    }

    [UnmanagedCallersOnly]
    private static void OnRender(void* user, float* frames, uint frameCount)
    {
        var samples = 0;

        try
        {
            var self = (AudioDevice?)GCHandle.FromIntPtr((nint)user).Target;
            if (self is null) return;

            samples = checked((int)(frameCount * self.Channels));
            var buffer = new Span<float>(frames, samples);

            var render = self.Render;
            if (render is null) buffer.Clear();
            else render(buffer);
        }
        catch
        {
            // An exception here would tear down the process from a native thread.
            // Silence is the only sane outcome.
            if (samples > 0) new Span<float>(frames, samples).Clear();
        }
    }

    [UnmanagedCallersOnly]
    private static void OnNotify(void* user, int notification)
    {
        try
        {
            if (notification != NativeAudio.DeviceLostEvent) return;

            var self = (AudioDevice?)GCHandle.FromIntPtr((nint)user).Target;
            if (self is null || self._disposed) return;

            // Wątek roboczy miniaudio: tylko flaga i przekazanie dalej. Zatrzymanie albo
            // zamknięcie urządzenia stąd skończyłoby się zakleszczeniem.
            self._running = false;
            ThreadPool.UnsafeQueueUserWorkItem(static device => device.Lost?.Invoke(), self, preferLocal: false);
        }
        catch
        {
            // Wyjątek nie może przejść do kodu natywnego.
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
        _running = false;
        NativeAudio.DeviceStop(_handle);

        Render = null;

        if (_handle != nint.Zero)
        {
            NativeAudio.DeviceDestroy(_handle);
            _handle = nint.Zero;
        }

        if (_self.IsAllocated) _self.Free();
    }
}
