using Cewka.Audio.Decoding;

namespace Cewka.Audio.Tests.Engine;

/// <summary>
/// Dekoder wytwarzający stałą wartość próbki, po której w wyjściu rozpoznaje się utwór.
/// Może rzucić wyjątek po zadanej liczbie ramek albo zawiesić odczyt do zwolnienia.
/// </summary>
internal sealed class SyntheticDecoder(long totalFrames, float value, int channels, int sampleRate) : IAudioDecoder
{
    private long _position;

    /// <summary>Ramka, na której odczyt rzuca wyjątek; −1 wyłącza.</summary>
    public long ThrowAtFrame { get; init; } = -1;

    /// <summary>Gdy ustawione, odczyt czeka na sygnał, zanim cokolwiek zwróci.</summary>
    public ManualResetEventSlim? Gate { get; init; }

    /// <summary>Ustawiane, gdy odczyt zaczyna czekać na <see cref="Gate"/>.</summary>
    public ManualResetEventSlim? Waiting { get; init; }

    /// <summary>Przewinięcie, które się nie udaje: dekoder zostaje tam, gdzie był.</summary>
    public bool IgnoreSeeks { get; init; }

    public int DisposeCount { get; private set; }

    public int SampleRate { get; } = sampleRate;
    public int Channels { get; } = channels;
    public long TotalFrames { get; } = totalFrames;
    public long Position => _position;
    public bool CanSeek => true;
    public int SourceSampleRate => SampleRate;
    public int SourceChannels => Channels;
    public int InstantaneousBitrate => 0;

    public int Read(Span<float> destination)
    {
        if (Gate is { } gate)
            while (!gate.Wait(1)) Waiting?.Set();

        var wanted = destination.Length / Channels;
        var limit = ThrowAtFrame >= 0 ? Math.Min(TotalFrames, ThrowAtFrame) : TotalFrames;
        var frames = (int)Math.Clamp(limit - _position, 0, wanted);

        if (frames == 0 && ThrowAtFrame >= 0 && _position >= ThrowAtFrame)
            throw new InvalidDataException("Uszkodzony pakiet.");

        destination[..(frames * Channels)].Fill(value);
        _position += frames;
        return frames;
    }

    public void Seek(long frame)
    {
        if (!IgnoreSeeks) _position = Math.Clamp(frame, 0, TotalFrames);
    }

    public void Dispose() => DisposeCount++;
}
