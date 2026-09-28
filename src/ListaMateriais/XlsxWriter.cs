using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static ListaMateriais.OoXml;

namespace ListaMateriais;

/// <summary>
/// Fills the .xlsx template by editing the sheet XML directly (no Excel needed),
/// keeping layout, styles and every other package part.
/// </summary>
internal static partial class XlsxWriter
{
    private const long MaxTemplateBytes = 200L * 1024 * 1024;

    private static readonly XName Row = Main + "row";
    private static readonly XName Cell = Main + "c";
    private static readonly XName Value = Main + "v";
    private static readonly XName Formula = Main + "f";

    /// <returns>Materials missing from the template that were added as new rows.</returns>
    public static IReadOnlyList<string> Fill(
        OrderedDictionary<string, Material> materials, string output, string template)
    {
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(template), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O arquivo de saída não pode ser o próprio modelo.");
        if (new FileInfo(template).Length > MaxTemplateBytes)
            throw new InvalidDataException("O modelo é grande demais.");

        using var templateStream = new MemoryStream(File.ReadAllBytes(template), writable: false);
        using var zip = new ZipArchive(templateStream, ZipArchiveMode.Read);

        var sheetPath = Sheets(zip).FirstOrDefault(s => s.Name == Config.SheetName)?.Path
            ?? throw new InvalidDataException($"A aba \"{Config.SheetName}\" não existe no modelo.");
        var document = LoadXml(zip, sheetPath);
        var sheet = new Sheet(document.Root!);
        var strings = SharedStrings(zip);

        var rowByDescription = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (row, text) in sheet.TextsByRow("B", strings))
        {
            if (row >= Config.FirstItemRow)
                rowByDescription[text] = row;
        }
        var totalRows = sheet.TextsByRow("G", strings).Where(t => t.Text == "TOTAL").Select(t => t.Row).ToList();
        if (totalRows.Count == 0)
            throw new InvalidDataException("A linha TOTAL (coluna G) não foi encontrada no modelo.");
        var lastTotal = totalRows.Max();

        var notFound = new OrderedDictionary<string, Material>(StringComparer.Ordinal);
        var filledRows = new HashSet<int>();
        foreach (var (name, material) in materials)
        {
            if (!rowByDescription.TryGetValue(Config.LookupName(name), out var rowNumber))
            {
                notFound[name] = material;
                continue;
            }
            filledRows.Add(rowNumber);
            sheet.SetContent($"D{rowNumber}", new XElement(Value, Number(material.Quantity)));
        }

        sheet.FixTotalFormulas();
        sheet.UnshareFormulas();
        foreach (var row in filledRows)
        {
            if (sheet.FindCell($"H{row}")?.Element(Formula) is null)
                sheet.SetFormula($"H{row}", $"G{row}*D{row}");
        }

        if (notFound.Count > 0)
        {
            sheet.InsertRows(lastTotal - 1, notFound);
            for (var i = 0; i < notFound.Count; i++)
                filledRows.Add(lastTotal + i);
            lastTotal += notFound.Count;
        }

        var unused = Enumerable.Range(Config.FirstItemRow, Math.Max(0, lastTotal - Config.FirstItemRow))
            .Where(r => !filledRows.Contains(r))
            .ToHashSet();
        sheet.DeleteRows(unused);
        sheet.SetContent(Config.SectionTitleCell);
        foreach (var (cellRef, formula) in Config.GrandTotal(Config.FirstItemRow + filledRows.Count))
            sheet.SetFormula(cellRef, formula);
        sheet.DropCachedFormulaValues();

