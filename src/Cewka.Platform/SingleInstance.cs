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
/// Ścieżki są tu wypisane, a nie brane wprost ze <see cref="AppPaths"/>, żeby sprawdzenia
/// mogły uruchamiać ten mechanizm w katalogu tymczasowym, nie ruszając prawdziwych ustawień
/// ani działającego programu.
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
/// <para><b>Po co.</b> Skojarzenie plików sprawia, że otwarcie albumu z eksploratora uruchamia
/// program raz dla każdego zaznaczonego pliku. Bez tego mechanizmu powstałoby kilkanaście okien
/// walczących o urządzenie dźwiękowe. Tutaj pierwsza kopia zostaje, a każda następna oddaje jej
/// swoje ścieżki i natychmiast kończy pracę.</para>
///
/// <para><b>Dlaczego blokada pliku.</b> O pierwszeństwie rozstrzyga wyłączna blokada pliku
/// w katalogu ustawień. Wcześniej stał tu muteks nazwany i pod Linuksem okazał się zawodny:
/// .NET trzyma takie muteksy w <c>/tmp/.dotnet/shm/session&lt;SID&gt;</c>, czyli osobno dla każdej
/// sesji POSIX. Menedżer plików uruchamia programy przez systemd, a ten odcina każdy z nich
/// własnym <c>setsid</c> — każda kopia trafiała więc do pustego katalogu, uznawała się za
/// pierwszą i otwierała kolejne okno, choć poprzednia nasłuchiwała tuż obok. Blokada pliku nie
/// zna pojęcia sesji, a jądro zwalnia ją nawet wtedy, gdy program zginie bez pożegnania.</para>
///
/// <para><b>Sprawdzenie po zmianach w tym pliku.</b> Sedno błędu widać wyłącznie między dwiema
/// sesjami POSIX, więc żadne sprawdzenie w jednym procesie go nie wyłapie. Pod Linuksem:
/// uruchomić kopię, potem <c>setsid program plik.mp3</c> i policzyć procesy — ma zostać jeden.</para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Guards against a hostile or broken sender flooding the channel.</summary>
    private const int MaxMessageBytes = 1 << 20;

    /// <summary>
    /// How many times start-up may go round. Rola zmienia właściciela dokładnie między
    /// sprawdzeniem blokady a połączeniem tylko wtedy, gdy poprzednia kopia właśnie kończyła
    /// pracę — kilka podejść wystarczy, żeby to przeczekać.
    /// </summary>
    private const int Attempts = 3;

    private readonly FileStream _lock;
    private readonly IHandoffListener _channel;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(FileStream held, IHandoffListener channel)
    {
        _lock = held;
        _channel = channel;
    }

    /// <summary>Paths sent by another copy of the application. Raised off the interface thread.</summary>
    public event Action<string[]>? PathsReceived;

    /// <summary>
    /// Outcome of start-up: either this copy runs the show, or its paths went to one that
    /// already does.
    /// </summary>
    /// <param name="Instance">Nie-null, gdy ta kopia wzięła rolę i ma nasłuchiwać.</param>
    /// <param name="HandedOver">Prawda, gdy ścieżki trafiły gdzie indziej i nie ma tu nic do roboty.</param>
    public readonly record struct Claim(SingleInstance? Instance, bool HandedOver);

    /// <summary>
    /// Takes the role of the one running copy, or hands the paths over to whoever holds it.
    ///
    /// <para>Nieudane przekazanie przy zajętej blokadzie znaczy, że poprzednia kopia zniknęła
    /// dokładnie w tej chwili. Wtedy trzeba wrócić po rolę, a nie otwierać okno bez nasłuchu —
    /// taka kopia nie odebrałaby już niczego i każde następne uruchomienie mnożyłoby okna.</para>
    /// </summary>
    public static Claim Start(InstanceAddress address, IReadOnlyList<string> paths)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var instance = TryAcquire(address);
            if (instance is not null) return new Claim(instance, false);

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
        FileStream held;

        try
        {
            held = new FileStream(
                address.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Blokadę trzyma inna kopia. To zwykły przebieg, nie awaria.
            return null;
        }
        catch (Exception ex)
        {
            // Katalog tylko do odczytu albo inna niespodzianka. Gorszym wynikiem jest program,
            // który się nie otwiera, niż dodatkowe okno — więc ta kopia po prostu rusza.
            Console.Error.WriteLine($"[cewka] pojedyncza instancja niedostępna: {ex.Message}");
            return null;
        }

        try
        {
            Stamp(held);
            return new SingleInstance(held, HandoffChannel.Listen(address));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] kanał przekazywania ścieżek niedostępny: {ex.Message}");
            held.Dispose();
            return null;
        }
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

    /// <summary>Begins accepting hand-offs. Safe to call once.</summary>
    public void StartListening()
    {
        if (_listener is not null) return;

        _listener = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "Cewka.Instance",
        };

        _listener.Start();
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

                // Zgłaszane także przy pustej liście: uruchomienie programu bez argumentów,
                // gdy jeden już działa, ma wysunąć jego okno na wierzch.
                PathsReceived?.Invoke(ReadMessage(connection));
            }
            catch (OperationCanceledException)
            {
                return;
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

    private static string[] ReadMessage(Stream stream)
    {
        var header = new byte[4];
        if (!ReadExactly(stream, header)) return [];

        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessageBytes) return [];

        var payload = new byte[length];
        if (!ReadExactly(stream, payload)) return [];

        return JsonSerializer.Deserialize(
            Encoding.UTF8.GetString(payload), HandoffJsonContext.Default.StringArray) ?? [];
    }

    private static bool ReadExactly(Stream stream, Span<byte> destination)
    {
        var offset = 0;

        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read <= 0) return false;
            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stop.Cancel();

        // Zamknięcie kanału zdejmuje wątek stojący na przyjęciu połączenia.
        _channel.Dispose();
        _listener?.Join(500);

        // Zwolnienie blokady. Gdyby tu nie doszło — bo program zginął — zrobi to jądro.
        _lock.Dispose();
        _stop.Dispose();
    }
}
