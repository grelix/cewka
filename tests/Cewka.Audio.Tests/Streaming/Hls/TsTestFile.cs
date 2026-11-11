namespace Cewka.Audio.Tests.Streaming.Hls;

/// <summary>Strumień transportowy składany w teście: tabele, ścieżka dźwięku, a na żądanie obraz i ID3.</summary>
internal static class TsTestFile
{
    public const int AudioPid = 0x101;
    public const int VideoPid = 0x100;
    public const int Id3Pid = 0x102;
    private const int PmtPid = 0x1000;

    public const byte StreamTypeAdts = 0x0F;
    public const byte StreamTypeMp3 = 0x03;
    private const byte StreamTypeVideo = 0x1B;
    private const byte StreamTypeId3 = 0x15;

    public static byte[] Packet(int pid, bool start, ReadOnlySpan<byte> payload, int control = 0, bool error = false)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = (byte)((error ? 0x80 : 0) | (start ? 0x40 : 0) | pid >> 8);
        packet[2] = (byte)pid;

        var stuffing = 184 - payload.Length;

        if (stuffing == 0)
        {
            packet[3] = (byte)(control << 6 | 0x10);
        }
        else
        {
            packet[3] = (byte)(control << 6 | 0x30);
            packet[4] = (byte)(stuffing - 1);
            packet.AsSpan(5, stuffing - 1).Fill(0xFF);
            if (stuffing > 1) packet[5] = 0;
        }

        payload.CopyTo(packet.AsSpan(4 + stuffing));
        return packet;
    }

    private static byte[] Section(byte table, byte[] body)
    {
        var length = 5 + body.Length + 4;
        byte[] header = [0, table, (byte)(0xB0 | length >> 8), (byte)length, 0, 1, 0xC1, 0, 0];
        return [.. header, .. body, 0, 0, 0, 0];
    }

    public static byte[] Pat() =>
        Packet(0, true, Section(0x00, [0, 0, 0xE0, 0x10, 0, 1, (byte)(0xE0 | PmtPid >> 8), PmtPid & 0xFF]));

    public static byte[] Pmt(byte audioType = StreamTypeAdts, bool otherTracksFirst = false)
    {
        List<byte> body = [(byte)(0xE0 | VideoPid >> 8), VideoPid & 0xFF, 0xF0, 0];

        if (otherTracksFirst)
        {
            body.AddRange([StreamTypeVideo, (byte)(0xE0 | VideoPid >> 8), VideoPid & 0xFF, 0xF0, 0]);
            body.AddRange([StreamTypeId3, (byte)(0xE0 | Id3Pid >> 8), Id3Pid & 0xFF, 0xF0, 0]);
        }

        body.AddRange([audioType, (byte)(0xE0 | AudioPid >> 8), AudioPid & 0xFF, 0xF0, 0]);

        return Packet(PmtPid, true, Section(0x02, [.. body]));
    }

    /// <summary>Jeden pakiet PES podzielony na pakiety transportowe.</summary>
    /// <param name="headerExtra">Długość opcjonalnej części nagłówka PES.</param>
    public static IEnumerable<byte[]> Pes(int pid, byte[] data, int headerExtra = 5, int control = 0)
    {
        byte[] header = [0, 0, 1, 0xC0, 0, 0, 0x80, 0x80, (byte)headerExtra, .. new byte[headerExtra]];
        byte[] all = [.. header, .. data];

        for (var at = 0; at < all.Length; at += 184)
            yield return Packet(pid, at == 0, all.AsSpan(at, Math.Min(184, all.Length - at)), control);
    }

    /// <summary>Tabele i sam dźwięk.</summary>
    public static byte[] Mux(byte[] audio, byte audioType = StreamTypeAdts, bool withOtherTracks = false)
    {
        List<byte> file = [.. Pat(), .. Pmt(audioType, withOtherTracks)];

        if (withOtherTracks)
        {
            foreach (var packet in Pes(VideoPid, new byte[500])) file.AddRange(packet);
            foreach (var packet in Pes(Id3Pid, "ID3 tytuł"u8.ToArray())) file.AddRange(packet);
        }

        foreach (var packet in Pes(AudioPid, audio)) file.AddRange(packet);

        return [.. file];
    }
}
