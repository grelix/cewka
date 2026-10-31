using System.Diagnostics;
using Cewka.Audio;
using Cewka.Audio.Decoding;
using Cewka.Audio.Devices;

namespace Cewka.AudioProbe;

/// <summary>
/// Narzędzie deweloperskie do sprawdzania warstwy dźwięku na prawdziwych plikach.
///
/// Użycie:
///   dotnet run --project tools/Cewka.AudioProbe -- [katalog-z-muzyką]
///
/// Sprawdza warstwę natywną, listę urządzeń, otwarcie urządzenia (na ciszy, bez dźwięku)
/// i dekodowanie po jednym pliku z każdego napotkanego formatu.
/// </summary>
internal static class Program
{
    private const int PipelineRate = 48000;
    private const int PipelineChannels = 2;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var root = args.Length > 0
            ? args[0]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

        var failures = 0;

        Section("Warstwa natywna");
        try
        {
            Console.WriteLine($"  miniaudio {AudioDeviceList.NativeVersion}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  BLAD: {ex.Message}");
            return 1;
        }

        Section("Urzadzenia odtwarzania");
        try
        {
            var devices = AudioDeviceList.Enumerate();
            foreach (var device in devices)
                Console.WriteLine($"  [{device.Index}] {device.Name}{(device.IsDefault ? "  (domyslne)" : "")}");

            if (devices.Count == 0) Console.WriteLine("  brak urzadzen");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  BLAD: {ex.Message}");
            failures++;
        }

        Section("Otwarcie urzadzenia (cisza)");
        try
        {
            var frames = 0L;
            var callbacks = 0;

            using (var device = new AudioDevice(PipelineRate, PipelineChannels))
            {
                device.Render = buffer =>
                {
                    Interlocked.Add(ref frames, buffer.Length / PipelineChannels);
                    Interlocked.Increment(ref callbacks);
                    buffer.Clear();
                };

                Console.WriteLine($"  urzadzenie: {device.Name}");
                Console.WriteLine($"  format:     {device.SampleRate} Hz, {device.Channels} kan.");

                device.Start();
                Thread.Sleep(500);
                device.Stop();
            }

            var expected = PipelineRate * 0.5;
            var ratio = frames / expected;
            Console.WriteLine($"  wywolan:    {callbacks}");
            Console.WriteLine($"  ramek:      {frames} (oczekiwano ok. {expected:N0}, stosunek {ratio:P0})");

            if (callbacks == 0)
            {
                Console.WriteLine("  BLAD: zwrotne wywolanie renderowania nie zostalo ani razu wywolane");
                failures++;
            }
            else if (ratio is < 0.7 or > 1.3)
            {
                Console.WriteLine("  UWAGA: liczba ramek odbiega od czasu rzeczywistego");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  BLAD: {ex.Message}");
            failures++;
        }

        Section($"Dekodowanie plikow z: {root}");
        if (!Directory.Exists(root))
        {
            Console.WriteLine("  katalog nie istnieje");
            return failures;
        }

        var found = PickOnePerFormat(root).ToList();
        foreach (var (format, path) in found)
        {
            if (!DecodeOne(format, path)) failures++;
        }

        // Bez pliku Opus w katalogu plik powstaje na miejscu; Concentus potrafi też kodować.
        if (found.All(f => f.Format != AudioFileFormat.Opus))
        {
            Section("Opus (plik wygenerowany na potrzeby testu)");
            var generated = OpusSample.Create();
            try
            {
                if (!DecodeOne(AudioFileFormat.Opus, generated)) failures++;
            }
            finally
            {
                File.Delete(generated);
            }
        }

        Section("Wynik");
        Console.WriteLine(failures == 0 ? "  wszystko w porzadku" : $"  bledow: {failures}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Finds the first file of each recognised format, so the run stays short.</summary>
    private static IEnumerable<(AudioFileFormat Format, string Path)> PickOnePerFormat(string root)
    {
        var seen = new HashSet<AudioFileFormat>();

        foreach (var path in FilesInOrder(root))
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (!AudioFileFormatDetector.SupportedExtensions.Contains(extension) &&
                extension is not (".wma" or ".oga")) continue;

            AudioFileFormat format;
            try { format = AudioFileFormatDetector.Detect(path); }
            catch { continue; }

            if (!seen.Add(format)) continue;
            yield return (format, path);

            if (seen.Count >= 8) yield break;
        }
    }

    /// <summary>
    /// Pliki w stałej kolejności, sortowane w obrębie każdego katalogu. Globalne sortowanie
    /// całego drzewa musiałoby najpierw przejść całą bibliotekę, zanim zwróci pierwszy plik.
    /// </summary>
    private static IEnumerable<string> FilesInOrder(string directory)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true };

