namespace Cewka.Audio.Devices;

/// <summary>
/// Wyjście dźwięku widziane przez silnik odtwarzania. Prawdziwe jest jedno,
/// <see cref="AudioDevice"/>; interfejs istnieje po to, żeby testy mogły wywoływać
/// <see cref="Render"/> same, okres po okresie, bez karty dźwiękowej i bez zegara.
/// </summary>
internal interface IAudioOutput : IDisposable
{
    int SampleRate { get; }

    int Channels { get; }

    int PeriodSizeInFrames { get; }

    string Name { get; }

    /// <summary>True, dopóki urządzenie faktycznie pobiera próbki; po utracie urządzenia false.</summary>
    bool IsRunning { get; }

    /// <summary>True, gdy wybranego urządzenia nie znaleziono i otwarto domyślne.</summary>
    bool IsFallback { get; }

    AudioDevice.RenderCallback? Render { get; set; }

    /// <summary>Urządzenie przestało pracować bez polecenia. Zgłaszane spoza wątku dźwiękowego.</summary>
    event Action? Lost;

    void Start();

    void Stop();
}
