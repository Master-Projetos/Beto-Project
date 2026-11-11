using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static ListaMateriais.OoXml;

namespace ListaMateriais;

/// <summary>
/// Edits the Excel workbook embedded in a Word document, keeping its layout and styles:
/// only the values (and the cached results of the formulas) change.
/// </summary>
internal static class Sheet
{
    private sealed class Book(ZipArchive zip, string sheetPath, XDocument sheet, IReadOnlyList<string> strings)
    {
        public ZipArchive Zip { get; } = zip;
        public string SheetPath { get; } = sheetPath;
        public XDocument Sheet { get; } = sheet;
        public IReadOnlyList<string> Strings { get; } = strings;
        public IEnumerable<XElement> Rows => Sheet.Descendants(Main + "row");
        public int AddedRows { get; set; }
    }

    private static Book Open(byte[] bytes)
    {
        var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var path = Sheets(zip)[0].Path;
        return new Book(zip, path, LoadXml(zip, path), SharedStrings(zip));
    }

    private static string Key(string text) =>
        Regex.Replace(text.Replace('“', '"').Replace('”', '"'), @"\s+", " ").Trim().ToUpperInvariant();

    private static XElement? Cell(XElement row, string column) =>
        row.Elements(Main + "c").FirstOrDefault(c => SplitRef((string)c.Attribute("r")!).Column == column);

    private static string Text(Book book, XElement row, string column)
    {
        var cell = Cell(row, column);
        if (cell is null)
            return string.Empty;
        return (string?)cell.Attribute("t") switch
        {
            "s" when int.TryParse(cell.Element(Main + "v")?.Value, out var i) && i < book.Strings.Count => book.Strings[i],
            "inlineStr" => RichText(cell.Element(Main + "is")!),
            _ => cell.Element(Main + "v")?.Value ?? string.Empty,
        };
    }

    private static double Value(XElement row, string column) =>
        Cell(row, column)?.Element(Main + "v") is { } v
        && double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>Writes a number in the cell of <paramref name="row"/>, creating it in column order if needed.</summary>
    private static void Set(XElement row, string column, double value)
    {
        var cell = Cell(row, column);
        if (cell is null)
        {
            cell = new XElement(Main + "c", new XAttribute("r", column + (string)row.Attribute("r")!));
            var after = row.Elements(Main + "c").LastOrDefault(c =>
                ColumnIndex(SplitRef((string)c.Attribute("r")!).Column) < ColumnIndex(column));
            if (after is null)
                row.AddFirst(cell);
            else
                after.AddAfterSelf(cell);
        }
        cell.SetAttributeValue("t", null);
        cell.Element(Main + "is")?.Remove();
        var v = cell.Element(Main + "v");
        if (v is null)
            cell.Add(v = new XElement(Main + "v"));
        v.Value = Number(Math.Round(value, 10));
    }

    private static bool HasFormula(XElement row, string column) => Cell(row, column)?.Element(Main + "f") is not null;

    public static string? TitleOf(byte[] bytes)
    {
        try
        {
            var book = Open(bytes);
            var first = book.Rows.FirstOrDefault();
            return first is null ? null : Text(book, first, "A");
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException or IOException)
        {
            return null;
        }
    }

    public static bool HasLabel(byte[] bytes, string label)
    {
        var book = Open(bytes);
        return book.Rows.Any(r => Key(Text(book, r, "A")) == label);
    }

    /// <summary>
    /// Puts the quantity and unit price of each item in its row (matched by name) and refreshes the
    /// cached results. Items with no row in the workbook are returned in <c>Missing</c>.
    /// </summary>
    public static (byte[] Bytes, double Total, List<string> Added, double AddedHeightPt) FillMaterials(byte[] bytes,
        IEnumerable<Occurrence> items)
    {
        var book = Open(bytes);
        var rows = book.Rows.ToList();
        var data = rows.Skip(1).TakeWhile(r => !Config.IsTotalLabel(Text(book, r, "A"))).ToList();
        var byName = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var row in data)
            byName.TryAdd(Config.LookupName(Text(book, row, "A")), row);

        var added = new List<(string Name, string Unit, double Quantity, double Price)>();
        foreach (var group in items.GroupBy(i => Config.LookupName(i.Name)))
        {
            if (!byName.TryGetValue(group.Key, out var row))
            {
                // Not in the template: goes in a new row before the total.
                added.Add((group.First().Name, group.Select(i => i.Material.Unit).FirstOrDefault(u => u.Length > 0) ?? string.Empty,
                    group.Sum(i => i.Material.Quantity), group.Select(i => i.Material.UnitPrice).FirstOrDefault(p => p != 0)));
                continue;
            }
            Set(row, "C", group.Sum(i => i.Material.Quantity));
            var price = group.Select(i => i.Material.UnitPrice).FirstOrDefault(p => p != 0);
            if (price != 0)
                Set(row, "D", price);
        }

