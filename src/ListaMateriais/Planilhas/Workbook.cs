using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using ListaMateriais.Kmz;
using static ListaMateriais.OoXml;

namespace ListaMateriais.Planilhas;

/// <summary>
/// An .xlsx opened to paint cells and filter rows. <see cref="Plan"/> says what a job would do;
/// <see cref="Save"/> does it on a copy, editing only the sheets chosen and the styles, so everything
/// else in the file (formulas, charts, formats) stays as it was. The original is never changed.
/// </summary>
internal sealed partial class Workbook
{
    /// <summary>Rows of a table that are looked at; the same limit as the rest of the app.</summary>
    internal const int MaxRows = XlsxTable.MaxRows;
    private const int MaxPaintedCells = 2_000_000;
    private const int MaxColumns = 16_384;

    private readonly byte[] _bytes;

    public string Path { get; }

    /// <summary>Every sheet of the file, in the order Excel shows them.</summary>
    public IReadOnlyList<SheetData> Sheets { get; }

    private Workbook(string path, byte[] bytes, IReadOnlyList<SheetData> sheets)
    {
        Path = path;
        _bytes = bytes;
        Sheets = sheets;
    }

    public static Workbook Open(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        try
        {
            var bytes = Files.ReadAllBytesShared(path);
            using var zip = OpenZip(bytes);
            var strings = SharedStrings(zip);
            var dates = DateStyles(zip);
            var date1904 = LoadXml(zip, "xl/workbook.xml").Root!.Element(Main + "workbookPr")?.Attribute("date1904")?.Value
                is "1" or "true";
            var sheets = OoXml.Sheets(zip).Select(s => ReadSheet(zip, s, strings, dates, date1904)).ToList();
            return new Workbook(path, bytes, sheets);
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
            throw new AppException($"\"{name}\" não é uma planilha Excel (.xlsx) válida, está corrompida ou tem senha.\n\n" +
                                   "Se for um arquivo .xls antigo ou .csv, abra no Excel e use Salvar como > " +
                                   "Pasta de Trabalho do Excel (*.xlsx).", e);
        }
    }

    private static ZipArchive OpenZip(byte[] bytes) => new(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);

    /// <summary>A sheet that cannot be read keeps the reason, so the others can still be worked on.</summary>
    private static SheetData ReadSheet(ZipArchive zip, SheetInfo info, IReadOnlyList<string> strings,
        IReadOnlySet<int> dates, bool date1904)
    {
        var rows = new SortedDictionary<int, string?[]>();
        try
        {
            if (TryLoadXml(zip, info.Path)?.Root is not { } root || root.Name != Main + "worksheet"
                || root.Element(Main + "sheetData") is not { } sheetData)
                return new SheetData(info.Name, info.Path, rows, "não é uma aba com células (pode ser um gráfico).");

            AssignReferences(sheetData);
            var uncalculated = new HashSet<int>();
            foreach (var row in sheetData.Elements(Main + "row"))
            {
                var cells = new List<string?>();
                foreach (var cell in row.Elements(Main + "c"))
                {
                    var column = ColumnIndex(SplitRef((string)cell.Attribute("r")!).Column) - 1;
                    if (cell.Element(Main + "f") is not null && cell.Element(Main + "v") is null)
                        uncalculated.Add(column);
                    if (column >= MaxColumns || CellText(cell, strings, dates, date1904) is not { Length: > 0 } text)
                        continue;
                    while (cells.Count <= column)
                        cells.Add(null);
                    cells[column] = text;
                }
                if (cells.Count > 0)
                    rows[RowNumber(row)] = [.. cells];
            }
            return new SheetData(info.Name, info.Path, rows, Uncalculated: uncalculated);
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or FormatException or ArgumentException)
        {
            Trace.TraceWarning("Falha ao ler a aba {0}: {1}", info.Name, e);
            return new SheetData(info.Name, info.Path, [], "não foi possível ler esta aba (conteúdo inválido).");
        }
    }

