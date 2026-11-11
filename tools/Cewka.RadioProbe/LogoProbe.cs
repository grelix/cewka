using System.Buffers;
using Cewka.Audio.Streaming;

namespace Cewka.RadioProbe;

internal sealed class LogoResult
{
    public required string StationId { get; init; }
    public required string Url { get; init; }
    public Uri? FinalUrl { get; set; }
    public int? HttpStatus { get; set; }
    public string? ContentType { get; set; }
    public string? Format { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool TooLarge { get; set; }
    public long BytesRead { get; set; }
    public LiveFailure? Failure { get; set; }
}

/// <summary>Status, format i wymiary logo z nagłówka obrazu; nie czyta dalej, niż trzeba.</summary>
internal sealed class LogoProbe(LiveOptions options, TimeSpan time)
{
    public const int MaxBytes = 2 * 1024 * 1024;

    private const int ReadChunkBytes = 16 * 1024;

    public async Task<LogoResult> ProbeAsync(string stationId, Uri url, bool allowLocal, CancellationToken ct)
    {
        var result = new LogoResult { StationId = stationId, Url = url.OriginalString, FinalUrl = url };
        var budget = new ByteBudget(MaxBytes);
        var probing = options with
        {
            ConnectTimeout = time,
            WrapConnection = connection => new MeteredStream(connection, budget),
        };

        using var http = new StreamHttp(probing);
        var opener = new StreamOpener(http, new AddressPolicy(probing), probing);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(time);

        try
        {
            using var response = await opener.FetchImageAsync(url, allowLocal, limit.Token).ConfigureAwait(false);
            result.TooLarge = response.Content.Headers.ContentLength > MaxBytes;
            await ReadHeaderAsync(response, budget, result, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var reason = opener.LastTrace?.HttpStatus is null ? LiveFailureReason.ConnectionFailed : LiveFailureReason.Stalled;
            result.Failure = new LiveFailure(reason, Detail: "Limit czasu");
        }
        catch (Exception e) when (ByteBudgetExhaustedException.IsIn(e))
        {
            result.TooLarge = true;
            result.Failure = new LiveFailure(LiveFailureReason.UnrecognisedContent, Detail: "Brak wymiarów w limicie danych");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result.Failure = LiveErrors.Classify(e).Failure;
        }

        if (opener.LastTrace is { } trace)
        {
            result.FinalUrl = trace.FinalUrl ?? result.FinalUrl;
            result.HttpStatus = trace.HttpStatus;
            result.ContentType = trace.ContentType;
        }

        result.BytesRead = budget.Used;
        return result;
    }

    private static async Task ReadHeaderAsync(HttpResponseMessage response, ByteBudget budget, LogoResult result, CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var data = new ArrayBufferWriter<byte>(ReadChunkBytes);
        while (budget.Remaining > 0)
        {
            var count = (int)Math.Min(ReadChunkBytes, budget.Remaining);
            var read = await body.ReadAsync(data.GetMemory(count)[..count], ct).ConfigureAwait(false);
            data.Advance(read);

            var status = ImageHeader.TryRead(data.WrittenSpan, out var image);
            if (status == ImageHeaderStatus.Found)
            {
                result.Format = image.Format;
                result.Width = image.Width;
                result.Height = image.Height;
                return;
            }

            if (status == ImageHeaderStatus.NotAnImage || read == 0)
                break;
        }

        result.TooLarge |= budget.Remaining == 0;
        result.Failure = new LiveFailure(LiveFailureReason.UnrecognisedContent);
    }
}
