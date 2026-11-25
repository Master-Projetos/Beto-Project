using System.Globalization;
using System.Text;
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
    internal const int MaxRows = 200_000;
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

/// <summary>The city and state typed by the user, for sheets (or rows) that do not say them.</summary>
internal sealed record AddressDefaults(string? City = null, string? State = null)
{
    public static readonly AddressDefaults None = new();
}

/// <summary>What came out of a sheet.</summary>
/// <param name="Points">The points ready, the rows left out and why, and what was corrected.</param>
/// <param name="Pending">Addresses not looked up yet, once each.</param>
/// <param name="FromCoordinates">Points placed by their latitude and longitude.</param>
/// <param name="Located">Points placed by their address, by how precise the place found is.</param>
internal sealed record SheetReading(PointList Points, IReadOnlyList<AddressQuery> Pending, int FromCoordinates,
    IReadOnlyDictionary<GeoPrecision, int> Located)
{
    public static readonly SheetReading Empty = new(PointList.Empty, [], 0, new Dictionary<GeoPrecision, int>());
}

/// <summary>Turns the rows of a sheet into points, given the columns the user picked.</summary>
internal static class SheetPoints
{
    /// <summary>Columns guessed from the header row: (name, latitude, longitude), -1 when not found.</summary>
    public static (int Name, int Latitude, int Longitude) GuessColumns(SheetTable table)
    {
        var columns = ColumnGuesser.Guess(table, hasHeader: true);
        return (columns.Name, columns.Latitude, columns.Longitude);
    }

    /// <summary>True when the first row holds column names instead of a point.</summary>
    public static bool HasHeader(SheetTable table, int latitude, int longitude) =>
        table.Rows.Count > 0 && !(Coordinate.TryParse(Cell(table.Rows[0], latitude), out _)
                                  && Coordinate.TryParse(Cell(table.Rows[0], longitude), out _));

    /// <param name="name">Column of the names, or -1 to number the points.</param>
    public static PointList Read(SheetTable table, int name, int latitude, int longitude, bool hasHeader) =>
        Read(table, new SheetColumns(Name: name, Latitude: latitude, Longitude: longitude), hasHeader, AddressDefaults.None, _ => null).Points;

    /// <summary>
    /// Each row becomes a point: by its latitude and longitude when it has them, otherwise by its address,
    /// as far as <paramref name="lookup"/> already knows it (the addresses it does not know are returned as pending).
    /// </summary>
    /// <param name="lookup">The place found for an address, or null when it was not looked up yet.</param>
    public static SheetReading Read(SheetTable table, SheetColumns columns, bool hasHeader, AddressDefaults defaults,
        Func<AddressQuery, GeoResult?> lookup)
    {
        var points = new PointCollector(CoordinateOrder.LatitudeFirst);
        var pending = new Dictionary<string, AddressQuery>();
        var located = new Dictionary<GeoPrecision, int>();
        var fromCoordinates = 0;
        for (var i = hasHeader ? 1 : 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var where = $"Linha {table.RowNumbers[i]}";
            var name = columns.Name >= 0 ? Cell(row, columns.Name) : null;

            if (columns.HasCoordinates && (Cell(row, columns.Latitude) is not null || Cell(row, columns.Longitude) is not null))
            {
                if (Coordinates(row, columns, where, points, report: !columns.HasAddress) is var (lat, lon))
                {
                    points.Add(where, name, lat, lon);
                    fromCoordinates++;
                    continue;
                }
                if (!columns.HasAddress)
                    continue;
            }
            if (!columns.HasAddress)
                continue;

            if (Query(row, columns, defaults, out var problem) is not { } query)
            {
                if (problem is not null)
                    points.Problem(where, problem);
                continue;
            }
            switch (lookup(query))
            {
                case null:
                    pending.TryAdd(query.Key, query);
                    break;
                case { Found: false }:
                    points.Problem(where, $"endereço não encontrado no mapa: {query.Text}.");
                    break;
                case { Precision: GeoPrecision.City }:
                    located[GeoPrecision.City] = located.GetValueOrDefault(GeoPrecision.City) + 1;
                    points.Problem(where, $"só a cidade foi encontrada para \"{query.Text}\"; o ponto ficaria no centro da cidade e foi deixado de fora.");
                    break;
                case var found:
                    located[found.Precision] = located.GetValueOrDefault(found.Precision) + 1;
                    points.Add(where, name, found.Latitude, found.Longitude,
                        $"Localizado pelo endereço, {GeoResult.Describe(found.Precision)}.\nNa planilha: {query.Text}\nNo mapa: {found.Label}");
                    break;
            }
        }
        return new SheetReading(points.Result, [.. pending.Values], fromCoordinates, located);
    }

