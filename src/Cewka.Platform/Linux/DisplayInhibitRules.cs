namespace Cewka.Platform.Linux;

/// <summary>
/// Wywołania blokujące wygaszanie ekranu i wybór drogi, wydzielone z obsługi szyny, żeby dało
/// się je sprawdzić bez niej.
///
/// <para>W piaskownicy (Flatpak, Snap) pierwszy jest portal <c>org.freedesktop.portal.Inhibit</c>,
/// bo <c>org.freedesktop.ScreenSaver</c> bywa tam niedostępny. Poza nią pierwszy jest ScreenSaver:
/// podaje nazwę programu wprost, a backend portalu w GNOME odmawia programowi bez identyfikatora
/// (uruchomionemu spoza pliku .desktop) tylko wpisem w dzienniku, bez sygnału <c>Response</c>.
/// Lżejsze menedżery okien mają często tylko jedną z tych usług, więc druga jest zapasowa.</para>
/// </summary>
internal static class DisplayInhibitRules
{
    internal const string PortalDestination = "org.freedesktop.portal.Desktop";
    internal const string PortalPath = "/org/freedesktop/portal/desktop";
    internal const string PortalInterface = "org.freedesktop.portal.Inhibit";
    internal const string RequestInterface = "org.freedesktop.portal.Request";

    internal const string ScreenSaverDestination = "org.freedesktop.ScreenSaver";
    internal const string ScreenSaverPath = "/org/freedesktop/ScreenSaver";
    internal const string ScreenSaverInterface = "org.freedesktop.ScreenSaver";

    internal const string ApplicationName = "Cewka";

    internal const string ErrorNoMemory = "org.freedesktop.DBus.Error.NoMemory";

    /// <summary>Flaga <c>Idle</c> portalu: bez wygaszania i usypiania z powodu bezczynności.</summary>
    internal const uint InhibitIdle = 8;

    internal const string ResponseMember = "Response";

    /// <summary>
    /// Jak długo czekać na sygnał <c>Response</c> po uchwycie z portalu. Portal odpowiada
    /// uchwytem od razu, a blokadę zakłada backend; błąd backendu zgłasza tym sygnałem,
    /// a powodzenia wcale. Backend GNOME odmawia jednak programowi bez identyfikatora bez
    /// sygnału, więc jego brak potwierdza blokadę tylko w piaskownicy. Krótko, bo wątek
    /// interfejsu czeka przy każdym wejściu w scenę.
    /// </summary>
    internal const int PortalResponseMilliseconds = 200;

    /// <summary>Co ile po przejęciu sprawdzać, czy backend nie odmówił później.</summary>
    internal static readonly TimeSpan PortalWatchInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Ile sprawdzeń po przejęciu: wolny backend odpowiada po wielu sekundach, a portal zgłasza
    /// odmowę najpóźniej po limicie wywołania backendu (w GDBus 25 s).
    /// </summary>
    internal const int PortalWatchTicks = 30;

    /// <summary><c>Inhibit(s window, u flags, a{sv} options) → o handle</c>; bez okna, bo blokada dotyczy całej sesji.</summary>
    public static DBusCall PortalInhibit(string reason) => new(
        PortalDestination, PortalPath, PortalInterface, "Inhibit",
        [
            new DBusString(string.Empty),
            new DBusUInt32(InhibitIdle),
            new DBusStringOptions([new("reason", reason)]),
        ]);

    /// <summary>Blokadę z portalu zdejmuje zamknięcie żądania, którego ścieżkę zwrócił <c>Inhibit</c>.</summary>
    public static DBusCall PortalRelease(string handle) =>
        new(PortalDestination, handle, RequestInterface, "Close", []);

    /// <summary><c>Inhibit(s application, s reason) → u cookie</c>.</summary>
    public static DBusCall ScreenSaverInhibit(string reason) => new(
        ScreenSaverDestination, ScreenSaverPath, ScreenSaverInterface, "Inhibit",
        [new DBusString(ApplicationName), new DBusString(reason)]);