    private static int RowNumber(XElement row) =>
        int.Parse((string?)row.Attribute("r") ?? throw new InvalidDataException("Linha sem número."),
            NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>Row and cell references are optional in the file format: writes the implied ones.</summary>
    private static void AssignReferences(XElement sheetData)
    {
        var number = 0;
        foreach (var row in sheetData.Elements(Main + "row"))
        {
            if (row.Attribute("r") is null)
                row.SetAttributeValue("r", number + 1);
            number = RowNumber(row);
            var column = 0;
            foreach (var cell in row.Elements(Main + "c"))
            {
                if ((string?)cell.Attribute("r") is { } reference)
                    column = ColumnIndex(SplitRef(reference).Column);
                else
                    cell.SetAttributeValue("r", $"{SheetTable.ColumnLetter(column++)}{number}");
            }
        }
    }

    /// <summary>The text of a cell about as Excel shows it in Portuguese: dates as dd/mm/yyyy, decimals with a comma.</summary>
    private static string? CellText(XElement cell, IReadOnlyList<string> strings, IReadOnlySet<int> dates, bool date1904)
    {
        var type = (string?)cell.Attribute("t") ?? "n";
        if (type == "inlineStr")
            return cell.Element(Main + "is") is { } inline ? RichText(inline).Trim() : null;
        var raw = cell.Element(Main + "v")?.Value;
        if (raw is null)
            return null;
        switch (type)
        {
            case "s":
                return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < strings.Count
                    ? strings[i].Trim()
                    : null;
            case "b":
                return raw == "1" ? "VERDADEIRO" : "FALSO";
            case "n" when double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value):
                var style = (int?)cell.Attribute("s") ?? 0;
                var serial = value + (date1904 ? 1462 : 0);
                if (dates.Contains(style) && serial is >= 1 and < 2_958_466)
                    return DateTime.FromOADate(serial).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
                return value.ToString("0.###############", CultureInfo.InvariantCulture).Replace('.', ',');
            default:
                return raw.Trim();
        }
    }

