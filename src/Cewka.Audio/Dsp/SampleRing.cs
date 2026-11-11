namespace Cewka.Audio.Dsp;

/// <summary>
/// Pierścień ostatnich próbek lewego i prawego kanału. Pisze jeden wątek dźwiękowy, czyta
/// wątek rysowania; ani zapis, ani odczyt nie alokuje ani nie blokuje.
/// <para>
/// Zapis zapowiada blok licznikiem <c>_claimed</c>, zapisuje próbki i dopiero potem podnosi
/// <c>_written</c>. Czytelnik bierze próbki sprzed <c>_written</c>, a po skopiowaniu sprawdza,
/// czy zapis nie dobiegł do kopiowanego miejsca; jeśli tak, ponawia odczyt.
/// </para>
/// </summary>
public sealed class SampleRing
{
    /// <summary>Pojemność w ramkach; potęga dwójki. Zapas na okno 4096 i opóźnienie wyjścia.</summary>
    public const int Capacity = 16384;

    private const int Mask = Capacity - 1;
    private const int ReadAttempts = 3;

    private readonly float[] _left = new float[Capacity];
    private readonly float[] _right = new float[Capacity];
    private long _claimed;
    private long _written;

    /// <summary>Liczba ramek zapisanych od początku.</summary>
    public long Written => Volatile.Read(ref _written);

    /// <summary>Wątek dźwiękowy: dopisuje ramki z bufora przeplatanego; mono trafia do obu kanałów.</summary>
    public void Write(ReadOnlySpan<float> interleaved, int frames, int channels)
    {
        var position = _written;
        Volatile.Write(ref _claimed, position + frames);
        Interlocked.MemoryBarrier();

        for (var frame = 0; frame < frames; frame++)
        {
            var left = interleaved[frame * channels];
            var right = channels > 1 ? interleaved[frame * channels + 1] : left;
            var slot = (int)((position + frame) & Mask);
            _left[slot] = left;
            _right[slot] = right;
        }

        Volatile.Write(ref _written, position + frames);
    }

    /// <summary>Wątek dźwiękowy: zeruje zawartość; licznik zostaje, żeby czytelnicy nie cofnęli się w czasie.</summary>
    public void Clear()
    {
        var position = _written;
        Volatile.Write(ref _claimed, position + Capacity);
        Interlocked.MemoryBarrier();
        Array.Clear(_left);
        Array.Clear(_right);
        Volatile.Write(ref _written, position + Capacity);
    }

    /// <summary>
    /// Kopiuje ostatnie <c>left.Length</c> ramek, kończąc <paramref name="delayFrames"/> ramek przed
    /// najnowszą. Ramki sprzed początku nagrania są zerami. Zwraca false, gdy mimo ponowień
    /// zapis wyprzedził odczyt (zawartość jest wtedy nieużyteczna).
    /// </summary>
    public bool Read(Span<float> left, Span<float> right, int delayFrames)
    {
        var count = Math.Min(left.Length, right.Length);
        delayFrames = Math.Clamp(delayFrames, 0, Capacity - count);

        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            var end = Volatile.Read(ref _written) - delayFrames;
            var start = end - count;

            for (var i = 0; i < count; i++)
            {
                var position = start + i;
                if (position < 0)
                {
                    left[i] = 0;
                    right[i] = 0;
                    continue;
                }

                var slot = (int)(position & Mask);
                left[i] = _left[slot];
                right[i] = _right[slot];
            }

            Interlocked.MemoryBarrier();
            if (start >= Volatile.Read(ref _claimed) - Capacity) return true;
        }

        return false;
    }
}
