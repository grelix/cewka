using Cewka.Platform;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Sprawdzenia mechanizmu jednej działającej kopii.
///
/// <para>Każde z nich pracuje we własnym katalogu tymczasowym i pod własną nazwą, więc nie
/// dotyka ustawień osoby uruchamiającej testy ani programu, który akurat u niej działa.</para>
///
/// <para>Pułapka: rozdzielenie kopii między sesje POSIX ujawnia się wyłącznie między dwiema
/// sesjami, a proces testowy siedzi w jednej. Potwierdzeniem jest próba opisana
/// w <see cref="SingleInstance"/>, uruchamiana ręcznie pod Linuksem.</para>
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
    /// Po zamknięciu pierwszej kopii rola musi być znów do wzięcia: blokada zwalnia się wraz
    /// z uchwytem.
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
    /// Program ma ruszyć od razu. Próg leży poniżej czasu jednej próby połączenia (1 s), więc
    /// przekracza go już pierwsza zbędna próba, a obciążona maszyna ma zapas.
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
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(900), $"start trwał {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Zajęta blokada to wyłącznie EWOULDBLOCK (Unix) albo naruszenie współdzielenia (Windows).
    /// Inny błąd wejścia-wyjścia, uznany za zajętą blokadę, kosztowałby trzy sekundy prób
    /// połączenia z kopią, której nie ma.
    /// </summary>
    [Theory]
    [InlineData(11, false, true, true)]
    [InlineData(35, false, false, true)]
    [InlineData(30, false, true, false)]
    [InlineData(5, false, true, false)]
    [InlineData(2, false, true, false)]
    [InlineData(35, false, true, false)]
    [InlineData(unchecked((int)0x80070020), true, false, true)]
    [InlineData(unchecked((int)0x80070021), true, false, true)]
    [InlineData(unchecked((int)0x80070003), true, false, false)]
    [InlineData(unchecked((int)0x80070005), true, false, false)]
    public void ZajetaBlokadaRozpoznanaPoKodzieBledu(int hresult, bool windows, bool linux, bool expected)
    {
        Assert.Equal(expected, SingleInstance.IsLockConflict(hresult, windows, linux));
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

    /// <summary>
    /// Zamykana kopia przestaje słuchać, zanim zapisze kolejkę, ale rolę oddaje dopiero po
    /// zapisie. Kopia uruchomiona w tym czasie czeka na rolę i otwiera plik sama, zamiast oddać
    /// go kopii, która już niczego nie przyjmie.
    /// </summary>
    [Fact]
    public void KopiaUruchomionaWTrakcieZamykaniaPrzejmujeRole()
    {
        var address = Temporary();

        var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);
        instance.StopListening();

        Assert.False(SingleInstance.TryHandOff(address, ["/muzyka/utwor.mp3"]));

        // Zapis kończy się w trakcie startu następnej kopii, która dopiero wtedy dostaje rolę.
        var release = new Thread(() =>
        {
            Thread.Sleep(300);
            instance.Dispose();
        });

        release.Start();

        var claim = SingleInstance.Start(address, ["/muzyka/utwor.mp3"]);
        release.Join();

        using var next = claim.Instance;
        Assert.False(claim.HandedOver);
        Assert.NotNull(next);
    }

    /// <summary>
    /// Pod Linuksem wysłanie kończy się, gdy dane leżą w buforze jądra, więc nadawca, który zaczął
    /// pisać przed zamknięciem nasłuchu, uznał już przekazanie za udane. Wiadomość musi dojść.
    /// </summary>
    [Fact]
    public void ZamkniecieNasluchuDoczytujeRozpoczetaWiadomosc()
    {
        var address = Temporary();

        using var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);

        var received = Listen(instance);
        var message = Message("/muzyka/utwor.mp3");

        using var sender = ConnectRaw(address);
        sender.Write(message, 0, 6);
        sender.Flush();
        Thread.Sleep(100);

        var stopping = new Thread(instance.StopListening);
        stopping.Start();
        Thread.Sleep(100);

        sender.Write(message, 6, message.Length - 6);
        sender.Flush();

        Assert.True(stopping.Join(Patience));
        Assert.Equal(["/muzyka/utwor.mp3"], Await(received));
    }

    /// <summary>
    /// Nadawca połączony w czasie, gdy odbiorca czyta poprzednią wiadomość, czeka w kolejce
    /// gniazda albo na przygotowanej instancji potoku. Zamknięcie nasłuchu odbiera i jego ścieżki.
    /// </summary>
    [Fact]
    public void ZamkniecieNasluchuOdbieraNadawcowJuzPolaczonych()
    {
        var address = Temporary();

        using var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);

        var all = new List<string>();
        instance.Attach(paths => { lock (all) all.AddRange(paths); });

        var message = Message("/muzyka/pierwszy.mp3");

        using var first = ConnectRaw(address);
        first.Write(message, 0, 6);
        first.Flush();
        Thread.Sleep(100);

        // W Windows zapis do potoku czeka na odczyt, więc drugi nadawca idzie osobnym wątkiem.
        var handedOff = false;
        var second = new Thread(() => handedOff = SingleInstance.TryHandOff(address, ["/muzyka/drugi.mp3"]));
        second.Start();
        Thread.Sleep(200);

        var stopping = new Thread(instance.StopListening);
        stopping.Start();
        Thread.Sleep(100);

        first.Write(message, 6, message.Length - 6);
        first.Flush();

        Assert.True(stopping.Join(Patience));
        Assert.True(second.Join(Patience));
        Assert.True(handedOff);
        lock (all) Assert.Equal(["/muzyka/pierwszy.mp3", "/muzyka/drugi.mp3"], all);
    }

    /// <summary>
    /// Dwóch nadawców zawieszonych w połowie wiadomości nie może wydłużyć odbioru ponad czas,
    /// na który czeka zamykanie. Każdy nadawca, któremu przekazanie się udało, ma swoje ścieżki
    /// u odbiorcy przed powrotem StopListening; nic nie może dojść po nim.
    /// </summary>
    [Fact]
    public void ZamkniecieNasluchuNieGubiNadawcowZaZawieszonymi()
    {
        var address = Temporary();

        using var instance = SingleInstance.TryAcquire(address);
        Assert.NotNull(instance);

        var all = new List<string>();
        var late = new List<string>();
        var returned = false;

        instance.Attach(paths =>
        {
            lock (all)
            {
                all.AddRange(paths);
                if (returned) late.AddRange(paths);
            }
        });

        // Pierwszy zawieszony: czyta go pętla nasłuchu w chwili zamknięcia.
        var first = Message("/muzyka/pierwszy.mp3");
        using var a = ConnectRaw(address);
        a.Write(first, 0, 6);
        a.Flush();
        Thread.Sleep(100);

        // Drugi zawieszony: czeka w kolejce gniazda albo na przygotowanej instancji potoku.
        var second = Message("/muzyka/drugi.mp3");
        var resume = new ManualResetEventSlim();
        var secondDone = false;

        var b = new Thread(() =>
        {
            try
            {
                using var stream = ConnectRaw(address);
                stream.Write(second, 0, 6);
                stream.Flush();

                resume.Wait();
                stream.Write(second, 6, second.Length - 6);
                stream.Flush();
                secondDone = true;
            }
            catch (Exception)
            {
                // Odbiorca zamknął połączenie: nadawca wie, że przekazanie się nie udało.
            }
        });

        b.Start();
        Thread.Sleep(200);

        // Trzeci wysyła całość; pod Linuksem kończy pracę z sukcesem, zanim ktokolwiek go odbierze.
        var thirdDone = false;
        var c = new Thread(() => thirdDone = SingleInstance.TryHandOff(address, ["/muzyka/trzeci.mp3"]));
        c.Start();
        Thread.Sleep(300);

        instance.StopListening();
        lock (all) returned = true;

        // Drugi nadawca budzi się po powrocie zamykania: jego zapis nie może się już udać po cichu.
        resume.Set();
        Assert.True(b.Join(TimeSpan.FromSeconds(10)));
        Assert.True(c.Join(TimeSpan.FromSeconds(10)));
        Thread.Sleep(500);

        lock (all)
        {
            Assert.Empty(late);
            if (secondDone) Assert.Contains("/muzyka/drugi.mp3", all);
            if (thirdDone) Assert.Contains("/muzyka/trzeci.mp3", all);
        }
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
    /// Blokada jest zajęta, więc pierwsze podejście odpada, ale nikt nie odbiera, bo właściciel
    /// właśnie skończył pracę. Kopia musi wtedy wrócić po rolę. Okno bez nasłuchu sprawiłoby,
    /// że każde następne uruchomienie dokłada kolejne okno.
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
    private static IDisposable ConnectSilently(InstanceAddress address) => ConnectRaw(address);

    /// <summary>Połączenie nadawcy, który pisze wiadomość sam, po kawałku.</summary>
    private static Stream ConnectRaw(InstanceAddress address)
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
        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Wiadomość w postaci, w jakiej idzie kanałem: długość, potem lista w JSON.</summary>
    private static byte[] Message(params string[] paths)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(paths));
        return [.. BitConverter.GetBytes(payload.Length), .. payload];
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
