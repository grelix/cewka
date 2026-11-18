using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Cewka.Audio.Dsp;

/// <summary>
/// Obwiednie plików na dysku, po jednym małym pliku na utwór, w katalogu obok pamięci podręcznej
/// głośności. W samym pliku pamięci podręcznej każda zajmowała ponad 5 kB, a ten jest czytany
/// w całości przy starcie i zapisywany w całości po każdym pomiarze.
///
/// <para>Plik niesie rozmiar i czas modyfikacji utworu, z którego powstał; obwiednia innej wersji
/// utworu jest odrzucana przy odczycie.</para>
/// </summary>
internal sealed class EnvelopeStore(string directory)
{
    /// <summary>„ENV1” w kolejności bajtów na dysku.</summary>
    private const uint Magic = 0x31564E45;

    private const int HeaderBytes = 4 + 8 + 8 + 4;

    private const string Extension = ".env";

    /// <summary>
    /// Nazwa pliku z samej ścieżki utworu, bez rozróżniania wielkości liter, tak jak klucze pamięci
    /// podręcznej. Ponowny pomiar tego samego utworu nadpisuje więc poprzedni plik.
    /// </summary>
    internal string FileFor(string trackPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trackPath.ToUpperInvariant()));
        return Path.Combine(directory, Convert.ToHexStringLower(hash.AsSpan(0, 20)) + Extension);
    }

    /// <returns>Fałsz, gdy pliku nie udało się zapisać; obwiednia musi wtedy przetrwać gdzie indziej.</returns>
    public bool Write(string trackPath, long length, long modifiedTicks, FileEnvelope envelope)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var count = envelope.Count;
            var bytes = new byte[HeaderBytes + count * 2];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), modifiedTicks);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), count);
            envelope.Peak.CopyTo(bytes.AsSpan(HeaderBytes));
            envelope.Rms.CopyTo(bytes.AsSpan(HeaderBytes + count));

            // Przez plik tymczasowy: przerwany zapis nie zostawia połowy obwiedni pod właściwą nazwą.
            var target = FileFor(trackPath);
            var temporary = target + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[cewka] nie zapisano obwiedni: {ex.Message}");
            return false;
        }
    }

    /// <summary>Obwiednia utworu o tym rozmiarze i czasie modyfikacji; null, gdy jej nie ma albo jest z innej wersji.</summary>
    public FileEnvelope? Read(string trackPath, long length, long modifiedTicks)
    {
        try
        {
            var file = FileFor(trackPath);
            if (!File.Exists(file)) return null;

            var bytes = File.ReadAllBytes(file);
            if (bytes.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic) return null;
            if (BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(4)) != length) return null;
            if (BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12)) != modifiedTicks) return null;

            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
            if (count <= 0 || bytes.Length != HeaderBytes + (long)count * 2) return null;

            return new FileEnvelope(bytes.AsSpan(HeaderBytes, count).ToArray(), bytes.AsSpan(HeaderBytes + count, count).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public void Delete(string trackPath)
    {
        try
        {
            File.Delete(FileFor(trackPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Usuwa obwiednie utworów, których nie ma już w pamięci podręcznej, oraz porzucone pliki
    /// tymczasowe. Bez tego obwiednia usuniętego albo przeniesionego utworu zostawałaby na zawsze.
    /// </summary>
    public void Prune(IEnumerable<string> keptTrackPaths)
    {
        try
        {
            if (!Directory.Exists(directory)) return;

            var kept = new HashSet<string>(keptTrackPaths.Select(FileFor), StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (kept.Contains(file)) continue;
                if (!file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(Extension + ".tmp", StringComparison.OrdinalIgnoreCase))
                    continue;

                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