        // Dowiązania do katalogów pomijane, żeby pętla dowiązań nie zapętliła rekurencji.
        var directories = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        foreach (var file in Directory.EnumerateFiles(directory, "*", options).Order(StringComparer.Ordinal))
            yield return file;

        foreach (var subdirectory in Directory.EnumerateDirectories(directory, "*", directories).Order(StringComparer.Ordinal))
        foreach (var file in FilesInOrder(subdirectory))
            yield return file;
    }

    private static bool DecodeOne(AudioFileFormat format, string path)
    {
        Console.WriteLine();
        Console.WriteLine($"  {format}  ->  {Path.GetFileName(path)}");

        if (!DecoderFactory.IsSupported(format))
        {
            Console.WriteLine($"     pominiete: {AudioFileFormatDetector.DescribeUnsupported(format)}");
            return true;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            using var decoder = DecoderFactory.Open(path, PipelineRate, PipelineChannels);

            Console.WriteLine($"     zrodlo:  {decoder.SourceSampleRate} Hz, {decoder.SourceChannels} kan.");
            Console.WriteLine($"     wyjscie: {decoder.SampleRate} Hz, {decoder.Channels} kan., " +
                              $"{decoder.TotalFrames} ramek ({decoder.TotalFrames / (double)decoder.SampleRate:F1} s)");

            var buffer = new float[4096 * PipelineChannels];
            long total = 0;
            var peak = 0f;
            double sumOfSquares = 0;

            // Read at most fifteen seconds; enough to catch a decoder that stalls or drifts.
            var limit = PipelineRate * 15L;
            while (total < limit)
            {
                var frames = decoder.Read(buffer);
                if (frames == 0) break;

                for (var i = 0; i < frames * PipelineChannels; i++)
                {
                    var value = buffer[i];
                    var magnitude = Math.Abs(value);
                    if (magnitude > peak) peak = magnitude;
                    sumOfSquares += value * (double)value;
                }

                total += frames;
            }

            stopwatch.Stop();

            var rms = total > 0 ? Math.Sqrt(sumOfSquares / (total * PipelineChannels)) : 0;
            var seconds = total / (double)PipelineRate;

            Console.WriteLine($"     odczyt:  {total} ramek ({seconds:F1} s) w {stopwatch.ElapsedMilliseconds} ms " +
                              $"= {(stopwatch.ElapsedMilliseconds > 0 ? seconds * 1000 / stopwatch.ElapsedMilliseconds : 0):F0}x czasu rzeczywistego");
            Console.WriteLine($"     szczyt:  {peak:F4}   RMS: {rms:F4}");

            if (total == 0) { Console.WriteLine("     BLAD: nie odczytano ani jednej ramki"); return false; }
            if (peak == 0) { Console.WriteLine("     BLAD: sama cisza"); return false; }
            if (peak > 1.001f) { Console.WriteLine("     UWAGA: probki poza zakresem [-1, 1]"); }

            // Sprawdzenie przewijania.
            if (decoder.CanSeek && decoder.TotalFrames > PipelineRate)
            {
                decoder.Seek(decoder.TotalFrames / 2);
                var afterSeek = decoder.Read(buffer);
                Console.WriteLine($"     przewijanie do polowy: odczytano {afterSeek} ramek");
                if (afterSeek == 0) { Console.WriteLine("     BLAD: po przewinieciu brak danych"); return false; }
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"     BLAD: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("=== " + title + " ===");
    }
}
