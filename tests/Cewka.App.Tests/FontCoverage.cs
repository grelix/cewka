using System.Buffers.Binary;

namespace Cewka.App.Tests;

/// <summary>
/// Odczytuje z pliku fontu zbiór znaków, które ten font potrafi narysować.
///
/// <para><b>Dlaczego własny odczyt, a nie biblioteka.</b> Potrzebna jest jedna informacja:
/// czy dany znak ma w foncie odpowiadający mu glif. Tablica <c>cmap</c>, która to opisuje, jest
/// opisana w specyfikacji OpenType i jej odczyt zajmuje kilkadziesiąt linii — mniej niż
/// dobranie i utrzymanie zależności, a przy tym bez ryzyka, że biblioteka zgłosi obecność glifu
/// zastępczego jako obecność znaku.</para>
///
/// <para>Obsługiwane są podtablice w formacie 4 (znaki z podstawowej płaszczyzny) i 12 (pełny
/// zakres). Inne formaty pochodzą z fontów sprzed dwudziestu lat i nie występują w tym, co
/// program osadza.</para>
/// </summary>
internal sealed class FontCoverage
{
    private readonly HashSet<int> _covered;

    private FontCoverage(HashSet<int> covered) => _covered = covered;

    public int GlyphCount => _covered.Count;

    public bool HasGlyph(char character) => _covered.Contains(character);

    public static FontCoverage Load(string path)
    {
        var data = File.ReadAllBytes(path);
        var cmap = FindTable(data, "cmap")
                   ?? throw new InvalidOperationException($"font {Path.GetFileName(path)} nie ma tablicy cmap");

        var subtable = ChooseSubtable(data, cmap)
                       ?? throw new InvalidOperationException(
                           $"font {Path.GetFileName(path)} nie ma podtablicy cmap w formacie 4 ani 12");

        var format = U16(data, subtable);

        return new FontCoverage(format switch
        {
            4 => ReadFormat4(data, subtable),
            12 => ReadFormat12(data, subtable),
            _ => throw new InvalidOperationException($"nieobsługiwany format cmap: {format}"),
        });
    }

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));

    private static uint U32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    /// <summary>Przegląda katalog tablic na początku pliku i zwraca położenie żądanej tablicy.</summary>
    private static int? FindTable(byte[] data, string tag)
    {
        var count = U16(data, 4);

        for (var i = 0; i < count; i++)
        {
            var record = 12 + i * 16;
            var name = System.Text.Encoding.ASCII.GetString(data, record, 4);

            if (name == tag) return (int)U32(data, record + 8);
        }

        return null;
    }

    /// <summary>
    /// Wybiera podtablicę: najpierw pełnozakresową (format 12), potem tę dla podstawowej
    /// płaszczyzny (format 4). Kolejność jest ta sama, jaką stosują silniki tekstu.
    /// </summary>
    private static int? ChooseSubtable(byte[] data, int cmap)
    {
        var count = U16(data, cmap + 2);
        int? format4 = null;

        for (var i = 0; i < count; i++)
        {
            var record = cmap + 4 + i * 8;
            var offset = cmap + (int)U32(data, record + 4);

            switch (U16(data, offset))
            {
                case 12: return offset;
                case 4: format4 ??= offset; break;
            }
        }

        return format4;
    }

    private static HashSet<int> ReadFormat4(byte[] data, int table)
    {
        var segments = U16(data, table + 6) / 2;

        var endCodes = table + 14;
        var startCodes = endCodes + segments * 2 + 2;
        var deltas = startCodes + segments * 2;
        var rangeOffsets = deltas + segments * 2;

        var covered = new HashSet<int>();

        for (var segment = 0; segment < segments; segment++)
        {
            var start = U16(data, startCodes + segment * 2);
            var end = U16(data, endCodes + segment * 2);
            var delta = (short)U16(data, deltas + segment * 2);
            var rangeOffset = U16(data, rangeOffsets + segment * 2);

            // Ostatni odcinek zawsze kończy się na 0xFFFF i nie opisuje znaków.
            if (start == 0xFFFF) continue;

            for (var code = start; code <= end && code != 0xFFFF; code++)
            {
                int glyph;

                if (rangeOffset == 0)
                {
                    glyph = (code + delta) & 0xFFFF;
                }
                else
                {
                    // Adres liczony od pozycji samego wpisu, nie od początku tablicy — tak
                    // zapisano to w specyfikacji i tak trzeba to odtworzyć.
                    var index = rangeOffsets + segment * 2 + rangeOffset + (code - start) * 2;
                    if (index + 1 >= data.Length) continue;

                    glyph = U16(data, index);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }

                // Glif zerowy to glif zastępczy: znak nie jest pokryty.
                if (glyph != 0) covered.Add(code);
            }
        }

        return covered;
    }

    private static HashSet<int> ReadFormat12(byte[] data, int table)
    {
        var groups = (int)U32(data, table + 12);
        var covered = new HashSet<int>();

        for (var i = 0; i < groups; i++)
        {
            var group = table + 16 + i * 12;

            var start = (int)U32(data, group);
            var end = (int)U32(data, group + 4);
            var startGlyph = (int)U32(data, group + 8);

            for (var code = start; code <= end; code++)
            {
                if (startGlyph + (code - start) != 0) covered.Add(code);
            }
        }

        return covered;
    }
}
