using System.Runtime.Versioning;
using Cewka.Platform;
using Cewka.Platform.Linux;
using Xunit;

namespace Cewka.App.Tests;

/// <summary>
/// Blokada wygaszania ekranu: kontrakt wspólny dla systemów, prawdziwe żądanie zasilania
/// w Windows i wywołania szyny D-Bus w Linuksie.
/// </summary>
public sealed class DisplayWakeLockTests
{
    private const string Reason = "Cewka: sprawdzenie blokady wygaszania ekranu";

    private readonly ManualTime _time = new();

    /// <summary>Blokady, które na tym systemie muszą zostać przyjęte.</summary>
    public static TheoryData<string> Implementations()
    {
        var data = new TheoryData<string> { "atrapa" };
        if (OperatingSystem.IsWindows()) data.Add("windows");
        return data;
    }

    private static IDisplayWakeLock Create(string implementation)
    {
        if (implementation == "windows" && OperatingSystem.IsWindows()) return new WindowsDisplayWakeLock(Reason);
        return new NoDisplayWakeLock();
    }

    // ---------- Kontrakt ----------

    [Fact]
    public void FabrykaDajeBlokadeBiezacegoSystemu()
    {
        using var wakeLock = DisplayWakeLock.Create(Reason);

        var expected = OperatingSystem.IsWindows() ? typeof(WindowsDisplayWakeLock)
            : OperatingSystem.IsLinux() ? typeof(LinuxDisplayWakeLock)
            : typeof(NoDisplayWakeLock);

        Assert.IsType(expected, wakeLock);
        Assert.False(wakeLock.IsHeld);
    }

