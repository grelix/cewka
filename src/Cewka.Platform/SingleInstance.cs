using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cewka.Platform;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(string[]))]
internal sealed partial class HandoffJsonContext : JsonSerializerContext;

/// <summary>
/// Where the one-copy machinery keeps its two things: the file whose lock decides who is
/// running, and the address on which that copy listens.
/// <para>
/// Ścieżki są podawane jawnie, żeby sprawdzenia mogły uruchamiać ten mechanizm w katalogu
/// tymczasowym, nie ruszając prawdziwych ustawień ani działającego programu.
/// </para>
/// </summary>
/// <param name="Name">Nazwa potoku w przestrzeni nazw jądra Windows; pod Linuksem nieużywana.</param>
/// <param name="LockFile">Plik, którego wyłączna blokada oznacza rolę działającej kopii.</param>
/// <param name="SocketFile">Gniazdo odbierające ścieżki pod Linuksem; w Windows nieużywane.</param>
public sealed record InstanceAddress(string Name, string LockFile, string SocketFile)
{
    /// <summary>The address the application itself uses.</summary>
    public static InstanceAddress Default(string name) =>
        new(name, AppPaths.InstanceLockFile, AppPaths.InstanceSocketFile);
}

/// <summary>
/// Keeps a single running copy of the application and hands paths over to it.
///
/// <para>Skojarzenie plików sprawia, że otwarcie albumu z eksploratora uruchamia program raz dla
/// każdego zaznaczonego pliku. Pierwsza kopia zostaje, a każda następna oddaje jej swoje ścieżki
/// i natychmiast kończy pracę, więc nie powstaje kilkanaście okien walczących o urządzenie
/// dźwiękowe. O pierwszeństwie rozstrzyga wyłączna blokada pliku w katalogu ustawień; jądro
/// zwalnia ją także wtedy, gdy program zginie bez pożegnania.</para>
///
/// <para>Pułapka: muteks nazwany .NET-u żyje pod Linuksem w <c>/tmp/.dotnet/shm/session&lt;SID&gt;</c>,
/// osobno dla każdej sesji POSIX, a menedżer plików uruchamia każdą kopię we własnej sesji.
/// Każda uznałaby się za pierwszą. Tego nie wyłapie sprawdzenie w jednym procesie; po zmianach
/// w tym pliku pod Linuksem uruchomić kopię, potem <c>setsid program plik.mp3</c> i policzyć
/// procesy. Ma zostać jeden.</para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Guards against a hostile or broken sender flooding the channel.</summary>
    private const int MaxMessageBytes = 1 << 20;

    /// <summary>Messages kept until the window attaches.</summary>
    private const int MaxPendingMessages = 256;

    /// <summary>
    /// How many times start-up may go round. Rola zmienia właściciela dokładnie między
    /// sprawdzeniem blokady a połączeniem tylko wtedy, gdy poprzednia kopia właśnie kończyła
    /// pracę — kilka podejść wystarczy, żeby to przeczekać.
    /// </summary>
    private const int Attempts = 3;

    private readonly FileStream _lock;
    private readonly IHandoffListener _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _listener;
    private readonly object _gate = new();
    private readonly List<string[]> _pending = [];
    private Action<string[]>? _receiver;
    private bool _disposed;

    /// <summary>
    /// Nasłuch rusza w chwili zdobycia roli. Kopie uruchomione w trakcie budowania okna
    /// oddają ścieżki do bufora i od razu kończą pracę.
    /// </summary>
    private SingleInstance(FileStream held, IHandoffListener channel)
    {
        _lock = held;
        _channel = channel;

        _listener = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "Cewka.Instance",
        };

        _listener.Start();
    }

    /// <summary>
    /// Outcome of start-up: either this copy runs the show, or its paths went to one that
    /// already does.
    /// </summary>
    /// <param name="Instance">Nie-null, gdy ta kopia wzięła rolę i ma nasłuchiwać.</param>
    /// <param name="HandedOver">Prawda, gdy ścieżki trafiły gdzie indziej i nie ma tu nic do roboty.</param>
    public readonly record struct Claim(SingleInstance? Instance, bool HandedOver);

    private enum Acquisition
    {
        Acquired,

        /// <summary>Blokadę trzyma inna kopia.</summary>
        Busy,

        /// <summary>Blokady nie da się założyć w ogóle, więc nikt inny też jej nie trzyma.</summary>
        Unavailable,

        /// <summary>Blokada zdobyta, ale kanału nie da się otworzyć.</summary>
        ChannelFailed,
    }

    /// <summary>
    /// Takes the role of the one running copy, or hands the paths over to whoever holds it.
    ///
    /// <para>Nieudane przekazanie przy zajętej blokadzie znaczy, że poprzednia kopia zniknęła
    /// dokładnie w tej chwili. Wtedy trzeba wrócić po rolę, a nie otwierać okno bez nasłuchu —
    /// taka kopia nie odebrałaby już niczego i każde następne uruchomienie mnożyłoby okna.
    /// Gdy mechanizm jest niedostępny, kolejne podejścia nic nie zmienią, a każde kosztuje
    /// sekundę czekania na połączenie, więc program rusza od razu.</para>
    /// </summary>
    public static Claim Start(InstanceAddress address, IReadOnlyList<string> paths)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            switch (Acquire(address, out var instance))
            {
                case Acquisition.Acquired:
                    return new Claim(instance, false);

                case Acquisition.Unavailable:
                    return new Claim(null, false);

                // Kanał zajęty pod tą nazwą: jeśli to działająca kopia tego użytkownika,
                // przyjmie ścieżki od razu; inaczej dalsze próby nic nie dadzą.
                case Acquisition.ChannelFailed:
                    return new Claim(null, TryHandOff(address, paths));
            }

            if (TryHandOff(address, paths)) return new Claim(null, true);
        }

        // Blokada zajęta, a mimo to nikt nie odbiera: poprzednia kopia żyje, lecz nie słucha.
        // Nie ma tu dobrego wyjścia — dodatkowe okno jest lepsze niż plik, który przepadł.
        return new Claim(null, false);
    }

    /// <summary>
    /// Claims the role of the one running instance. Returns <c>null</c> when another copy already
    /// holds it, in which case <see cref="TryHandOff"/> should be used instead.
    /// </summary>
    public static SingleInstance? TryAcquire(InstanceAddress address)
    {
        Acquire(address, out var instance);
        return instance;
    }

    private static Acquisition Acquire(InstanceAddress address, out SingleInstance? instance)
    {
        instance = null;
        FileStream held;

        try
        {
            held = new FileStream(
                address.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsHeldByAnother(ex))
        {
            // Blokadę trzyma inna kopia. To zwykły przebieg, nie awaria.
            return Acquisition.Busy;
        }
        catch (Exception ex)
        {
            // Katalog tylko do odczytu albo inna niespodzianka. Gorszym wynikiem jest program,
            // który się nie otwiera, niż dodatkowe okno — więc ta kopia po prostu rusza.
            Console.Error.WriteLine($"[cewka] pojedyncza instancja niedostępna: {ex.Message}");
            return Acquisition.Unavailable;
        }

        try
        {
            Stamp(held);
            instance = new SingleInstance(held, HandoffChannel.Listen(address));
            return Acquisition.Acquired;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] kanał przekazywania ścieżek niedostępny: {ex.Message}");
            held.Dispose();
            return Acquisition.ChannelFailed;
        }
    }

    private static bool IsHeldByAnother(IOException ex) =>
        IsLockConflict(ex.HResult, OperatingSystem.IsWindows(), OperatingSystem.IsLinux());

    /// <summary>
    /// Whether the error code .NET puts in <see cref="Exception.HResult"/> means that another
    /// process holds the lock.
    /// <para>
    /// Brak katalogu, system plików tylko do odczytu czy błąd wejścia-wyjścia to też
    /// <see cref="IOException"/>, ale wtedy nikt blokady nie trzyma i nie ma komu oddawać ścieżek.
    /// W Windows rozstrzyga naruszenie współdzielenia albo blokady, na Uniksie EWOULDBLOCK
    /// z <c>flock</c>, które .NET podaje jako surowy numer błędu.
    /// </para>
    /// </summary>
    internal static bool IsLockConflict(int hresult, bool windows, bool linux)
    {
        if (windows)
        {
            const int sharingViolation = 32;
            const int lockViolation = 33;

            return (hresult & 0xFFFF) is sharingViolation or lockViolation;
        }

        const int wouldBlockLinux = 11;
        const int wouldBlockBsd = 35;

        return hresult == (linux ? wouldBlockLinux : wouldBlockBsd);
    }

    /// <summary>
    /// Passes paths to the copy that is already running. Returns false when nobody answered,
    /// which means the role is free again and should be claimed.
    /// </summary>
    public static bool TryHandOff(InstanceAddress address, IReadOnlyList<string> paths)
    {
        try
        {
            HandoffChannel.Send(address, Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(paths.ToArray(), HandoffJsonContext.Default.StringArray)));

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Connects the receiver of paths sent by other copies. Messages that came before are
    /// delivered at once, in order of arrival. The receiver is called off the interface thread.
    /// </summary>
    public void Attach(Action<string[]> receiver)
    {
        lock (_gate)
        {
            _receiver = receiver;

            foreach (var paths in _pending) receiver(paths);
            _pending.Clear();
        }
    }

    /// <summary>Zgłaszane także przy pustej liście: uruchomienie bez argumentów wysuwa okno na wierzch.</summary>
    private void Deliver(string[] paths)
    {
        lock (_gate)
        {
            if (_receiver is not null) _receiver(paths);
            else if (_pending.Count < MaxPendingMessages) _pending.Add(paths);
        }
    }

    /// <summary>
    /// Numer procesu w pliku blokady. Nie bierze udziału w rozstrzyganiu — jest po to, żeby
    /// dało się sprawdzić, która kopia trzyma rolę, gdy coś pójdzie nie tak.
    /// </summary>
    private static void Stamp(FileStream held)
    {
        held.SetLength(0);
        held.Write(Encoding.UTF8.GetBytes(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));

        held.Flush();
    }

    private void ListenLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var connection = _channel.Accept(_stop.Token);
                if (connection is null) return;

                Deliver(ReadMessage(connection));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Nadawca połączył się i zamilkł. Połączenie jest już zamknięte, a pozostali
                // czekają, więc bez pauzy.
                Console.Error.WriteLine("[cewka] nadawca ścieżek nie przysłał wiadomości w wyznaczonym czasie.");
            }
            catch (Exception ex)
            {
                if (_stop.IsCancellationRequested) return;

                Console.Error.WriteLine($"[cewka] przekazanie ścieżek nie powiodło się: {ex.Message}");

                // Pauza chroni przed pętlą pełnego obciążenia, gdyby kanał był trwale zepsuty.
                if (_stop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250))) return;
            }
        }
    }

    /// <summary>
    /// Reads one message, giving up after <see cref="HandoffChannel.ReadTimeout"/>. Bez limitu
    /// nadawca wstrzymany w połowie (debuger, skaner antywirusowy) trzymałby kanał bez końca.
    /// </summary>
    private string[] ReadMessage(Stream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(HandoffChannel.ReadTimeout);

        var header = new byte[4];
        if (!ReadExactly(stream, header, timeout.Token)) return [];

        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessageBytes) return [];

        var payload = new byte[length];
        if (!ReadExactly(stream, payload, timeout.Token)) return [];

        return JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(payload), HandoffJsonContext.Default.StringArray) ?? [];
    }

    private static bool ReadExactly(Stream stream, byte[] destination, CancellationToken token)
    {
        try
        {
            stream.ReadExactlyAsync(destination, token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stop.Cancel();

        // Zamknięcie kanału zdejmuje wątek stojący na przyjęciu połączenia.
        _channel.Dispose();
        _listener.Join(500);

        // Zwolnienie blokady. Gdyby tu nie doszło — bo program zginął — zrobi to jądro.
        _lock.Dispose();
        _stop.Dispose();
    }
}
