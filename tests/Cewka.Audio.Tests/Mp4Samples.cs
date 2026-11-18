using System.Buffers.Binary;
using System.Text;

namespace Cewka.Audio.Tests;

/// <summary>
/// Pliki M4A z sygnałem ciągłym przez granicę dwóch utworów. Wzorce w <c>Dane</c> powstały
/// w GStreamerze 1.24 z plików WAV 16 bit z <see cref="Signal"/>:
/// <c>filesrc ! wavparse ! audioconvert ! avenc_aac bitrate=128000 ! mp4mux ! filesink</c>.
/// Część A to ramki 0–69 999, część B 70 000–132 299. Koder poprzedza nagranie 1024 próbkami
/// rozbiegu, a mp4mux nie zapisuje tego nigdzie: lista edycji zaczyna się od zera, tylko
/// ostatnia próbka tablicy czasów jest skrócona do końca nagrania. Opis rozbiegu, jaki
/// zapisują iTunes (<c>iTunSMPB</c>) i ffmpeg (lista edycji), dopisują testy.
/// </summary>
internal static class Mp4Samples
{
    public const int Rate = 44100;
    public const int Delay = 1024;
    public const int FirstFrames = 70000;
    public const int SecondFrames = 62300;

    /// <summary>Ramki AAC po 1024 próbki: rozbieg, nagranie i dopełnienie ostatniej.</summary>
    public static int Padding(int frames) => (Delay + frames + 1023) / 1024 * 1024 - Delay - frames;

    public static byte[] First => Load("ciagly-a-44100.m4a");
    public static byte[] Second => Load("ciagly-b-44100.m4a");

    private static byte[] Load(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Dane", name));

    /// <summary>Świergot 200 Hz rosnący o 600 Hz na sekundę z tonem stałym; wartość ramki obu części.</summary>
    public static double Signal(double seconds, int channel)
    {
        var chirp = 0.25 * Math.Sin(2 * Math.PI * (200 * seconds + 300 * seconds * seconds));
        var tone = 0.15 * Math.Sin(2 * Math.PI * (channel == 0 ? 523.25 : 659.25) * seconds);
        return chirp + tone;
    }

    /// <summary>Pierwszy kanał sygnału w częstotliwości <paramref name="rate"/>, od początku części A.</summary>
    public static float[] Reference(int frames, int rate)
    {
        var data = new float[frames];
        for (var i = 0; i < frames; i++) data[i] = (float)Signal((double)i / rate, 0);
        return data;
    }

    /// <summary>
    /// Ramka wyjścia (przeplatanego, dwa kanały), w której stoi ramka <paramref name="from"/>
    /// wzorca. Świergot ma ostrą funkcję korelacji, więc szczyt jest jednoznaczny.
    /// </summary>
    public static int Offset(float[] output, float[] reference, int from, int count, int range)
    {
        var best = double.NegativeInfinity;
        var bestLag = int.MinValue;
        var frames = output.Length / 2;

        for (var lag = -range; lag <= range; lag++)
        {
            var sum = 0.0;
            for (var n = from; n < from + count; n++)
            {
                var o = n + lag;
                if (o < 0 || o >= frames || n >= reference.Length) continue;
                sum += output[2 * o] * reference[n];
            }

            if (sum > best)
            {
                best = sum;
                bestLag = lag;
            }
        }

        return from + bestLag;
    }

    /// <summary>Najmniejsza wartość skuteczna pierwszego kanału w oknach po 64 ramki.</summary>
    public static double QuietestWindow(float[] output, int from, int to)
    {
        const int window = 64;
        var lowest = double.PositiveInfinity;

        for (var start = from; start + window <= to; start += window / 2)
        {
            var sum = 0.0;
            for (var n = start; n < start + window; n++) sum += output[2 * n] * output[2 * n];
            lowest = Math.Min(lowest, Math.Sqrt(sum / window));
        }

        return lowest;
    }

    /// <summary>Znacznik iTunes z opóźnieniem, dopełnieniem i liczbą próbek, dopisany w moov/udta/meta/ilst.</summary>
    public static byte[] WithSmpb(byte[] file, long delay, long padding, long length) =>
        WithSmpbText(file, $" 00000000 {delay:X8} {padding:X8} {length:X16}" + string.Concat(Enumerable.Repeat(" 00000000", 8)));

    public static byte[] WithSmpbText(byte[] file, string text)
    {
        var payload = Encoding.ASCII.GetBytes(text);
        var mean = Atom("mean", [.. new byte[4], .. "com.apple.iTunes"u8]);
        var name = Atom("name", [.. new byte[4], .. "iTunSMPB"u8]);
        var data = Atom("data", [0, 0, 0, 1, 0, 0, 0, 0, .. payload]);
        var freeform = Atom("----", [.. mean, .. name, .. data]);

        var copy = file.ToArray();
        var moov = Find(copy, 0, copy.Length, "moov")!.Value;
        var udta = LastChild(copy, moov, "udta");
        var meta = Find(copy, udta.Start + 8, udta.End, "meta")!.Value;
        var ilst = Find(copy, meta.Start + 12, meta.End, "ilst")!.Value;

        // Rodzice rosną o wstawkę; moov stoi za mdat, więc położenia próbek się nie zmieniają.
        if (moov.Start < Find(copy, 0, copy.Length, "mdat")!.Value.Start)
            throw new InvalidOperationException("Wzorzec ma moov przed mdat.");

        foreach (var parent in new[] { moov, udta, meta, ilst })
            BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(parent.Start),
                BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(parent.Start)) + (uint)freeform.Length);

        return [.. copy.AsSpan(0, ilst.End), .. freeform, .. copy.AsSpan(ilst.End)];
    }

    /// <summary>Lista edycji jak u ffmpeg: początek po rozbiegu, długość w skali czasu filmu.</summary>
    public static byte[] WithEditList(byte[] file, int mediaTime, long length)
    {
        var copy = file.ToArray();
        var moov = Find(copy, 0, copy.Length, "moov")!.Value;
        var mvhd = Find(copy, moov.Start + 8, moov.End, "mvhd")!.Value;
        var movieScale = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(mvhd.Start + 20));
        var trak = Find(copy, moov.Start + 8, moov.End, "trak")!.Value;
        var edts = Find(copy, trak.Start + 8, trak.End, "edts")!.Value;
        var elst = Find(copy, edts.Start + 8, edts.End, "elst")!.Value;

        var entry = elst.Start + 16;
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(entry), (uint)Math.Round(length * (double)movieScale / Rate));
        BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(entry + 4), mediaTime);
        return copy;
    }

    private static byte[] Atom(string type, byte[] body)
    {
        var atom = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(atom, (uint)atom.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(atom, 4);
        body.CopyTo(atom, 8);
        return atom;
    }

    internal readonly record struct Region(int Start, int End);

    internal static Region? Find(byte[] data, int start, int end, string type)
    {
        for (var position = start; position + 8 <= end;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            if (size < 8) return null;
            if (Encoding.ASCII.GetString(data, position + 4, 4) == type) return new Region(position, position + size);
            position += size;
        }

        return null;
    }

    private static Region LastChild(byte[] data, Region parent, string type)
    {
        Region? last = null;
        for (var position = parent.Start + 8; position + 8 <= parent.End;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            if (Encoding.ASCII.GetString(data, position + 4, 4) == type) last = new Region(position, position + size);
            position += size;
        }

        return last ?? throw new InvalidOperationException($"Brak atomu {type}.");
    }
}
