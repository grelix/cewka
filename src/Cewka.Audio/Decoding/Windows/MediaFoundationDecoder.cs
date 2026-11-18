using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Cewka.Audio.Decoding.Windows.MediaFoundation;

namespace Cewka.Audio.Decoding.Windows;

/// <summary>
/// AAC, M4A i ALAC przez Media Foundation. Dekoder prosi tylko o próbki zmiennoprzecinkowe
/// w częstotliwości źródła; dopasowanie do urządzenia robi wspólna klasa bazowa, bo jakość
/// resamplera systemowego zależy od wydania Windows. Rozbieg kodera i dopełnienie końca
/// przycina <see cref="TimedSampleDecoder"/> według opisu z pliku (<see cref="Mp4GaplessReader"/>).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MediaFoundationDecoder : TimedSampleDecoder
{
    private static readonly Lock StartupLock = new();
    private static bool _started;

    public MediaFoundationDecoder(string path, int outputSampleRate, int outputChannels, BitrateMeter? meter = null)
        : base(new MediaFoundationSampleSource(path), ReadGapless(path), outputSampleRate, outputChannels, meter)
    {
    }

    /// <summary>Opis rozbiegu jest dodatkiem: plik, z którego nie da się go odczytać, gra w całości.</summary>
    private static Mp4Gapless? ReadGapless(string path)
    {
        try
        {
            return Mp4GaplessReader.Read(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            DecoderFailures.Log($"Media Foundation: plik gra bez przycięcia rozbiegu ({ex.Message}).");
            return null;
        }
    }

    /// <summary>
    /// Media Foundation has to be started once per process. It is reference counted, so the
    /// matching shutdown is deliberately omitted: the platform is wanted for the whole run,
    /// and shutting it down while another decoder still holds a reader would fail.
    /// </summary>
    internal static void EnsureStarted()
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
}

/// <summary>Czytnik źródła Media Foundation jako porcje próbek z czasami.</summary>
[SupportedOSPlatform("windows")]
internal sealed class MediaFoundationSampleSource : ITimedSampleSource
{
    private IMFSourceReader? _reader;
    private float[] _samples = [];
    private int _count;

    public MediaFoundationSampleSource(string path)
    {
        MediaFoundationDecoder.EnsureStarted();

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
            if (!ReadOutputFormat(reader, out var rate, out var channels, out var mask))
                throw Failure("odczyt formatu wyjściowego", 0);

            Rate = rate;
            Channels = channels;
            Mask = mask;
            Duration = ReadDuration(reader);
        }
        catch
        {
            // Obiekt nie powstał, więc bez tego czytnik trzymałby plik do przebiegu finalizatora.
            Dispose();
            throw;
        }
    }

    public int Rate { get; private set; }
    public int Channels { get; private set; }
    public uint Mask { get; private set; }
    public TimeSpan? Duration { get; }

    public ReadOnlySpan<float> Samples => _samples.AsSpan(0, _count);

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

    /// <summary>Fetches one sample and copies it out, so the COM buffer can be released at once.</summary>
    public bool Next(out TimedBlock block)
    {
        block = default;
        _count = 0;
        if (_reader is null) return false;

        while (true)
        {
            var result = _reader.ReadSample(
                MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                out _, out var flags, out var timestamp, out var sample);

            if (result < 0)
            {
                DecoderFailures.Log($"Media Foundation: odczyt próbki nie powiódł się (0x{result:X8}).");
                return false;
            }

            if ((flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0)
            {
                if (sample is not null) Marshal.ReleaseComObject(sample);
                return false;
            }

            // Ta próbka jest już w nowym formacie.
            if ((flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) != 0)
            {
                if (!ReadOutputFormat(_reader, out var rate, out var channels, out var mask))
                {
                    DecoderFailures.Log("Media Foundation: nie odczytano nowego formatu próbek.");
                    if (sample is not null) Marshal.ReleaseComObject(sample);
                    return false;
                }

                Rate = rate;
                Channels = channels;
                Mask = mask;
            }

            // Brak próbki bez końca strumienia zdarza się przy zmianie formatu w środku pliku.
            if (sample is null) continue;

            try
            {
                if (!Copy(sample)) return false;
            }
            finally
            {
                Marshal.ReleaseComObject(sample);
            }

            block = new TimedBlock(timestamp, Rate, Channels, Mask, _count / Channels);
            return true;
        }
    }

    private bool Copy(IMFSample sample)
    {
        if (sample.ConvertToContiguousBuffer(out var buffer) < 0) return false;

        try
        {
            if (buffer.Lock(out var pointer, out _, out var length) < 0) return false;

            try
            {
                var floats = length / sizeof(float) / Channels * Channels;
                if (_samples.Length < floats) _samples = new float[floats];
                Marshal.Copy(pointer, _samples, 0, floats);
                _count = floats;
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

        return true;
    }

    public void Seek(long ticks)
    {
        if (_reader is null) return;

        _count = 0;
        var format = GUID_NULL;
        var position = PropVariant.FromHundredNanoseconds(ticks);

        _reader.SetCurrentPosition(ref format, ref position);
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

    public void Dispose()
    {
        if (_reader is null) return;

        Marshal.ReleaseComObject(_reader);
        _reader = null;
    }
}
