using Cewka.Audio.Dsp;

namespace Cewka.Audio.Playback;

/// <summary>
/// Stan toru dla słyszanego utworu w jednej chwili: format źródła z dekodera, wzmocnienie wyrównania,
/// które naprawdę działa, i parametry urządzenia. Opisuje to, co słychać, a nie to, co już leży w buforze.
/// </summary>
/// <param name="Entry">Słyszany wpis; null, gdy nic nie gra.</param>
/// <param name="SourceSampleRate">Częstotliwość próbkowania źródła; 0, gdy jeszcze nieznana.</param>
/// <param name="SourceChannels">Liczba kanałów źródła; 0, gdy jeszcze nieznana.</param>
/// <param name="Resampling">Poziom konwersji, z jakim powstał dekoder słyszanego wpisu.</param>
/// <param name="Gain">Wzmocnienie wyrównania w torze; null, gdy wyrównanie jest wyłączone albo poziom nieznany.</param>
/// <param name="DeviceOpen">Urządzenie jest otwarte; przed pierwszym odtworzeniem nie jest.</param>
/// <param name="DeviceName">Nazwa otwartego urządzenia.</param>
/// <param name="DeviceSampleRate">Częstotliwość próbkowania urządzenia.</param>
/// <param name="DeviceChannels">Liczba kanałów urządzenia.</param>
public sealed record SignalPathSnapshot(
    QueueEntry? Entry,
    int SourceSampleRate,
    int SourceChannels,
    ResamplingLevel Resampling,
    TrackGain? Gain,
    bool DeviceOpen,
    string DeviceName,
    int DeviceSampleRate,
    int DeviceChannels)
{
    public bool IsLive => Entry?.IsLive == true;

    /// <summary>Dekoder zmienia częstotliwość próbkowania źródła na częstotliwość urządzenia.</summary>
    public bool Resamples => DeviceOpen && SourceSampleRate > 0 && SourceSampleRate != DeviceSampleRate;

    /// <summary>Dekoder zmienia układ kanałów źródła na układ urządzenia.</summary>
    public bool RemapsChannels => DeviceOpen && SourceChannels > 0 && SourceChannels != DeviceChannels;
}