    public static DBusCall ScreenSaverRelease(uint cookie) => new(
        ScreenSaverDestination, ScreenSaverPath, ScreenSaverInterface, "UnInhibit",
        [new DBusUInt32(cookie)]);

    /// <summary>
    /// Czy błąd portalu znaczy, że na tej szynie go nie ma i nie warto pytać ponownie. Brak
    /// odpowiedzi w czasie bywa zwykłym powolnym startem portalu, więc wtedy następna blokada
    /// znów zaczyna od niego; tę jedną przejmuje <c>ScreenSaver</c> na nowym połączeniu.
    /// </summary>
    public static bool IsPortalMissing(string errorName) => IsServiceMissing(errorName);

    /// <summary>Czy błąd znaczy, że usługi albo jej metody nie ma na tej szynie.</summary>
    public static bool IsServiceMissing(string errorName) =>
        errorName is "org.freedesktop.DBus.Error.ServiceUnknown"
            or "org.freedesktop.DBus.Error.NameHasNoOwner"
            or "org.freedesktop.DBus.Error.UnknownMethod"
            or "org.freedesktop.DBus.Error.UnknownInterface"
            or "org.freedesktop.DBus.Error.UnknownObject"
        || errorName.StartsWith("org.freedesktop.DBus.Error.Spawn.", StringComparison.Ordinal);

    /// <summary>Program działa w piaskownicy Flatpak albo Snap.</summary>
    public static bool IsSandboxed(Func<string, bool> fileExists, Func<string, string?> environment) =>
        fileExists("/.flatpak-info") || !string.IsNullOrEmpty(environment("SNAP"));

    /// <summary>
    /// Czy po tym błędzie nie wiadomo, czy usługa założyła albo zdjęła blokadę. Przekroczony
    /// czas nie cofa wysłanego wywołania: usługa może je jeszcze wykonać, a jej odpowiedzi nikt
    /// już nie odczyta.
    /// </summary>
    public static bool IsOutcomeUnknown(string errorName) =>
        errorName is ""
            or "org.freedesktop.DBus.Error.NoReply"
            or "org.freedesktop.DBus.Error.Timeout"
            or "org.freedesktop.DBus.Error.TimedOut"
            or "org.freedesktop.DBus.Error.Disconnected"
            or "org.freedesktop.DBus.Error.IOError"
            or ErrorNoMemory;

    /// <summary>
    /// Czy usługa mogła założyć blokadę, której wywołujący nie zna: odpowiedź przyszła, ale bez
    /// uchwytu, albo nie wiadomo, czy wywołanie zadziałało. Zdejmuje ją tylko rozłączenie
    /// połączenia, które ją założyło.
    /// </summary>
    public static bool MayHoldUnknownInhibit(DBusReply unrecognised) =>
        !unrecognised.IsError || IsOutcomeUnknown(unrecognised.ErrorName!);

    /// <summary>
    /// Czy portal trzyma blokadę po danym kodzie <c>Response</c>: bez sygnału albo z kodem 0
    /// tak, z każdym innym kodem backend odmówił, a portal zamknął już żądanie. Brak sygnału
    /// znaczy powodzenie tylko wtedy, gdy portal zna identyfikator programu, czyli w piaskownicy;
    /// dlatego poza nią portal jest wyłącznie drogą zapasową.
    /// </summary>
    public static bool IsPortalHeld(uint? response) => response is null or 0;

    /// <summary>
    /// Wspólny początek ścieżek żądań portalu dla połączenia o danej nazwie unikalnej:
    /// <c>:1.42</c> daje <c>/org/freedesktop/portal/desktop/request/1_42</c>.
    /// </summary>
    public static string PortalRequestPrefix(string uniqueName) =>
        $"{PortalPath}/request/{uniqueName.TrimStart(':').Replace('.', '_')}";

    /// <summary>Reguła, z którą szyna przekazuje połączeniu sygnały <c>Response</c> jego żądań.</summary>
    public static string PortalResponseRule(string uniqueName) =>
        $"type='signal',interface='{RequestInterface}',member='{ResponseMember}',"
        + $"path_namespace='{PortalRequestPrefix(uniqueName)}'";
}
