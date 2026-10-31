using System.Text;

namespace Cewka.App.Services;

/// <summary>
/// Zapis pliku tekstowego, po którym na dysku jest albo stara, albo nowa treść, nigdy urwana.
/// <para>
/// Treść trafia najpierw do pliku tymczasowego o nazwie niepowtarzalnej dla procesu, jest
/// utrwalana na nośniku i dopiero wtedy podmienia plik docelowy. Wspólna nazwa pliku
/// tymczasowego pozwalałaby dwóm kopiom programu zamykanym naraz podmienić cudzy, niedokończony
/// zapis; podmiana bez utrwalenia mogłaby po zaniku zasilania zostawić pusty plik.
/// </para>
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string path, string text, Encoding? encoding = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var chosen = encoding ?? Utf8;
                stream.Write(chosen.GetPreamble());
                stream.Write(chosen.GetBytes(text));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
