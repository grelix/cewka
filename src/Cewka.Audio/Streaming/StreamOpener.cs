using System.Buffers;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using Cewka.Audio.Streaming.Hls;

namespace Cewka.Audio.Streaming;

internal sealed record IcyHeaders(string? Name, string? Genre, string? Url, int? BitrateKbps, int? SampleRate, string? ContentType);

/// <summary>Blok metadanych z danych przeczytanych przy rozpoznawaniu; Offset to miejsce w Head.</summary>
internal readonly record struct HeadMetadataBlock(int Offset, byte[] Block);

/// <summary>Otwarte połączenie z dźwiękiem. Head to dźwięk przeczytany już przy rozpoznawaniu, bez metadanych.</summary>
internal sealed record OpenedStream(HttpResponseMessage Response, Stream Body, LiveCodec Codec, byte[] Head,
    IcyHeaders Headers, int MetaInt, Uri FinalUrl, IcyDemuxer? Demuxer) : IDisposable
{
    public IReadOnlyList<HeadMetadataBlock> HeadMetadata { get; init; } = [];

    /// <summary>Dłuższa cisza niż zwykle nie jest zastojem: HLS oddaje dźwięk co segment.</summary>
    public TimeSpan? StallTimeout { get; init; }

    public void Dispose()
    {
        Body.Dispose();
        Response.Dispose();
    }
}

/// <param name="textFallback">Kodowanie nagłówków stacji spoza UTF-8; to samo co dla tytułów.</param>
internal sealed class StreamOpener(StreamHttp http, AddressPolicy policy, LiveOptions options, Encoding? textFallback = null)
{
    private const int MinMetaInt = 256;
    private const int MaxMetaInt = 65536;
    private const int ReadChunkBytes = 8 * 1024;
    private const int FirstBytesKept = 16;

    private sealed class OpenRun
    {
        // Zagnieżdżone listy rosną wykładniczo, więc limit obowiązuje na całe otwarcie.
        public int EntriesLeft = PlaylistParser.MaxEntries;
    }

    private sealed record Fetched(HttpResponseMessage Response, Uri FinalUrl, bool Downgraded, bool AllowLocal);

    /// <summary>Ostatnie pobranie; do raportu narzędzia katalogu.</summary>
    public ConnectionTrace? LastTrace { get; private set; }

