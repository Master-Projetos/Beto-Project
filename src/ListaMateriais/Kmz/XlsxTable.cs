using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using static ListaMateriais.OoXml;

namespace ListaMateriais.Kmz;

/// <summary>A worksheet as plain text: one array per used row, indexed by column (0 = A). Empty cells are null.</summary>
/// <param name="RowNumbers">The Excel row number of each entry of <paramref name="Rows"/> (blank rows are skipped).</param>
internal sealed record SheetTable(string Name, IReadOnlyList<string?[]> Rows, IReadOnlyList<int> RowNumbers, int Columns)
{
    /// <summary>"A", "B"... "AA".</summary>
    public static string ColumnLetter(int index)
    {
        var letters = string.Empty;
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            letters = (char)('A' + (n - 1) % 26) + letters;
        return letters;
    }
}

/// <summary>Reads every sheet of an .xlsx as text, to let the user point at the columns that hold the points.</summary>
internal static class XlsxTable
{
    private const int MaxRows = 200_000;
    private const int MaxColumns = 256;

    public static IReadOnlyList<SheetTable> Read(string path)
    {
        var name = Path.GetFileName(path);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(Files.ReadAllBytesShared(path), writable: false), ZipArchiveMode.Read);
            var strings = SharedStrings(zip);
            var tables = new List<SheetTable>();
            foreach (var sheet in Sheets(zip))
            {
                if (TryLoadXml(zip, sheet.Path)?.Root is { } root && root.Name == Main + "worksheet")
                    tables.Add(ReadSheet(sheet.Name, root, strings));
            }
            return tables;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new AppException($"A planilha \"{name}\" não foi encontrada.\n\n" +
                                   "Ela pode ter sido movida, renomeada ou excluída. Selecione-a novamente.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new AppException($"Sem permissão para ler a planilha \"{name}\".", e);
        }
        catch (IOException e)
        {
            throw new AppException($"Não foi possível abrir a planilha \"{name}\".", e);
        }
        catch (Exception e) when (e is InvalidDataException or XmlException)
        {
            throw new AppException($"\"{name}\" não é uma planilha Excel (.xlsx) válida ou está corrompida.\n\n" +
                                   "Se for um arquivo .xls antigo ou .csv, abra no Excel e use Salvar como > " +
                                   "Pasta de Trabalho do Excel (*.xlsx).", e);
        }
    }

    private static SheetTable ReadSheet(string name, XElement worksheet, IReadOnlyList<string> strings)
    {
        var rows = new List<string?[]>();
        var numbers = new List<int>();
        var columns = 0;
        var number = 0;
        foreach (var row in worksheet.Elements(Main + "sheetData").Elements(Main + "row"))
        {
            if (rows.Count >= MaxRows)
                break;
            number = int.TryParse((string?)row.Attribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var r)
                ? r
                : number + 1;
            var cells = new List<string?>();
            var column = -1;
            foreach (var cell in row.Elements(Main + "c"))
            {
                column = (string?)cell.Attribute("r") is { } reference ? ColumnIndex(SplitRef(reference).Column) - 1 : column + 1;
                if (column >= MaxColumns || CellText(cell, strings) is not { Length: > 0 } text)
                    continue;
                while (cells.Count <= column)
                    cells.Add(null);
                cells[column] = text;
            }
            if (cells.Count == 0)
                continue;
            columns = Math.Max(columns, cells.Count);
            rows.Add([.. cells]);
            numbers.Add(number);
        }
        return new SheetTable(name, rows, numbers, columns);
    }

    private static string? CellText(XElement cell, IReadOnlyList<string> strings)
    {
        var type = (string?)cell.Attribute("t") ?? "n";
        if (type == "inlineStr")
            return cell.Element(Main + "is") is { } inline ? RichText(inline).Trim() : null;
        var raw = cell.Element(Main + "v")?.Value;
        if (raw is null)
            return null;
        return type switch
        {
            "s" => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < strings.Count
                ? strings[i].Trim()
                : null,
            "e" => null,
            // Numbers are stored with a dot; 1E-05 and the like become plain decimals.
            "n" => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value.ToString("0.###############", CultureInfo.InvariantCulture)
                : raw,
            _ => raw.Trim(),
        };
    }
}

/// <summary>Turns the rows of a sheet into points, given the columns the user picked.</summary>
internal static class SheetPoints
{
    /// <summary>Columns guessed from the header row: (name, latitude, longitude), -1 when not found.</summary>
    public static (int Name, int Latitude, int Longitude) GuessColumns(SheetTable table)
    {
        int name = -1, latitude = -1, longitude = -1;
        if (table.Rows.Count > 0)
        {
            var header = table.Rows[0];
            for (var i = 0; i < header.Length; i++)
            {
                if (header[i] is not { } text)
                    continue;
                if (latitude < 0 && PointText.IsLatitudeKey(text))
                    latitude = i;
                else if (longitude < 0 && PointText.IsLongitudeKey(text))
                    longitude = i;
                else if (name < 0 && PointText.IsNameKey(text))
                    name = i;
            }
        }
        return (name, latitude, longitude);
    }

    /// <summary>True when the first row holds column names instead of a point.</summary>
    public static bool HasHeader(SheetTable table, int latitude, int longitude) =>
        table.Rows.Count > 0 && !(Coordinate.TryParse(Cell(table.Rows[0], latitude), out _)
                                  && Coordinate.TryParse(Cell(table.Rows[0], longitude), out _));

    /// <param name="name">Column of the names, or -1 to number the points.</param>
    public static PointList Read(SheetTable table, int name, int latitude, int longitude, bool hasHeader)
    {
        var points = new PointCollector(CoordinateOrder.LatitudeFirst);
        for (var i = hasHeader ? 1 : 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var latitudeText = Cell(row, latitude);
            var longitudeText = Cell(row, longitude);
            if (latitudeText is null && longitudeText is null)
                continue;
            var where = $"Linha {table.RowNumbers[i]}";
            if (!Coordinate.TryParse(latitudeText, out var lat))
                points.Problem(where, $"latitude inválida (\"{latitudeText}\").");
            else if (!Coordinate.TryParse(longitudeText, out var lon))
                points.Problem(where, $"longitude inválida (\"{longitudeText}\").");
            else
                points.Add(where, name >= 0 ? Cell(row, name) : null, lat, lon);
        }
        return points.Result;
    }

    private static string? Cell(string?[] row, int column) => column >= 0 && column < row.Length ? row[column] : null;
}
