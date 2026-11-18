using System.Text.Json;
using Avalonia.Threading;
using Cewka.App.Models;
using Cewka.App.Radio;
using Cewka.Audio.Dsp;
using Cewka.Platform;

namespace Cewka.App.Services;

/// <summary>
/// Wczytuje i zapisuje <see cref="AppSettings"/>. Zapis jest odkładany, bo interfejs zmienia
/// ustawienia przy każdym ruchu suwaka, i niepodzielny, żeby awaria nie zostawiła urwanego pliku.
/// </summary>
public sealed class SettingsStore
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(700);

    private readonly string _path;
    private readonly DispatcherTimer _debounce;
    private bool _dirty;

    public SettingsStore() : this(AppPaths.SettingsFile)
    {
    }

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load(path);

        _debounce = new DispatcherTimer { Interval = SaveDelay };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_dirty) SaveNow();
        };
    }

    public AppSettings Current { get; private set; }

    /// <summary>
    /// Odczytuje ustawienie jednej kopii przed startem Avalonii, bo kopia oddająca pliki nie może
    /// budować okna.
    /// <para>
    /// Pułapka: pełnego magazynu nie da się tu utworzyć, bo ma zegar dyspozytora, a dyspozytora
    /// jeszcze nie ma.
    /// </para>
    /// <para>
    /// Plik jest tylko przeglądany w poszukiwaniu jednej właściwości. Pełny odczyt kosztował tu
    /// prawie 30 ms pierwszego użycia czytnika ustawień, zanim Avalonia w ogóle ruszyła; teraz
    /// wykonuje go <see cref="Preload"/> w tle.
    /// </para>
    /// </summary>
    public static bool ReadSingleInstanceFlag() => ReadSingleInstanceFlag(AppPaths.SettingsFile);

    internal static bool ReadSingleInstanceFlag(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppSettings().SingleInstance;

            var reader = new Utf8JsonReader(File.ReadAllBytes(path).AsSpan().TrimStart(Utf8Preamble), new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            var flag = new AppSettings().SingleInstance;

            // Do końca pliku: urwany albo zepsuty dalej oznacza ustawienia domyślne, tak jak przy pełnym odczycie.
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1
                    || !reader.ValueTextEquals("singleInstance"u8))
                    continue;

                reader.Read();
                flag = reader.TokenType switch
                {
                    JsonTokenType.True => true,
                    JsonTokenType.False => false,
                    _ => throw new JsonException("singleInstance nie jest wartością logiczną"),
                };
            }

            if (flag) return true;
        }
        catch
        {
            // Przeglądanie nie rozstrzygnęło (inne kodowanie, plik zablokowany albo zepsuty): niżej pełny odczyt.
        }

        // Wyłączoną jedną kopię i każdy przypadek wątpliwy rozstrzyga pełny odczyt, żeby flaga
        // mówiła to samo co magazyn: plik z poprawną flagą, ale odrzucony w całości (nieznana
        // wartość innego ustawienia), daje ustawienia domyślne, a program bez roli jednej kopii
        // działałby wtedy wbrew nim. Odczyt nie idzie na marne, bo przejmuje go magazyn.
        try
        {
            Preload(path);
            return PeekPreloaded(path) is not { ReadError: null, ParseError: null, Loaded.SingleInstance: false };
        }
        catch
        {
            return true;
        }
    }

    private static ReadOnlySpan<byte> Utf8Preamble => [0xEF, 0xBB, 0xBF];

    // ---------- Odczyt w tle ----------

    /// <summary>Wynik odczytu pliku bez skutków ubocznych: kopie i komunikaty robi dopiero magazyn.</summary>
    private sealed record Raw(bool Exists, AppSettings? Loaded, Exception? ReadError, Exception? ParseError);

    private static readonly object PreloadGate = new();
    private static string? _preloadedPath;
    private static Task<Raw>? _preloaded;

    /// <summary>
    /// Zaczyna czytać plik ustawień w tle. Wołane z <c>Program.Main</c> przed startem Avalonii:
    /// wątek główny czeka wtedy na kartę graficzną, a magazyn utworzony po tym czekaniu dostaje
    /// ustawienia gotowe. Wynik służy jednemu magazynowi; następny czyta plik sam.
    /// </summary>
    public static void Preload() => Preload(AppPaths.SettingsFile);

    internal static void Preload(string path)
    {
        lock (PreloadGate)
        {
            // Odczyt tego samego pliku już trwa albo czeka na magazyn: drugi niczego by nie dodał.
            if (_preloaded is not null && string.Equals(_preloadedPath, path, StringComparison.Ordinal)) return;

            _preloadedPath = path;
            _preloaded = Task.Run(() => Read(path));
        }
    }

    /// <summary>Wynik odczytu w tle bez zabierania go magazynowi; null, gdy dla tej ścieżki go nie ma.</summary>
    private static Raw? PeekPreloaded(string path)
    {
        Task<Raw>? task;

        lock (PreloadGate)
            task = string.Equals(_preloadedPath, path, StringComparison.Ordinal) ? _preloaded : null;

        return task?.GetAwaiter().GetResult();
    }

    private static Raw TakePreloadedOrRead(string path)
    {
        Task<Raw>? task = null;

        lock (PreloadGate)
        {
            if (_preloaded is not null && string.Equals(_preloadedPath, path, StringComparison.Ordinal))
            {
                task = _preloaded;
                _preloaded = null;
                _preloadedPath = null;
            }
        }

        return task is null ? Read(path) : task.GetAwaiter().GetResult();
    }

    private static Raw Read(string path)
    {
        if (!File.Exists(path)) return new Raw(false, null, null, null);

        string json;
        try
        {
            json = ReadWithRetry(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Raw(true, null, ex, null);
        }

        try
        {
            return new Raw(true, JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings), null, null);
        }
        catch (Exception ex)
        {
            return new Raw(true, null, null, ex);
        }
    }

    /// <summary>Marks the settings as changed; the file is written a moment later.</summary>
    public void Touch()
    {
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Zapis jest zamówiony i czeka na zegar.</summary>
    internal bool SavePending => _debounce.IsEnabled;

    /// <summary>Writes immediately. Called on shutdown, where a debounce would be lost.</summary>
    public void SaveNow()
    {
        _debounce.Stop();
        _dirty = false;

        if (_readFailed && !MayOverwrite()) return;

        try
        {
            Current.SchemaVersion = AppSettings.CurrentSchema;
            AtomicFile.Write(_path, JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex)
        {
            // Settings are a convenience, never a reason to take the application down.
            Console.Error.WriteLine($"[cewka] nie udało się zapisać ustawień: {ex.Message}");
        }
    }

    /// <summary>Plik istniał, ale przy starcie nie dał się przeczytać, więc obowiązują domyślne.</summary>
    private bool _readFailed;

    /// <summary>Ponowny odczyt pokazał plik w całości; zostaje nietknięty do końca sesji.</summary>
    private bool _keepFile;

    /// <summary>
    /// Plik zablokowany przy starcie przez program antywirusowy albo kopię zapasową ma zwykle
    /// poprawną treść, a zapis domyślnych by ją zniszczył. Przed pierwszym zapisem plik jest
    /// czytany jeszcze raz: poprawny zostaje bez zmian, nadal zablokowany czeka na następny
    /// zapis, a uszkodzony trafia do kopii obok.
    /// </summary>
    private bool MayOverwrite()
    {
        if (_keepFile) return false;

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _readFailed = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cewka] ustawienia nadal niedostępne, zapis odłożony: {ex.Message}");
            return false;
        }

        try
        {
            JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
            Console.Error.WriteLine("[cewka] plik ustawień jest poprawny; zmiany z tej sesji nie zostaną zapisane");
            _keepFile = true;
            return false;
        }
        catch (Exception)
        {
            KeepCopy(_path);
            _readFailed = false;
            return true;
        }
    }

    private AppSettings Load(string path)
    {
        var raw = TakePreloadedOrRead(path);
        if (!raw.Exists) return Defaults();

        if (raw.ReadError is { } readError)
        {
            Console.Error.WriteLine($"[cewka] nie udało się odczytać ustawień, użyto domyślnych: {readError.Message}");
            _readFailed = true;
            return Defaults();
        }

        try
        {
            if (raw.ParseError is { } parseError) throw parseError;

            return Sanitise(Migrate(raw.Loaded ?? Defaults()));
        }
        catch (Exception ex)
        {
            // Nieznana wartość z nowszej wersji albo błąd ręcznej edycji. Domyślne ustawienia
            // obowiązują tylko w tej sesji; oryginał zostaje obok pod osobną nazwą.
            Console.Error.WriteLine($"[cewka] ustawienia nieczytelne, użyto domyślnych: {ex.Message}");
            KeepCopy(path);
            return Defaults();
        }
    }

    private static AppSettings Defaults() => new() { SchemaVersion = AppSettings.CurrentSchema };

    /// <summary>Plik bywa chwilowo zablokowany przez program antywirusowy albo synchronizację.</summary>
    private static string ReadWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void KeepCopy(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Copy(path, $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cewka] nie udało się zachować kopii ustawień: {ex.Message}");
        }
    }

    /// <summary>Przenosi plik zapisany przez starszą wersję do bieżącego znaczenia pól.</summary>
    private static AppSettings Migrate(AppSettings s)
    {
        if (s.SchemaVersion < 1)
        {
            // W schemacie 0 głośność jest wzmocnieniem liniowym, a wyciszenie zeruje ją samą.
            if (s.Volume <= 0)
            {
                s.IsMuted = true;
                s.Volume = 0.5;
            }
            else
            {
                s.Volume = VolumeCurve.FromGain(s.Volume);
            }
        }

        // Kolejka otwarta na stałe przed wysuwanym panelem to dziś panel przypięty.
        if (s.QueueOpen is { } open)
        {
            s.QueuePinned = open;
            s.QueueOpen = null;
        }

        s.SchemaVersion = AppSettings.CurrentSchema;
        return s;
    }

    /// <summary>Repairs values that a hand-edited or older file could get wrong.</summary>
    private static AppSettings Sanitise(AppSettings s)
    {
        s.EqualiserGains ??= new double[10];
        s.Language ??= "auto";

        s.Volume = Math.Clamp(s.Volume, 0, 1);
        s.Preamp = Math.Clamp(s.Preamp, -12, 12);

        if (s.EqualiserGains.Length != 10)
        {
            var fixedGains = new double[10];
            for (var i = 0; i < 10; i++)
                fixedGains[i] = i < s.EqualiserGains.Length ? s.EqualiserGains[i] : 0;
            s.EqualiserGains = fixedGains;
        }

        for (var i = 0; i < s.EqualiserGains.Length; i++)
            s.EqualiserGains[i] = Math.Clamp(s.EqualiserGains[i], -12, 12);

        if (!Enum.IsDefined(s.EqualiserMode)) s.EqualiserMode = EqualiserMode.Graphic;
        s.ParametricBands = SoundProfile.Settings.Bands(s.ParametricBands);
        s.ParametricPreamp = SoundProfile.Settings.ClampFinite(
            s.ParametricPreamp, ParametricEqualiser.MinPreamp, ParametricEqualiser.MaxPreamp);

        // Jeden profil na urządzenie (pierwszy, czyli używany ostatnio, wygrywa), najwyżej 32.
        s.SoundProfiles = [.. (s.SoundProfiles ?? [])
            .Where(profile => profile is not null && !string.IsNullOrEmpty(profile.Device))
            .DistinctBy(profile => profile.Device, StringComparer.Ordinal)
            .Take(SoundProfile.MaxProfiles)];
        foreach (var profile in s.SoundProfiles) profile.Sanitise();
        s.SeekStep = AudioPreferences.NearestSeekStep(s.SeekStep);
        s.CrossfadeSeconds = AudioPreferences.NearestCrossfade(s.CrossfadeSeconds);

        if (!Enum.IsDefined(s.NormalisationMode)) s.NormalisationMode = NormalisationMode.Auto;

        // Wskaźniki wycofano; zapisany wybór i wartość spoza wyliczenia dają Płytę.
        if (s.SceneVisual == SceneVisual.Meters || !Enum.IsDefined(s.SceneVisual)) s.SceneVisual = SceneVisual.Disc;

        // Płyty nie da się pominąć w cyklu V; nieznane i wycofane wartości z listy znikają.
        s.SceneCycleOff = [.. (s.SceneCycleOff ?? []).Where(v => Enum.IsDefined(v) && v is not (SceneVisual.Disc or SceneVisual.Meters)).Distinct()];

        if (!Enum.IsDefined(s.DiscSpin)) s.DiscSpin = DiscSpin.Normal;
        if (!Enum.IsDefined(s.BackdropMotion)) s.BackdropMotion = BackdropMotion.Normal;
        if (!Enum.IsDefined(s.SpectrumResponse)) s.SpectrumResponse = SpectrumResponse.Normal;
        s.RadioRecentLimit = RecentLimits.Nearest(s.RadioRecentLimit);

        if (!Enum.IsDefined(s.QueueReveal)) s.QueueReveal = QueueReveal.Hover;
        if (!Enum.IsDefined(s.QueueHideDelay)) s.QueueHideDelay = QueueHideDelay.TwoSeconds;
        if (!Enum.IsDefined(s.QueueRevealDelay)) s.QueueRevealDelay = QueueRevealDelay.Normal;

        // Szerokość ręcznie wpisana poniżej najmniejszej rośnie do niej; nieliczbowa wraca do domyślnej.
        // Górnej granicy nie ma, bo zależy od okna i obcina ją dopiero wyświetlanie.
        if (s.QueueWidth is { } width)
            s.QueueWidth = double.IsFinite(width) ? Math.Max(width, QueuePanelWidth.Minimum) : null;

        return s;
    }
}
