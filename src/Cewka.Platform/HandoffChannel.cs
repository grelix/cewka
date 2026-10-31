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

    /// <summary>
    /// Bez czekania zwraca połączenie nadawcy, który połączył się, zanim odbiorca go przyjął,
    /// albo <c>null</c>. Wołane przy zamykaniu, więc nie przygotowuje miejsca dla następnych.
    /// </summary>
    Stream? TakePending();

    /// <summary>
    /// Nowy nadawca nie może się już połączyć i dostaje odmowę; połączeni wcześniej czekają
    /// na <see cref="TakePending"/>.
    /// </summary>
    void StopAccepting();
}

/// <summary>
/// Przenosi ścieżki z kopii, która startuje, do kopii już działającej.
/// W Windows to potok nazwany; przestrzeń nazw potoków jest wspólna dla wszystkich zalogowanych,
/// więc obie strony przyjmują tylko bieżącego użytkownika. Pod Linuksem potok .NET byłby gniazdem
/// we wspólnym <c>TMPDIR</c>, dlatego gniazdo zakładane jest w katalogu jednego użytkownika.
///
/// <para>Pułapka: potok nie ma kolejki połączeń jak gniazdo, więc następna instancja potoku
/// powstaje zaraz po przyjęciu połączenia, jeszcze przed odczytem wiadomości.</para>
/// </summary>
internal static class HandoffChannel
{
    /// <summary>
    /// Jak długo nadawca próbuje się połączyć. Wystarcza na szparę między zdobyciem blokady
    /// a otwarciem kanału; dłużej opóźniałoby własne okno, gdy nikt nie słucha.
    /// </summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Guards the accept loop against a sender that connects and then goes quiet.</summary>
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Kolejka połączeń w jądrze. Zaznaczenie całego albumu uruchamia kopię na każdy plik.
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
        // CurrentUserOnly sprawdza właściciela potoku przed wysłaniem: potok innego
        // użytkownika kończy się wyjątkiem.
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        client.Connect((int)ConnectTimeout.TotalMilliseconds);

        client.Write(BitConverter.GetBytes(payload.Length));
        client.Write(payload);
        client.Flush();
    }

    private sealed class PipeListener : IHandoffListener
    {
        private readonly string _name;
        private readonly object _gate = new();
        private bool _disposed;

        /// <summary>
        /// Instancja potoku przygotowana z góry, żeby nadawca mógł się połączyć, zanim
        /// odbiorca stanie na przyjęciu połączenia.
        /// </summary>
        private NamedPipeServerStream? _ready;

        public PipeListener(string name)
        {
            _name = name;

            // Pierwsza instancja nie może dołączyć do cudzego potoku o tej nazwie.
            _ready = Create(name, PipeOptions.FirstPipeInstance);
        }

        public Stream? Accept(CancellationToken token)
        {
            NamedPipeServerStream server;

            lock (_gate)
            {
                if (_disposed) return null;
                server = _ready ?? Create(_name, PipeOptions.None);
                _ready = null;
            }

            try
            {
                server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
            }
            catch
            {
                server.Dispose();
                throw;
            }

            // Następny nadawca ma od razu gdzie się połączyć, choćby ten zwlekał z wiadomością.
            lock (_gate)
            {
                if (!_disposed) _ready ??= Create(_name, PipeOptions.None);
            }

            return server;
        }

        public Stream? TakePending()
        {
            NamedPipeServerStream? server;

            lock (_gate)
            {
                server = _ready;
                _ready = null;
            }

            if (server is null) return null;

            // Połączony już nadawca kończy oczekiwanie od razu; inaczej oczekiwanie zostaje przerwane.
            using var none = new CancellationTokenSource();
            var connected = server.WaitForConnectionAsync(none.Token);
            none.Cancel();

            try
            {
                connected.GetAwaiter().GetResult();
                return server;
            }
            catch
            {
                server.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Nic do zrobienia: ostatnią instancję potoku zabiera <see cref="TakePending"/>, a nowej
        /// nikt już nie tworzy.
        /// </summary>
        public void StopAccepting()
        {
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _ready?.Dispose();
                _ready = null;
            }
        }

        private static NamedPipeServerStream Create(string name, PipeOptions extra) => new(
            name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | extra);
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

            // Plik gniazda po awarii blokowałby założenie nowego. Rolę rozstrzygnęła już
            // blokada pliku, więc pod tym adresem nikt nie słucha.
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

            return new NetworkStream(accepted, ownsSocket: true);
        }

        public Stream? TakePending() => _socket.Poll(0, SelectMode.SelectRead)
            ? new NetworkStream(_socket.Accept(), ownsSocket: true)
            : null;

        /// <summary>
        /// Bez pliku gniazda nowy nadawca dostaje ENOENT, a gniazdo nasłuchu z kolejką połączeń
        /// działa dalej. Inaczej jądro przyjmowałoby nadawców aż do zamknięcia gniazda, a ich
        /// wysłanie kończyłoby się sukcesem bez odbioru.
        /// </summary>
        public void StopAccepting() => Usun(_path);

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