    /// <summary>The cell styles that show a number as a date.</summary>
    private static HashSet<int> DateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        if (TryLoadXml(zip, "xl/styles.xml")?.Root is not { } root)
            return result;
        var custom = new Dictionary<int, string>();
        foreach (var format in root.Elements(Main + "numFmts").Elements(Main + "numFmt"))
        {
            if ((int?)format.Attribute("numFmtId") is { } id)
                custom[id] = (string?)format.Attribute("formatCode") ?? string.Empty;
        }
        var index = 0;
        foreach (var xf in root.Elements(Main + "cellXfs").Elements(Main + "xf"))
        {
            var id = (int?)xf.Attribute("numFmtId") ?? 0;
            if (custom.TryGetValue(id, out var code) ? IsDateFormat(code) : id is >= 14 and <= 17 or 22 or >= 27 and <= 36 or >= 50 and <= 58)
                result.Add(index);
            index++;
        }
        return result;
    }

    /// <summary>True when a format code has a day or a year outside quotes, brackets and escapes.</summary>
    private static bool IsDateFormat(string code)
    {
        var quoted = false;
        var bracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (quoted)
                quoted = c != '"';
            else if (bracket)
                bracket = c != ']';
            else if (c == '"')
                quoted = true;
            else if (c == '[')
                bracket = true;
            else if (c is '\\' or '_' or '*')
                i++;
            else if (c is 'd' or 'D' or 'y' or 'Y')
                return true;
        }
        return false;
    }

    /// <summary>What a sheet gets: which cells, which rows are hidden.</summary>
    private sealed class SheetPlan
    {
        /// <summary>(row, column) of each cell to paint; null takes the fill away.</summary>
        public Dictionary<(int Row, int Column), Color?> Paint { get; } = [];
        public List<int> Shown { get; } = [];
        public List<int> Hidden { get; } = [];
        public bool Filtered { get; set; }
        public int HeaderRow { get; set; }
        public int LastRow { get; set; }
        public int DataRows { get; set; }
        public IReadOnlyList<(int Index, string Name)> Columns { get; set; } = [];
        public List<string> Warnings { get; } = [];
        public IReadOnlySet<int> Uncalculated { get; set; } = new HashSet<int>();

        public SheetResult Result(string name) =>
            new(name, HeaderRow, DataRows, Paint.Count, Filtered ? Shown.Count : null, Hidden.Count, Warnings);
    }

    /// <summary>What the job would do to each chosen sheet, without writing anything.</summary>
    public JobResult Plan(Job job) => new([.. Chosen(job).Select(sheet => PlanSheet(sheet, job).Result(sheet.Name))]);

    private IEnumerable<SheetData> Chosen(Job job) => Sheets.Where(s => job.Sheets.Contains(s.Name, StringComparer.Ordinal));

    private static SheetPlan PlanSheet(SheetData sheet, Job job)
    {
        var plan = new SheetPlan();
        if (sheet.Problem is not null)
        {
            plan.Warnings.Add(sheet.Problem);
            return plan;
        }
        if (sheet.Rows.Count == 0)
        {
            plan.Warnings.Add("está vazia.");
            return plan;
        }

        var header = job.HeaderRow > 0 ? job.HeaderRow : sheet.FindHeaderRow();
        var last = sheet.Rows.Keys.Last();
        if (header > last)
        {
            plan.Warnings.Add($"não há nada a partir da linha {header}, indicada como cabeçalho.");
            return plan;
        }
        if (last - header > MaxRows)
        {
            plan.Warnings.Add($"tem linhas demais: só as primeiras {MaxRows:N0} depois do cabeçalho foram tratadas.");
            last = header + MaxRows;
        }
        plan.HeaderRow = header;
        plan.LastRow = last;
        plan.DataRows = last - header;
        plan.Columns = sheet.Columns(header);
        plan.Uncalculated = sheet.Uncalculated ?? new HashSet<int>();
        if (plan.DataRows == 0)
            plan.Warnings.Add("só tem o cabeçalho, sem linhas de dados.");

        // Blank rows in the middle of the table count too: "Está vazio" has to find them.
        var numbers = Enumerable.Range(header + 1, plan.DataRows).ToList();
        var table = new SheetTable(sheet.Name,
            [.. numbers.Select(n => sheet.Rows.GetValueOrDefault(n) ?? [])], numbers, 0);

        PlanPaint(plan, table, job.Paint);
        PlanFilter(plan, table, job.Filter);
        return plan;
    }

    private static void PlanPaint(SheetPlan plan, SheetTable table, IReadOnlyList<PaintRule> rules)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var label = $"pintura {i + 1}";
            var columns = new List<int>();
            if (rule.Columns.Count == 0)
            {
                columns.AddRange(plan.Columns.Select(c => c.Index));
            }
            else
            {
                foreach (var name in rule.Columns)
                {
                    var found = SheetData.Find(plan.Columns, name);
                    if (found.Count == 0)
                        plan.Warnings.Add($"coluna \"{name}\" não encontrada ({label}).");
                    columns.AddRange(found);
                }
            }
            if (columns.Count == 0 || Matching(plan, table, rule.When, label) is not { } rows)
                continue;
            if (rows.Count == 0 && table.Rows.Count > 0)
                plan.Warnings.Add($"nenhuma linha atende à condição da {label}.");
            if (rule.IncludeHeader)
                rows = [plan.HeaderRow, .. rows];
            foreach (var row in rows)
            {
                foreach (var column in columns)
                {
                    if (plan.Paint.Count >= MaxPaintedCells)
                    {
                        plan.Warnings.Add($"células demais para pintar: parou em {MaxPaintedCells:N0}.");
                        return;
                    }
                    plan.Paint[(row, column)] = rule.Fill;
                }
            }
        }
    }

    private static void PlanFilter(SheetPlan plan, SheetTable table, IReadOnlyList<Condition> filter)
    {
        if (!filter.Any(c => c.Active) || table.Rows.Count == 0
            || Matching(plan, table, filter, "filtro") is not { } rows)
            return;
        if (rows.Count == 0)
        {
            // Hiding everything would look like the data was lost.
            plan.Warnings.Add("nenhuma linha atende ao filtro; nada foi ocultado.");
            return;
        }
        var shown = rows.ToHashSet();
        plan.Filtered = true;
        plan.Shown.AddRange(rows);
        plan.Hidden.AddRange(table.RowNumbers.Where(n => !shown.Contains(n)));
    }

    /// <summary>
    /// The rows that meet the conditions; null (with a warning) when they cannot be checked on this sheet,
    /// so that a missing column never ends up painting or hiding the wrong rows.
    /// </summary>
    private static List<int>? Matching(SheetPlan plan, SheetTable table, IReadOnlyList<Condition> conditions, string label)
    {
        var rules = new List<SheetFilterRule>();
        var usable = true;
        foreach (var condition in conditions)
        {
            if (!condition.Active)
                continue;
            var found = SheetData.Find(plan.Columns, condition.Column);
            if (found.Count == 0)
            {
                plan.Warnings.Add($"coluna \"{condition.Column}\" não encontrada; {label} não foi aplicado(a).");
                usable = false;
            }
            else if (SheetFilters.NeedsValue(condition.Operator) && string.IsNullOrWhiteSpace(condition.Value))
            {
                plan.Warnings.Add($"falta o valor da condição sobre \"{condition.Column}\"; {label} não foi aplicado(a).");
                usable = false;
            }
            else
            {
                if (plan.Uncalculated.Contains(found[0]))
                    plan.Warnings.Add($"a coluna \"{condition.Column}\" tem fórmulas sem resultado gravado, lidas como vazias " +
                                      $"({label}). Abra e salve a planilha no Excel.");
                rules.Add(new SheetFilterRule(found[0], condition.Operator, condition.Value, Or: condition.Or));
            }
        }
        return usable ? [.. SheetFilters.Apply(table, rules, hasHeader: false).RowNumbers] : null;
    }
}
