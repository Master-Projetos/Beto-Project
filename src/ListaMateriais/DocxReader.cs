using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using static ListaMateriais.OoXml;

namespace ListaMateriais;

/// <summary>Reads the material tables from Excel workbooks embedded in a .docx.</summary>
internal static class DocxReader
{
    private const int MaxEmbeddedWorkbooks = 500;

    public static OrderedDictionary<string, Material> ReadMaterials(string docxPath)
    {
        var materials = new OrderedDictionary<string, Material>(StringComparer.Ordinal);
        using var docx = ZipFile.OpenRead(docxPath);

        var embedded = docx.Entries
            .Where(e => e.FullName.StartsWith("word/embeddings/", StringComparison.Ordinal)
                        && e.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .Take(MaxEmbeddedWorkbooks);

        foreach (var entry in embedded)
        {
            using var stream = new MemoryStream(ReadBytes(entry), writable: false);
            using var xlsx = new ZipArchive(stream, ZipArchiveMode.Read);
            ReadWorkbook(xlsx, materials);
        }
        return materials;
    }

    private static void ReadWorkbook(ZipArchive xlsx, OrderedDictionary<string, Material> materials)
    {
        var strings = SharedStrings(xlsx);
        foreach (var sheet in Sheets(xlsx))
        {
            var document = TryLoadXml(xlsx, sheet.Path);
            if (document?.Root is not { } root || root.Name != Main + "worksheet")
                continue;

            var rows = NonEmptyRows(root, strings);
            if (rows.Count == 0 || rows[0][0] is not string title
                || !Config.MaterialTableTitles.Contains(Config.Normalize(title)))
                continue;

            foreach (var row in rows.Skip(1))
            {
                if (row[0] is not { } name || row[2] is not double quantity)
                    continue;
                var unitPrice = row[3] is double price ? price : 0;
                var unit = row[1] is { } u ? Format(u) : string.Empty;
                materials[Format(name).Trim()] = new Material(quantity, unit, RoundPrice(unitPrice));
                Trace.TraceInformation(
                    "{0} | unidade: {1} | quantidade: {2} | valor unitário: {3} | valor total: {4}",
                    name, unit, quantity, unitPrice, row[4]);
            }
        }
    }

    /// <summary>
    /// Rows with at least one value, holding the first 5 columns starting at the
    /// sheet's first used column (same semantics as openpyxl iter_rows).
    /// </summary>
    private static List<object?[]> NonEmptyRows(XElement worksheet, IReadOnlyList<string> strings)
    {
        var minColumn = int.MaxValue;
        var values = new SortedDictionary<int, Dictionary<int, object>>();
        var rowNumber = 0;

        foreach (var row in worksheet.Elements(Main + "sheetData").Elements(Main + "row"))
        {
            rowNumber = int.TryParse((string?)row.Attribute("r"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var r) ? r : rowNumber + 1;
            var column = 0;
            foreach (var cell in row.Elements(Main + "c"))
            {
                column = (string?)cell.Attribute("r") is { } cellRef
                    ? ColumnIndex(SplitRef(cellRef).Column)
                    : column + 1;
                minColumn = Math.Min(minColumn, column);
                if (CellValue(cell, strings) is not { } value)
                    continue;
                if (!values.TryGetValue(rowNumber, out var cells))
                    values[rowNumber] = cells = [];
                cells[column] = value;
            }
        }

        return values.Values.Select(cells =>
        {
            var slots = new object?[5];
            for (var i = 0; i < slots.Length; i++)
                slots[i] = cells.GetValueOrDefault(minColumn + i);
            return slots;
        }).ToList();
    }

    private static object? CellValue(XElement cell, IReadOnlyList<string> strings)
    {
        var type = (string?)cell.Attribute("t") ?? "n";
        if (type == "inlineStr")
            return cell.Element(Main + "is") is { } inline ? RichText(inline) : null;

        var raw = cell.Element(Main + "v")?.Value;
        if (raw is null)
            return null;

        return type switch
        {
            "s" => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var i)
                   && i < strings.Count ? strings[i] : null,
            "str" or "e" => raw,
            "b" => raw == "1",
            _ => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : raw,
        };
    }

    /// <summary>Rounds to cents with half away from zero (12.345 becomes 12.35).</summary>
    private static double RoundPrice(double value) =>
        Math.Abs(value) < 7.9e27
            ? (double)Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero)
            : value;

    private static string Format(object value) => value switch
    {
        double d => Number(d),
        bool b => b ? "True" : "False",
        _ => value.ToString() ?? string.Empty,
    };
}
