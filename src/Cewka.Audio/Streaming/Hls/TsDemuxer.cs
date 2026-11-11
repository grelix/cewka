using System.Buffers;

namespace Cewka.Audio.Streaming.Hls;

/// <summary>
/// Pierwsza ścieżka dźwięku ze strumienia transportowego MPEG (ADTS albo MP3); obraz i metadane ID3 są pomijane.
/// Znaczniki czasu nie są potrzebne, bo dźwięk idzie ciągiem, a ramki kodeka same się synchronizują.
/// </summary>
internal sealed class TsDemuxer
{
    private const int PacketBytes = 188;
    private const byte SyncByte = 0x47;
    private const int PatPid = 0;
    private const int NoPid = -1;

    private const byte StreamTypeMpeg1Audio = 0x03;
    private const byte StreamTypeMpeg2Audio = 0x04;
    private const byte StreamTypeAdts = 0x0F;

    private readonly byte[] _partial = new byte[PacketBytes];
    private int _partialBytes;
    private int _pmtPid = NoPid;
    private int _audioPid = NoPid;

    /// <summary>Bajty nagłówka PES, które przeszły na następny pakiet.</summary>
    private int _pesHeaderLeft;

    /// <summary>Do pierwszego początku PES dane należą do przerwanego pakietu.</summary>
    private bool _insidePes;

    public bool HasAudioTrack => _audioPid != NoPid;

    /// <summary>Segment się skończył: urwany pakiet z jego końca nie ma związku z następnym.</summary>
    public void EndSegment() => _partialBytes = 0;

    public void Push(ReadOnlySpan<byte> data, IBufferWriter<byte> audio)
    {
        if (_partialBytes > 0)
        {
            var take = Math.Min(PacketBytes - _partialBytes, data.Length);
            data[..take].CopyTo(_partial.AsSpan(_partialBytes));
            _partialBytes += take;
            data = data[take..];

            if (_partialBytes < PacketBytes) return;

            _partialBytes = 0;

            if (_partial[0] == SyncByte)
            {
                ReadPacket(_partial, audio);
            }
            else
            {
                // Dopełnianie zaczęło się od złego bajtu: reszta wraca do wyszukiwania.
                Span<byte> copy = stackalloc byte[PacketBytes - 1];
                _partial.AsSpan(1).CopyTo(copy);
                Push(copy, audio);
            }
        }

        while (data.Length >= PacketBytes)
        {
            if (data[0] == SyncByte)
            {
                ReadPacket(data[..PacketBytes], audio);
                data = data[PacketBytes..];
                continue;
            }

            var next = data.IndexOf(SyncByte);
            if (next < 0) return;

            data = data[next..];
        }

        if (!data.IsEmpty)
        {
            var next = data.IndexOf(SyncByte);
            if (next < 0) return;

            data = data[next..];
            data.CopyTo(_partial);
            _partialBytes = data.Length;
        }
    }

    private void ReadPacket(ReadOnlySpan<byte> packet, IBufferWriter<byte> audio)
    {
        var transportError = (packet[1] & 0x80) != 0;
        var unitStart = (packet[1] & 0x40) != 0;
        var pid = (packet[1] & 0x1F) << 8 | packet[2];
        var scrambled = (packet[3] & 0xC0) != 0;
        var adaptation = (packet[3] >> 4) & 3;

        if (transportError || scrambled || (adaptation & 1) == 0) return;

        var payload = packet[4..];

        if ((adaptation & 2) != 0)
        {
            var length = packet[4];
            if (1 + length >= payload.Length) return;

            payload = payload[(1 + length)..];
        }

        if (pid == PatPid)
        {
            if (unitStart) ReadPat(payload);
        }
        else if (pid == _pmtPid)
        {
            if (unitStart) ReadPmt(payload);
        }
        else if (pid == _audioPid)
        {
            ReadAudio(payload, unitStart, audio);
        }
    }

    /// <summary>Sekcja zaczyna się po polu wskaźnika; tabele radia mieszczą się w jednym pakiecie.</summary>
    private static ReadOnlySpan<byte> Section(ReadOnlySpan<byte> payload, byte tableId)
    {
        if (payload.IsEmpty) return default;

        var pointer = payload[0];
        if (1 + pointer + 3 > payload.Length) return default;

        var section = payload[(1 + pointer)..];
        if (section[0] != tableId) return default;

        var length = (section[1] & 0x0F) << 8 | section[2];

        // Bez końcowych 4 bajtów CRC; sekcja dłuższa od pakietu daje to, co się zmieściło.
        var end = Math.Min(3 + length - 4, section.Length);

        return end <= 8 ? default : section[8..end];
    }

    private void ReadPat(ReadOnlySpan<byte> payload)
    {
        var programs = Section(payload, 0x00);

        for (var at = 0; at + 4 <= programs.Length; at += 4)
        {
            var number = programs[at] << 8 | programs[at + 1];
            if (number == 0) continue;

            _pmtPid = (programs[at + 2] & 0x1F) << 8 | programs[at + 3];
            return;
        }
    }

    private void ReadPmt(ReadOnlySpan<byte> payload)
    {
        var body = Section(payload, 0x02);
        if (body.Length < 4) return;

        var infoLength = (body[2] & 0x0F) << 8 | body[3];
        var at = 4 + infoLength;

        while (at + 5 <= body.Length)
        {
            var type = body[at];
            var pid = (body[at + 1] & 0x1F) << 8 | body[at + 2];
            var length = (body[at + 3] & 0x0F) << 8 | body[at + 4];

            if (type is StreamTypeAdts or StreamTypeMpeg1Audio or StreamTypeMpeg2Audio)
            {
                if (pid != _audioPid)
                {
                    _audioPid = pid;
                    _insidePes = false;
                    _pesHeaderLeft = 0;
                }

                return;
            }

            at += 5 + length;
        }
    }

    private void ReadAudio(ReadOnlySpan<byte> payload, bool unitStart, IBufferWriter<byte> audio)
    {
        if (unitStart)
        {
            _insidePes = false;
            _pesHeaderLeft = 0;

            if (payload.Length < 9 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1) return;

            _insidePes = true;
            _pesHeaderLeft = 9 + payload[8];
        }

        if (!_insidePes) return;

        if (_pesHeaderLeft > 0)
        {
            var skip = Math.Min(_pesHeaderLeft, payload.Length);
            _pesHeaderLeft -= skip;
            payload = payload[skip..];
        }

        if (!payload.IsEmpty) audio.Write(payload);
    }
}