        var addedHeight = 0.0;
        var totalRow0 = rows.FirstOrDefault(r => Config.IsTotalLabel(Text(book, r, "A")));
        if (added.Count > 0 && totalRow0 is not null && data.Count > 0)
        {
            addedHeight = AddRows(book, data, totalRow0, added);
            rows = book.Rows.ToList();
            data = rows.Skip(1).TakeWhile(r => !Config.IsTotalLabel(Text(book, r, "A"))).ToList();
        }

        foreach (var row in data.Where(r => HasFormula(r, "E")))
            Set(row, "E", Value(row, "C") * Value(row, "D"));
        var total = 0.0;
        if (rows.FirstOrDefault(r => Config.IsTotalLabel(Text(book, r, "A"))) is { } totalRow)
        {
            foreach (var column in new[] { "C", "D", "E" }.Where(c => HasFormula(totalRow, c)))
                Set(totalRow, column, data.Sum(r => Value(r, column)));
            total = Value(totalRow, "E");
        }
        return (Save(book), total, added.Select(a => a.Name).ToList(), addedHeight);
    }

    private static readonly Regex Range = new(@"(\$?[A-Z]{1,3}\$?)(\d+):(\$?[A-Z]{1,3}\$?)(\d+)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Inserts one row per item before the total row, copying the style of the last data row, and moves the
    /// total (and what follows) down. Sum ranges, the sheet's dimension and the visible range follow.
    /// Returns how much taller the sheet got, in points.
    /// </summary>
    private static double AddRows(Book book, List<XElement> data, XElement totalRow,
        List<(string Name, string Unit, double Quantity, double Price)> items)
    {
        var n = items.Count;
        var totalNo = int.Parse((string)totalRow.Attribute("r")!, CultureInfo.InvariantCulture);
        var model = data[^1];

        // Rows from the total down move by n, and ranges that ended right above the total grow to cover the new rows.
        string Shift(string formula) => Range.Replace(formula, m =>
        {
            var end = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
            var start = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            if (end >= totalNo - 1)
                end += n;
            if (start >= totalNo)
                start += n;
            return $"{m.Groups[1].Value}{start}:{m.Groups[3].Value}{end}";
        });
        foreach (var f in book.Sheet.Descendants(Main + "f"))
        {
            f.Value = Shift(f.Value);
        }

        // Merged cells, conditional formats and the like point at rows by address too.
        var cellRef = new Regex(@"([A-Z]{1,3})(\d+)", RegexOptions.CultureInvariant);
        string MoveRefs(string refs) => cellRef.Replace(refs, m =>
        {
            var no = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return m.Groups[1].Value + (no >= totalNo ? no + n : no);
        });
        foreach (var e in book.Sheet.Descendants().Where(e => e.Name.LocalName is "mergeCell" or "hyperlink" or "conditionalFormatting" or "dataValidation"))
        {
            foreach (var name in new[] { "ref", "sqref" })
            {
                if ((string?)e.Attribute(name) is { } value)
                    e.SetAttributeValue(name, MoveRefs(value));
            }
        }

        foreach (var row in book.Rows.Where(r => int.Parse((string)r.Attribute("r")!, CultureInfo.InvariantCulture) >= totalNo).ToList())
        {
            var no = int.Parse((string)row.Attribute("r")!, CultureInfo.InvariantCulture) + n;
            row.SetAttributeValue("r", no);
            foreach (var cell in row.Elements(Main + "c"))
                cell.SetAttributeValue("r", SplitRef((string)cell.Attribute("r")!).Column + no);
        }

        var modelNo = int.Parse((string)model.Attribute("r")!, CultureInfo.InvariantCulture);
        var anchor = model;
        for (var i = 0; i < n; i++)
        {
            var no = totalNo + i;
            var row = new XElement(model);
            row.SetAttributeValue("r", no);
            row.SetAttributeValue("ht", null);
            row.SetAttributeValue("customHeight", null);
            foreach (var cell in row.Elements(Main + "c"))
            {
                var column = SplitRef((string)cell.Attribute("r")!).Column;
                cell.SetAttributeValue("r", column + no);
                cell.SetAttributeValue("t", null);
                cell.Elements().Remove();
            }
            var (name, unit, quantity, price) = items[i];
            if (unit.Equals("un", StringComparison.OrdinalIgnoreCase))
                unit = "uni"; // the template's spelling
            SetText(row, "A", name);
            SetText(row, "B", unit);
            Set(row, "C", quantity);
            Set(row, "D", price);
            Set(row, "E", quantity * price);
            Cell(row, "E")!.AddFirst(new XElement(Main + "f", $"C{no}*D{no}"));
            anchor.AddAfterSelf(row);
            anchor = row;
        }

        var height = n * (double.TryParse((string?)model.Attribute("ht"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ht)
            ? ht
            : double.TryParse((string?)book.Sheet.Descendants(Main + "sheetFormatPr").FirstOrDefault()?.Attribute("defaultRowHeight"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 15);
        if (book.Sheet.Descendants(Main + "dimension").FirstOrDefault() is { } dimension && (string?)dimension.Attribute("ref") is { } dref)
            dimension.SetAttributeValue("ref", Regex.Replace(dref, @"(\d+)$", m => (int.Parse(m.Value, CultureInfo.InvariantCulture) + n).ToString(CultureInfo.InvariantCulture)));
        book.AddedRows = n;
        _ = modelNo;
        return height;
    }

    /// <summary>Writes text in the cell of <paramref name="row"/> (created if missing), keeping its style.</summary>
    private static void SetText(XElement row, string column, string text)
    {
        var cell = Cell(row, column);
        if (cell is null)
        {
            cell = new XElement(Main + "c", new XAttribute("r", column + (string)row.Attribute("r")!));
            row.Add(cell);
        }
        cell.Elements().Remove();
        cell.SetAttributeValue("t", "inlineStr");
        cell.Add(new XElement(Main + "is", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
    }

    /// <summary>Fills the "CUSTO ESTIMADO" summary: the lines named in <paramref name="values"/>, then its sums.</summary>
    public static byte[] FillSummary(byte[] bytes, Dictionary<string, double> values)
    {
        var book = Open(bytes);
        var byLabel = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var row in book.Rows)
            byLabel.TryAdd(Key(Text(book, row, "A")), row);

        foreach (var (label, value) in values)
        {
            if (byLabel.TryGetValue(Key(label), out var row))
                Set(row, "B", value);
        }

        double B(string label) => byLabel.TryGetValue(Key(label), out var r) ? Value(r, "B") : 0;
        void Sum(string label, double value)
        {
            if (byLabel.TryGetValue(Key(label), out var r) && HasFormula(r, "C"))
                Set(r, "C", value);
        }

        var material = B("MATERIAL CLASSE \"L\"") + B("MATERIAL CLASSE \"F\"") + B("MATERIAL DE TORRE") + B("MATERIAL DATA CENTER");
        Sum("MATERIAL CLASSE \"L\"", material);
        Sum("PROJETO LICENCIAMENTO", B("PROJETO LICENCIAMENTO"));
        Sum("MÃO DE OBRA REGIONAL", B("MÃO DE OBRA REGIONAL"));
        var labor = B("MÃO DE OBRA CLASSE \"L\"") + B("MÃO DE OBRA CLASSE \"F\"");
        Sum("MÃO DE OBRA CLASSE \"L\"", labor);
        var investment = material + B("PROJETO LICENCIAMENTO") + B("MÃO DE OBRA REGIONAL") + labor;
        byLabel.TryGetValue("INVESTIMENTO", out var invest);
        if (invest is not null)
            Set(invest, "B", investment);
        if (byLabel.TryGetValue("CUSTO ESTIMADO", out var head))
            Set(head, "B", book.Rows.Where(r => r != head && r != invest).Sum(r => Value(r, "B")));
        return Save(book);
    }

    /// <summary>The workbook with the edited sheet, asking Excel to recalculate when the object is opened.</summary>
    private static byte[] Save(Book book)
    {
        using var output = new MemoryStream();
        using (var result = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in book.Zip.Entries)
            {
                using var stream = result.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
                if (entry.FullName == book.SheetPath)
                    stream.Write(Serialize(book.Sheet));
                else if (entry.FullName == "xl/workbook.xml")
                {
                    var workbook = LoadXml(book.Zip, entry.FullName);
                    var calc = workbook.Root!.Element(Main + "calcPr");
                    if (calc is null)
                        workbook.Root.Element(Main + "sheets")!.AddAfterSelf(calc = new XElement(Main + "calcPr"));
                    calc.SetAttributeValue("fullCalcOnLoad", "1");
                    if (book.AddedRows > 0 && workbook.Root.Element(Main + "oleSize") is { } ole && (string?)ole.Attribute("ref") is { } oref)
                        ole.SetAttributeValue("ref", Regex.Replace(oref, @"(\d+)$",
                            m => (int.Parse(m.Value, CultureInfo.InvariantCulture) + book.AddedRows).ToString(CultureInfo.InvariantCulture)));
                    stream.Write(Serialize(workbook));
                }
                else
                {
                    using var original = entry.Open();
                    original.CopyTo(stream);
                }
            }
        }
        book.Zip.Dispose();
        return output.ToArray();
    }
}
