using System.Text;

namespace Cewka.App.Radio;

/// <summary>Wynik importu playlisty stacji.</summary>
/// <param name="Stations">Stacje z adresami dopuszczonymi (bez <c>AllowLocalAddress</c>).</param>
/// <param name="Rejected">Wpisy odrzucone, z powodem: adres niepoprawny, inny schemat niż http(s), domena z listy blokad.</param>
public sealed record PlaylistImport(IReadOnlyList<RadioStation> Stations, IReadOnlyList<(string Url, AddressVerdict Reason)> Rejected);

/// <summary>Import M3U i PLS do ulubionych oraz eksport do rozszerzonego M3U.</summary>
public static class StationPlaylist
{
    /// <summary>Plik M3U/M3U8 (z <c>#EXTINF</c> lub bez) albo PLS; rozpoznaje po treści.</summary>
    public static PlaylistImport Import(string text, RadioBlocklist? blocklist = null)
    {
        blocklist ??= RadioBlocklist.Current;
        var entries = text.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase)
            ? ParsePls(text)
            : ParseM3u(text);

        var stations = new List<RadioStation>();
        var rejected = new List<(string, AddressVerdict)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, url) in entries)
        {
            // Adresy z importu nigdy nie sięgają do sieci lokalnej; blokady jak przy wpisanych.
            var verdict = blocklist.CheckAddress(url);
            if (verdict != AddressVerdict.Allowed)
            {
                rejected.Add((url, verdict));
                continue;
            }

            var station = RadioState.CreateCustom(name ?? url, url, country: null, typed: false);
            if (blocklist.IsBlockedName(station.Name))
            {
                rejected.Add((url, AddressVerdict.Blocked));
                continue;
            }

            if (seen.Add(station.Id))
                stations.Add(station);
        }

        return new PlaylistImport(stations, rejected);
    }

    /// <summary>
    /// Eksport ulubionych do rozszerzonego M3U. Nazwę z krajem (przy nazwie powtórzonej w liście) buduje
    /// wywołujący przez <paramref name="displayName"/>.
    /// </summary>
    public static string Export(IEnumerable<RadioStation> stations, Func<RadioStation, string>? displayName = null)
    {
        var builder = new StringBuilder("#EXTM3U\n");
        foreach (var station in stations)
        {
            var url = station.Streams.FirstOrDefault()?.Url;
            if (string.IsNullOrEmpty(url))
                continue;

            var name = (displayName?.Invoke(station) ?? station.Name).Replace('\r', ' ').Replace('\n', ' ');
            builder.Append("#EXTINF:-1,").Append(name).Append('\n').Append(url).Append('\n');
        }

        return builder.ToString();
    }

    private static List<(string? Name, string Url)> ParseM3u(string text)
    {
        var result = new List<(string?, string)>();
        string? name = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t', '﻿');
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                name = comma >= 0 && comma + 1 < line.Length ? line[(comma + 1)..].Trim() : null;
            }
            else if (line[0] != '#')
            {
                result.Add((string.IsNullOrWhiteSpace(name) ? null : name, line));
                name = null;
            }
        }

        return result;
    }

    private static List<(string? Name, string Url)> ParsePls(string text)
    {
        var files = new SortedDictionary<int, string>();
        var titles = new Dictionary<int, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t', '﻿');
            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            var key = line[..equals].Trim();
            var value = line[(equals + 1)..].Trim();
            if (TryNumber(key, "File", out var fileIndex))
                files[fileIndex] = value;
            else if (TryNumber(key, "Title", out var titleIndex))
                titles[titleIndex] = value;
        }

        return [.. files.Select(f => ((string?)titles.GetValueOrDefault(f.Key), f.Value))];

        static bool TryNumber(string key, string prefix, out int number)
        {
            number = 0;
            return key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key.AsSpan(prefix.Length), out number);
        }
    }
}