    /// <summary>The address of a row, put together from its columns and the city typed by the user.</summary>
    /// <param name="problem">Why the row has no address that can be looked up; null for an empty row.</param>
    public static AddressQuery? Query(string?[] row, SheetColumns columns, AddressDefaults defaults, out string? problem)
    {
        problem = null;
        var parsed = AddressText.Parse(Cell(row, columns.Street));
        var typeCell = Cell(row, columns.StreetType);
        var type = parsed.Type ?? (typeCell is null ? null : AddressText.StreetType(typeCell) ?? typeCell);
        var street = parsed.Street is null ? null : type is null ? parsed.Street : $"{type} {parsed.Street}";
        var number = AddressText.HouseNumber(Cell(row, columns.Number)) ?? parsed.Number;
        var district = Cell(row, columns.District) ?? parsed.District;
        var postal = AddressText.PostalCode(Cell(row, columns.PostalCode)) ?? parsed.PostalCode;

        string? city = null;
        var state = AddressText.StateCode(Cell(row, columns.State));
        if (Cell(row, columns.City) is { } cityCell)
        {
            var (name, uf) = AddressText.SplitCity(cityCell);
            city = name;
            state ??= uf;
        }
        city ??= string.IsNullOrWhiteSpace(defaults.City) ? null : defaults.City.Trim();
        state ??= AddressText.StateCode(defaults.State);

        if (street is null && district is null)
        {
            if (row.Any(cell => !string.IsNullOrWhiteSpace(cell)))
                problem = "sem coordenadas nem endereço.";
            return null;
        }
        if (city is null)
        {
            problem = "sem cidade: escolha a coluna da cidade ou escreva a cidade no campo \"Cidade\".";
            return null;
        }
        return new AddressQuery(street, number, district, city, state, postal);
    }