    /// <summary>Blokada nie liczy przejęć: jedno zwolnienie zdejmuje ją po dowolnej ich liczbie.</summary>
    [Theory]
    [MemberData(nameof(Implementations))]
    public void DrugiePrzejecieNicNieZmienia(string implementation)
    {
        using var wakeLock = Create(implementation);

        wakeLock.Acquire();
        wakeLock.Acquire();
        Assert.True(wakeLock.IsHeld);

        wakeLock.Release();
        Assert.False(wakeLock.IsHeld);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ZwolnienieBezPrzejeciaNicNieRobi(string implementation)
    {
        using var wakeLock = Create(implementation);

        wakeLock.Release();
        wakeLock.Release();
        Assert.False(wakeLock.IsHeld);

        wakeLock.Acquire();
        Assert.True(wakeLock.IsHeld);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void BlokadeDaSiePrzejacPonowniePoZwolnieniu(string implementation)
    {
        using var wakeLock = Create(implementation);

        wakeLock.Acquire();
        wakeLock.Release();
        wakeLock.Acquire();

        Assert.True(wakeLock.IsHeld);
    }

    [Theory]
    [MemberData(nameof(Implementations))]
    public void ZamkniecieZwalniaBlokade(string implementation)
    {
        var wakeLock = Create(implementation);

        wakeLock.Acquire();
        wakeLock.Dispose();
        Assert.False(wakeLock.IsHeld);

        wakeLock.Dispose();
        Assert.False(wakeLock.IsHeld);
    }

    // ---------- Windows ----------

    /// <summary>Po zamknięciu uchwyt żądania już nie istnieje, więc przejęcie nie ma czego ustawić.</summary>
    [WindowsFact]
    public void ZamknietaBlokadaWindowsNieDajeSiePrzejac()
    {
        if (!OperatingSystem.IsWindows()) return;

        var wakeLock = new WindowsDisplayWakeLock(Reason);
        wakeLock.Acquire();
        wakeLock.Dispose();

        wakeLock.Acquire();

        Assert.False(wakeLock.IsHeld);
    }

    /// <summary>
    /// Odmowa API (tu nieznany rodzaj żądania) zostaje w blokadzie: wywołujący widzi tylko
    /// <see cref="IDisplayWakeLock.IsHeld"/> równe false, a przyczyna trafia do dziennika błędów.
    /// </summary>
    [WindowsFact]
    public void BladApiWindowsNieWychodziPozaBlokade()
    {
        if (!OperatingSystem.IsWindows()) return;

        var original = Console.Error;
        var log = new StringWriter();
        Console.SetError(log);

        try
        {
            using var wakeLock = new WindowsDisplayWakeLock(Reason, requestType: 99);

            wakeLock.Acquire();
            Assert.False(wakeLock.IsHeld);

            wakeLock.Release();
            Assert.False(wakeLock.IsHeld);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("[cewka] blokada wygaszania ekranu: PowerSetRequest", log.ToString());
    }

    // ---------- Linux: treść wywołań ----------

    [Fact]
    public void PortalDostajeSesjeFlageBezczynnosciIPowod()
    {
        var call = DisplayInhibitRules.PortalInhibit(Reason);

        Assert.Equal("org.freedesktop.portal.Desktop", call.Destination);
        Assert.Equal("/org/freedesktop/portal/desktop", call.Path);
        Assert.Equal("org.freedesktop.portal.Inhibit", call.Interface);
        Assert.Equal("Inhibit", call.Member);
        Assert.Equal("sua{sv}", call.Signature);

        Assert.Equal(new DBusString(string.Empty), call.Arguments[0]);
        Assert.Equal(new DBusUInt32(8), call.Arguments[1]);

        var options = Assert.IsType<DBusStringOptions>(call.Arguments[2]);
        Assert.Equal([new KeyValuePair<string, string>("reason", Reason)], options.Entries);
    }

    [Fact]
    public void BlokadePortaluZdejmujeZamkniecieJegoZadania()
    {
        const string handle = "/org/freedesktop/portal/desktop/request/1_42/t";

        var call = DisplayInhibitRules.PortalRelease(handle);

        Assert.Equal("org.freedesktop.portal.Desktop", call.Destination);
        Assert.Equal(handle, call.Path);
        Assert.Equal("org.freedesktop.portal.Request", call.Interface);
        Assert.Equal("Close", call.Member);
        Assert.Equal(string.Empty, call.Signature);
    }

    [Fact]
    public void ScreenSaverDostajeNazweProgramuIPowod()
    {
        var call = DisplayInhibitRules.ScreenSaverInhibit(Reason);

        Assert.Equal("org.freedesktop.ScreenSaver", call.Destination);
        Assert.Equal("/org/freedesktop/ScreenSaver", call.Path);
        Assert.Equal("org.freedesktop.ScreenSaver", call.Interface);
        Assert.Equal("Inhibit", call.Member);
        Assert.Equal("ss", call.Signature);
        Assert.Equal([new DBusString("Cewka"), new DBusString(Reason)], call.Arguments);
    }

    [Fact]
    public void ScreenSaverZwalniaBlokadePoCiasteczku()
    {
        var call = DisplayInhibitRules.ScreenSaverRelease(42);

        Assert.Equal("org.freedesktop.ScreenSaver", call.Destination);
        Assert.Equal("UnInhibit", call.Member);
        Assert.Equal("u", call.Signature);
        Assert.Equal([new DBusUInt32(42)], call.Arguments);
    }

    /// <summary>
    /// Brak portalu na szynie kieruje kolejne blokady prosto do ScreenSaver. Spóźniona odpowiedź
    /// albo odmowa nie, bo przy następnej próbie portal może już działać.
    /// </summary>
    [Theory]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown", true)]
    [InlineData("org.freedesktop.DBus.Error.NameHasNoOwner", true)]
    [InlineData("org.freedesktop.DBus.Error.UnknownMethod", true)]
    [InlineData("org.freedesktop.DBus.Error.UnknownInterface", true)]
    [InlineData("org.freedesktop.DBus.Error.UnknownObject", true)]
    [InlineData("org.freedesktop.DBus.Error.Spawn.ChildExited", true)]
    [InlineData("org.freedesktop.DBus.Error.NoReply", false)]
    [InlineData("org.freedesktop.DBus.Error.Timeout", false)]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied", false)]
    [InlineData("org.freedesktop.portal.Error.NotAllowed", false)]
    [InlineData("", false)]
    public void PortalPomijanyTylkoGdyGoNieMa(string errorName, bool missing)
    {
        Assert.Equal(missing, DisplayInhibitRules.IsPortalMissing(errorName));
    }

    [Theory]
    [InlineData("org.freedesktop.DBus.Error.NoReply", true)]
    [InlineData("org.freedesktop.DBus.Error.Timeout", true)]
    [InlineData("org.freedesktop.DBus.Error.TimedOut", true)]
    [InlineData("org.freedesktop.DBus.Error.Disconnected", true)]
    [InlineData("org.freedesktop.DBus.Error.NoMemory", true)]
    [InlineData("", true)]
    [InlineData("org.freedesktop.DBus.Error.ServiceUnknown", false)]
    [InlineData("org.freedesktop.DBus.Error.AccessDenied", false)]
    [InlineData("org.freedesktop.portal.Error.NotAllowed", false)]
    public void BrakOdpowiedziNieMowiCzyBlokadaPowstala(string errorName, bool unknown)
    {
        Assert.Equal(unknown, DisplayInhibitRules.IsOutcomeUnknown(errorName));
    }

    // ---------- Linux: decyzje na atrapie szyny ----------

    /// <summary>
    /// Portal przyjmuje wywołanie po upływie czasu: blokada powstaje, a jej uchwytu nikt nie
    /// odczyta. Zapasowy ScreenSaver idzie więc nowym połączeniem, a porzucenie starego zdejmuje
    /// spóźnioną blokadę portalu.
    /// </summary>
    [Fact]
    public void SpoznionyPortalNieZostawiaBlokadyPoZwolnieniu()
    {
        var bus = new FakeBus { Portal = Service.Slow };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();

        Assert.True(wakeLock.IsHeld);
        var held = Assert.Single(bus.Inhibitions);
        Assert.Equal(FakeBus.ScreenSaverKind, held.Kind);

        wakeLock.Release();

        Assert.Empty(bus.Inhibitions);
    }

    /// <summary>Odpowiedź portalu bez uchwytu też zostawia blokadę, której nie da się zamknąć.</summary>
    [Fact]
    public void OdpowiedzPortaluBezUchwytuNieZostawiaBlokady()
    {
        var bus = new FakeBus { Portal = Service.Garbled };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        Assert.True(wakeLock.IsHeld);
        Assert.Single(bus.Inhibitions);

        wakeLock.Release();
        Assert.Empty(bus.Inhibitions);
    }

    [Theory]
    [InlineData(Service.Slow)]
    [InlineData(Service.Garbled)]
    public void NieudanyScreenSaverNieZostawiaBlokady(Service screenSaver)
    {
        var bus = new FakeBus { Portal = Service.Missing, ScreenSaver = screenSaver };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();

        Assert.False(wakeLock.IsHeld);
        Assert.Empty(bus.Inhibitions);
    }

    /// <summary>
    /// Nieudane zwolnienie gubi uchwyt albo ciasteczko, a blokada mogła zostać. Zdejmuje ją
    /// porzucenie połączenia; następne przejęcie łączy się od nowa.
    /// </summary>
    [Theory]
    [InlineData(true, Service.Slow)]
    [InlineData(true, Service.Refuses)]
    [InlineData(false, Service.Slow)]
    [InlineData(false, Service.Refuses)]
    public void NieudaneZwolnienieNieZostawiaBlokady(bool portal, Service release)
    {
        var bus = new FakeBus
        {
            Portal = portal ? Service.Works : Service.Missing,
            PortalClose = release,
            ScreenSaverRelease = release,
        };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        Assert.Single(bus.Inhibitions);

        wakeLock.Release();
        Assert.False(wakeLock.IsHeld);
        Assert.Empty(bus.Inhibitions);

        wakeLock.Acquire();
        Assert.True(wakeLock.IsHeld);
        Assert.Equal(2, bus.Connections);
    }

    /// <summary>Jednoznaczna odmowa niczego nie zostawia, więc połączenie zostaje.</summary>
    [Theory]
    [InlineData(Service.Missing)]
    [InlineData(Service.Refuses)]
    public void OdmowaPortaluPrzechodziDoScreenSaverBezNowegoPolaczenia(Service portal)
    {
        var bus = new FakeBus { Portal = portal };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        Assert.True(wakeLock.IsHeld);
        Assert.Equal(FakeBus.ScreenSaverKind, Assert.Single(bus.Inhibitions).Kind);

        wakeLock.Release();
        wakeLock.Acquire();
        wakeLock.Release();

        Assert.Empty(bus.Inhibitions);
        Assert.Equal(1, bus.Connections);
    }

    /// <summary>
    /// Poza piaskownicą pierwszy jest ScreenSaver: backend portalu w GNOME odmawia programowi bez
    /// identyfikatora bez sygnału Response, więc portalowi nie da się tam ufać.
    /// </summary>
    [Fact]
    public void PozaPiaskownicaPierwszyJestScreenSaver()
    {
        var bus = new FakeBus();
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time, sandboxed: false);

        wakeLock.Acquire();

        Assert.True(wakeLock.IsHeld);
        Assert.Equal(FakeBus.ScreenSaverKind, Assert.Single(bus.Inhibitions).Kind);
        Assert.DoesNotContain(bus.Calls, call => call.StartsWith("org.freedesktop.portal.", StringComparison.Ordinal));
        Assert.Empty(bus.ResponseWaits);

        wakeLock.Release();
        Assert.Empty(bus.Inhibitions);
    }

    /// <summary>Pulpit bez ScreenSaver poza piaskownicą: blokadę zakłada portal.</summary>
    [Fact]
    public void PozaPiaskownicaBezScreenSaverBlokadeZakladaPortal()
    {
        var bus = new FakeBus { ScreenSaver = Service.Missing };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time, sandboxed: false);

        wakeLock.Acquire();

        Assert.True(wakeLock.IsHeld);
        Assert.Equal(FakeBus.PortalKind, Assert.Single(bus.Inhibitions).Kind);

        wakeLock.Release();
        Assert.Empty(bus.Inhibitions);
    }

    /// <summary>Odmowa ScreenSaver (inna niż brak usługi) poza piaskownicą nie przerzuca do portalu.</summary>
    [Fact]
    public void PozaPiaskownicaOdmowaScreenSaverNieZakladaPortalu()
    {
        var bus = new FakeBus { ScreenSaver = Service.Refuses };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time, sandboxed: false);

        wakeLock.Acquire();

        Assert.False(wakeLock.IsHeld);
        Assert.Empty(bus.Inhibitions);
        Assert.DoesNotContain(bus.Calls, call => call.StartsWith("org.freedesktop.portal.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, "/snap/cewka/1", true)]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    public void PiaskownicaRozpoznawanaPoPlikuFlatpakAlboZmiennejSnap(bool flatpakInfo, string? snap, bool sandboxed)
    {
        var result = DisplayInhibitRules.IsSandboxed(
            path => flatpakInfo && path == "/.flatpak-info",
            name => name == "SNAP" ? snap : null);

        Assert.Equal(sandboxed, result);
    }

    [Fact]
    public void BrakPortaluKierujeKolejneBlokadyProstoDoScreenSaver()
    {
        var bus = new FakeBus { Portal = Service.Missing };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        wakeLock.Release();
        wakeLock.Acquire();

        Assert.Equal(1, bus.Calls.Count(call => call == "org.freedesktop.portal.Inhibit.Inhibit"));
    }

    [Fact]
    public void DzialajacyPortalNieAngazujeScreenSaver()
    {
        var bus = new FakeBus();
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        Assert.True(wakeLock.IsHeld);
        Assert.Equal(FakeBus.PortalKind, Assert.Single(bus.Inhibitions).Kind);

        wakeLock.Release();
        Assert.Empty(bus.Inhibitions);
        Assert.DoesNotContain(bus.Calls, call => call.StartsWith("org.freedesktop.ScreenSaver.", StringComparison.Ordinal));
        Assert.Equal(1, bus.Connections);
    }

    /// <summary>
    /// W piaskownicy portal daje uchwyt, ale backend kończy się błędem i portal zgłasza to
    /// sygnałem Response. Blokadę przejmuje ScreenSaver, a kolejne przejęcia już portalu nie pytają.
    /// </summary>
    [Theory]
    [InlineData(2u)]
    [InlineData(1u)]
    public void OdmowaBackenduPortaluPrzechodziDoScreenSaver(uint response)
    {
        var bus = new FakeBus { PortalResponse = response };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();

        Assert.True(wakeLock.IsHeld);
        Assert.Equal(FakeBus.ScreenSaverKind, Assert.Single(bus.Inhibitions).Kind);

        wakeLock.Release();
        Assert.Empty(bus.Inhibitions);

        wakeLock.Acquire();
        Assert.Equal(FakeBus.ScreenSaverKind, Assert.Single(bus.Inhibitions).Kind);
        Assert.Equal(1, bus.Calls.Count(call => call == "org.freedesktop.portal.Inhibit.Inhibit"));
        Assert.Equal(1, bus.Connections);
    }

    /// <summary>Gdy ScreenSaver też zawiedzie, następne przejęcie znów zaczyna od portalu.</summary>
    [Fact]
    public void OdmowaBackenduBezScreenSaverNieWylaczaPortalu()
    {
        var bus = new FakeBus { PortalResponse = 2, ScreenSaver = Service.Missing };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        Assert.False(wakeLock.IsHeld);

        wakeLock.Acquire();
        Assert.Equal(2, bus.Calls.Count(call => call == "org.freedesktop.portal.Inhibit.Inhibit"));
        Assert.Empty(bus.Inhibitions);
    }

    /// <summary>Bez sygnału albo z kodem 0 blokadę trzyma portal; na sygnał czeka się raz.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    public void PortalBezOdmowyTrzymaBlokade(uint? response)
    {
        var bus = new FakeBus { PortalResponse = response };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();

        Assert.True(wakeLock.IsHeld);
        var held = Assert.Single(bus.Inhibitions);
        Assert.Equal(FakeBus.PortalKind, held.Kind);
        Assert.Equal([(held.Id, DisplayInhibitRules.PortalResponseMilliseconds)], bus.ResponseWaits);
    }

    /// <summary>
    /// Backend odmawia dopiero po czasie oczekiwania przy przejęciu (wolny backend, jego limit
    /// wywołania). Blokadę przejmuje wtedy ScreenSaver, a kolejne przejęcia już portalu nie pytają.
    /// </summary>
    [Fact]
    public void SpoznionaOdmowaPortaluPrzekazujeBlokadeScreenSaver()
    {
        var bus = new FakeBus();
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        Assert.Equal(FakeBus.PortalKind, Assert.Single(bus.Inhibitions).Kind);

        _time.Tick();
        Assert.Equal(FakeBus.PortalKind, Assert.Single(bus.Inhibitions).Kind);

        bus.RespondLate(2);
        _time.Tick();

        Assert.True(wakeLock.IsHeld);
        Assert.Equal(FakeBus.ScreenSaverKind, Assert.Single(bus.Inhibitions).Kind);
        Assert.Empty(_time.Running);

        wakeLock.Release();
        Assert.Empty(bus.Inhibitions);

        wakeLock.Acquire();
        Assert.Equal(FakeBus.ScreenSaverKind, Assert.Single(bus.Inhibitions).Kind);
        Assert.Equal(1, bus.Calls.Count(call => call == "org.freedesktop.portal.Inhibit.Inhibit"));
    }

    /// <summary>Bez ScreenSaver spóźniona odmowa zostawia scenę bez blokady, a następne przejęcie znów pyta portal.</summary>
    [Fact]
    public void SpoznionaOdmowaBezScreenSaverOddajeBlokade()
    {
        var bus = new FakeBus { ScreenSaver = Service.Missing };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        bus.RespondLate(2);
        _time.Tick();

        Assert.False(wakeLock.IsHeld);
        Assert.Empty(bus.Inhibitions);
        Assert.Empty(_time.Running);

        wakeLock.Acquire();
        Assert.True(wakeLock.IsHeld);
        Assert.Equal(2, bus.Calls.Count(call => call == "org.freedesktop.portal.Inhibit.Inhibit"));
    }

    /// <summary>Potwierdzenie z kodem 0 albo upływ czasu nasłuchu kończy nasłuch, a blokada zostaje.</summary>
    [Fact]
    public void PotwierdzenieAlboKoniecNasluchuZostawiaBlokadePortalu()
    {
        var bus = new FakeBus();
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        bus.RespondLate(0);
        _time.Tick();

        Assert.True(wakeLock.IsHeld);
        Assert.Equal(FakeBus.PortalKind, Assert.Single(bus.Inhibitions).Kind);
        Assert.Empty(_time.Running);

        wakeLock.Release();
        wakeLock.Acquire();
        Assert.Single(_time.Running);

        for (var i = 0; i < DisplayInhibitRules.PortalWatchTicks; i++) _time.Tick();

        Assert.True(wakeLock.IsHeld);
        Assert.Equal(FakeBus.PortalKind, Assert.Single(bus.Inhibitions).Kind);
        Assert.Empty(_time.Running);
    }

    /// <summary>
    /// Zwolnienie kończy nasłuch, więc odliczenie, które zdążyło się zacząć, nic już nie zmienia;
    /// po ponownym przejęciu nasłuch dotyczy tylko nowego żądania.
    /// </summary>
    [Fact]
    public void ZwolnienieKonczyNasluchPortalu()
    {
        var bus = new FakeBus();
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        var first = Assert.Single(_time.Running);

        wakeLock.Release();
        Assert.Empty(_time.Running);

        first.FireLate();
        Assert.DoesNotContain(bus.Calls, call => call.StartsWith("org.freedesktop.ScreenSaver.", StringComparison.Ordinal));

        wakeLock.Acquire();
        bus.RespondLate(2);
        first.FireLate();
        Assert.DoesNotContain(bus.Calls, call => call.StartsWith("org.freedesktop.ScreenSaver.", StringComparison.Ordinal));

        _time.Tick();
        Assert.Equal(FakeBus.ScreenSaverKind, Assert.Single(bus.Inhibitions).Kind);
        Assert.True(wakeLock.IsHeld);
    }

    [Fact]
    public void ZamkniecieKonczyNasluchPortalu()
    {
        var bus = new FakeBus();
        var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        wakeLock.Dispose();

        Assert.Empty(_time.Running);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0u, true)]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(uint.MaxValue, false)]
    public void PortalTrzymaBlokadeTylkoBezOdmowy(uint? response, bool held)
    {
        Assert.Equal(held, DisplayInhibitRules.IsPortalHeld(response));
    }

    [Fact]
    public void SygnalyPortaluSaDopasowywaneDoZadanPolaczenia()
    {
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_42", DisplayInhibitRules.PortalRequestPrefix(":1.42"));
        Assert.Equal(
            "type='signal',interface='org.freedesktop.portal.Request',member='Response',"
            + "path_namespace='/org/freedesktop/portal/desktop/request/1_42'",
            DisplayInhibitRules.PortalResponseRule(":1.42"));
    }

    [Fact]
    public void ZamkniecieZamykaPolaczenie()
    {
        var bus = new FakeBus();
        var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        wakeLock.Dispose();

        Assert.False(bus.IsConnected);
        Assert.Empty(bus.Inhibitions);
    }

    [Fact]
    public void BezSzynySesjiBlokadaNieProbujePonownie()
    {
        var bus = new FakeBus { BusMissing = true };
        using var wakeLock = new LinuxDisplayWakeLock(Reason, bus, _time);

        wakeLock.Acquire();
        wakeLock.Acquire();

        Assert.False(wakeLock.IsHeld);
        Assert.Equal(1, bus.ConnectAttempts);
    }

    public enum Service
    {
        Works,

        /// <summary>Usługi nie ma na szynie.</summary>
        Missing,

        /// <summary>Odmawia błędem i niczego nie zmienia.</summary>
        Refuses,

        /// <summary>Robi swoje, ale odpowiedź przychodzi po czasie, więc wywołujący dostaje NoReply.</summary>
        Slow,

        /// <summary>Robi swoje, a odpowiedź nie ma oczekiwanego argumentu.</summary>
        Garbled,
    }

    /// <summary>
    /// Atrapa szyny z portalem i ScreenSaver: pamięta, które połączenie założyło którą blokadę,
    /// i jak prawdziwe usługi zdejmuje blokady połączenia, które się rozłączyło.
    /// </summary>
    private sealed class FakeBus : IInhibitTransport
    {
        public const string PortalKind = "portal";
        public const string ScreenSaverKind = "screensaver";

        private const string NoReply = "org.freedesktop.DBus.Error.NoReply";

        private readonly HashSet<string> _answered = [];
        private readonly Dictionary<string, uint> _lateResponses = [];

        private int _connection;
        private uint _nextId;

        public Service Portal { get; init; }
        public Service PortalClose { get; init; }
        public Service ScreenSaver { get; init; }
        public Service ScreenSaverRelease { get; init; }
        public bool BusMissing { get; init; }

        /// <summary>Kod sygnału Response po uchwycie z portalu; inny niż 0 to odmowa backendu.</summary>
        public uint? PortalResponse { get; init; }

        public List<(int Connection, string Kind, string Id)> Inhibitions { get; } = [];
        public List<string> Calls { get; } = [];
        public List<(string Handle, int Timeout)> ResponseWaits { get; } = [];
        public int Connections { get; private set; }
        public int ConnectAttempts { get; private set; }

        public bool IsConnected => _connection != 0;

        public bool Connect()
        {
            ConnectAttempts++;
            if (BusMissing) return false;
            if (_connection == 0) _connection = ++Connections;
            return true;
        }

        public void Drop()
        {
            Inhibitions.RemoveAll(inhibition => inhibition.Connection == _connection);
            _connection = 0;
        }

        public DBusReply Call(DBusCall call)
        {
            Assert.True(IsConnected, "Wywołanie bez połączenia.");
            Calls.Add($"{call.Interface}.{call.Member}");

            return (call.Interface, call.Member) switch
            {
                (DisplayInhibitRules.PortalInterface, "Inhibit") => Inhibit(Portal, PortalKind),
                (DisplayInhibitRules.RequestInterface, "Close") => Release(PortalClose, PortalKind, call.Path),
                (DisplayInhibitRules.ScreenSaverInterface, "Inhibit") => Inhibit(ScreenSaver, ScreenSaverKind),
                (DisplayInhibitRules.ScreenSaverInterface, "UnInhibit") => Release(
                    ScreenSaverRelease, ScreenSaverKind, ((DBusUInt32)call.Arguments[0]).Value.ToString()),
                _ => throw new InvalidOperationException($"Nieoczekiwane wywołanie {call.Interface}.{call.Member}."),
            };
        }

        private DBusReply Inhibit(Service behavior, string kind)
        {
            switch (behavior)
            {
                case Service.Missing:
                    return DBusReply.Error("org.freedesktop.DBus.Error.ServiceUnknown", "brak usługi");
                case Service.Refuses:
                    return DBusReply.Error("org.freedesktop.DBus.Error.AccessDenied", "odmowa");
            }

            var number = ++_nextId;
            var id = kind == PortalKind ? $"/org/freedesktop/portal/desktop/request/1_{_connection}/t{number}" : number.ToString();

            // Odmowa backendu: portal zamyka żądanie, blokady nie ma.
            if (kind != PortalKind || DisplayInhibitRules.IsPortalHeld(PortalResponse))
                Inhibitions.Add((_connection, kind, id));

            return behavior switch
            {
                Service.Slow => DBusReply.Error(NoReply, "brak odpowiedzi"),
                Service.Garbled => new DBusReply(null),
                _ => new DBusReply(kind == PortalKind ? id : number),
            };
        }

        public uint? WaitForResponse(string handle, int timeoutMilliseconds)
        {
            Assert.True(IsConnected, "Oczekiwanie bez połączenia.");
            ResponseWaits.Add((handle, timeoutMilliseconds));

            if (_lateResponses.Remove(handle, out var late)) return late;

            // Sygnał przychodzi raz; kolejne oczekiwania na to samo żądanie go nie dostają.
            return _answered.Add(handle) ? PortalResponse : null;
        }

        /// <summary>
        /// Backend odpowiada po czasie oczekiwania przy przejęciu. Kod inny niż 0 to odmowa:
        /// portal zamyka żądanie, blokady nie ma.
        /// </summary>
        public void RespondLate(uint code)
        {
            foreach (var portal in Inhibitions.Where(inhibition => inhibition.Kind == PortalKind).ToList())
            {
                _lateResponses[portal.Id] = code;
                if (code != 0) Inhibitions.Remove(portal);
            }
        }

        /// <summary>Spóźnione zwolnienie może nie dojść wcale, więc atrapa zostawia wtedy blokadę.</summary>
        private DBusReply Release(Service behavior, string kind, string id)
        {
            switch (behavior)
            {
                case Service.Slow:
                    return DBusReply.Error(NoReply, "brak odpowiedzi");
                case Service.Refuses or Service.Missing:
                    return DBusReply.Error("org.freedesktop.DBus.Error.AccessDenied", "odmowa");
            }

            var removed = Inhibitions.RemoveAll(inhibition =>
                inhibition.Connection == _connection && inhibition.Kind == kind && inhibition.Id == id);
            Assert.Equal(1, removed);
            return new DBusReply(null);
        }
    }

    /// <summary>Zegar, którego liczniki odlicza test.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public IEnumerable<ManualTimer> Running => _timers.Where(timer => !timer.Disposed);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }

        /// <summary>Jedno odliczenie każdego działającego licznika.</summary>
        public void Tick()
        {
            foreach (var timer in Running.ToList()) timer.FireLate();
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }

        /// <summary>Wywołanie także po zamknięciu licznika, jak odliczenie, które zdążyło się zacząć.</summary>
        public void FireLate() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // ---------- Linux: libdbus i szyna ----------

    /// <summary>Wiadomość zbudowana przez libdbus z opisu ma ten sam adres i podpis; szyna niepotrzebna.</summary>
    [LinuxFact]
    public void OpisWywolaniaStajeSieWiadomosciaLibdbus()
    {
        if (!OperatingSystem.IsLinux()) return;

        foreach (var call in new[]
                 {
                     DisplayInhibitRules.PortalInhibit(Reason),
                     DisplayInhibitRules.PortalRelease("/org/freedesktop/portal/desktop/request/1_42/t"),
                     DisplayInhibitRules.ScreenSaverInhibit(Reason),
                     DisplayInhibitRules.ScreenSaverRelease(42),
                 })
        {
            var message = call.ToMessage();
            Assert.NotEqual(nint.Zero, message);

            try
            {
                Assert.Equal(call.Destination, DBus.ReadString(DBus.dbus_message_get_destination(message)));
                Assert.Equal(call.Path, DBus.ReadString(DBus.dbus_message_get_path(message)));
                Assert.Equal(call.Interface, DBus.ReadString(DBus.dbus_message_get_interface(message)));
                Assert.Equal(call.Member, DBus.ReadString(DBus.dbus_message_get_member(message)));
                Assert.Equal(call.Signature, DBus.ReadString(DBus.dbus_message_get_signature(message)));
            }
            finally
            {
                DBus.dbus_message_unref(message);
            }
        }
    }

    /// <summary>
    /// Sygnał Response żądania z przestrzeni ścieżek połączenia dociera przez regułę dopasowania;
    /// sygnał innego żądania nie kończy oczekiwania.
    /// </summary>
    [LinuxSessionBusFact]
    public void SygnalResponseDocieraDoPolaczeniaBlokady()
    {
        if (!OperatingSystem.IsLinux()) return;

        var transport = new DBusInhibitTransport();
        try
        {
            Assert.True(transport.Connect());
            var handle = DisplayInhibitRules.PortalRequestPrefix(transport.UniqueName!) + "/cewka_test";

            Assert.Null(transport.WaitForResponse(handle, 50));

            EmitResponse(handle + "_inne", 1);
            EmitResponse(handle, 2);

            Assert.Equal(2u, transport.WaitForResponse(handle, 5000));
            Assert.Null(transport.WaitForResponse(handle, 50));
        }
        finally
        {
            transport.Drop();
        }
    }

    /// <summary>Sprawdzenie bez czekania odczytuje także sygnał, który dopiero doszedł do gniazda.</summary>
    [LinuxSessionBusFact]
    public void SprawdzenieBezCzekaniaWidziSygnalZGniazda()
    {
        if (!OperatingSystem.IsLinux()) return;

        var transport = new DBusInhibitTransport();
        try
        {
            Assert.True(transport.Connect());
            var handle = DisplayInhibitRules.PortalRequestPrefix(transport.UniqueName!) + "/cewka_test_pozny";

            EmitResponse(handle, 2);

            uint? response = null;
            var deadline = Environment.TickCount64 + 5000;
            while (response is null && Environment.TickCount64 < deadline)
            {
                response = transport.WaitForResponse(handle, 0);
                if (response is null) Thread.Sleep(10);
            }

            Assert.Equal(2u, response);
        }
        finally
        {
            transport.Drop();
        }
    }

    /// <summary>Rozgłasza sygnał Response z osobnego połączenia, jak robi to portal.</summary>
    [SupportedOSPlatform("linux")]
    private static unsafe void EmitResponse(string path, uint code)
    {
        DBus.Error error;
        DBus.dbus_error_init(&error);

        var connection = DBus.dbus_bus_get_private(DBus.BusSession, &error);
        Assert.NotEqual(nint.Zero, connection);
        DBus.dbus_connection_set_exit_on_disconnect(connection, 0);

        try
        {
            using var signalPath = new DBus.Utf8(path);
            using var iface = new DBus.Utf8(DisplayInhibitRules.RequestInterface);
            using var member = new DBus.Utf8(DisplayInhibitRules.ResponseMember);

            var signal = DBus.dbus_message_new_signal(signalPath.Pointer, iface.Pointer, member.Pointer);
            Assert.NotEqual(nint.Zero, signal);

            DBus.MessageIter iter;
            DBus.dbus_message_iter_init_append(signal, &iter);
            DBus.dbus_message_iter_append_basic(&iter, DBus.TypeUInt32, &code);

            DBus.dbus_connection_send(connection, signal, null);
            DBus.dbus_connection_flush(connection);
            DBus.dbus_message_unref(signal);
        }
        finally
        {
            DBus.dbus_connection_close(connection);
            DBus.dbus_connection_unref(connection);
        }
    }

    /// <summary>
    /// Na prawdziwej szynie przejęcie i zwolnienie nie rzucają. Czy blokada zostanie przyjęta,
    /// zależy od pulpitu (portal albo ScreenSaver), więc sprawdzany jest tylko jej koniec.
    /// </summary>
    [LinuxSessionBusFact]
    public void BlokadaNaSzynieSesjiPrzechodziPelenCykl()
    {
        if (!OperatingSystem.IsLinux()) return;

        var wakeLock = new LinuxDisplayWakeLock(Reason);

        wakeLock.Acquire();
        wakeLock.Acquire();
        wakeLock.Release();
        Assert.False(wakeLock.IsHeld);

        wakeLock.Acquire();
        wakeLock.Dispose();
        Assert.False(wakeLock.IsHeld);

        wakeLock.Acquire();
        Assert.False(wakeLock.IsHeld);
    }

    // ---------- Linux: konstruktor publiczny ----------

    /// <summary>
    /// Zmienna środowiskowa jest wspólna dla całego procesu, więc test, który ją zmienia,
    /// idzie w kolekcji bez równoległości.
    /// </summary>
    [Collection(UiCollection.Name)]
    public sealed class PublicConstructor
    {
        /// <summary>Konstruktor publiczny rozpoznaje piaskownicę ze środowiska procesu.</summary>
        [Fact]
        public void KonstruktorPublicznyRozpoznajePiaskowniceZeSrodowiska()
        {
            var snap = Environment.GetEnvironmentVariable("SNAP");

            // Konstruktor tylko tworzy transport, a libdbus dotyka dopiero połączenie, więc test
            // działa w każdym systemie.
#pragma warning disable CA1416
            try
            {
                Environment.SetEnvironmentVariable("SNAP", "/snap/cewka/1");
                using (var wakeLock = new LinuxDisplayWakeLock(Reason))
                {
                    Assert.True(wakeLock.IsSandboxed);
                }

                Environment.SetEnvironmentVariable("SNAP", null);
                using (var wakeLock = new LinuxDisplayWakeLock(Reason))
                {
                    Assert.Equal(File.Exists("/.flatpak-info"), wakeLock.IsSandboxed);
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("SNAP", snap);
            }
#pragma warning restore CA1416
        }
    }
}
