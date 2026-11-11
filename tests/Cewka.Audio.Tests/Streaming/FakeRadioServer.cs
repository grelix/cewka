using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Cewka.Audio.Tests.Streaming;

internal sealed record FakeRequest(int Index, string Method, string Path, IReadOnlyDictionary<string, string> Headers)
{
    public bool WantsMetadata => Headers.ContainsKey("Icy-MetaData");
}

/// <summary>
/// Atrapa stacji na adresie lokalnym: każde połączenie obsługuje skrypt, który pisze surowe
/// bajty, więc może urwać, milczeć albo podać wiersz statusu po jednym bajcie.
/// </summary>
internal sealed class FakeRadioServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<FakeRequest, Stream, CancellationToken, Task> _script;
    private readonly X509Certificate2? _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly List<FakeRequest> _requests = [];
    private readonly Task _acceptLoop;
    private int _accepted;
    private long _bytesSent;

    public FakeRadioServer(
        Func<FakeRequest, Stream, CancellationToken, Task> script,
        string address = "127.0.0.1",
        X509Certificate2? certificate = null)
    {
        _script = script;
        _certificate = certificate;
        _listener = new TcpListener(IPAddress.Parse(address), 0);
        _listener.Start();
        Address = address;
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public string Address { get; }
    public int Port { get; }
    public int ConnectionCount => Volatile.Read(ref _accepted);
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public IReadOnlyList<FakeRequest> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    public Uri Url(string path = "/stream") =>
        new($"{(_certificate is null ? "http" : "https")}://{Address}:{Port}{path}");

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                client.NoDelay = true;
                var index = Interlocked.Increment(ref _accepted) - 1;
                lock (_connections)
                    _connections.Add(HandleAsync(client, index));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (_stop.IsCancellationRequested && e is ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Zamknięcie atrapy wyprzedziło kolejne przyjęcie połączenia.
        }
    }

    private async Task HandleAsync(TcpClient client, int index)
    {
        using var _ = client;
        try
        {
            Stream stream = client.GetStream();
            if (_certificate is not null)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
                stream = ssl;
            }

            var request = await ReadRequestAsync(stream, index, _stop.Token);
            if (request is null)
                return;
            lock (_requests)
                _requests.Add(request);
            await _script(request, new CountingStream(stream, this), _stop.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException
                                      or System.Security.Authentication.AuthenticationException)
        {
            // Klient rozłączył się albo atrapa kończy pracę.
        }
    }

    private static async Task<FakeRequest?> ReadRequestAsync(Stream stream, int index, CancellationToken ct)
    {
        var text = new StringBuilder();
        var one = new byte[1];
        while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, ct) == 0)
                return null;
            text.Append((char)one[0]);
        }

        var lines = text.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var parts = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        return new FakeRequest(index, parts[0], parts[1], headers);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        Task[] running;
        lock (_connections)
            running = [.. _connections];
        await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10));
        _stop.Dispose();
    }

    private void Count(int bytes) => Interlocked.Add(ref _bytesSent, bytes);

    /// <summary>Licznik po stronie atrapy; klient nie ma na niego wpływu.</summary>
    private sealed class CountingStream(Stream inner, FakeRadioServer owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            owner.Count(count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            owner.Count(buffer.Length);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}

/// <summary>Skrypty i bloki odpowiedzi atrapy.</summary>
internal static class FakeRadio
{
    public const string IcyOk = "ICY 200 OK\r\nicy-name:Stacja\r\nicy-br:128\r\ncontent-type:audio/mpeg\r\n";

    public static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);
    public static string Latin1String(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    public static async Task WriteAsync(this Stream stream, string text, CancellationToken ct)
    {
        await stream.WriteAsync(Latin1(text), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Głowa odpowiedzi z pustym wierszem kończącym, potem treść.</summary>
    public static async Task RespondAsync(this Stream stream, string head, byte[] body, CancellationToken ct)
    {
        await stream.WriteAsync(Latin1(head + "\r\n"), ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static Func<FakeRequest, Stream, CancellationToken, Task> Respond(string head, byte[] body) =>
        (_, stream, ct) => stream.RespondAsync(head, body, ct);

    public static Func<FakeRequest, Stream, CancellationToken, Task> Respond(string head, string body = "") =>
        Respond(head, Latin1(body));

    /// <summary>Milczy, aż klient się rozłączy albo atrapa skończy pracę.</summary>
    public static async Task HoldAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[64];
        while (await stream.ReadAsync(buffer, ct) > 0)
        {
        }
    }

    /// <summary>Prawidłowe ramki MPEG1 Layer III, 128 kb/s, 44,1 kHz (417 B każda).</summary>
    public static byte[] Mp3Frames(int count)
    {
        const int frameBytes = 417;
        var data = new byte[frameBytes * count];
        for (var i = 0; i < count; i++)
        {
            var at = i * frameBytes;
            data[at] = 0xFF;
            data[at + 1] = 0xFB;
            data[at + 2] = 0x90;
            data[at + 3] = 0x64;
        }

        return data;
    }

    /// <summary>Ramki ADTS AAC-LC, 44,1 kHz, stereo, po 256 B.</summary>
    public static byte[] AdtsFrames(int count)
    {
        const int frameBytes = 256;
        var data = new byte[frameBytes * count];
        for (var i = 0; i < count; i++)
        {
            var at = i * frameBytes;
            data[at] = 0xFF;
            data[at + 1] = 0xF1;
            data[at + 2] = 0x50;
            data[at + 3] = (byte)(0x80 | (frameBytes >> 11));
            data[at + 4] = (byte)((frameBytes >> 3) & 0xFF);
            data[at + 5] = (byte)(((frameBytes & 7) << 5) | 0x1F);
            data[at + 6] = 0xFC;
        }

        return data;
    }

    /// <summary>Wstawia bloki metadanych co metaInt bajtów dźwięku.</summary>
    public static byte[] WithMetadata(byte[] audio, int metaInt, Func<int, byte[]> blockAt)
    {
        using var output = new MemoryStream();
        var index = 0;
        for (var at = 0; at < audio.Length; at += metaInt)
        {
            var count = Math.Min(metaInt, audio.Length - at);
            output.Write(audio, at, count);
            if (count == metaInt)
                output.Write(MetadataBlock(blockAt(index++)));
        }

        return output.ToArray();
    }

    /// <summary>Bajt długości i treść dopełniona zerami do wielokrotności 16.</summary>
    public static byte[] MetadataBlock(byte[] text)
    {
        var units = (text.Length + 15) / 16;
        var block = new byte[1 + units * 16];
        block[0] = (byte)units;
        text.CopyTo(block, 1);
        return block;
    }

    public static byte[] Title(string title) => Encoding.UTF8.GetBytes($"StreamTitle='{title}';");
}

/// <summary>Własny certyfikat główny i certyfikat serwera tylko na czas testu.</summary>
internal sealed class TestCertificates : IDisposable
{
    /// <param name="issuerAddress">
    /// Certyfikat serwera podpisze certyfikat pośredni, którego serwer nie wysyła; wystawcę można pobrać spod tego adresu.
    /// </param>
    public TestCertificates(Uri? issuerAddress = null)
    {
        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Cewka Test Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        Root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(4));

        var signer = Root;
        if (issuerAddress is not null)
        {
            using var intermediateKey = RSA.Create(2048);
            var intermediateRequest = new CertificateRequest("CN=Cewka Test Intermediate", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            var intermediateSerial = new byte[8];
            RandomNumberGenerator.Fill(intermediateSerial);
            using var intermediate = intermediateRequest.Create(Root, DateTimeOffset.UtcNow.AddMinutes(-45), DateTimeOffset.UtcNow.AddHours(3.5), intermediateSerial);
            Intermediate = intermediate.CopyWithPrivateKey(intermediateKey);
            signer = Intermediate;
        }

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (issuerAddress is not null)
            request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [issuerAddress.ToString()]));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        using var signed = request.Create(signer, DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow.AddHours(3), serial);
        using var withKey = signed.CopyWithPrivateKey(key);

        // TLS po stronie serwera w Windows wymaga klucza z PFX.
        Server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    public X509Certificate2 Root { get; }
    public X509Certificate2? Intermediate { get; }
    public X509Certificate2 Server { get; }

    public void Dispose()
    {
        Server.Dispose();
        Intermediate?.Dispose();
        Root.Dispose();
    }
}
