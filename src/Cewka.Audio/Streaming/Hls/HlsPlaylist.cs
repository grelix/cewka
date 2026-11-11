using System.Globalization;
using System.Text;

namespace Cewka.Audio.Streaming.Hls;

internal sealed record HlsVariant(Uri Url, int? BandwidthBps, string? Codecs, bool HasResolution, string? AudioGroup);

internal sealed record HlsRendition(Uri Url, string Group, bool IsDefault);

internal sealed record HlsSegment(Uri Url, double Seconds, long Sequence);

internal abstract record HlsPlaylist;

/// <summary>Playlista główna: warianty tej samej stacji.</summary>
internal sealed record HlsMaster(IReadOnlyList<HlsVariant> Variants, IReadOnlyList<HlsRendition> AudioRenditions) : HlsPlaylist;

/// <summary>Playlista wariantu.</summary>
/// <param name="Unsupported">Szyfrowanie, fMP4 albo zakresy bajtów: co uniemożliwia odtworzenie; tylko do dziennika.</param>
internal sealed record HlsMedia(double TargetSeconds, bool Ended, IReadOnlyList<HlsSegment> Segments, string? Unsupported) : HlsPlaylist;

/// <summary>Wybrany adres playlisty wariantu.</summary>
/// <param name="BitrateKbps">Tylko dla wariantu bez obrazu; z obrazem pasmo nie mówi nic o dźwięku.</param>
internal readonly record struct HlsChoice(Uri Url, int? BitrateKbps);

/// <summary>Playlisty według RFC 8216. Treść pochodzi z sieci, więc wiersze nie do odczytania są pomijane.</summary>
internal static class HlsPlaylistParser
{
    /// <summary>Gdy serwer nie poda czasu docelowego, nie dojdzie do odświeżania częstszego niż typowy segment.</summary>
    private const double DefaultTargetSeconds = 6;

    private static readonly string[] VideoCodecs = ["avc", "hvc", "hev", "vp8", "vp9", "vp09", "av01", "mp4v"];

    public static HlsPlaylist Parse(ReadOnlySpan<byte> content, Uri baseUrl)
    {
        var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
        var variants = new List<HlsVariant>();
        var renditions = new List<HlsRendition>();
        var segments = new List<HlsSegment>();
        var target = 0.0;
        var sequence = 0L;
        var ended = false;
        string? unsupported = null;
        double? seconds = null;
        Dictionary<string, string>? streamInf = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line[0] != '#')
            {
                if (TryResolve(baseUrl, line, out var url))
                {
                    if (streamInf is not null)
                        variants.Add(ToVariant(url, streamInf));
                    else if (seconds is { } duration)
                        segments.Add(new HlsSegment(url, duration, sequence + segments.Count));
                }

                streamInf = null;
                seconds = null;
                continue;
            }

