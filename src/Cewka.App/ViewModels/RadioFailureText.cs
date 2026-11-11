using Cewka.App.Localisation;
using Cewka.Audio.Streaming;

namespace Cewka.App.ViewModels;

/// <summary>
/// Kody przyczyn z toru radia zamienione na teksty interfejsu. Szczegół techniczny (kod HTTP, tekst wyjątku)
/// zostaje w dzienniku; użytkownik dostaje, co się stało i co można zrobić.
/// </summary>
internal static class RadioFailureText
{
    /// <summary>Klucz komunikatu dla przyczyny; każda wartość wyliczenia ma swój klucz.</summary>
    public static string KeyFor(LiveFailureReason reason) => reason switch
    {
        LiveFailureReason.ConnectionFailed => "RadioFailureNetwork",
        LiveFailureReason.Forbidden or LiveFailureReason.UnavailableForLegalReasons => "RadioFailureGeo",
        LiveFailureReason.StreamEnded => "RadioFailureEnded",
        LiveFailureReason.TlsFailed or LiveFailureReason.InsecureDowngrade => "RadioFailureSecure",
        LiveFailureReason.AddressRefused or LiveFailureReason.UrlBlocked => "RadioFailureRefused",
        LiveFailureReason.NotAStream or LiveFailureReason.PlaylistTooDeep
            or LiveFailureReason.PlaylistTooLarge or LiveFailureReason.PlaylistEmpty => "RadioFailureNotStream",
        LiveFailureReason.UnrecognisedContent or LiveFailureReason.UndecodableData
            or LiveFailureReason.CodecNotSupportedYet => "RadioFailureFormat",
        LiveFailureReason.HlsNotSupportedYet => "RadioFailureHls",
        LiveFailureReason.CodecUnavailable => OperatingSystem.IsWindows() ? "RadioFailureCodecWindows"
            : OperatingSystem.IsLinux() ? "RadioFailureCodecLinux" : "RadioFailureCodecOther",
        _ => "RadioFailureNoResponse",
    };

    /// <summary>Czy ponowna próba ma sens: nie ma go przy adresie niedozwolonym, formacie i dekoderze, których próba nie zmieni.</summary>
    public static bool CanRetry(LiveFailureReason reason) => reason is not (
        LiveFailureReason.AddressRefused or LiveFailureReason.UrlBlocked or LiveFailureReason.NotAStream
        or LiveFailureReason.PlaylistTooDeep or LiveFailureReason.PlaylistTooLarge or LiveFailureReason.PlaylistEmpty
        or LiveFailureReason.UnrecognisedContent or LiveFailureReason.UndecodableData
        or LiveFailureReason.CodecNotSupportedYet or LiveFailureReason.HlsNotSupportedYet
        or LiveFailureReason.CodecUnavailable or LiveFailureReason.Forbidden
        or LiveFailureReason.UnavailableForLegalReasons);

    public static string For(LiveFailureReason reason) => For(reason, OsReleaseText());

    /// <summary>Tekst z nazwą pakietu dekodera dla dystrybucji opisanej w <paramref name="osRelease"/> (zawartość /etc/os-release).</summary>
    public static string For(LiveFailureReason reason, string? osRelease)
    {
        var key = KeyFor(reason);
        return key == "RadioFailureCodecLinux"
            ? string.Format(Strings.Current[key], DecoderPackage(osRelease))
            : Strings.Current[key];
    }

    /// <summary>Nazwa pakietu z dekoderem AAC (gst-libav) w rodzinie dystrybucji z pliku os-release.</summary>
    public static string DecoderPackage(string? osRelease)
    {
        var ids = new List<string>();
        foreach (var raw in (osRelease ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var name = line[..separator];
            if (name is not ("ID" or "ID_LIKE")) continue;

            ids.AddRange(line[(separator + 1)..].Trim('"', '\'').ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        if (ids.Any(id => id is "fedora" or "rhel" or "centos" or "rocky" or "almalinux")) return "gstreamer1-libav";
        if (ids.Any(id => id is "debian" or "ubuntu" or "linuxmint" or "pop" or "raspbian")) return "gstreamer1.0-libav";
        if (ids.Any(id => id is "arch" or "manjaro" or "endeavouros")) return "gst-libav";
        return "gst-libav";
    }

    private static string? OsReleaseText()
    {
        if (!OperatingSystem.IsLinux()) return null;

        try { return File.ReadAllText("/etc/os-release"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
