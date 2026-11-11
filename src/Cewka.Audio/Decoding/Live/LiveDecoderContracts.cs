using System.Diagnostics.CodeAnalysis;
using Cewka.Audio.Streaming;

namespace Cewka.Audio.Decoding.Live;

/// <summary>Dekoder źródła na żywo: brak danych to stan, a nie koniec strumienia.</summary>
internal interface ILiveDecoder : IAudioDecoder
{
    /// <summary>Dane, czekam albo koniec; nigdy nie blokuje.</summary>
    LiveReadStatus ReadLive(Span<float> destination, out int frames);

    /// <summary>Znacznik, którego miejsce dekoder już minął; ramka w jednostkach <see cref="IAudioDecoder.Position"/>.</summary>
    bool TryTakeMarker([MaybeNullWhen(false)] out StreamMarker marker, out long outputFrame);

    /// <summary>Null po zwykłym zatrzymaniu.</summary>
    LiveFailure? EndFailure { get; }

    LiveCodec Codec { get; }
}

internal readonly record struct LiveFrameFormat(int SampleRate, int Channels, int BitrateKbps);

/// <summary>Dekoder ramek jednego kodeka, pchany porcjami bajtów.</summary>
internal interface ILiveFrameDecoder : IDisposable
{
    /// <summary>Najmniejsza porcja, której nie wolno skracać; krótszej dekoder nie dostaje.</summary>
    int MinInputBytes { get; }

    /// <summary>Najwięcej próbek (wszystkich kanałów) z jednej ramki.</summary>
    int MaxOutputSamples { get; }

    /// <summary>
    /// Dekoduje najwyżej jedną ramkę i zwraca ramki PCM. Zero przy <paramref name="consumed"/>
    /// większym od zera to śmieci albo ramka bez rezerwy bitów, a przy zerowym: za mało danych.
    /// Dekoder systemu może oddać próbki bez zużycia bajtów, bo ma je jeszcze z poprzednich ramek.
    /// </summary>
    int Decode(ReadOnlySpan<byte> input, Span<float> output, out int consumed, out LiveFrameFormat format);

    void Reset();

    /// <summary>Tego strumienia ten dekoder nie zagra; czyści go dopiero <see cref="Reset"/>.</summary>
    LiveFailure? Failure => null;

    /// <summary>Próbki gotowe bez nowych bajtów, gdy sieć milczy. Nie blokuje; dotyczy dekoderów pracujących w tle.</summary>
    int TakePending(Span<float> output, out LiveFrameFormat format)
    {
        format = default;
        return 0;
    }

    /// <summary>
    /// Bajtów już nie będzie: oddaje resztę próbek, wywoływane aż do zera. Przed kolejnymi danymi potrzebny
    /// jest <see cref="Reset"/>.
    /// </summary>
    int Drain(Span<float> output, out LiveFrameFormat format)
    {
        format = default;
        return 0;
    }
}
