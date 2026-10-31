using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Sprawdzenia mechanizmu jednej działającej kopii.
///
/// <para>Każde z nich pracuje we własnym katalogu tymczasowym i pod własną nazwą, więc nie
/// dotyka ustawień osoby uruchamiającej testy ani programu, który akurat u niej działa.</para>
///
/// <para><b>Czego tu nie ma.</b> Sedno naprawianego błędu — muteks nazwany osobny dla każdej
/// sesji POSIX — ujawnia się wyłącznie między dwiema sesjami. Jeden proces testowy siedzi
/// w jednej sesji, więc żadne sprawdzenie stąd tego nie wyłapie; potwierdzeniem jest próba
/// opisana w <see cref="SingleInstance"/>, uruchamiana ręcznie pod Linuksem.</para>
/// </summary>
public sealed class SingleInstanceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly List<string> _directories = [];

    // ---------- Rozstrzyganie pierwszeństwa ----------

    [Fact]
    public void DrugaKopiaNieDostajeRoli()
    {
        var address = Temporary();

        using var first = SingleInstance.TryAcquire(address);
        Assert.NotNull(first);

        using var second = SingleInstance.TryAcquire(address);
        Assert.Null(second);
    }

    /// <summary>
    /// Po zamknięciu pierwszej kopii rola musi być znów do wzięcia. Muteks nazwany zostawiał
    /// pod Linuksem plik po sobie; blokada zwalnia się wraz z uchwytem.
    /// </summary>
    [Fact]
    public void RolaWracaPoZamknieciuPierwszejKopii()
    {
        var address = Temporary();

        var first = SingleInstance.TryAcquire(address);
        Assert.NotNull(first);
        first.Dispose();

        using var second = SingleInstance.TryAcquire(address);
        Assert.NotNull(second);
    }

    /// <summary>
    /// W pliku blokady zostaje numer procesu, który trzymał rolę. Póki kopia działa, plik jest
    /// zamknięty dla wszystkich — ale gdy ktoś zastanie dwa okna, wartość mówi, kto był
    /// pierwszy. Pod Linuksem blokada jest umowna, więc czyta ją zwykłe <c>cat</c> od razu.
    /// </summary>
    [Fact]
    public void PlikBlokadyNiesieNumerProcesu()
    {
        var address = Temporary();

        var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);
        instance.Dispose();

        Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(address.LockFile));
    }

    // ---------- Przekazywanie ścieżek ----------

    [Fact]
    public void SciezkiTrafiajaDoPierwszejKopii()
    {
        var address = Temporary();

        using var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);

        var received = Listen(instance);

        Assert.True(SingleInstance.TryHandOff(address, ["/muzyka/pierwszy.mp3", "/muzyka/drugi.flac"]));
        Assert.Equal(["/muzyka/pierwszy.mp3", "/muzyka/drugi.flac"], Await(received));
    }

    /// <summary>
    /// Uruchomienie bez argumentów, gdy program już działa, ma wysunąć jego okno na wierzch —
    /// więc pusta lista też musi dojść, zamiast zostać po drodze uznana za brak wiadomości.
    /// </summary>
    [Fact]
    public void UruchomienieBezSciezekTezDociera()
    {
        var address = Temporary();

        using var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);

        var received = Listen(instance);

        Assert.True(SingleInstance.TryHandOff(address, []));
        Assert.Empty(Await(received));
    }

    /// <summary>
    /// Zaznaczenie albumu w menedżerze plików uruchamia kopię na każdy plik i wszystkie zgłaszają
    /// się w tej samej chwili. Żadna nie może przepaść ani odbić się od zajętego kanału.
    /// </summary>
    [Fact]
    public void KilkaKopiiNarazOddajeSwojeSciezki()
    {
        const int copies = 12;
        var address = Temporary();

        using var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);

        var all = new List<string>();
        var complete = new ManualResetEventSlim();

        instance.Attach(paths =>
        {
            lock (all)
            {
                all.AddRange(paths);
                if (all.Count == copies) complete.Set();
            }
        });

        Assert.Equal(0, SendConcurrently(address, copies));
        Assert.True(complete.Wait(Patience), $"doszło {all.Count} z {copies} ścieżek");

        lock (all) Assert.Equal(copies, all.Distinct().Count());
    }

    /// <summary>
    /// Pierwsza kopia buduje okno przez kilka sekund, a kopie uruchomione w tym czasie nie mogą
    /// czekać na odbiorcę: ich ścieżki mają poczekać w kanale, a one same zakończyć pracę.
    /// </summary>
    [Fact]
    public void SciezkiPrzedPodpieciemOdbiorcyCzekajaWKanale()
    {
        const int copies = 12;
        var address = Temporary();

        using var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);

        Assert.Equal(0, SendConcurrently(address, copies));

        var all = new List<string>();
        var complete = new ManualResetEventSlim();

        // Wiadomości mogą jeszcze płynąć w chwili podpięcia odbiorcy; żadna nie może przepaść.
        Thread.Sleep(200);
        instance.Attach(paths =>
        {
            lock (all)
            {
                all.AddRange(paths);
                if (all.Count == copies) complete.Set();
            }
        });

        Assert.True(complete.Wait(Patience), $"doszło {all.Count} z {copies} ścieżek");
        lock (all) Assert.Equal(copies, all.Distinct().Count());
    }

    /// <summary>
    /// Nadawca, który się połączył i zamilkł (wstrzymany proces, skaner antywirusowy), nie może
    /// zablokować kanału pozostałym.
    /// </summary>
    [Fact]
    public void MilczacyNadawcaNieBlokujeKanalu()
    {
        var address = Temporary();

        using var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);

        var received = Listen(instance);

        using var silent = ConnectSilently(address);

        Assert.True(SingleInstance.TryHandOff(address, ["/muzyka/utwor.mp3"]));
        Assert.Equal(["/muzyka/utwor.mp3"], Await(received));
    }

    /// <summary>
    /// Gdy blokady nie da się założyć wcale, nikt jej nie trzyma i nie ma komu oddawać ścieżek.
    /// Program ma ruszyć od razu, a nie po trzech sekundach prób połączenia.
    /// </summary>
    [Fact]
    public void StartRuszaOdRazuGdyBlokadaNiedostepna()
    {
        var address = Temporary();
        var missing = Path.Combine(Path.GetDirectoryName(address.LockFile)!, "brak", "instance.lock");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var claim = SingleInstance.Start(address with { LockFile = missing }, ["/muzyka/utwor.mp3"]);

        Assert.Null(claim.Instance);
        Assert.False(claim.HandedOver);
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500), $"start trwał {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>Zamknięta kopia przestaje przyjmować ścieżki, więc następna przejmuje rolę.</summary>
    [Fact]
    public void PoZwolnieniuRoliNiktNiePrzyjmujeSciezek()
    {
        var address = Temporary();

        var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);
        instance.Dispose();

        var claim = SingleInstance.Start(address, ["/muzyka/utwor.mp3"]);

        using var next = claim.Instance;
        Assert.False(claim.HandedOver);
        Assert.NotNull(next);
    }

    [Fact]
    public void PrzekazanieDoNikogoSieNieUdaje()
    {
        var address = Temporary();

        Assert.False(SingleInstance.TryHandOff(address, ["/muzyka/utwor.mp3"]));
    }

    // ---------- Uruchomienie jako całość ----------

    [Fact]
    public void StartBierzeRoleGdyNikogoNieMa()
    {
        var address = Temporary();

        var claim = SingleInstance.Start(address, []);

        using var instance = claim.Instance;
        Assert.False(claim.HandedOver);
        Assert.NotNull(instance);
    }

    [Fact]
    public void StartOddajeSciezkiGdyKtosJuzDziala()
    {
        var address = Temporary();

        using var first = SingleInstance.TryAcquire(address);
        Assert.NotNull(first);

        var received = Listen(first);

        var claim = SingleInstance.Start(address, ["/muzyka/utwor.mp3"]);

        Assert.True(claim.HandedOver);
        Assert.Null(claim.Instance);
        Assert.Equal(["/muzyka/utwor.mp3"], Await(received));
    }

    /// <summary>
    /// Sedno drugiej z naprawianych usterek. Blokada jest zajęta, więc pierwsze podejście
    /// odpada, ale nikt nie odbiera — bo właściciel właśnie skończył pracę. Kopia musi wtedy
    /// wrócić po rolę. Wcześniej otwierała okno bez nasłuchu i od tej chwili każde następne
    /// uruchomienie dokładało kolejne okno.
    /// </summary>
    [Fact]
    public void StartWracaPoRoleGdyWlascicielZniknal()
    {
        var address = Temporary();

        var held = new FileStream(
            address.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        // Właściciel znika w trakcie startu kolejnej kopii — dokładnie ta chwila, w której
        // sprawdzenie blokady i próba połączenia mówią co innego.
        var release = new Thread(() =>
        {
            Thread.Sleep(200);
            held.Dispose();
        });

        release.Start();

        var claim = SingleInstance.Start(address, ["/muzyka/utwor.mp3"]);
        release.Join();

        using var instance = claim.Instance;
        Assert.False(claim.HandedOver);
        Assert.NotNull(instance);
    }

    // ---------- Miejsce na dysku ----------

    /// <summary>
    /// Blokada musi leżeć tam, gdzie ustawienia: to samo miejsce niezależnie od tego, skąd
    /// program uruchomiono. Przeniesienie jej do katalogu zależnego od sesji przywróciłoby
    /// naprawiany błąd.
    /// </summary>
    [Fact]
    public void BlokadaLezyPrzyUstawieniach()
    {
        var address = InstanceAddress.Default("Cewka.sprawdzenie");

        Assert.Equal(AppPaths.ConfigDirectory, Path.GetDirectoryName(address.LockFile));
        Assert.Equal(AppPaths.InstanceLockFile, address.LockFile);
    }

    // ---------- Pomocnicze ----------

    private static Task<string[]> Listen(SingleInstance instance)
    {
        var arrived = new TaskCompletionSource<string[]>();

        instance.Attach(paths => arrived.TrySetResult(paths));

        return arrived.Task;
    }

    /// <summary>Uruchamia nadawców w tej samej chwili; zwraca liczbę odrzuconych.</summary>
    private static int SendConcurrently(InstanceAddress address, int copies)
    {
        var refused = 0;
        var start = new ManualResetEventSlim();

        var senders = Enumerable.Range(0, copies).Select(i => new Thread(() =>
        {
            start.Wait();
            if (!SingleInstance.TryHandOff(address, [$"/muzyka/{i:00}.mp3"])) Interlocked.Increment(ref refused);
        })).ToArray();

        foreach (var sender in senders) sender.Start();
        start.Set();
        foreach (var sender in senders) sender.Join();

        return refused;
    }

    /// <summary>Łączy się z kanałem i nic nie wysyła.</summary>
    private static IDisposable ConnectSilently(InstanceAddress address)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".", address.Name, System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.CurrentUserOnly);

            pipe.Connect(1000);
            return pipe;
        }

        var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Unspecified);

        socket.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(address.SocketFile));
        return socket;
    }

    private static string[] Await(Task<string[]> received)
    {
        Assert.True(received.Wait(Patience), "ścieżki nie doszły w wyznaczonym czasie");
        return received.Result;
    }

    private InstanceAddress Temporary()
    {
        var stamp = Guid.NewGuid().ToString("N");

        // Krótka nazwa katalogu nie jest kaprysem: adres gniazda dziedziny Uniksa mieści się
        // w 108 bajtach i dłuższa ścieżka po prostu się nie zmieści.
        var directory = Path.Combine(Path.GetTempPath(), "cewka-" + stamp[..8]);

        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        return new InstanceAddress(
            "Cewka.test." + stamp,
            Path.Combine(directory, "instance.lock"),
            Path.Combine(directory, "instance.sock"));
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Sprzątanie katalogu tymczasowego nie jest powodem, żeby sprawdzenie upadło.
            }
        }
    }
}
