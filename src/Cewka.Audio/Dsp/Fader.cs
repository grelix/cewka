namespace Cewka.Audio.Dsp;

/// <summary>
/// Liniowe wyciszanie i podgłaśnianie w zadanym czasie, dla miękkiej pauzy. Zlecenie przychodzi
/// z dowolnego wątku, a wątek dźwiękowy przejmuje je bez blokady na początku przebiegu i liczy
/// krok od poziomu, na którym jest. Zmiana kierunku w połowie zaczyna się więc bez skoku.
/// </summary>
public sealed class Fader : IAudioProcessor
{
    private sealed record Request(double Target, double Seconds);

    private Request? _pending;

    private int _channels = 2;
    private int _sampleRate = 48000;

    // Stan wątku dźwiękowego; poziom i cel odczytują także inne wątki.
    private double _level = 1;
    private double _target = 1;
    private double _step;

    /// <summary>Poziom, do którego zmierza ostatnie zlecenie.</summary>
    public double Target => Volatile.Read(ref _pending) is { } request ? request.Target : Volatile.Read(ref _target);

    /// <summary>Poziom osiągnięty przez wątek dźwiękowy.</summary>
    public double Level => Volatile.Read(ref _level);

    /// <summary>Zleca dojście do <paramref name="target"/> w <paramref name="seconds"/>; zero lub mniej to skok.</summary>
    public void FadeTo(double target, double seconds) =>
        Volatile.Write(ref _pending, new Request(Math.Clamp(target, 0, 1), seconds));

    public void Prepare(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
    }

    public void Process(Span<float> buffer, int frames)
    {
        TakeRequest();

        var level = _level;
        var target = _target;

        if (level == target)
        {
            if (level >= 1) return;
            if (level <= 0)
            {
                buffer[..(frames * _channels)].Clear();
                return;
            }
        }

        for (var frame = 0; frame < frames; frame++)
        {
            if (level != target)
                level = target > level ? Math.Min(target, level + _step) : Math.Max(target, level - _step);

            var gain = (float)level;
            var baseIndex = frame * _channels;
            for (var c = 0; c < _channels; c++) buffer[baseIndex + c] *= gain;
        }

        Volatile.Write(ref _level, level);
    }

    /// <summary>Wyciszenie nie jest historią sygnału: czyszczenie toru zostawia poziom.</summary>
    public void Reset() => TakeRequest();

    private void TakeRequest()
    {
        if (Interlocked.Exchange(ref _pending, null) is not { } request) return;

        Volatile.Write(ref _target, request.Target);
        var frames = request.Seconds * _sampleRate;
        if (frames < 1)
        {
            _step = 0;
            Volatile.Write(ref _level, request.Target);
        }
        else
        {
            _step = Math.Abs(request.Target - _level) / frames;
        }
    }
}
