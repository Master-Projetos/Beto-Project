using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using static ListaMateriais.OoXml;

namespace ListaMateriais;

/// <summary>Reads the material tables from Excel workbooks embedded in a .docx.</summary>
internal static partial class DocxReader
{
    private const int MaxEmbeddedWorkbooks = 500;

    /// <summary>Reads every table that looks like a material list; the user picks which ones to use.</summary>
    public static DocxContent Read(string docxPath)
    {
        using var docx = Open(docxPath);
        var tables = new List<MaterialTable>();
        var warnings = new List<string>();
        if (Files.IsOpenInWord(docxPath))
            warnings.Add("O documento está aberto no Word, então foi lida a última versão salva. " +
                         "Se você mudou algo e não salvou, salve no Word e gere de novo.");

        var embedded = docx.Entries
            .Where(e => e.FullName.StartsWith("word/embeddings/", StringComparison.Ordinal)
                        && e.FullName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .Take(MaxEmbeddedWorkbooks)
            .ToList();

        foreach (var entry in embedded)
        {
            try
            {
                using var stream = new MemoryStream(ReadBytes(entry), writable: false);
                using var xlsx = new ZipArchive(stream, ZipArchiveMode.Read);
                ReadWorkbook(xlsx, tables);
            }
            catch (Exception e) when (e is InvalidDataException or XmlException)
            {
                Trace.TraceWarning("Planilha incorporada ignorada ({0}): {1}", entry.FullName, e.Message);
                warnings.Add($"Uma planilha incorporada está danificada e foi ignorada ({Path.GetFileName(entry.FullName)}).");
            }
        }

        return new DocxContent(tables, ReadProjectInfo(docx), embedded.Count, warnings);
    }

    private static ZipArchive Open(string docxPath)
    {
        var name = Path.GetFileName(docxPath);
        try
        {
            // A snapshot in memory: works while Word has the document open.
            return new ZipArchive(new MemoryStream(Files.ReadAllBytesShared(docxPath), writable: false),
                ZipArchiveMode.Read);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new AppException($"O documento \"{name}\" não foi encontrado.\n\n" +
                                   "Ele pode ter sido movido, renomeado ou excluído. Selecione-o novamente.", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new AppException($"Sem permissão para ler o documento \"{name}\".", e);
        }
        catch (IOException e)
        {
            throw new AppException($"Não foi possível abrir o documento \"{name}\".\n\n" +
                                   "Se ele estiver aberto em outro programa, feche-o e tente de novo.", e);
        }
        catch (InvalidDataException e)
        {
            throw new AppException($"\"{name}\" não é um documento Word (.docx) válido ou está corrompido.\n\n" +
                                   "Se for um arquivo .doc antigo, abra no Word e use Salvar como > " +
                                   "Documento do Word (*.docx).", e);
        }
    }

    /// <summary>
    /// Reads the project code (first 4 digits of the trailing "0000-00"), the date right below
    /// the author and the city from the "Cidade:" row of the summary table.
    /// </summary>
    private static ProjectInfo ReadProjectInfo(ZipArchive docx)
    {
        XElement? body;
        try
        {
            body = TryLoadXml(docx, "word/document.xml")?.Root?.Element(Word + "body");
        }
        catch (Exception e) when (e is InvalidDataException or XmlException)
        {
            Trace.TraceWarning("Não foi possível ler o texto do documento: {0}", e.Message);
            body = null;
        }
        if (body is null)
            return new ProjectInfo(null, null, null);

        var paragraphs = body.Descendants(Word + "p").Select(ParagraphText).ToList();

        int? code = null;
        string? date = null;
        for (var i = 0; i < paragraphs.Count && code is null; i++)
        {
            if (CodePattern().Match(paragraphs[i]) is not { Success: true } match)
                continue;
            code = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            // The date sits a couple of lines below the code, after the author's name.
            date = paragraphs.Skip(i + 1).Take(5).FirstOrDefault(p => DatePattern().IsMatch(p));
        }

        string? city = null;
        foreach (var row in body.Descendants(Word + "tr"))
        {
            var cells = row.Elements(Word + "tc").Select(c => string.Join(' ', c.Descendants(Word + "p").Select(ParagraphText)).Trim()).ToList();
            var label = cells.FindIndex(c => c.TrimEnd(':').Trim().Equals("Cidade", StringComparison.OrdinalIgnoreCase));
            if (label >= 0 && label + 1 < cells.Count && cells[label + 1].Length > 0)
            {
                city = cells[label + 1];
                break;
            }
        }
        return new ProjectInfo(code, city, date);
    }

    private static string ParagraphText(XElement paragraph) =>
        string.Concat(paragraph.Descendants(Word + "t").Select(t => t.Value)).Trim();

    [GeneratedRegex(@"^[A-Za-z]+(?:-[A-Za-z0-9]+)*-(\d{4})-\d{2}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^\d{2}/\d{2}/\d{4}$")]
    private static partial Regex DatePattern();

    /// <summary>
    /// Adds each sheet that holds item rows. The first row is the title unless it is already
    /// an item, in which case the table has no title.
    /// </summary>
    private static void ReadWorkbook(ZipArchive xlsx, List<MaterialTable> tables)
    {
        var strings = SharedStrings(xlsx);
        foreach (var sheet in Sheets(xlsx))
        {
            var document = TryLoadXml(xlsx, sheet.Path);
            if (document?.Root is not { } root || root.Name != Main + "worksheet")
                continue;

            var rows = NonEmptyRows(root, strings);
            if (rows.Count == 0)
                continue;
            var titled = !IsItem(rows[0]);
            var title = titled && rows[0].FirstOrDefault(v => v is not null) is { } first
                        && Config.Normalize(Format(first)) is { Length: > 0 } normalized
                ? normalized
                : MaterialTable.Untitled;

            var items = new List<Occurrence>();
            foreach (var row in titled ? rows.Skip(1) : rows)
            {
                if (!IsItem(row) || row[0] is not { } name || row[2] is not double quantity)
                    continue;
                var unitPrice = row[3] is double price ? price : 0;
                var unit = row[1] is { } u ? Format(u) : string.Empty;
                items.Add(new Occurrence(Format(name).Trim(), title,
                    new Material(quantity, unit, RoundPrice(unitPrice))));
                Trace.TraceInformation(
                    "{0} | {1} | unidade: {2} | quantidade: {3} | valor unitário: {4} | valor total: {5}",
                    title, name, unit, quantity, unitPrice, row[4]);
            }
            if (items.Count > 0)
                tables.Add(new MaterialTable(title, items));
        }
    }

    /// <summary>A row with a name and a numeric quantity that is not a total line.</summary>
    private static bool IsItem(object?[] row) =>
        row[0] is { } name && row[2] is double
        && Format(name).Trim().Length > 0 && !Config.IsTotalLabel(Format(name));

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
