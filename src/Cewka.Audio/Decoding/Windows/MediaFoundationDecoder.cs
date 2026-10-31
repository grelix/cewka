using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Cewka.Audio.Decoding.Windows.MediaFoundation;

namespace Cewka.Audio.Decoding.Windows;

/// <summary>
/// AAC, M4A and ALAC through Media Foundation, which every supported version of Windows
/// already carries. This is what lets the player handle the whole declared range of formats
/// without shipping a decoder of its own or asking anyone to install one.
///
/// <para>Dekoder prosi Media Foundation wyłącznie o zamianę na próbki zmiennoprzecinkowe
/// w częstotliwości źródłowej. Dopasowanie do formatu urządzenia robi ta sama klasa bazowa,
/// co dla Vorbisa i Opusa, dzięki czemu wszystkie ścieżki brzmią tak samo — zamiast polegać
/// na resamplerze systemowym, którego jakość zależy od wersji Windows.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MediaFoundationDecoder : ManagedDecoderBase
{
    private static readonly Lock StartupLock = new();
    private static bool _started;

    private readonly string _path;
    private IMFSourceReader? _reader;

    private byte[] _pending = [];
    private int _pendingOffset;
    private int _pendingBytes;
    private bool _endOfStream;

    public MediaFoundationDecoder(string path, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
    {
        _path = path;
        EnsureStarted();

        var result = MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader);
        if (result < 0) throw Failure("otwarcie pliku", result);

        _reader = reader;

        try
        {
            // Wyłączone wszystkie strumienie poza pierwszym dźwiękowym: plik może zawierać
            // obraz albo rozdziały, których dekodowanie byłoby czystą stratą.
            reader.SetStreamSelection(MF_SOURCE_READER_ALL_STREAMS, false);

            result = reader.SetStreamSelection(MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);
            if (result < 0) throw Failure("wybór ścieżki dźwiękowej", result);

            ConfigureOutput(reader);
            if (!ReadOutputFormat(reader, out var sourceRate, out var sourceChannels, out var mask))
                throw Failure("odczyt formatu wyjściowego", 0);

            Configure(sourceRate, sourceChannels, outputSampleRate, outputChannels, meter,
                ChannelLayouts.FromWaveMask(mask, sourceChannels));

            TotalFrames = ReadDuration(reader) is { } duration && duration > TimeSpan.Zero
                ? (long)Math.Round(duration.TotalSeconds * outputSampleRate)
                : 0;
        }
        catch
        {
            // Obiekt nie powstał, więc bez tego czytnik trzymałby plik do przebiegu finalizatora.
            Dispose();
            throw;
        }
    }

    public override bool CanSeek => true;

    /// <summary>
    /// Asks for uncompressed float at the file's own rate. Passing a partial type lets the
    /// source reader insert whatever decoder the file needs.
    /// </summary>
    private static void ConfigureOutput(IMFSourceReader reader)
    {
        var result = MFCreateMediaType(out var target);
        if (result < 0) throw Failure("utworzenie typu wyjściowego", result);

        try
        {
            var major = MFMediaType_Audio;
            var subtype = MFAudioFormat_Float;
            var majorKey = MF_MT_MAJOR_TYPE;
            var subtypeKey = MF_MT_SUBTYPE;

            target.SetGUID(ref majorKey, ref major);
            target.SetGUID(ref subtypeKey, ref subtype);

            result = reader.SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, target);
            if (result < 0) throw Failure("ustawienie formatu wyjściowego", result);
        }
        finally
        {
            Marshal.ReleaseComObject(target);
        }
    }

    /// <summary>
    /// Reads the rate, channel count and channel mask the reader currently delivers.
    /// The mask is zero when the type does not carry one.
    /// </summary>
    private static bool ReadOutputFormat(IMFSourceReader reader, out int sampleRate, out int channels, out uint mask)
    {
        sampleRate = 0;
        channels = 0;
        mask = 0;

        if (reader.GetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, out var actual) < 0) return false;

        try
        {
            var rateKey = MF_MT_AUDIO_SAMPLES_PER_SECOND;
            var channelsKey = MF_MT_AUDIO_NUM_CHANNELS;
            var maskKey = MF_MT_AUDIO_CHANNEL_MASK;

            actual.GetUINT32(ref rateKey, out sampleRate);
            actual.GetUINT32(ref channelsKey, out channels);
            if (actual.GetUINT32(ref maskKey, out var rawMask) >= 0) mask = unchecked((uint)rawMask);
        }
        finally
        {
            Marshal.ReleaseComObject(actual);
        }

        if (sampleRate <= 0) sampleRate = 48000;
        if (channels <= 0) channels = 2;
        return true;
    }

    private static TimeSpan? ReadDuration(IMFSourceReader reader)
    {
        var key = MF_PD_DURATION;
        if (reader.GetPresentationAttribute(MF_SOURCE_READER_MEDIASOURCE, ref key, out var value) < 0)
            return null;

        // Media Foundation liczy czas w jednostkach 100 ns.
        return value.Value > 0 ? TimeSpan.FromTicks(value.Value) : null;
    }

    protected override int ReadSource(Span<float> destination, int frames)
    {
        if (_reader is null) return 0;

        var written = 0;
        var bytesPerFrame = SourceChannels * sizeof(float);

        while (written < frames)
        {
            if (_pendingBytes == 0)
            {
                if (_endOfStream || !PullSample()) break;
                continue;
            }

            var available = _pendingBytes / bytesPerFrame;
            var take = Math.Min(frames - written, available);
            if (take <= 0) { _pendingBytes = 0; continue; }

            var source = MemoryMarshal.Cast<byte, float>(
                _pending.AsSpan(_pendingOffset, take * bytesPerFrame));

            source.CopyTo(destination[(written * SourceChannels)..]);

            written += take;
            _pendingOffset += take * bytesPerFrame;
            _pendingBytes -= take * bytesPerFrame;
        }

        return written;
    }

    /// <summary>Fetches one sample and copies it out, so the COM buffer can be released at once.</summary>
    private bool PullSample()
    {
        var result = _reader!.ReadSample(
            MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
            out _, out var flags, out _, out var sample);

        if (result < 0)
        {
            DecoderFailures.Log($"Media Foundation: odczyt próbki nie powiódł się (0x{result:X8}).");
            _endOfStream = true;
            return false;
        }

        if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
        {
            _endOfStream = true;
            if (sample is not null) Marshal.ReleaseComObject(sample);
            return false;
        }

        // Konwerter jest ustawiony na format z otwarcia; bajty w innym formacie dałyby
        // dźwięk o złej wysokości albo pomieszane kanały, więc taki strumień się kończy.
        if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0 &&
            (!ReadOutputFormat(_reader!, out var rate, out var channels, out _) ||
             rate != SourceSampleRate || channels != SourceChannels))
        {
            DecoderFailures.Log("Media Foundation: format próbek zmienił się w trakcie strumienia.");
            _endOfStream = true;
            if (sample is not null) Marshal.ReleaseComObject(sample);
            return false;
        }

        // Brak próbki bez końca strumienia zdarza się przy zmianie formatu w środku pliku.
        if (sample is null) return true;

        try
        {
            if (sample.ConvertToContiguousBuffer(out var buffer) < 0) return false;

            try
            {
                if (buffer.Lock(out var pointer, out _, out var length) < 0) return false;

                try
                {
                    if (_pending.Length < length) _pending = new byte[length];
                    Marshal.Copy(pointer, _pending, 0, length);

                    _pendingOffset = 0;
                    _pendingBytes = length;
                }
                finally
                {
                    buffer.Unlock();
                }
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }

        return true;
    }

    protected override void SeekSource(long sourceFrame)
    {
        if (_reader is null) return;

        _pendingOffset = 0;
        _pendingBytes = 0;
        _endOfStream = false;

        var format = GUID_NULL;
        var position = PropVariant.FromHundredNanoseconds(
            (long)(sourceFrame / (double)SourceSampleRate * TimeSpan.TicksPerSecond));

        _reader.SetCurrentPosition(ref format, ref position);
    }

    /// <summary>
    /// Media Foundation has to be started once per process. It is reference counted, so the
    /// matching shutdown is deliberately omitted: the platform is wanted for the whole run,
    /// and shutting it down while another decoder still holds a reader would fail.
    /// </summary>
    private static void EnsureStarted()
    {
        lock (StartupLock)
        {
            if (_started) return;

            var result = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
            if (result < 0)
            {
                DecoderFailures.Log($"Media Foundation: uruchomienie nie powiodło się (0x{result:X8}).");
                throw DecoderFailures.Create(DecoderFailure.SystemCodecsUnavailable);
            }

            _started = true;
        }
    }

    private static AudioException Failure(string operation, int hresult)
    {
        DecoderFailures.Log($"Media Foundation: {operation} nie powiodło się (0x{hresult:X8}).");

        return DecoderFailures.Create(hresult switch
        {
            MF_E_INVALIDSTREAMNUMBER => DecoderFailure.NoAudioStream,
            MF_E_INVALIDMEDIATYPE or MF_E_TOPO_CODEC_NOT_FOUND => DecoderFailure.CodecMissing,
            MF_E_UNSUPPORTED_BYTESTREAM_TYPE => DecoderFailure.UnrecognisedFormat,
            _ => DecoderFailure.DamagedFile,
        });
    }

    protected override void DisposeCore()
    {
        if (_reader is null) return;

        Marshal.ReleaseComObject(_reader);
        _reader = null;
    }
}