    /// <summary>The coordinates of a row, repaired when the slip is clear; null (and a problem when asked) otherwise.</summary>
    private static (double Latitude, double Longitude)? Coordinates(string?[] row, SheetColumns columns, string where, PointCollector points, bool report)
    {
        var latitudeText = Cell(row, columns.Latitude);
        var longitudeText = Cell(row, columns.Longitude);
        var latitudeRepaired = false;
        var longitudeRepaired = false;
        if (!Coordinate.TryParse(latitudeText, out var lat) && !(latitudeRepaired = Coordinate.TryRepair(latitudeText, out lat)))
        {
            if (report)
                points.Problem(where, $"latitude inválida (\"{latitudeText}\").");
            return null;
        }
        if (!Coordinate.TryParse(longitudeText, out var lon) && !(longitudeRepaired = Coordinate.TryRepair(longitudeText, out lon)))
        {
            if (report)
                points.Problem(where, $"longitude inválida (\"{longitudeText}\").");
            return null;
        }
        if (latitudeRepaired)
            points.Fixed(where, $"latitude digitada \"{latitudeText}\", lida como {lat.ToString("0.######", CultureInfo.CurrentCulture)}.");
        if (longitudeRepaired)
            points.Fixed(where, $"longitude digitada \"{longitudeText}\", lida como {lon.ToString("0.######", CultureInfo.CurrentCulture)}.");
        return (lat, lon);
    }

    private static string? Cell(string?[] row, int column) =>
        column >= 0 && column < row.Length && !string.IsNullOrWhiteSpace(row[column]) ? row[column]!.Trim() : null;
}


/// <summary>A condition applied to a worksheet column before its rows are converted to map points.</summary>
/// <param name="Or">Joins this rule to the previous one with OU instead of E.</param>
internal sealed record SheetFilterRule(int Column, string Operator, string Value, bool Active = true, bool Or = false);

/// <summary>
/// Applies the user's spreadsheet filters. Rules joined by E must all hold; OU starts another group,
/// and a row passes when any group holds (E binds tighter than OU, as in a written sentence).
/// </summary>
internal static class SheetFilters
{
    public static readonly string[] Operators =
    [
        "É igual a", "Diferente de", "É um destes", "Contém", "Não contém", "Começa com", "Termina com",
        "Maior que", "Menor que", "Está vazio", "Não está vazio",
    ];

    /// <summary>Operators that take no value.</summary>
    public static bool NeedsValue(string op) => op is not ("Está vazio" or "Não está vazio");

    public static SheetTable Apply(SheetTable table, IReadOnlyList<SheetFilterRule> rules, bool hasHeader)
    {
        var groups = new List<List<SheetFilterRule>>();
        foreach (var rule in rules.Where(rule => rule.Active))
        {
            if (groups.Count == 0 || rule.Or)
                groups.Add([]);
            groups[^1].Add(rule);
        }
        if (groups.Count == 0)
            return table;

        var rows = new List<string?[]>();
        var numbers = new List<int>();
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            if (hasHeader && i == 0 || groups.Any(group => group.All(rule => Matches(Cell(row, rule.Column), rule))))
            {
                rows.Add(row);
                numbers.Add(table.RowNumbers[i]);
            }
        }
        return new SheetTable(table.Name, rows, numbers, table.Columns);
    }

    private static bool Matches(string? cell, SheetFilterRule rule)
    {
        var value = Normalize(rule.Value);
        var actual = Normalize(cell);
        return rule.Operator switch
        {
            "Contém" => actual.Contains(value, StringComparison.Ordinal),
            "Não contém" => !actual.Contains(value, StringComparison.Ordinal),
            "É igual a" => string.Equals(actual, value, StringComparison.Ordinal),
            "Diferente de" => !string.Equals(actual, value, StringComparison.Ordinal),
            "Começa com" => actual.StartsWith(value, StringComparison.Ordinal),
            "Termina com" => actual.EndsWith(value, StringComparison.Ordinal),
            "É um destes" => value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(actual, StringComparer.Ordinal),
            "Maior que" => Compare(actual, value) > 0,
            "Menor que" => Compare(actual, value) < 0,
            "Está vazio" => string.IsNullOrWhiteSpace(cell),
            "Não está vazio" => !string.IsNullOrWhiteSpace(cell),
            _ => true,
        };
    }

    /// <summary>Compares as numbers when both are numbers (with "," or "." decimals), otherwise as text.</summary>
    private static int? Compare(string actual, string value)
    {
        if (actual.Length == 0)
            return null;
        if (Number(actual) is { } left && Number(value) is { } right)
            return left.CompareTo(right);
        if (Date(actual) is { } earlier && Date(value) is { } later)
            return earlier.CompareTo(later);
        return string.CompareOrdinal(actual, value);
    }

    private static double? Number(string text) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static DateTime? Date(string text) =>
        DateTime.TryParseExact(text, ["d/M/yyyy", "d/M/yy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>Upper case, without accents or surrounding spaces: how cells and values are compared.</summary>
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                result.Append(character);
        return result.ToString().Trim().ToUpperInvariant();
    }

    private static string? Cell(string?[] row, int column) =>
        column >= 0 && column < row.Length ? row[column] : null;
}
