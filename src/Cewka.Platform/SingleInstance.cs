using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cewka.Platform;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(string[]))]
internal sealed partial class HandoffJsonContext : JsonSerializerContext;

/// <summary>
/// Keeps a single running copy of the application and hands paths over to it.
///
/// <para><b>Po co.</b> Skojarzenie plików sprawia, że otwarcie albumu z eksploratora uruchamia
/// program raz dla każdego zaznaczonego pliku. Bez tego mechanizmu powstałoby kilkanaście okien
/// walczących o urządzenie dźwiękowe. Tutaj pierwsza kopia zostaje, a każda następna oddaje jej
/// swoje ścieżki i natychmiast kończy pracę.</para>
///
/// <para><b>Dlaczego muteks i potok.</b> Muteks rozstrzyga, kto jest pierwszy — samo sprawdzenie
/// potoku przegrałoby wyścig przy dwóch kopiach ruszających w tej samej chwili. Potok nazwany
/// przenosi ścieżki i działa w obu systemach: w Windows jest potokiem, w Linuksie gniazdem
/// dziedziny Uniksa w katalogu tymczasowym.</para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>Longer than a cold start needs, short enough that a stale name is not a hang.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Guards against a hostile or broken sender flooding the pipe.</summary>
    private const int MaxMessageBytes = 1 << 20;

    private readonly string _name;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(string name, Mutex mutex)
    {
        _name = name;
        _mutex = mutex;
    }

    /// <summary>Paths sent by another copy of the application. Raised off the interface thread.</summary>
    public event Action<string[]>? PathsReceived;

    /// <summary>
    /// Claims the role of the one running instance. Returns <c>null</c> when another copy already
    /// holds it, in which case <see cref="TryHandOff"/> should be used instead.
    /// </summary>
    public static SingleInstance? TryAcquire(string name)
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, $"{name}.instance", out var created);
            if (created) return new SingleInstance(name, mutex);

            mutex.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            // Brak muteksu (nietypowa polityka systemu) nie może uniemożliwić uruchomienia —
            // gorszym wynikiem jest dodatkowe okno niż program, który się nie otwiera.
            Console.Error.WriteLine($"[cewka] pojedyncza instancja niedostępna: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Passes paths to the copy that is already running. Returns false when nobody answered,
    /// which means this copy should simply open its own window.
    /// </summary>
    public static bool TryHandOff(string name, IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            client.Connect((int)ConnectTimeout.TotalMilliseconds);

            var payload = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(paths.ToArray(), HandoffJsonContext.Default.StringArray));

            client.Write(BitConverter.GetBytes(payload.Length));
            client.Write(payload);
            client.Flush();

            return true;
        }
        catch
        {
            // Najczęstszy powód: poprzednia kopia zakończyła się między sprawdzeniem muteksu
            // a połączeniem. Wtedy ta kopia po prostu działa dalej jako zwykłe uruchomienie.
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

    private void ListenLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                server.WaitForConnectionAsync(_stop.Token).GetAwaiter().GetResult();

                // Zgłaszane także przy pustej liście: uruchomienie programu bez argumentów,
                // gdy jeden już działa, ma wysunąć jego okno na wierzch.
                PathsReceived?.Invoke(ReadMessage(server));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cewka] przekazanie ścieżek nie powiodło się: {ex.Message}");

                // Pauza chroni przed pętlą pełnego obciążenia, gdyby potok był trwale zepsuty.
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
        _listener?.Join(500);

        try { _mutex.ReleaseMutex(); } catch { /* nie posiadamy go, gdy start się nie powiódł */ }
        _mutex.Dispose();
        _stop.Dispose();
    }
}
