using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Cewka.Audio.Decoding.Live;
using Cewka.Audio.Streaming;
using static Cewka.Audio.Decoding.Windows.MediaFoundation;

namespace Cewka.Audio.Decoding.Windows;

/// <summary>
/// Transformata AAC Media Foundation pchana ramkami ADTS, bez źródeł plikowych. SBR i PS zgłasza przez
/// zmianę typu wyjściowego, więc częstotliwość i liczba kanałów czytane są z typu, a nie z nagłówka.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MediaFoundationAacBackend : IAacBackend
{
    /// <summary>AAC Profile L2 z dokumentacji dekodera; zmierzony strumień i tak opisuje nagłówek ramki.</summary>
    private const int ProfileLevel = 0x29;

    private const int AdtsPayload = 1;

    /// <summary>Największa ramka po SBR: 2048 próbek na 8 kanałów.</summary>
    private const int OutputBytes = 2048 * 8 * sizeof(float);

    [ComImport]
    [Guid("32d186a7-218f-4c75-8876-dd77273a8999")]
    private class AacDecoderClass;

    private IMFTransform? _transform;
    private IMFSample? _output;
    private IMFMediaBuffer? _outputBuffer;
    private nint _outputPointer;
    private int _sampleRate;
    private int _channels;
    private bool _draining;

    public MediaFoundationAacBackend(AdtsHeader header)
    {
        // Dekoder obsługuje LC i rozszerzenia HE-AAC; inne profile i układ kanałów z treści ramki odrzuca.
        if (header.Profile != AdtsHeader.LowComplexityProfile || header.ChannelConfiguration == 0)
        {
            throw new AacBackendException(
                LiveFailureReason.UndecodableData,
                $"Media Foundation nie obsługuje profilu {header.Profile} z konfiguracją kanałów {header.ChannelConfiguration}.");
        }

        try
        {
            MediaFoundationDecoder.EnsureStarted();
            _transform = (IMFTransform)new AacDecoderClass();
        }
        catch (Exception e) when (e is AudioException or COMException or DllNotFoundException or InvalidCastException)
        {
            throw new AacBackendException(LiveFailureReason.CodecUnavailable, e.Message);
        }

        try
        {
            SetInputType(_transform, header);
            SelectOutputType(_transform);
            PrepareOutput(_transform);

            Check(_transform.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "początek strumienia");
            Check(_transform.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "początek danych");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public bool Failed { get; private set; }

    public void Push(ReadOnlySpan<byte> frame)
    {
        if (_transform is null) return;

        // Dekoder może trzymać próbkę wejściową po powrocie, więc każda ramka dostaje własną.
        IMFSample? sample = null;
        IMFMediaBuffer? buffer = null;

        try
        {
            if (MFCreateSample(out sample) < 0 || MFCreateMemoryBuffer(frame.Length, out buffer) < 0)
            {
                Failed = true;
                return;
            }

            if (buffer.Lock(out var memory, out _, out _) < 0)
            {
                Failed = true;
                return;
            }

            frame.CopyTo(new Span<byte>((void*)memory, frame.Length));
            buffer.Unlock();
            buffer.SetCurrentLength(frame.Length);
            sample.AddBuffer(buffer);

            // Odrzucona uszkodzona ramka: następna ma własny nagłówek i dekoduje się normalnie.
            _transform.ProcessInput(0, sample, 0);
        }
        finally
        {
            if (buffer is not null) Marshal.ReleaseComObject(buffer);
            if (sample is not null) Marshal.ReleaseComObject(sample);
        }
    }

    public int Pull(Span<float> output, out int sampleRate, out int channels)
    {
        sampleRate = _sampleRate;
        channels = _channels;

        if (_transform is null || _outputBuffer is null) return 0;

        // Zmiana typu nie oddaje próbek; po niej wystarcza jedno powtórzenie, a pętla chroni przed serią zmian.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var data = new OutputDataBuffer { Sample = _outputPointer };
            var result = _transform.ProcessOutput(0, 1, ref data, out _);

            if (data.Events != nint.Zero) Marshal.Release(data.Events);

            if (result == MF_E_TRANSFORM_NEED_MORE_INPUT) return 0;

            if (result == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                SelectOutputType(_transform);
                sampleRate = _sampleRate;
                channels = _channels;
                continue;
            }

            if (result < 0)
            {
                DecoderFailures.Log($"Media Foundation: dekoder AAC zwrócił 0x{result:X8}.");
                Failed = true;
                return 0;
            }

            return CopyOutput(output);
        }

        Failed = true;
        return 0;
    }

    public int Drain(Span<float> output, out int sampleRate, out int channels)
    {
        if (!_draining && _transform is not null)
        {
            _draining = true;
            _transform.ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0);
        }

        return Pull(output, out sampleRate, out channels);
    }

    private int CopyOutput(Span<float> output)
    {
        var buffer = _outputBuffer!;

        if (buffer.Lock(out var memory, out _, out var bytes) < 0)
        {
            Failed = true;
            return 0;
        }

        try
        {
            var frames = Math.Min(bytes / sizeof(float), output.Length) / _channels;
            new ReadOnlySpan<float>((void*)memory, frames * _channels).CopyTo(output);
            return frames;
        }
        finally
        {
            buffer.Unlock();
            buffer.SetCurrentLength(0);
        }
    }

    private static void SetInputType(IMFTransform transform, AdtsHeader header)
    {
        Check(MFCreateMediaType(out var type), "utworzenie typu wejściowego");

        try
        {
            SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            SetGuid(type, MF_MT_SUBTYPE, MFAudioFormat_AAC);
            SetInt(type, MF_MT_AUDIO_SAMPLES_PER_SECOND, header.SampleRate);
            SetInt(type, MF_MT_AUDIO_NUM_CHANNELS, header.ChannelConfiguration == 7 ? 8 : header.ChannelConfiguration);
            SetInt(type, MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            SetInt(type, MF_MT_AAC_PAYLOAD_TYPE, AdtsPayload);
            SetInt(type, MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, ProfileLevel);

            // Końcówka HEAACWAVEINFO (typ ładunku, profil, 8 bajtów zarezerwowanych) i AudioSpecificConfig.
            Span<byte> userData = stackalloc byte[14];
            userData.Clear();
            userData[0] = AdtsPayload;
            userData[2] = ProfileLevel;
            userData[12] = (byte)(header.AudioSpecificConfig >> 8);
            userData[13] = (byte)header.AudioSpecificConfig;

            var key = MF_MT_USER_DATA;
            fixed (byte* pointer = userData)
            {
                Check(type.SetBlob(ref key, (nint)pointer, userData.Length), "opis strumienia");
            }

            var result = transform.SetInputType(0, type, 0);
            if (result < 0)
            {
                throw new AacBackendException(
                    LiveFailureReason.UndecodableData, $"Dekoder AAC odrzucił strumień (0x{result:X8}).");
            }
        }
        finally
        {
            Marshal.ReleaseComObject(type);
        }
    }

    /// <summary>Wybiera próbki zmiennoprzecinkowe; dekoder proponuje je w częstotliwości po SBR.</summary>
    private void SelectOutputType(IMFTransform transform)
    {
        for (var index = 0; transform.GetOutputAvailableType(0, index, out var type) >= 0; index++)
        {
            try
            {
                var key = MF_MT_SUBTYPE;
                if (type.GetGUID(ref key, out var subtype) < 0 || subtype != MFAudioFormat_Float) continue;

                Check(transform.SetOutputType(0, type, 0), "ustawienie typu wyjściowego");

                var rateKey = MF_MT_AUDIO_SAMPLES_PER_SECOND;
                var channelsKey = MF_MT_AUDIO_NUM_CHANNELS;
                Check(type.GetUINT32(ref rateKey, out _sampleRate), "odczyt częstotliwości");
                Check(type.GetUINT32(ref channelsKey, out _channels), "odczyt liczby kanałów");
                return;
            }
            finally
            {
                Marshal.ReleaseComObject(type);
            }
        }

        throw new AacBackendException(LiveFailureReason.UndecodableData, "Dekoder AAC nie oddaje próbek zmiennoprzecinkowych.");
    }

    private void PrepareOutput(IMFTransform transform)
    {
        Check(transform.GetOutputStreamInfo(0, out var info), "opis wyjścia");

        if ((info.Flags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) != 0)
            throw new AacBackendException(LiveFailureReason.CodecUnavailable, "Dekoder AAC sam przydziela próbki wyjściowe.");

        Check(MFCreateSample(out _output), "próbka wyjściowa");
        Check(MFCreateMemoryBuffer(Math.Max(info.Size, OutputBytes), out _outputBuffer), "bufor wyjściowy");
        Check(_output.AddBuffer(_outputBuffer), "bufor próbki wyjściowej");

        _outputPointer = Marshal.GetComInterfaceForObject(_output, typeof(IMFSample));
    }

    private static void SetGuid(IMFMediaType type, Guid key, Guid value) =>
        Check(type.SetGUID(ref key, ref value), "opis typu");

    private static void SetInt(IMFMediaType type, Guid key, int value) =>
        Check(type.SetUINT32(ref key, value), "opis typu");

    private static void Check(int result, string what)
    {
        if (result < 0)
            throw new AacBackendException(LiveFailureReason.UndecodableData, $"Dekoder AAC, {what}: 0x{result:X8}.");
    }

    public void Dispose()
    {
        if (_outputPointer != nint.Zero)
        {
            Marshal.Release(_outputPointer);
            _outputPointer = nint.Zero;
        }

        if (_outputBuffer is not null) Marshal.ReleaseComObject(_outputBuffer);
        if (_output is not null) Marshal.ReleaseComObject(_output);
        if (_transform is not null) Marshal.ReleaseComObject(_transform);

        _outputBuffer = null;
        _output = null;
        _transform = null;
    }
}
