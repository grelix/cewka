using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Cewka.Platform;

/// <summary>What the operating system currently knows about the application's file types.</summary>
public enum AssociationState
{
    /// <summary>The system offers no way to register, or registration failed to be read.</summary>
    Unsupported,

    /// <summary>Nothing registered.</summary>
    Absent,

    /// <summary>Registered, but pointing at a different copy of the program than this one.</summary>
    Stale,

    /// <summary>Registered and pointing here.</summary>
    Current,
}

/// <summary>
/// Registers the application as a program able to open audio files.
///
/// <para><b>Tylko dla bieżącego użytkownika.</b> Wszystkie wpisy trafiają pod
/// <c>HKEY_CURRENT_USER</c>, więc skojarzenie nie wymaga uprawnień administratora i nie zmienia
/// niczego innym użytkownikom komputera. Program nie przejmuje przy tym żadnego rozszerzenia na
/// siłę: dopisuje się do listy „Otwórz za pomocą", a wpis w rejestrze programów sprawia, że można
/// go wskazać w ustawieniach domyślnych aplikacji. Wybór należy do użytkownika, nie do instalacji.</para>
///
/// <para><b>Dlaczego rejestr obsługiwany bezpośrednio.</b> Klasa <c>Microsoft.Win32.Registry</c>
/// jest dostępna dopiero dla platform docelowych z sufiksem <c>-windows</c>. Przejście na taką
/// platformę wciągnęłoby cały pakiet referencyjny Windows SDK do projektu, który poza tym jednym
/// miejscem jest w pełni wieloplatformowy — trzy funkcje z <c>advapi32</c> są tańsze.</para>
/// </summary>
public static unsafe class FileAssociations
{
    /// <summary>Identifier of the file class, as it appears in the registry.</summary>
    private const string ProgId = "Cewka.Audio";

    private const string ClassKey = $@"Software\Classes\{ProgId}";
    private const string CapabilitiesKey = @"Software\Cewka\Capabilities";
    private const string RegisteredApplicationsKey = @"Software\RegisteredApplications";
    private const string ApplicationName = "Cewka";

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>Path used in the registered command; also what a stale entry is compared against.</summary>
    public static string? ExecutablePath => Environment.ProcessPath;

    /// <summary>Reads back what is registered, so the settings window can state it plainly.</summary>
    public static AssociationState Query()
    {
        if (!OperatingSystem.IsWindows()) return AssociationState.Unsupported;

        var registered = Native.ReadString($@"{ClassKey}\shell\open\command");
        if (registered is null) return AssociationState.Absent;

        return string.Equals(registered, BuildCommand(), StringComparison.OrdinalIgnoreCase)
            ? AssociationState.Current
            : AssociationState.Stale;
    }

    /// <summary>
    /// Adds the application to the list of programs able to open the given extensions.
    /// </summary>
    /// <param name="extensions">Extensions including the leading dot, for example <c>.mp3</c>.</param>
    public static void Register(IReadOnlyList<string> extensions)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        var executable = ExecutablePath
                         ?? throw new InvalidOperationException("Nie udało się ustalić ścieżki programu.");

        Native.WriteString(ClassKey, null, "Plik dźwiękowy");
        Native.WriteString($@"{ClassKey}\DefaultIcon", null, $"{executable},0");
        Native.WriteString($@"{ClassKey}\shell\open", "FriendlyAppName", ApplicationName);
        Native.WriteString($@"{ClassKey}\shell\open\command", null, BuildCommand());

        // Zestaw obsługiwanych rozszerzeń w dwóch miejscach: lista „Otwórz za pomocą" działa od
        // razu, a deklaracja możliwości pokazuje program w ustawieniach domyślnych aplikacji.
        foreach (var extension in extensions)
        {
            Native.WriteNone($@"Software\Classes\{extension}\OpenWithProgids", ProgId);
            Native.WriteString($@"{CapabilitiesKey}\FileAssociations", extension, ProgId);
        }

        Native.WriteString(CapabilitiesKey, "ApplicationName", ApplicationName);
        Native.WriteString(CapabilitiesKey, "ApplicationDescription",
            "Minimalistyczny odtwarzacz plików lokalnych.");
        Native.WriteString(CapabilitiesKey, "ApplicationIcon", $"{executable},0");
        Native.WriteString(RegisteredApplicationsKey, ApplicationName, CapabilitiesKey);

        Native.NotifyShell();
    }

    /// <summary>Removes everything <see cref="Register"/> wrote.</summary>
    public static void Unregister(IReadOnlyList<string> extensions)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();

        foreach (var extension in extensions)
            Native.DeleteValue($@"Software\Classes\{extension}\OpenWithProgids", ProgId);

        Native.DeleteTree(ClassKey);
        Native.DeleteTree(@"Software\Cewka");
        Native.DeleteValue(RegisteredApplicationsKey, ApplicationName);

        Native.NotifyShell();
    }

    /// <summary>
    /// The quoting matters: without the inner quotation marks a path containing a space arrives
    /// at the program split into several arguments.
    /// </summary>
    private static string BuildCommand() => $"\"{ExecutablePath}\" \"%1\"";

    [SupportedOSPlatform("windows")]
    private static class Native
    {
        private static readonly nint CurrentUser = unchecked((nint)0x80000001);

        private const uint TypeNone = 0;
        private const uint TypeString = 1;
        private const uint RestrictString = 0x00000002;
        private const int Success = 0;

        /// <summary>Tells the shell that file associations changed, so menus pick it up at once.</summary>
        public static void NotifyShell() => SHChangeNotify(0x08000000, 0x0000, nint.Zero, nint.Zero);

        public static void WriteString(string subKey, string? name, string value)
        {
            fixed (char* data = value)
            {
                // Rozmiar w bajtach, razem ze znakiem kończącym — inaczej odczyt zwraca napis
                // bez zakończenia i sklejony z tym, co leży dalej.
                var bytes = (uint)((value.Length + 1) * sizeof(char));
                Check(RegSetKeyValueW(CurrentUser, subKey, name, TypeString, data, bytes), subKey);
            }
        }

        /// <summary>Writes a valueless entry, the form the shell expects on an OpenWithProgids list.</summary>
        public static void WriteNone(string subKey, string name) =>
            Check(RegSetKeyValueW(CurrentUser, subKey, name, TypeNone, null, 0), subKey);

        public static string? ReadString(string subKey)
        {
            var length = 0u;
            if (RegGetValueW(CurrentUser, subKey, null, RestrictString, out _, null, ref length) != Success)
                return null;

            var buffer = new char[length / sizeof(char) + 1];

            fixed (char* data = buffer)
            {
                if (RegGetValueW(CurrentUser, subKey, null, RestrictString, out _, data, ref length) != Success)
                    return null;
            }

            return new string(buffer, 0, (int)Math.Max(0, length / sizeof(char) - 1));
        }

        public static void DeleteValue(string subKey, string name) =>
            RegDeleteKeyValueW(CurrentUser, subKey, name);

        public static void DeleteTree(string subKey) => RegDeleteTreeW(CurrentUser, subKey);

        private static void Check(int result, string subKey)
        {
            if (result != Success)
                throw new InvalidOperationException(
                    $"Zapis do rejestru nie powiódł się ({subKey}, kod {result}).");
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegSetKeyValueW(
            nint key, string subKey, string? valueName, uint type, void* data, uint dataBytes);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegGetValueW(
            nint key, string subKey, string? valueName, uint flags,
            out uint type, void* data, ref uint dataBytes);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegDeleteKeyValueW(nint key, string subKey, string valueName);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegDeleteTreeW(nint key, string subKey);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
    }
}
