using System.IO.Pipes;
using System.Net.Sockets;

namespace Cewka.Platform;

/// <summary>An open channel on which the running copy accepts paths from later ones.</summary>
internal interface IHandoffListener : IDisposable
{
    /// <summary>
    /// Waits for one sender and returns the stream carrying its message. Returns
    /// <c>null</c> once the channel is closing.
    /// </summary>
    Stream? Accept(CancellationToken token);
}

/// <summary>
/// Carries paths from a copy that is starting to the copy that is already running.
///
/// <para><b>Dlaczego dwa rozwiązania.</b> W Windows potok nazwany żyje w przestrzeni nazw
/// jądra i nie zależy od żadnego katalogu — działa i zostaje. W Linuksie potok nazwany .NET-u
/// jest w rzeczywistości gniazdem dziedziny Uniksa, którego ścieżkę wyznacza zmienna
/// <c>TMPDIR</c>. To zawodzi na dwa sposoby: kopia uruchomiona z innym <c>TMPDIR</c> szuka
/// gniazda w innym miejscu i nie znajduje go, a katalog <c>/tmp</c> jest wspólny dla wszystkich
/// użytkowników maszyny — obca osoba może zająć nazwę i odbierać ścieżki otwieranych plików.
/// Dlatego pod Linuksem gniazdo zakładamy sami, w katalogu należącym do jednego użytkownika.</para>
///
/// <para><b>Adres powstaje od razu przy zdobyciu roli</b>, a nie dopiero przy pierwszym
/// nasłuchu. Zanim pierwsza kopia zbuduje okno, mija ułamek sekundy — a menedżer plików potrafi
/// w tym czasie uruchomić kopię na każdy zaznaczony plik. Gniazdo otwarte wcześniej przyjmuje
/// te połączenia do kolejki jądra i żadne nie przepada.</para>
/// </summary>
internal static class HandoffChannel
{
    /// <summary>
    /// How long a sender keeps trying. Wystarczy na szparę między zdobyciem blokady a otwarciem
    /// gniazda; dłuższe czekanie tylko opóźniałoby otwarcie własnego okna, gdy nikogo nie ma.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Guards the accept loop against a sender that connects and then goes quiet.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Waiting connections the kernel holds. Zaznaczenie całego albumu uruchamia kopię na każdy
    /// plik, więc kolejka musi pomieścić kilkadziesiąt naraz.
    /// </summary>
    private const int Backlog = 64;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Opens the channel. Only the copy holding the role may call this.</summary>
    public static IHandoffListener Listen(InstanceAddress address)
        => OperatingSystem.IsWindows() ? new PipeListener(address.Name) : new SocketListener(address.SocketFile);

    /// <summary>Sends one message. Throws when nobody is listening.</summary>
    public static void Send(InstanceAddress address, byte[] payload)
    {
        if (OperatingSystem.IsWindows()) SendThroughPipe(address.Name, payload);
        else SendThroughSocket(address.SocketFile, payload);
    }

    // ---------- Windows ----------

    private static void SendThroughPipe(string name, byte[] payload)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
        client.Connect((int)ConnectTimeout.TotalMilliseconds);

        client.Write(BitConverter.GetBytes(payload.Length));
        client.Write(payload);
        client.Flush();
    }

    private sealed class PipeListener(string name) : IHandoffListener
    {
        /// <summary>
        /// Instancja potoku przygotowana z góry, żeby nadawca mógł się połączyć, zanim
        /// odbiorca stanie na przyjęciu połączenia.
        /// </summary>
        private NamedPipeServerStream? _ready = Create(name);

        public Stream? Accept(CancellationToken token)
        {
            var server = Interlocked.Exchange(ref _ready, null) ?? Create(name);

            try
            {
                server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                return server;
            }
            catch
            {
                server.Dispose();
                throw;
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _ready, null)?.Dispose();

        private static NamedPipeServerStream Create(string name) => new(
            name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    // ---------- Systemy uniksowe ----------

    private static void SendThroughSocket(string path, byte[] payload)
    {
        using var client = Connect(path);

        client.SendTimeout = (int)ReadTimeout.TotalMilliseconds;
        SendAll(client, BitConverter.GetBytes(payload.Length));
        SendAll(client, payload);

        // Zamknięcie strony nadawczej mówi odbiorcy, że wiadomość jest cała. Dane zostają
        // w buforze jądra i doczekają odebrania, nawet jeśli ta kopia zaraz zakończy pracę.
        client.Shutdown(SocketShutdown.Send);
    }

    private static Socket Connect(string path)
    {
        var endpoint = new UnixDomainSocketEndPoint(path);
        var deadline = Environment.TickCount64 + (long)ConnectTimeout.TotalMilliseconds;

        while (true)
        {
            var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            try
            {
                client.Connect(endpoint);
                return client;
            }
            catch (SocketException)
            {
                // Gniazdo nieutworzone albo nikt jeszcze nie odbiera. Nieudane połączenie
                // zostawia gniazdo w stanie nie do użytku, więc kolejna próba bierze nowe.
                client.Dispose();
                if (Environment.TickCount64 >= deadline) throw;
                Thread.Sleep(RetryDelay);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private static void SendAll(Socket socket, ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var sent = socket.Send(data);
            if (sent <= 0) throw new IOException("Odbiorca zamknął gniazdo w trakcie przesyłania.");

            data = data[sent..];
        }
    }

    private sealed class SocketListener : IHandoffListener
    {
        private readonly string _path;
        private readonly Socket _socket;

        public SocketListener(string path)
        {
            _path = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Po awarii zostaje plik gniazda i sam zablokowałby założenie nowego. Wolno go
            // usunąć: o roli tej kopii rozstrzygnęła już blokada pliku, więc pod tym adresem
            // na pewno nikt nie nasłuchuje.
            Usun(path);

            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            try
            {
                _socket.Bind(new UnixDomainSocketEndPoint(path));
                _socket.Listen(Backlog);
            }
            catch
            {
                _socket.Dispose();
                throw;
            }
        }

        public Stream? Accept(CancellationToken token)
        {
            Socket accepted;

            try
            {
                accepted = _socket.AcceptAsync(token).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                return null;
            }

            accepted.ReceiveTimeout = (int)ReadTimeout.TotalMilliseconds;
            return new NetworkStream(accepted, ownsSocket: true);
        }

        public void Dispose()
        {
            _socket.Dispose();
            Usun(_path);
        }

        private static void Usun(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Nie ma czego posprzątać albo nie wolno — założenie gniazda i tak powie,
                // czy adres jest wolny.
            }
        }
    }
}
