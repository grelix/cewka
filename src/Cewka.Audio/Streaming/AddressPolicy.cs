using System.Net;
using System.Net.Sockets;

namespace Cewka.Audio.Streaming;

/// <summary>Wszystkie adresy hosta są lokalne, a reguła ich nie dopuszcza.</summary>
internal sealed class AddressRefusedException(string host) : Exception($"Adres lokalny odrzucony: {host}");

/// <summary>
/// Reguła adresów: stacja z katalogu nigdy nie sięga do sieci lokalnej, adres wpisany przez
/// użytkownika może od niej zacząć, a przejście z publicznego do lokalnego jest zawsze odmową.
/// </summary>
internal sealed class AddressPolicy(LiveOptions options)
{
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out var length);
        if (length == 4)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => true,
                100 => bytes[1] is >= 64 and <= 127,
                169 => bytes[1] == 254,
                172 => bytes[1] is >= 16 and <= 31,
                192 => bytes[1] == 168 || (bytes[1] == 0 && bytes[2] == 0),
                198 => bytes[1] is 18 or 19,
                >= 224 => true,
                _ => false,
            };
        }

        return address.Equals(IPAddress.IPv6Loopback) || address.Equals(IPAddress.IPv6Any)
            || bytes[0] == 0xFF
            || (bytes[0] & 0xFE) == 0xFC
            || (bytes[0] == 0xFE && (bytes[1] & 0x80) == 0x80);
    }

    /// <summary>Wywołanie zwrotne klienta: łączy tylko z adresami, które reguła dopuszcza.</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = Unbracket(context.DnsEndPoint.Host);
        var requested = context.InitialRequestMessage.RequestUri is { } uri ? Unbracket(uri.IdnHost) : host;

        // Serwer pośredniczący może stać w sieci lokalnej: wybrał go użytkownik albo system. Adres stacji
        // sprawdza CheckThroughProxyAsync przed wysłaniem. CONNECT to własne żądanie tunelu, bez kontekstu.
        context.InitialRequestMessage.Options.TryGetValue(StreamHttp.ContextKey, out var request);
        if (context.InitialRequestMessage.Method == HttpMethod.Connect
            || !string.Equals(host, requested, StringComparison.OrdinalIgnoreCase))
        {
            return await ConnectToProxyAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
        }

        var allowLocal = request?.AllowLocal ?? false;

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await options.ResolveHost(host, ct).WaitAsync(ct).ConfigureAwait(false);
        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        var allowed = allowLocal ? addresses : addresses.Where(a => !options.IsLocalAddress(a)).ToArray();
        if (allowed.Length == 0)
            throw new AddressRefusedException(host);

        // Czas dzielony po równo: niedziałający pierwszy adres nie może zużyć całego limitu.
        var perAddress = options.ConnectTimeout / allowed.Length;

        Exception? last = null;
        foreach (var address in allowed)
        {
            ct.ThrowIfCancellationRequested();
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(perAddress);
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await options.ConnectSocket(socket, new IPEndPoint(address, context.DnsEndPoint.Port), attempt.Token).ConfigureAwait(false);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = new TimeoutException($"Adres nie odpowiada: {address}");
                continue;
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            if (request is not null && !options.IsLocalAddress(address))
                request.Trace.ConnectedPublic = true;
            return new NetworkStream(socket, ownsSocket: true);
        }

        throw last!;
    }

    /// <summary>
    /// Żądanie przez serwer pośredniczący: nazwę rozwiązuje on, więc reguła sprawdza ją tu, przed wysłaniem.
    /// Nieudane rozwiązanie przepuszcza, bo za pośrednikiem firmowym nazwy publiczne często nie rozwiązują się
    /// lokalnie. Adresu stacji nie widać, więc nazwa liczy się jako publiczna: dalsze przejścia nie wrócą do sieci lokalnej.
    /// </summary>
    public async Task CheckThroughProxyAsync(Uri url, RequestContext context, CancellationToken ct)
    {
        if (IsLiteralLocal(url))
            return;

        var host = Unbracket(url.IdnHost);
        if (!context.AllowLocal && !IPAddress.TryParse(host, out _))
        {
            var addresses = await TryResolveAsync(host, ct).ConfigureAwait(false);

            // Którego adresu użyje pośrednik, nie wiadomo, więc wystarczy jeden lokalny.
            if (addresses.Any(options.IsLocalAddress))
                throw new AddressRefusedException(host);
        }

        context.Trace.ConnectedPublic = true;
    }

    private async Task<IPAddress[]> TryResolveAsync(string host, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(options.ProxyResolveTimeout);
        try
        {
            return await options.ResolveHost(host, limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is SocketException or OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>Adres wejściowy: blokady i literał IP, także gdy łączy serwer pośredniczący.</summary>
    public void CheckAddress(Uri url, bool allowLocal)
    {
        if (options.UrlGuard(url) is { } reason)
            throw new LiveOpenException(new LiveFailure(reason, Detail: url.Host));
        if (!allowLocal && IsLiteralLocal(url))
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.AddressRefused, Detail: url.Host));
    }

    /// <summary>Przejście to przekierowanie albo wpis listy.</summary>
    public void CheckTransition(Uri from, Uri to, bool allowLocal)
    {
        CheckAddress(to, allowLocal);
        if (IsLiteralLocal(to) && !IsLiteralLocal(from) && IPAddress.TryParse(Unbracket(from.Host), out _))
            throw new LiveOpenException(new LiveFailure(LiveFailureReason.AddressRefused, Detail: to.Host));
    }

    private bool IsLiteralLocal(Uri url)
    {
        var host = Unbracket(url.Host);
        return IPAddress.TryParse(host, out var address)
            ? options.IsLocalAddress(address)
            : string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static async ValueTask<Stream> ConnectToProxyAsync(DnsEndPoint proxy, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(proxy, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string Unbracket(string host) => host.Trim('[', ']');
}
