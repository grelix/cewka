using System.Runtime.CompilerServices;
using Cewka.Audio.Decoding;
using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Dsp;

namespace Cewka.Audio.Playback;

/// <summary>Migawka toru sygnału dla okienka z opisem przetwarzania.</summary>
public sealed partial class PlaybackEngine
{
    /// <summary>Format źródła z chwili otwarcia dekodera wpisu.</summary>
    private sealed record DecodedSource(int SampleRate, int Channels, ResamplingLevel Resampling);

    /// <summary>Wzmocnienie odczytane od nowa po zmianie ustawień, dla wpisu, który wtedy grał.</summary>
    private sealed record RefreshedGain(QueueEntry Entry, TrackGain? Gain);

    // Zapis w wątku dekodującym przy otwarciu pliku, odczyt w wątku interfejsu. Wpis może mieć otwarty
    // już następny dekoder, zanim jego dźwięk dotrze do wyjścia, więc format należy do wpisu, a słyszany
    // wpis wskazuje granica, którą przestawia wątek dźwiękowy.
    private readonly ConditionalWeakTable<QueueEntry, DecodedSource> _decodedSources = new();

    private volatile RefreshedGain? _refreshedGain;

    /// <summary>
    /// Stan toru dla słyszanego utworu. Bez blokad poza krótką blokadą kolejki przy stacji; wołane
    /// z wątku interfejsu, kiedy okienko toru jest otwarte.
    /// </summary>
    public SignalPathSnapshot SignalPath
    {
        get
        {
            var current = _current;
            var entry = current.Entry;
            var open = _device is not null;

            int rate = 0, channels = 0;
            var level = AudioQuality.Resampling;
            TrackGain? gain = null;

            if (entry is { IsLive: true })
            {
                // Stacja może zmienić format w trakcie, więc dane idą wprost z jej dekodera.
                if (_session?.Decoder is ILiveDecoder live)
                {
                    rate = live.SourceSampleRate;
                    channels = live.SourceChannels;
                }

                if (_decodedSources.TryGetValue(entry, out var known)) level = known.Resampling;
                lock (_sync) gain = _liveGain;
            }
            else if (entry is not null)
            {
                if (_decodedSources.TryGetValue(entry, out var known))
                {
                    rate = known.SampleRate;
                    channels = known.Channels;
                    level = known.Resampling;
                }

                gain = _refreshedGain is { } refreshed && ReferenceEquals(refreshed.Entry, entry)
                    ? refreshed.Gain
                    : current.Gain;
            }

            return new SignalPathSnapshot(
                entry,
                rate,
                channels,
                level,
                Graph.NormalisationEnabled ? gain : null,
                open,
                open ? DeviceName : string.Empty,
                SampleRate,
                Channels);
        }
    }

    /// <summary>Zapamiętuje format źródła świeżo otwartego dekodera; wątek dekodujący.</summary>
    private void RememberSource(QueueEntry entry, IAudioDecoder? decoder)
    {
        if (decoder is null) return;

        var level = decoder is ManagedDecoderBase managed ? managed.ResamplingLevel : AudioQuality.Current;
        _decodedSources.AddOrUpdate(entry, new DecodedSource(decoder.SourceSampleRate, decoder.SourceChannels, level));
    }

    private void RememberRefreshedGain(QueueEntry entry, TrackGain? gain) => _refreshedGain = new RefreshedGain(entry, gain);
}
