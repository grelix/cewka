namespace Cewka.Audio.Streaming;

/// <summary>Kodek rozpoznany z ramek, nie z nagłówków.</summary>
public enum LiveCodec { Unknown, Mp3, Aac, Ogg, Flac }

public enum LiveState { Connecting, Buffering, Playing, Rebuffering, Reconnecting, Failed, Stopped }

/// <summary>Kod przyczyny; tekst dobiera interfejs.</summary>
public enum LiveFailureReason
{
    Unspecified,
    ConnectionFailed, ServerError, Stalled, StreamEnded,
    Unauthorized, Forbidden, NotFound, UnavailableForLegalReasons, OtherHttpStatus,
    TlsFailed, AddressRefused, UrlBlocked, TooManyRedirects, InsecureDowngrade,
    PlaylistTooDeep, PlaylistTooLarge, PlaylistEmpty,
    NotAStream, UnrecognisedContent,
    HlsNotSupportedYet, CodecNotSupportedYet, UndecodableData,

    /// <summary>System nie ma dekodera tego kodeka: Windows N bez Media Feature Pack, Linux bez gst-libav.</summary>
    CodecUnavailable,
}

/// <param name="Detail">Szczegół do dziennika, nigdy na ekran.</param>
public readonly record struct LiveFailure(
    LiveFailureReason Reason, int? HttpStatus = null, LiveCodec Codec = LiveCodec.Unknown, string? Detail = null);

/// <param name="AllowLocalAddress">Adres wpisany przez użytkownika może prowadzić do sieci lokalnej.</param>
public sealed record LiveSource(
    string StationId, string Name, IReadOnlyList<Uri> Urls, string? CountryCode = null, bool AllowLocalAddress = false);

internal enum LiveReadStatus { Data, Waiting, Ended }

internal abstract record StreamMarker;

/// <summary>Początek połączenia albo nieciągłość: dekoder zaczyna od nowa.</summary>
internal sealed record StreamBegin(LiveCodec Codec) : StreamMarker;

/// <summary>Tytuł obowiązujący od tego miejsca; null, gdy stacja przestała go podawać.</summary>
internal sealed record StreamTitle(string? Title) : StreamMarker;

/// <summary>Bajty dźwięku ze znacznikami. Jeden czytelnik; nigdy nie blokuje.</summary>
internal interface ILiveByteSource
{
    /// <summary>Data: bajty do najbliższego znacznika albo sam znacznik (bytes = 0). Ended: przyczyna w Failure.</summary>
    LiveReadStatus Read(Span<byte> destination, out int bytes, out StreamMarker? marker);

    /// <summary>Null po zatrzymaniu przez użytkownika.</summary>
    LiveFailure? Failure { get; }

    /// <summary>Przepływność zmierzona przez dekoder; progi bufora przeliczają się z niej.</summary>
    void ReportBitrate(int kilobits);

    /// <summary>Tych danych nie da się zdekodować. True: źródło spróbuje inaczej, a czytelnik czeka na StreamBegin albo Ended.</summary>
    bool ReportUndecodable(LiveFailure failure);
}