        WritePackage(zip, output, sheetPath, document);
        return notFound.Keys.ToList();
    }

    private static void WritePackage(ZipArchive template, string output, string sheetPath, XDocument sheet)
    {
        var workbook = LoadXml(template, "xl/workbook.xml");
        ForceRecalculation(workbook.Root!);

        var contentTypes = LoadXml(template, "[Content_Types].xml");
        contentTypes.Root!.Elements(ContentTypes + "Override")
            .Where(e => ((string?)e.Attribute("PartName") ?? "").Contains("calcChain", StringComparison.Ordinal))
            .Remove();

        var rels = LoadXml(template, "xl/_rels/workbook.xml.rels");
        rels.Root!.Elements(PackageRels + "Relationship")
            .Where(e => ((string?)e.Attribute("Type") ?? "").EndsWith("/calcChain", StringComparison.Ordinal)
                        || ((string?)e.Attribute("Target") ?? "").Contains("calcChain", StringComparison.Ordinal))
            .Remove();

        var replaced = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [sheetPath] = Serialize(sheet),
            ["xl/workbook.xml"] = Serialize(workbook),
            ["[Content_Types].xml"] = Serialize(contentTypes),
            ["xl/_rels/workbook.xml.rels"] = Serialize(rels),
        };

        // Write to a temp file and swap at the end so a half-written spreadsheet is never left behind.
        var fullOutput = Path.GetFullPath(output);
        var temp = Path.Combine(Path.GetDirectoryName(fullOutput)!, $".~{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var outZip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var entry in template.Entries)
                {
                    if (entry.FullName == "xl/calcChain.xml")
                        continue;
                    var bytes = replaced.TryGetValue(entry.FullName, out var data) ? data : ReadBytes(entry);
                    using var target = outZip.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
                    target.Write(bytes);
                }
            }
            File.Move(temp, fullOutput, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static void ForceRecalculation(XElement workbook)
    {
        var calcPr = workbook.Element(Main + "calcPr");
        if (calcPr is null)
        {
            calcPr = new XElement(Main + "calcPr");
            // Schema order: calcPr must come right after these elements.
            var anchor = workbook.Elements()
                .LastOrDefault(e => e.Name.LocalName is "sheets" or "functionGroups" or "externalReferences" or "definedNames");
            if (anchor is null)
                workbook.Add(calcPr);
            else
                anchor.AddAfterSelf(calcPr);
        }
        calcPr.SetAttributeValue("fullCalcOnLoad", "1");
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])(\$?[A-Z]{1,3}\$?)(\d+)(?![A-Za-z0-9_(.])")]
    private static partial Regex CellRefPattern();

    /// <summary>Operations on a worksheet's XML.</summary>
    private sealed class Sheet(XElement root)
    {
        private readonly XElement _sheetData = root.Element(Main + "sheetData")
            ?? throw new InvalidDataException("A aba do modelo não tem sheetData.");

        private static int RowNumber(XElement row) =>
            int.Parse((string?)row.Attribute("r") ?? throw new InvalidDataException("Linha sem número no modelo."),
                NumberStyles.None, CultureInfo.InvariantCulture);

        private XElement? FindRow(int number) =>
            _sheetData.Elements(Row).FirstOrDefault(r => RowNumber(r) == number);

        public XElement? FindCell(string cellRef) =>
            FindRow(SplitRef(cellRef).Row)?.Elements(Cell).FirstOrDefault(c => (string?)c.Attribute("r") == cellRef);

        /// <summary>Normalized texts of a column's plain-value cells.</summary>
        public IEnumerable<(int Row, string Text)> TextsByRow(string column, IReadOnlyList<string> strings)
        {
            foreach (var cell in _sheetData.Elements(Row).Elements(Cell))
            {
                if ((string?)cell.Attribute("r") is not { } cellRef)
                    continue;
                var (col, row) = SplitRef(cellRef);
                if (col != column || cell.Elements().FirstOrDefault() is not { } first || first.Name != Value)
                    continue;
                var raw = first.Value;
                var text = (string?)cell.Attribute("t") == "s"
                    ? int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < strings.Count
                        ? strings[i]
                        : null
                    : raw;
                if (text is not null)
                    yield return (row, Config.Normalize(text));
            }
        }

        private XElement GetOrCreateCell(string cellRef)
        {
            var (column, rowNumber) = SplitRef(cellRef);
            var row = FindRow(rowNumber);
            if (row is null)
            {
                row = new XElement(Row, new XAttribute("r", rowNumber));
                var next = _sheetData.Elements(Row).FirstOrDefault(r => RowNumber(r) > rowNumber);
                if (next is null)
                    _sheetData.Add(row);
                else
                    next.AddBeforeSelf(row);
            }

            var cell = row.Elements(Cell).FirstOrDefault(c => (string?)c.Attribute("r") == cellRef);
            if (cell is not null)
                return cell;

            cell = new XElement(Cell, new XAttribute("r", cellRef));
            var index = ColumnIndex(column);
            var after = row.Elements(Cell).FirstOrDefault(c =>
                (string?)c.Attribute("r") is { } r && ColumnIndex(SplitRef(r).Column) > index);
            if (after is null)
                row.Add(cell);
            else
                after.AddBeforeSelf(cell);
            return cell;
        }

        /// <summary>Replaces the cell content while keeping its style.</summary>
        public void SetContent(string cellRef, params XElement[] content)
        {
            var cell = GetOrCreateCell(cellRef);
            cell.Attribute("t")?.Remove();
            cell.RemoveNodes();
            cell.Add(content);
        }

        public void SetFormula(string cellRef, string formula) => SetContent(cellRef, new XElement(Formula, formula));

        /// <summary>Totals that multiplied by $I$9 now use the row's own quantity.</summary>
        public void FixTotalFormulas()
        {
            foreach (var cell in _sheetData.Elements(Row).Elements(Cell))
            {
                if ((string?)cell.Attribute("r") is not { } cellRef || !cellRef.StartsWith('H'))
                    continue;
                if (cell.Element(Formula) is { } f && f.Value.Contains("$I$9", StringComparison.Ordinal))
                    f.Value = f.Value.Replace("$I$9", $"D{SplitRef(cellRef).Row}", StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Shared formulas break when rows move or are deleted. Masters become plain
        /// formulas; dependents in column H become G*D; the rest keep their value only.
        /// </summary>
        public void UnshareFormulas()
        {
            var shared = _sheetData.Descendants(Formula).Where(f => (string?)f.Attribute("t") == "shared").ToList();
            foreach (var f in shared)
            {
                if (f.Value.Length > 0)
                {
                    f.RemoveAttributes();
                    continue;
                }
                var (column, row) = SplitRef((string)f.Parent!.Attribute("r")!);
                if (column == "H")
                    f.ReplaceWith(new XElement(Formula, $"G{row}*D{row}"));
                else
                    f.Remove();
            }
        }

        public void InsertRows(int afterRow, OrderedDictionary<string, Material> materials)
        {
            var template = FindRow(afterRow)
                ?? throw new InvalidDataException($"Linha {afterRow} não existe no modelo.");
            ShiftRows(afterRow + 1, materials.Count);
            var newRows = materials.Select((item, i) => NewRow(template, afterRow + 1 + i, item.Key, item.Value));
            template.AddAfterSelf(newRows.ToList());
        }

        private static XElement NewRow(XElement template, int rowNumber, string name, Material material)
        {
            var styles = new SortedDictionary<int, (string Column, XAttribute? Style)>();
            foreach (var cell in template.Elements(Cell))
            {
                if ((string?)cell.Attribute("r") is { } r)
                {
                    var column = SplitRef(r).Column;
                    styles[ColumnIndex(column)] = (column, cell.Attribute("s"));
                }
            }

            var values = new Dictionary<string, object[]>
            {
                ["B"] = [new XAttribute("t", "inlineStr"), InlineString(name)],
                ["C"] = [new XAttribute("t", "inlineStr"), InlineString(material.Unit)],
                ["D"] = [new XElement(Value, Number(material.Quantity))],
                ["G"] = [new XElement(Value, Number(material.UnitPrice))],
                ["H"] = [new XElement(Formula, $"G{rowNumber}*D{rowNumber}")],
            };
            foreach (var column in values.Keys)
                styles.TryAdd(ColumnIndex(column), (column, null));

            var row = new XElement(Row, new XAttribute("r", rowNumber),
                template.Attributes().Where(a => a.Name != "r").Select(a => new XAttribute(a)));
            foreach (var (column, style) in styles.Values)
            {
                row.Add(new XElement(Cell,
                    new XAttribute("r", $"{column}{rowNumber}"),
                    style is null ? null : new XAttribute(style),
                    values.GetValueOrDefault(column)));
            }
            return row;
        }

        private static XElement InlineString(string text) =>
            new(Main + "is", new XElement(Main + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));

        private void ShiftRows(int firstRow, int count) =>
            RemapRows(row => row >= firstRow ? row + count : row);

        public void DeleteRows(IReadOnlySet<int> rows)
        {
            if (rows.Count == 0)
                return;

            _sheetData.Elements(Row).Where(r => rows.Contains(RowNumber(r))).Remove();

            if (root.Element(Main + "mergeCells") is { } merges)
            {
                merges.Elements(Main + "mergeCell").Where(m =>
                {
                    var range = ((string?)m.Attribute("ref") ?? "").Split(':');
                    var first = SplitRef(range[0]).Row;
                    var last = SplitRef(range[^1]).Row;
                    return Enumerable.Range(first, last - first + 1).All(rows.Contains);
                }).Remove();
                var remaining = merges.Elements(Main + "mergeCell").Count();
                if (remaining == 0)
                    merges.Remove();
                else
                    merges.SetAttributeValue("count", remaining);
            }

            var deleted = rows.Order().ToArray();
            RemapRows(row =>
            {
                var index = Array.BinarySearch(deleted, row);
                return row - (index >= 0 ? index : ~index);
            });
        }

        private static readonly HashSet<string> RefAttributes = ["r", "ref", "sqref", "activeCell", "topLeftCell"];

        /// <summary>Renumbers rows and every cell reference in the sheet.</summary>
        private void RemapRows(Func<int, int> newRow)
        {
            foreach (var row in _sheetData.Elements(Row))
                row.SetAttributeValue("r", newRow(RowNumber(row)));

            foreach (var element in root.Descendants())
            {
                if (element.Name == Row)
                    continue;
                foreach (var attribute in element.Attributes())
                {
                    if (attribute.Name.Namespace == XNamespace.None && RefAttributes.Contains(attribute.Name.LocalName))
                        attribute.Value = RemapRefs(attribute.Value, newRow);
                }
                if (element.Name.LocalName is "f" or "sqref" && !element.HasElements)
                    element.Value = RemapRefs(element.Value, newRow);
            }
        }

        private static string RemapRefs(string text, Func<int, int> newRow)
        {
            // Leave string literals ("...") inside formulas untouched.
            var parts = text.Split('"');
            for (var i = 0; i < parts.Length; i += 2)
            {
                parts[i] = CellRefPattern().Replace(parts[i], m =>
                    int.TryParse(m.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var row)
                        ? m.Groups[1].Value + newRow(row).ToString(CultureInfo.InvariantCulture)
                        : m.Value);
            }
            return string.Join('"', parts);
        }

        public void DropCachedFormulaValues()
        {
            foreach (var cell in _sheetData.Elements(Row).Elements(Cell))
            {
                if (cell.Element(Formula) is not null)
                    cell.Elements(Value).Remove();
            }
        }
    }
}