    /// <summary>Idzie za przekierowaniami i listami, aż dostanie dźwięk; to połączenie zostaje otwarte.</summary>
    public async Task<OpenedStream> OpenAsync(Uri url, bool allowLocal, CancellationToken ct)
    {
        CheckEntry(url, allowLocal);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.OpenTimeout);
        try
        {
            return await OpenEntryAsync(url, allowLocal, 0, false, new OpenRun(), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.ConnectionFailed, Detail: "Limit czasu otwarcia"));
        }
    }

    private void CheckEntry(Uri url, bool allowLocal)
    {
        policy.CheckAddress(url, allowLocal);
        if (url.Scheme is not ("http" or "https"))
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.UrlBlocked, Detail: url.Scheme));
    }

    private async Task<OpenedStream> OpenEntryAsync(
        Uri url, bool allowLocal, int depth, bool cameFromHttps, OpenRun run, CancellationToken ct)
    {
        var fetched = await FetchAsync(url, allowLocal, true, cameFromHttps, ct).ConfigureAwait(false);

        // Niewiarygodny metaint rozjechałby dźwięk z metadanymi, więc jedna próba bez metadanych.
        var metaInt = ParseMetaInt(fetched.Response);
        if (metaInt is null)
        {
            fetched.Response.Dispose();

            // Ta sama droga co przed chwilą, więc to, co już wiadomo o adresie, obowiązuje.
            fetched = await FetchAsync(url, fetched.AllowLocal, false, cameFromHttps, ct).ConfigureAwait(false);
            metaInt = ParseMetaInt(fetched.Response) ?? 0;
        }

        var keepOpen = false;
        try
        {
            var result = await ReadContentAsync(fetched, metaInt.Value, depth, cameFromHttps, run, ct).ConfigureAwait(false);
            keepOpen = true;
            return result;
        }
        finally
        {
            if (!keepOpen)
                fetched.Response.Dispose();
        }
    }

    /// <summary>
    /// Pobranie bez rozpoznawania treści, z tymi samymi przekierowaniami i regułą adresów. HTTP jest
    /// dopuszczony tylko dla dźwięku, więc obraz idzie wyłącznie po HTTPS, także po przekierowaniach.
    /// </summary>
    public async Task<HttpResponseMessage> FetchImageAsync(Uri url, bool allowLocal, CancellationToken ct)
    {
        CheckEntry(url, allowLocal);
        if (url.Scheme != "https")
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.InsecureDowngrade, Detail: url.Scheme));

        var fetched = await FetchAsync(url, allowLocal, false, false, ct, httpsOnly: true).ConfigureAwait(false);
        return fetched.Response;
    }

    /// <param name="cameFromHttps">Adres pochodzi z listy po HTTPS: każdy etap po HTTP to już obniżenie bezpieczeństwa.</param>
    /// <param name="httpsOnly">Przekierowanie na HTTP kończy pobranie, zanim żądanie wyjdzie jawnym tekstem.</param>
    private async Task<Fetched> FetchAsync(
        Uri url, bool allowLocal, bool icyMetadata, bool cameFromHttps, CancellationToken ct, bool httpsOnly = false)
    {
        var trace = new ConnectionTrace();
        LastTrace = trace;
        var visited = new HashSet<Uri> { url };
        var current = url;
        var downgraded = cameFromHttps && url.Scheme == "http";

        while (true)
        {
            trace.FinalUrl = current;
            trace.SawIcyStatusLine = false;
            var response = await http
                .SendAsync(current, new RequestContext(allowLocal, trace), icyMetadata, ct)
                .ConfigureAwait(false);
            allowLocal &= !trace.ConnectedPublic;

            var status = (int)response.StatusCode;
            trace.HttpStatus = status;
            trace.StatusLine = $"{(trace.SawIcyStatusLine ? "ICY" : $"HTTP/{response.Version}")} {status} {response.ReasonPhrase}".TrimEnd();
            trace.ContentType = Header(response.Content.Headers.NonValidated, "Content-Type");
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                using (response)
                {
                    var next = NextLocation(response, current, status);
                    if (trace.Redirects >= options.MaxRedirects || !visited.Add(next))
                        throw new LiveOpenException(new LiveFailure(LiveFailureReason.TooManyRedirects, status));

                    policy.CheckTransition(current, next, allowLocal);
                    if (httpsOnly && next.Scheme == "http")
                    {
                        trace.FinalUrl = next;
                        throw new LiveOpenException(new LiveFailure(LiveFailureReason.InsecureDowngrade, status));
                    }

                    downgraded |= next.Scheme == "http" && (cameFromHttps || current.Scheme == "https");
                    trace.Redirects++;
                    current = next;
                    continue;
                }
            }

            if (status is < 200 or > 299)
            {
                using (response)
                    throw StatusFailure(response, status);
            }

            return new Fetched(response, current, downgraded, allowLocal);
        }
    }

    private static Uri NextLocation(HttpResponseMessage response, Uri current, int status)
    {
        var location = response.Headers.Location;
        var next = location is null ? null : location.IsAbsoluteUri ? location : new Uri(current, location);
        if (next is null || next.Scheme is not ("http" or "https"))
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.OtherHttpStatus, status, Detail: location?.ToString()));
        return next;
    }

    private LiveOpenException StatusFailure(HttpResponseMessage response, int status)
    {
        var reason = status switch
        {
            401 => LiveFailureReason.Unauthorized,
            403 => LiveFailureReason.Forbidden,
            404 or 410 => LiveFailureReason.NotFound,
            451 => LiveFailureReason.UnavailableForLegalReasons,
            408 or 429 or >= 500 => LiveFailureReason.ServerError,
            _ => LiveFailureReason.OtherHttpStatus,
        };

        TimeSpan? retryAfter = null;
        if (reason == LiveFailureReason.ServerError && response.Headers.RetryAfter is { } header)
            retryAfter = header.Delta ?? header.Date - options.Time.GetUtcNow();
        return new LiveOpenException(new LiveFailure(reason, status), retryAfter);
    }

    /// <summary>0: bez metadanych; null: nagłówek niewiarygodny.</summary>
    private static int? ParseMetaInt(HttpResponseMessage response)
    {
        if (!response.Headers.NonValidated.TryGetValues("icy-metaint", out var values))
            return 0;

        int? result = null;
        foreach (var value in values)
        {
            if (!int.TryParse(value, out var number) || number is < MinMetaInt or > MaxMetaInt)
                return null;
            if (result is not null && result != number)
                return null;
            result = number;
        }

        return result ?? 0;
    }

    private async Task<OpenedStream> ReadContentAsync(
        Fetched fetched, int metaInt, int depth, bool cameFromHttps, OpenRun run, CancellationToken ct)
    {
        var response = fetched.Response;
        var contentType = Header(response.Content.Headers.NonValidated, "Content-Type");
        var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var collector = new HeadCollector();
        var demuxer = metaInt > 0 ? new IcyDemuxer(metaInt, collector) : null;
        var buffer = new byte[ReadChunkBytes];

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(options.StallTimeout);

        SniffKind kind;
        LiveCodec codec;
        PlaylistKind list;
        var cutShort = false;
        while (true)
        {
            kind = ContentSniffer.Classify(collector.Audio, contentType, out codec, out list);
            if (kind != SniffKind.NeedMore)
                break;
            if (collector.Audio.Length >= options.SniffBytes + ContentSniffer.LeadingTagBytes(collector.Audio))
            {
                kind = ContentSniffer.Classify(collector.Audio, contentType, out codec, out list, final: true);
                break;
            }

            int read;
            try
            {
                read = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (collector.Audio.IsEmpty)
                    throw new LiveOpenException(new LiveFailure(LiveFailureReason.Stalled));
                kind = ContentSniffer.Classify(collector.Audio, contentType, out codec, out list, final: true);
                cutShort = true;
                break;
            }

            if (read == 0)
            {
                if (collector.Audio.IsEmpty)
                    throw new LiveOpenException(new LiveFailure(LiveFailureReason.StreamEnded, Detail: "Pusta odpowiedź"));
                kind = ContentSniffer.Classify(collector.Audio, contentType, out codec, out list, final: true);
                cutShort = true;
                break;
            }

            Feed(buffer.AsSpan(0, read), demuxer, collector);
        }

        if (LastTrace is { } trace)
            trace.FirstBytes = collector.Audio[..Math.Min(collector.Audio.Length, FirstBytesKept)].ToArray();

        switch (kind)
        {
            case SniffKind.Hls:
                return await OpenHlsAsync(fetched, contentType, body, demuxer, collector, buffer, cameFromHttps, limit, ct)
                    .ConfigureAwait(false);
            case SniffKind.Html:
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.NotAStream, Detail: contentType));
            case SniffKind.Unknown:
                // Urwane przed rozpoznaniem: to może być stacja w trakcie restartu, więc warto ponowić.
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.UnrecognisedContent, Detail: contentType), transient: cutShort);
            case SniffKind.Playlist:
                return await OpenListAsync(fetched, list, body, demuxer, collector, buffer, depth, cameFromHttps, run, limit, ct)
                    .ConfigureAwait(false);
        }

        if (!options.CodecPlayable(codec))
        {
            // AAC gra program, a brakuje tylko dekodera systemu; inne kodeki nie mają jeszcze obsługi.
            var reason = codec == LiveCodec.Aac ? LiveFailureReason.CodecUnavailable : LiveFailureReason.CodecNotSupportedYet;
            throw new LiveOpenException(new LiveFailure(reason, Codec: codec));
        }

        return new OpenedStream(response, body, codec, collector.Audio.ToArray(), ReadHeaders(response, contentType),
            metaInt, fetched.FinalUrl, demuxer)
        {
            HeadMetadata = collector.Metadata,
        };
    }

    private async Task<OpenedStream> OpenListAsync(
        Fetched fetched, PlaylistKind kind, Stream body, IcyDemuxer? demuxer, HeadCollector collector, byte[] buffer,
        int depth, bool cameFromHttps, OpenRun run, CancellationTokenSource limit, CancellationToken ct)
    {
        var secure = cameFromHttps || fetched.FinalUrl.Scheme == "https";
        if (fetched.Downgraded)
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.InsecureDowngrade));
        if (depth >= options.MaxPlaylistDepth)
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.PlaylistTooDeep));

        await ReadRestOfListAsync(body, demuxer, collector, buffer, limit, ct).ConfigureAwait(false);

        // Znacznik HLS mógł leżeć dalej niż początek rozpoznany w pierwszej porcji.
        if (ContentSniffer.Classify(collector.Audio, null, out _, out _, final: true) == SniffKind.Hls)
            return await OpenHlsListAsync(fetched, null, collector, cameFromHttps, ct).ConfigureAwait(false);

        var entries = PlaylistParser.Parse(collector.Audio, kind, fetched.FinalUrl);
        if (entries.Count == 0)
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.PlaylistEmpty));

        // Lista pobrana: jej połączenie nie jest już potrzebne.
        fetched.Response.Dispose();

        // Przejściowy błąd dowolnego wpisu może minąć, więc adres trwale odpada dopiero, gdy wszystkie są trwałe.
        ExceptionDispatchInfo? first = null;
        ExceptionDispatchInfo? firstTransient = null;
        foreach (var entry in entries)
        {
            if (run.EntriesLeft-- <= 0)
                break;
            try
            {
                policy.CheckTransition(fetched.FinalUrl, entry, fetched.AllowLocal);
                return await OpenEntryAsync(entry, fetched.AllowLocal, depth + 1, secure, run, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                first ??= ExceptionDispatchInfo.Capture(e);
                if (!LiveErrors.Classify(e).Permanent)
                    firstTransient ??= ExceptionDispatchInfo.Capture(e);
            }
        }

        (firstTransient ?? first)?.Throw();
        throw new LiveOpenException(new LiveFailure(LiveFailureReason.PlaylistTooLarge, Detail: "Wyczerpany limit wpisów"));
    }

    /// <summary>Limit czasu rozpoznawania obowiązuje też dla reszty listy: lista nie jest strumieniem.</summary>
    private async Task ReadRestOfListAsync(
        Stream body, IcyDemuxer? demuxer, HeadCollector collector, byte[] buffer, CancellationTokenSource limit, CancellationToken ct)
    {
        while (true)
        {
            if (collector.Audio.Length > options.MaxPlaylistBytes)
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.PlaylistTooLarge));

            int read;
            try
            {
                read = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.Stalled, Detail: "Lista nie doszła w całości"));
            }

            if (read == 0)
                return;
            Feed(buffer.AsSpan(0, read), demuxer, collector);
        }
    }

    private async Task<OpenedStream> OpenHlsAsync(
        Fetched fetched, string? contentType, Stream body, IcyDemuxer? demuxer, HeadCollector collector, byte[] buffer,
        bool cameFromHttps, CancellationTokenSource limit, CancellationToken ct)
    {
        await ReadRestOfListAsync(body, demuxer, collector, buffer, limit, ct).ConfigureAwait(false);
        return await OpenHlsListAsync(fetched, contentType, collector, cameFromHttps, ct).ConfigureAwait(false);
    }

    /// <summary>Od playlisty głównej przez wariant do pierwszych bajtów dźwięku, po których rozpoznaje się kodek.</summary>
    private async Task<OpenedStream> OpenHlsListAsync(
        Fetched fetched, string? contentType, HeadCollector collector, bool cameFromHttps, CancellationToken ct)
    {
        if (fetched.Downgraded)
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.InsecureDowngrade));

        var headers = ReadHeaders(fetched.Response, contentType);
        var fetcher = new HlsFetcher(this, fetched.AllowLocal, cameFromHttps || fetched.FinalUrl.Scheme == "https");

        var playlist = HlsPlaylistParser.Parse(collector.Audio, fetched.FinalUrl);
        var mediaUrl = fetched.FinalUrl;

        if (playlist is HlsMaster master)
        {
            if (HlsPlaylistParser.Choose(master) is not { } choice)
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.HlsNotSupportedYet, Detail: "Żaden wariant nie ma obsługiwanego dźwięku"));

            headers = headers with { BitrateKbps = choice.BitrateKbps is { } kilobits && CompressedBuffer.IsPlausibleBitrate(kilobits) ? kilobits : null };

            var (content, finalUrl) = await fetcher.FetchPlaylistAsync(choice.Url, fetched.FinalUrl, ct).ConfigureAwait(false);
            playlist = HlsPlaylistParser.Parse(content, finalUrl);
            mediaUrl = finalUrl;
        }

        if (playlist is not HlsMedia media)
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.PlaylistTooDeep, Detail: "Playlista główna wskazuje następną"));
        if (media.Unsupported is { } unsupported)
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.HlsNotSupportedYet, Detail: unsupported));
        if (media.Segments.Count == 0)
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.PlaylistEmpty));

        // Lista pobrana: jej połączenie nie jest już potrzebne.
        fetched.Response.Dispose();

        var stream = new HlsAudioStream(fetcher, mediaUrl, media);
        var keepOpen = false;
        try
        {
            var (codec, head) = await RecogniseHlsAudioAsync(stream, ct).ConfigureAwait(false);

            if (!options.CodecPlayable(codec))
            {
                var reason = codec == LiveCodec.Aac ? LiveFailureReason.CodecUnavailable : LiveFailureReason.CodecNotSupportedYet;
                throw new LiveOpenException(new LiveFailure(reason, Codec: codec));
            }

            keepOpen = true;
            return new OpenedStream(fetched.Response, stream, codec, head, headers, 0, mediaUrl, null)
            {
                StallTimeout = stream.QuietLimit > options.StallTimeout ? stream.QuietLimit : options.StallTimeout,
            };
        }
        finally
        {
            if (!keepOpen)
                stream.Dispose();
        }
    }

    /// <summary>Kodek z ramek, jak przy zwykłym strumieniu; playlista go nie gwarantuje.</summary>
    private async Task<(LiveCodec Codec, byte[] Head)> RecogniseHlsAudioAsync(HlsAudioStream stream, CancellationToken ct)
    {
        var head = new ArrayBufferWriter<byte>(ReadChunkBytes);
        var chunk = new byte[ReadChunkBytes];

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(stream.QuietLimit > options.StallTimeout ? stream.QuietLimit : options.StallTimeout);

        while (true)
        {
            var kind = ContentSniffer.Classify(head.WrittenSpan, null, out var codec, out _);
            if (kind == SniffKind.NeedMore && head.WrittenCount >= options.SniffBytes)
                kind = ContentSniffer.Classify(head.WrittenSpan, null, out codec, out _, final: true);

            if (kind == SniffKind.Audio)
                return (codec, head.WrittenSpan.ToArray());
            if (kind != SniffKind.NeedMore)
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.UnrecognisedContent, Detail: "Segment HLS"));

            int read;
            try
            {
                read = await stream.ReadAsync(chunk, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.Stalled, Detail: "Segment HLS bez dźwięku"));
            }

            if (read == 0)
            {
                kind = ContentSniffer.Classify(head.WrittenSpan, null, out codec, out _, final: true);
                if (kind == SniffKind.Audio)
                    return (codec, head.WrittenSpan.ToArray());
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.StreamEnded, Detail: "Segment HLS bez dźwięku"), transient: true);
            }

            head.Write(chunk.AsSpan(0, read));
        }
    }

    private AddressPolicy Policy => policy;

    private LiveOptions Options => options;

    /// <summary>Playlisty tylko bez obniżenia bezpieczeństwa; segmenty to dźwięk, jak wpisy zwykłych list.</summary>
    private sealed class HlsFetcher(StreamOpener opener, bool allowLocal, bool secure) : IHlsFetcher
    {
        public async Task<(byte[] Content, Uri FinalUrl)> FetchPlaylistAsync(Uri url, Uri from, CancellationToken ct)
        {
            opener.Policy.CheckTransition(from, url, allowLocal);

            var fetched = await opener.FetchAsync(url, allowLocal, false, secure, ct).ConfigureAwait(false);
            using var response = fetched.Response;

            // Po pierwszym adresie publicznym droga do lokalnych nie wraca, także w następnych pobraniach.
            allowLocal = fetched.AllowLocal;

            if (fetched.Downgraded)
                throw new LiveOpenException(new LiveFailure(LiveFailureReason.InsecureDowngrade));

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(opener.Options.StallTimeout);

            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var collector = new HeadCollector();
            await opener.ReadRestOfListAsync(body, null, collector, new byte[ReadChunkBytes], limit, ct).ConfigureAwait(false);

            return (collector.Audio.ToArray(), fetched.FinalUrl);
        }

        public async Task<HttpResponseMessage> OpenSegmentAsync(Uri url, Uri from, CancellationToken ct)
        {
            opener.Policy.CheckTransition(from, url, allowLocal);

            var fetched = await opener.FetchAsync(url, allowLocal, false, secure, ct).ConfigureAwait(false);
            allowLocal = fetched.AllowLocal;
            return fetched.Response;
        }
    }

    private static void Feed(ReadOnlySpan<byte> data, IcyDemuxer? demuxer, HeadCollector collector)
    {
        if (demuxer is not null)
            demuxer.Push(data);
        else
            collector.OnAudio(data);
    }

    private IcyHeaders ReadHeaders(HttpResponseMessage response, string? contentType)
    {
        var headers = response.Headers.NonValidated;
        return new IcyHeaders(
            IcyText(headers, "icy-name"),
            IcyText(headers, "icy-genre"),
            IcyText(headers, "icy-url"),
            FirstNumber(Header(headers, "icy-br")) is { } bitrate && CompressedBuffer.IsPlausibleBitrate(bitrate) ? bitrate : null,
            FirstNumber(Header(headers, "icy-sr")),
            contentType);
    }

    private string? IcyText(System.Net.Http.Headers.HttpHeadersNonValidated headers, string name) =>
        IcyMetadataParser.Normalise(IcyMetadataParser.DecodeHeader(Header(headers, name), textFallback ?? Encoding.Latin1), null);

    private static string? Header(System.Net.Http.Headers.HttpHeadersNonValidated headers, string name) =>
        headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>Stacje podają na przykład „128,128”.</summary>
    private static int? FirstNumber(string? text)
    {
        var first = text?.Split(',')[0];
        return int.TryParse(first, out var number) && number > 0 ? number : null;
    }

    /// <summary>Zbiera dźwięk i metadane przeczytane, zanim wiadomo, co to za treść.</summary>
    private sealed class HeadCollector : IIcySink
    {
        private readonly ArrayBufferWriter<byte> _audio = new(ReadChunkBytes);
        public readonly List<HeadMetadataBlock> Metadata = [];

        public ReadOnlySpan<byte> Audio => _audio.WrittenSpan;

        public void OnAudio(ReadOnlySpan<byte> audio) => _audio.Write(audio);

        public void OnMetadata(ReadOnlySpan<byte> block) =>
            Metadata.Add(new HeadMetadataBlock(_audio.WrittenCount, block.ToArray()));
    }
}
