using Cewka.App.Localisation;
using Cewka.Audio;
using Cewka.Audio.Dsp;
using Cewka.Audio.Metadata;
using Cewka.Audio.Playback;
using Cewka.Audio.Streaming;

namespace Cewka.App.ViewModels;

/// <summary>Jeden etap toru sygnału: nazwa po lewej, opis po prawej.</summary>
public sealed record SignalPathStage(string Label, string Value);

/// <summary>Tor sygnału: okienko otwierane z opisu parametrów pliku albo jakości stacji.</summary>
public sealed partial class MainViewModel
{
    private const string Separator = " · ";
    private const string Arrow = " → ";

    private bool _signalPathOpen;
    private IReadOnlyList<SignalPathStage> _signalPathStages = [];
    private bool _signalPathUnchanged;

    /// <summary>Okienko toru jest otwarte; tylko wtedy jego treść odświeża się z zegarem okna.</summary>
    public bool IsSignalPathOpen
    {
        get => _signalPathOpen;
        set
        {
            if (_signalPathOpen == value) return;

            _signalPathOpen = value;
            if (value) RefreshSignalPath();
            Raise();
        }
    }

    /// <summary>Od źródła do urządzenia; między nimi tylko etapy, które w tej chwili zmieniają sygnał.</summary>
    public IReadOnlyList<SignalPathStage> SignalPathStages
    {
        get => _signalPathStages;
        private set => Set(ref _signalPathStages, value);
    }

    /// <summary>Między źródłem a urządzeniem nie ma żadnego etapu.</summary>
    public bool SignalPathUnchanged
    {
        get => _signalPathUnchanged;
        private set => Set(ref _signalPathUnchanged, value);
    }

    /// <summary>
    /// Składa tor od nowa przy otwartym okienku: z zegara odświeżania (zmiana utworu, ustawień, urządzenia)
    /// i po zmianie języka. Lista zmienia się tylko wtedy, gdy zmieniła się jej treść.
    /// </summary>
    internal void RefreshSignalPath()
    {
        if (!_signalPathOpen) return;

        var stages = BuildSignalPath(_engine.SignalPath);
        if (!stages.SequenceEqual(_signalPathStages)) SignalPathStages = stages;
        SignalPathUnchanged = stages.Count <= 2;
    }

    /// <summary>Etapy toru dla migawki silnika i bieżących ustawień.</summary>
    internal IReadOnlyList<SignalPathStage> BuildSignalPath(SignalPathSnapshot path)
    {
        var text = Strings.Current;
        var stages = new List<SignalPathStage> { new(text["SignalPathSource"], DescribeSource(path)) };

        if (path.RemapsChannels)
            stages.Add(new(text["SignalPathChannels"], ChannelsName(path.SourceChannels) + Arrow + ChannelsName(path.DeviceChannels)));

        if (path.Resamples)
        {
            stages.Add(new(text["SignalPathConversion"],
                Rate(path.SourceSampleRate) + Arrow + Rate(path.DeviceSampleRate) + Separator + text[LevelKey(path.Resampling)]));
        }

        if (path.Gain is { } gain && Math.Abs(gain.GainDecibels) >= 0.05)
        {
            var decibels = Decibels(gain.GainDecibels);

            // Stacja nie ma albumu: jej wzmocnienie to sam pomiar strumienia.
            var value = path.IsLive
                ? decibels
                : string.Format(text[gain.Kind == TrackGainKind.Album ? "SignalPathGainAlbum" : "SignalPathGainTrack"], decibels);
            stages.Add(new(text["OptionNormalisation"], value));
        }

        if (EqualiserEnabled && EqualiserShapesSound)
        {
            var value = IsParametricMode
                ? string.Format(text.Numbers, text["SignalPathEqualiserParametric"], Parametric.Bands.Count(band => band.Enabled))
                : text["SignalPathEqualiserGraphic"];
            stages.Add(new(text["Equaliser"], value));
        }

        var effects = SoundEffects.Where(effect => effect.Enabled).Select(effect => effect.Label).ToList();
        if (effects.Count > 0) stages.Add(new(text["Effects"], string.Join(", ", effects)));

        var percent = _isMuted ? 0 : (int)Math.Round(_volume * 100);
        if (percent < 100) stages.Add(new(text["SignalPathVolume"], percent.ToString(text.Numbers) + "%"));

        if (LimiterEnabled)
            stages.Add(new(text["OptionLimiter"], string.Format(text["SignalPathLimiterThreshold"], Threshold(Limiter.ThresholdDb))));

        stages.Add(new(text["SignalPathDevice"], DescribeDevice(path)));
        return stages;
    }

