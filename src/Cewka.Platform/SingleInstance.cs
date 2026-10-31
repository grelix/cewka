using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cewka.Platform;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(string[]))]
internal sealed partial class HandoffJsonContext : JsonSerializerContext;

/// <summary>
/// Plik blokady, który rozstrzyga, która kopia działa, i adres, na którym ta kopia słucha.
/// Podawane jawnie, żeby testy mogły pracować w katalogu tymczasowym.
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
/// Pilnuje, żeby działała jedna kopia programu, i przekazuje jej ścieżki z kolejnych uruchomień.
/// Eksplorator uruchamia program osobno dla każdego zaznaczonego pliku; pierwsza kopia zostaje,
/// następne oddają jej ścieżki i kończą pracę. O pierwszeństwie decyduje wyłączna blokada pliku,
/// którą jądro zwalnia także po awarii procesu.
///
/// <para>Pułapka: muteks nazwany .NET żyje pod Linuksem osobno dla każdej sesji POSIX, a menedżer
/// plików uruchamia każdą kopię we własnej sesji. Test w jednym procesie tego nie wykryje; po
/// zmianach tutaj uruchomić pod Linuksem kopię, potem <c>setsid program plik.mp3</c> i policzyć
/// procesy. Ma zostać jeden.</para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Guards against a hostile or broken sender flooding the channel.</summary>
    private const int MaxMessageBytes = 1 << 20;

    /// <summary>Messages kept until the window attaches.</summary>
    private const int MaxPendingMessages = 256;

    /// <summary>
    /// Jak długo start ponawia podejścia, gdy blokada jest zajęta, a nikt nie odbiera. Tak bywa,
    /// gdy poprzednia kopia akurat się zamyka: doczytuje nadawców (do dwóch limitów odczytu
    /// w <see cref="StopListening"/>) i zapisuje kolejkę. Termin musi to przeczekać, inaczej
    /// powstałoby drugie okno bez nasłuchu, które przy zamknięciu nadpisze zapisaną kolejkę.
    /// </summary>
    private static readonly TimeSpan StartPatience = 2 * HandoffChannel.ReadTimeout + TimeSpan.FromSeconds(2);

    /// <summary>Przerwa między podejściami, gdy odmowa przychodzi od razu.</summary>
    private static readonly TimeSpan RetryPause = TimeSpan.FromMilliseconds(50);

    private readonly FileStream _lock;
    private readonly IHandoffListener _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _listener;
    private readonly object _gate = new();
    private readonly List<string[]> _pending = [];
    private Action<string[]>? _receiver;
    private bool _stopped;
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
    /// Bierze rolę działającej kopii albo oddaje ścieżki tej, która ją ma.
    /// Nieudane przekazanie przy zajętej blokadzie znaczy, że poprzednia kopia właśnie zniknęła;
    /// wtedy trzeba wrócić po rolę, bo okno bez nasłuchu mnożyłoby okna przy każdym uruchomieniu.
    /// </summary>
    public static Claim Start(InstanceAddress address, IReadOnlyList<string> paths)
    {
        var deadline = Environment.TickCount64 + (long)StartPatience.TotalMilliseconds;

        while (true)
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
            if (Environment.TickCount64 >= deadline) break;

            Thread.Sleep(RetryPause);
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
    /// Czy kod błędu z <see cref="Exception.HResult"/> oznacza blokadę trzymaną przez inny proces.
    /// Brak katalogu czy system plików tylko do odczytu też dają <see cref="IOException"/>, ale
    /// wtedy nie ma komu oddawać ścieżek. Na Uniksie .NET podaje EWOULDBLOCK z <c>flock</c> jako
    /// surowy numer błędu.
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
                if (connection is null) break;

                Deliver(ReadMessage(connection));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Nadawca połączył się i zamilkł. Połączenie jest już zamknięte, a pozostali
                // czekają, więc bez pauzy.
                Console.Error.WriteLine("[cewka] nadawca ścieżek nie przysłał wiadomości w wyznaczonym czasie.");
            }
            catch (Exception ex)
            {
                if (_stop.IsCancellationRequested) break;

                Console.Error.WriteLine($"[cewka] przekazanie ścieżek nie powiodło się: {ex.Message}");

                // Pauza chroni przed pętlą pełnego obciążenia, gdyby kanał był trwale zepsuty.
                if (_stop.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250))) break;
            }
        }

        DeliverPending();
    }

    /// <summary>
    /// Odbiera nadawców połączonych przed zamknięciem nasłuchu. Pod Linuksem ich wysyłanie
    /// skończyło się już sukcesem, więc nikt inny tych ścieżek nie otworzy. Wszystkie połączenia
    /// czytane są naraz ze wspólnym terminem, więc nadawcy zawieszeni w połowie nie odsuwają
    /// odbioru tych, którzy wysłali całość.
    /// </summary>
    private void DeliverPending()
    {
        var connections = new List<Stream>();

        try
        {
            while (_channel.TakePending() is { } connection) connections.Add(connection);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] odbiór połączeń przy zamykaniu nie powiódł się: {ex.Message}");
        }

        if (connections.Count == 0) return;

        using var timeout = new CancellationTokenSource(HandoffChannel.ReadTimeout);
        var reads = connections.Select(connection => ReadMessageAsync(connection, timeout.Token)).ToArray();

        // Kolejność przybycia zostaje zachowana; czekanie na pierwsze nie wstrzymuje odczytu następnych.
        for (var i = 0; i < reads.Length; i++)
        {
            try
            {
                Deliver(reads[i].GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cewka] przekazanie ścieżek nie powiodło się: {ex.Message}");
            }
            finally
            {
                connections[i].Dispose();
            }
        }
    }

    /// <summary>
    /// Czyta jedną wiadomość, najdłużej przez <see cref="HandoffChannel.ReadTimeout"/>. Bez limitu
    /// nadawca wstrzymany w połowie (debuger, skaner antywirusowy) trzymałby kanał bez końca.
    /// Zamknięcie nasłuchu jej nie przerywa, bo pod Linuksem nadawca uznał już przekazanie za udane.
    /// </summary>
    private static string[] ReadMessage(Stream stream)
    {
        using var timeout = new CancellationTokenSource(HandoffChannel.ReadTimeout);
        return ReadMessageAsync(stream, timeout.Token).GetAwaiter().GetResult();
    }

    private static async Task<string[]> ReadMessageAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, token)) return [];

        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessageBytes) return [];

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, token)) return [];

        return JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(payload), HandoffJsonContext.Default.StringArray) ?? [];
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] destination, CancellationToken token)
    {
        try
        {
            await stream.ReadExactlyAsync(destination, token);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>
    /// Przestaje przyjmować ścieżki, ale rolę zostawia do <see cref="Dispose"/>. Ścieżki nadawców
    /// połączonych wcześniej docierają do odbiorcy przed powrotem. Kopia uruchomiona później
    /// nie połączy się i ponawia próby przejęcia roli.
    /// </summary>
    public void StopListening()
    {
        if (_stopped) return;
        _stopped = true;

        // Najpierw odmowa dla nowych nadawców: połączony w trakcie doczytywania uznałby
        // przekazanie za udane, a nikt by go już nie odebrał.
        _channel.StopAccepting();

        // Przerywa samo czekanie na połączenie. Wątek doczytuje rozpoczętą wiadomość (jeden limit
        // odczytu), potem naraz wszystkie oczekujące (drugi limit), więc ten czas mu wystarcza.
        _stop.Cancel();
        _listener.Join(2 * HandoffChannel.ReadTimeout + TimeSpan.FromMilliseconds(500));

        _channel.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopListening();

        // Po awarii procesu blokadę zwolni jądro.
        _lock.Dispose();
        _stop.Dispose();
    }
}