            if (Value(line, "#EXT-X-STREAM-INF:") is { } inf)
            {
                streamInf = Attributes(inf);
            }
            else if (Value(line, "#EXT-X-MEDIA:") is { } media)
            {
                var attributes = Attributes(media);
                if (attributes.GetValueOrDefault("TYPE") == "AUDIO"
                    && attributes.TryGetValue("URI", out var uri) && TryResolve(baseUrl, uri, out var url))
                {
                    renditions.Add(new HlsRendition(url, attributes.GetValueOrDefault("GROUP-ID", ""),
                        attributes.GetValueOrDefault("DEFAULT") == "YES"));
                }
            }
            else if (Value(line, "#EXTINF:") is { } inf2)
            {
                var number = inf2.Split(',')[0];
                seconds = double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
                    ? parsed
                    : 0;
            }
            else if (Value(line, "#EXT-X-TARGETDURATION:") is { } targetText)
            {
                if (double.TryParse(targetText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                    target = parsed;
            }
            else if (Value(line, "#EXT-X-MEDIA-SEQUENCE:") is { } sequenceText)
            {
                if (long.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    sequence = parsed;
            }
            else if (line == "#EXT-X-ENDLIST")
            {
                ended = true;
            }
            else if (Value(line, "#EXT-X-KEY:") is { } key)
            {
                if (Attributes(key).GetValueOrDefault("METHOD") != "NONE") unsupported = "Szyfrowane segmenty";
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                unsupported = "Segmenty fMP4";
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                // Bez obsługi zakresów każdy segment byłby całym plikiem, grany od początku za każdym razem.
                unsupported = "Segmenty z zakresem bajtów";
            }
        }

        if (variants.Count > 0) return new HlsMaster(variants, renditions);

        return new HlsMedia(target > 0 ? target : DefaultTargetSeconds, ended, segments, unsupported);
    }

    /// <summary>
    /// Najlepszy wariant bez obrazu. Gdy każdy ma obraz, osobna ścieżka dźwięku, a na końcu najtańszy wariant:
    /// demultiplekser odrzuci obraz, ale jego bajty i tak trzeba pobrać. Null, gdy żaden kodek nie wchodzi w grę.
    /// </summary>
    public static HlsChoice? Choose(HlsMaster master)
    {
        var usable = master.Variants.Where(v => HasUsableAudio(v.Codecs)).ToList();
        if (usable.Count == 0) return null;

        var audioOnly = usable.Where(v => !HasVideo(v)).OrderByDescending(v => v.BandwidthBps ?? 0).FirstOrDefault();
        if (audioOnly is not null)
        {
            var kilobits = audioOnly.BandwidthBps is { } bps ? (int?)Math.Round(bps / 1000.0) : null;
            return new HlsChoice(audioOnly.Url, kilobits);
        }

        var cheapest = usable.OrderBy(v => v.BandwidthBps ?? int.MaxValue).First();

        var separate = master.AudioRenditions
            .Where(r => r.Group == cheapest.AudioGroup)
            .OrderByDescending(r => r.IsDefault)
            .FirstOrDefault();

        return new HlsChoice(separate?.Url ?? cheapest.Url, null);
    }

    private static bool HasVideo(HlsVariant variant) =>
        variant.HasResolution || Tags(variant.Codecs).Any(c => VideoCodecs.Any(v => c.StartsWith(v, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Bez listy kodeków rozstrzyga dopiero treść; mp4a.40 obejmuje AAC i MP3, a mp4a.69 i 6b to MP3.</summary>
    private static bool HasUsableAudio(string? codecs) =>
        codecs is null || Tags(codecs).Any(c =>
            c.StartsWith("mp4a.40", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("mp4a.69", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("mp4a.6b", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Tags(string? codecs) =>
        codecs?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    private static HlsVariant ToVariant(Uri url, Dictionary<string, string> attributes)
    {
        int? bandwidth = attributes.TryGetValue("BANDWIDTH", out var text)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        return new HlsVariant(url, bandwidth, attributes.GetValueOrDefault("CODECS"),
            attributes.ContainsKey("RESOLUTION"), attributes.GetValueOrDefault("AUDIO"));
    }

    private static bool TryResolve(Uri baseUrl, string text, out Uri url)
    {
        if (Uri.TryCreate(baseUrl, text, out url!) && url.Scheme is "http" or "https") return true;

        url = baseUrl;
        return false;
    }

    private static string? Value(string line, string tag) =>
        line.StartsWith(tag, StringComparison.Ordinal) ? line[tag.Length..] : null;

    /// <summary>Lista NAZWA=wartość; przecinek w cudzysłowie, jak w CODECS, nie rozdziela.</summary>
    private static Dictionary<string, string> Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = 0;

        while (at < text.Length)
        {
            var equals = text.IndexOf('=', at);
            if (equals < 0) break;

            var name = text[at..equals].Trim();
            string value;

            if (equals + 1 < text.Length && text[equals + 1] == '"')
            {
                var close = text.IndexOf('"', equals + 2);
                if (close < 0) break;

                value = text[(equals + 2)..close];
                at = text.IndexOf(',', close);
            }
            else
            {
                at = text.IndexOf(',', equals);
                value = (at < 0 ? text[(equals + 1)..] : text[(equals + 1)..at]).Trim();
            }

            result[name] = value;

            if (at < 0) break;
            at++;
        }

        return result;
    }
}