    /// <summary>Korektor grającego trybu odbiega od płaskiego: przedwzmacniacz albo pasmo z wzmocnieniem.</summary>
    private bool EqualiserShapesSound => IsParametricMode
        ? Parametric.Preamp.Gain != 0 || Parametric.Bands.Any(band => band.Enabled && band.Gain != 0)
        : Preamp.Gain != 0 || Bands.Any(band => band.Gain != 0);

    private string DescribeSource(SignalPathSnapshot path)
    {
        var text = Strings.Current;
        var parts = new List<string>();

        if (path.IsLive || (path.Entry is null && _radioMode))
        {
            parts.Add(text["SignalPathStream"]);

            var codec = _engine.LiveCodec;
            if (codec != LiveCodec.Unknown) parts.Add(codec.ToString().ToUpperInvariant());

            var kbps = _engine.InstantaneousBitrate;
            if (kbps <= 0) kbps = _engine.LiveInfo?.HeaderBitrateKbps ?? 0;
            if (kbps > 0) parts.Add(Kilobits(kbps));

            if (path.SourceSampleRate > 0) parts.Add(Rate(path.SourceSampleRate));
            if (path.SourceChannels > 0) parts.Add(ChannelsName(path.SourceChannels));
            return string.Join(Separator, parts);
        }

        var metadata = path.Entry?.Metadata ?? _currentMetadata;
        if (metadata?.Codec is { Length: > 0 } name) parts.Add(name);

        var rate = path.SourceSampleRate > 0 ? path.SourceSampleRate : metadata?.SampleRate ?? 0;
        if (rate > 0) parts.Add(Rate(rate));

        if (metadata is { BitDepth: > 0 }) parts.Add(metadata.BitDepth.ToString(text.Numbers) + "\u00a0bit");
        else if (BitrateOf(metadata) is > 0 and var kilobits) parts.Add(Kilobits(kilobits));

        var channels = path.SourceChannels > 0 ? path.SourceChannels : metadata?.Channels ?? 0;
        if (channels > 0) parts.Add(ChannelsName(channels));

        // Głębię bitową podają tylko formaty bezstratne; stratne zapisują współczynniki przekształcenia.
        if (metadata is { BitDepth: > 0 }) parts.Add(text["SignalPathLossless"]);

        return parts.Count > 0 ? string.Join(Separator, parts) : "—";
    }

    /// <summary>Przy zmiennej przepływności bieżący pomiar, jak w opisie parametrów pliku.</summary>
    private int BitrateOf(TrackMetadata? metadata) =>
        metadata is null ? 0 : metadata.IsVariableBitrate && _shownBitrate > 0 ? _shownBitrate : metadata.Bitrate;

    private string DescribeDevice(SignalPathSnapshot path)
    {
        if (!path.DeviceOpen) return ChosenDevice ?? Strings.Current["DeviceSystemDefault"];

        var parts = new List<string> { path.DeviceName, Rate(path.DeviceSampleRate), ChannelsName(path.DeviceChannels) };

        // Miniaudio otwiera urządzenie w Windows przez WASAPI w trybie współdzielonym; zaplecza
        // nakładka nie podaje, więc nie ma go w opisie.
        if (OperatingSystem.IsWindows()) parts.Add(Strings.Current["SignalPathSharedMode"]);

        return string.Join(Separator, parts);
    }

    private static string LevelKey(ResamplingLevel level) => level switch
    {
        ResamplingLevel.Economy => "QualityOff",
        ResamplingLevel.High => "QualityHigh",
        _ => "QualityStandard",
    };

    private static string ChannelsName(int channels) => channels switch
    {
        1 => Strings.Current["ChannelsMono"],
        2 => Strings.Current["ChannelsStereo"],
        _ => string.Format(Strings.Current.Numbers, Strings.Current["ChannelsCount"], channels),
    };

    /// <summary>Jak w opisie parametrów pliku: „44,1 kHz”, „48 kHz”, z separatorem języka.</summary>
    private static string Rate(int hertz) =>
        (hertz % 1000 == 0
            ? (hertz / 1000).ToString(Strings.Current.Numbers)
            : (hertz / 1000.0).ToString("0.#", Strings.Current.Numbers)) + " kHz";

    private static string Kilobits(int kbps) => kbps.ToString(Strings.Current.Numbers) + "\u00a0kbps";

    /// <summary>Próg bez zbędnego zera po przecinku: „−1 dB”, „−0,5 dB”.</summary>
    private static string Threshold(double value) =>
        (value < 0 ? "−" : string.Empty) + Math.Abs(value).ToString("0.#", Strings.Current.Numbers) + "\u00a0dB";

    /// <summary>Ze znakiem jak na suwakach korektora: „+2,5 dB”, „−3,0 dB”.</summary>
    private static string Decibels(double value)
    {
        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        var sign = rounded > 0 ? "+" : rounded < 0 ? "−" : string.Empty;
        return sign + Math.Abs(rounded).ToString("0.0", Strings.Current.Numbers) + "\u00a0dB";
    }
}
