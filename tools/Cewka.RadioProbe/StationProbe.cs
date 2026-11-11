using System.Diagnostics;
using System.Net.Security;
using Cewka.Audio;
using Cewka.Audio.Streaming;

namespace Cewka.RadioProbe;

internal readonly record struct ProbeLimits(TimeSpan Time, int MaxBytes);

/// <summary>
/// Sprawdza jeden adres tą samą siecią co program: łączy, rozpoznaje treść, czyta do limitu czasu
/// albo bajtów i mierzy z ramek. Wszystko w pamięci, bez urządzenia dźwięku i bez zapisu strumienia.
/// </summary>
internal sealed class StationProbe(LiveOptions options, ProbeLimits limits)
{
    private const int ReadChunkBytes = 8 * 1024;
    private const int SignatureBytes = 8;

    public async Task<ProbeResult> ProbeAsync(ProbeTarget target, bool measureLoudness, CancellationToken ct)
    {
        var result = new ProbeResult { StationId = target.StationId, Url = target.Url.OriginalString, FinalUrl = target.Url };
        var started = Stopwatch.GetTimestamp();
        var budget = new ByteBudget(limits.MaxBytes);
        var probing = options with
        {
            ConnectTimeout = limits.Time,
            StallTimeout = limits.Time * 0.75,
            MaxPlaylistBytes = Math.Min(options.MaxPlaylistBytes, limits.MaxBytes / 3),

            // Rozstrzyga dekoder narzędzia, żeby dla każdego kodeka powstał pełny raport.
            CodecPlayable = _ => true,
            WrapConnection = connection => new MeteredStream(connection, budget),
        };

        using var http = new StreamHttp(probing);
        var opener = new StreamOpener(http, new AddressPolicy(probing), probing);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(limits.Time);

        try
        {
            using var opened = await opener.OpenAsync(target.Url, target.AllowLocal, limit.Token).ConfigureAwait(false);
            result.Codec = opened.Codec;
            result.HeaderKbps = opened.Headers.BitrateKbps;
            result.MetaInt = opened.MetaInt > 0 ? opened.MetaInt : null;

            var analysis = new FrameAnalysis(
                opened.Codec, measureLoudness, IcyMetadataParser.FallbackFor(target.CountryCode), target.StationName,
                () => result.FirstAudioSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds);
            using (analysis)
            {
                var ended = await ReadAsync(opened, analysis, budget, limit.Token).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                analysis.Finish();

                result.MeasuredKbps = analysis.MeasuredKbps;
                result.SampleRate = analysis.SampleRate;
                result.Channels = analysis.Channels;
                result.Title = analysis.Title;
                result.Lufs = analysis.Lufs;
                result.Failure = Verdict(opened.Codec, analysis, ended);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var reason = opener.LastTrace?.HttpStatus is null ? LiveFailureReason.ConnectionFailed : LiveFailureReason.Stalled;
            result.Failure = new LiveFailure(reason, Detail: "Limit czasu");
        }
        catch (Exception e) when (e is not (OperationCanceledException or AudioException or DllNotFoundException))
        {
            result.Failure = LiveErrors.Classify(e).Failure;
        }

        Fill(result, opener.LastTrace);
        result.BytesRead = budget.Used;
        result.TotalSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        return result;
    }

    /// <summary>True, gdy serwer zakończył strumień przed limitami.</summary>
    private static async Task<bool> ReadAsync(OpenedStream opened, FrameAnalysis analysis, ByteBudget budget, CancellationToken ct)
    {
        ReplayHead(opened, analysis);

        var chunk = new byte[ReadChunkBytes];
        try
        {
            while (analysis.WantsData && budget.Remaining > 0)
            {
                var count = (int)Math.Min(chunk.Length, budget.Remaining);
                var read = await opened.Body.ReadAsync(chunk.AsMemory(0, count), ct).ConfigureAwait(false);
                if (read == 0)
                    return true;

                if (opened.Demuxer is { } demuxer)
                    demuxer.Push(chunk.AsSpan(0, read));
                else
                    analysis.OnAudio(chunk.AsSpan(0, read));
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException e)
        {
            return !ByteBudgetExhaustedException.IsIn(e);
        }
        catch (HttpRequestException e) when (ByteBudgetExhaustedException.IsIn(e))
        {
            // Strumień z segmentów łączy się w trakcie czytania, więc limit może paść przy wysyłaniu żądania.
            return false;
        }
    }

    private static void ReplayHead(OpenedStream opened, FrameAnalysis analysis)
    {
        var at = 0;
        foreach (var block in opened.HeadMetadata)
        {
            analysis.OnAudio(opened.Head.AsSpan(at, block.Offset - at));
            analysis.OnMetadata(block.Block);
            at = block.Offset;
        }

        analysis.OnAudio(opened.Head.AsSpan(at));
        if (opened.Demuxer is { } demuxer)
            demuxer.Sink = analysis;
    }

    private static LiveFailure? Verdict(LiveCodec codec, FrameAnalysis analysis, bool ended)
    {
        if (!analysis.HasDecoder)
            return new LiveFailure(LiveFailureReason.CodecNotSupportedYet, Codec: codec);
        if (analysis.DecoderFailure is { } failure)
            return failure;
        if (ended)
            return new LiveFailure(LiveFailureReason.StreamEnded, Codec: codec, Detail: "Serwer zamknął połączenie przed limitem");
        return analysis.DecodedFrames == 0 ? new LiveFailure(LiveFailureReason.UndecodableData, Codec: codec) : null;
    }

    private static void Fill(ProbeResult result, ConnectionTrace? trace)
    {
        if (trace is not null)
        {
            result.FinalUrl = trace.FinalUrl ?? result.FinalUrl;
            result.HttpStatus = trace.HttpStatus;
            result.StatusLine = trace.StatusLine;
            result.IcyStatusLine = trace.SawIcyStatusLine;
            result.ContentType = trace.ContentType;
            result.Redirects = trace.Redirects;
            result.HeadersSeconds = trace.HttpStatus is null ? null : trace.HeadersSeconds;
            result.CertificateError = trace.CertificateErrors == SslPolicyErrors.None ? null : trace.CertificateErrors.ToString();
            if (trace.FirstBytes is { Length: > 0 } bytes)
                result.Signature = BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, SignatureBytes)).Replace('-', ' ');
        }

        if (result.FinalUrl is { } final)
        {
            result.Https = final.Scheme == Uri.UriSchemeHttps;
            result.ForbiddenInUrl = ForbiddenUrlParts.Find(final);
        }
    }
}
