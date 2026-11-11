using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
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
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    private static readonly XName Row = Main + "row";
    private static readonly XName Cell = Main + "c";
    private static readonly XName Value = Main + "v";
    private static readonly XName Formula = Main + "f";

    /// <summary>Throws <see cref="AppException"/> when the file cannot be used as a template.</summary>
    public static void CheckTemplate(string template) =>
        WithTemplate(template, zip => FindSheet(zip, template));

    /// <returns>Materials missing from the template that were added as new rows.</returns>
    public static IReadOnlyList<string> Fill(OrderedDictionary<string, Material> materials, ProjectInfo info,
        string output, string template, OutputLayout layout = OutputLayout.Simplified,
        bool hideEmptySections = false)
    {
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(template), StringComparison.OrdinalIgnoreCase))
            throw new AppException("A planilha não pode ser salva por cima do próprio modelo.\n\n" +
                                   "Escolha outro nome ou outra pasta.");

        return WithTemplate(template, zip => Fill(zip, materials, info, output, template, layout, hideEmptySections));
    }

    /// <summary>Opens the template, turning read and format errors into messages for the user.</summary>
    private static T WithTemplate<T>(string template, Func<ZipArchive, T> action)
    {
        var name = Path.GetFileName(template);
        byte[] bytes;
        try
        {
            if (new FileInfo(template).Length > MaxTemplateBytes)
                throw new AppException($"O modelo \"{name}\" é grande demais (máximo de " +
                                       $"{MaxTemplateBytes / 1024 / 1024} MB).");
            bytes = Files.ReadAllBytesShared(template);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new AppException($"O modelo \"{name}\" não foi encontrado.\n\n" +
                                   "Ele pode ter sido movido ou excluído. Escolha o modelo na página \"Modelos\".", e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new AppException($"Sem permissão para ler o modelo \"{name}\".", e);
        }
        catch (IOException e)
        {
            throw new AppException($"Não foi possível ler o modelo \"{name}\".\n\n" +
                                   "Se ele estiver aberto no Excel, feche-o e tente de novo.", e);
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            return action(zip);
        }
        catch (Exception e) when (e is InvalidDataException or XmlException)
        {
            throw new AppException($"O modelo \"{name}\" não é uma planilha .xlsx válida ou está corrompido.\n\n" +
                                   $"Detalhe: {e.Message}\n\n" +
                                   "Escolha outro modelo na página \"Modelos\".", e);
        }
    }

    /// <summary>The materials sheet: by name, ignoring case, or the only sheet of the workbook.</summary>
    private static string FindSheet(ZipArchive zip, string template)
    {
        var sheets = Sheets(zip);
        var match = sheets.FirstOrDefault(s => s.Name == Config.SheetName)
                    ?? sheets.FirstOrDefault(s => Config.Normalize(s.Name) == Config.SheetName)
                    ?? (sheets.Count == 1 ? sheets[0] : null);
        if (match is not null)
            return match.Path;
        throw new AppException(
            $"O modelo \"{Path.GetFileName(template)}\" não tem a aba \"{Config.SheetName}\".\n\n" +
            $"Abas encontradas: {string.Join(", ", sheets.Select(s => s.Name))}.\n\n" +
            $"Renomeie a aba da lista de materiais para {Config.SheetName} ou escolha outro modelo.");
    }

    private static IReadOnlyList<string> Fill(ZipArchive zip, OrderedDictionary<string, Material> materials,
        ProjectInfo info, string output, string template, OutputLayout layout, bool hideEmptySections)
    {
        var sheetPath = FindSheet(zip, template);
        var document = LoadXml(zip, sheetPath);
        var sheet = new Sheet(document.Root!);
        var strings = SharedStrings(zip);

        var texts = sheet.Texts(strings).Where(t => t.Row >= Config.FirstItemRow).ToList();
        var descriptions = texts.Where(t => t.Column == "B").ToList();

        // The last TOTAL line (a "Total" label or a SUM in column H) closes the list.
        // Templates without one get it right after the last item.
        var totalRows = texts.Where(t => Config.IsTotalLabel(t.Text)).Select(t => t.Row)
            .Concat(sheet.SumRows("H").Where(r => r >= Config.FirstItemRow))
            .ToList();
        int lastTotal;
        if (totalRows.Count > 0)
        {
            lastTotal = totalRows.Max();
        }
        else
        {
            lastTotal = descriptions.Select(t => t.Row).DefaultIfEmpty(Config.FirstItemRow - 1).Max() + 1;
            sheet.InsertTotalRow(lastTotal);
        }

        var rowByDescription = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, row, text) in descriptions)
        {
            if (row < lastTotal)
                rowByDescription[text] = row;
        }

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

        // Section subtotals of the complete layout: every TOTAL line before the last one.
        var sectionTotals = totalRows.Where(r => r < lastTotal).Distinct().Order().ToList();
        if (layout == OutputLayout.Complete)
        {
            foreach (var row in descriptions.Select(t => t.Row).Where(r => r < lastTotal).Distinct())
            {
                if (!filledRows.Contains(row) && !sectionTotals.Contains(row))
                    sheet.SetContent($"D{row}", new XElement(Value, "0"));
            }
        }

        var styles = LoadXml(zip, "xl/styles.xml");
        if (info.Code is { } code)
        {
            sheet.SetContent(Config.ProjectCodeCell, new XElement(Value, code.ToString(CultureInfo.InvariantCulture)));
            // Keeps leading zeros visible (0123) while the cell stays a number.
            sheet.SetStyle(Config.ProjectCodeCell, WithNumberFormat(styles, sheet.GetStyle(Config.ProjectCodeCell), "0000"));
        }
        if (info.Date is { } date)
            sheet.SetText(Config.DateCell, date);
        if (info.City is { } city)
            sheet.SetText(Config.CityCell, city);

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

        int lastRow;
        if (layout == OutputLayout.Simplified)
        {
            // Only the filled rows stay, as one list closed by the TOTAL line.
            var unused = Enumerable.Range(Config.FirstItemRow, Math.Max(0, lastTotal - Config.FirstItemRow))
                .Where(r => !filledRows.Contains(r))
                .ToHashSet();
            sheet.DeleteRows(unused);
            sheet.SetContent(Config.SectionTitleCell);
            lastRow = Config.FirstItemRow + filledRows.Count;
            foreach (var (cellRef, formula) in Config.GrandTotal(lastRow))
                sheet.SetFormula(cellRef, formula);
        }
        else
        {
            // Each TOTAL line sums its section; the grand total sums the TOTAL lines.
            List<int> totals = [.. sectionTotals, lastTotal];
            var start = Config.FirstItemRow;
            foreach (var total in totals)
            {
                sheet.SetFormula($"H{total}", total > start ? $"SUM(H{start}:H{total - 1})" : "0");
                start = total + 1;
            }
            sheet.SetFormula(Config.GrandTotalCell, $"SUM({string.Join(",", totals.Select(t => $"H{t}"))})");
            lastRow = lastTotal;
        }

        if (layout == OutputLayout.Compact)
        {
            // Section titles (text in A only) and TOTAL lines stay as dividers; unused items are hidden.
            var titles = texts.GroupBy(t => t.Row)
                .Where(g => g.All(t => t.Column == "A"))
                .Select(g => g.Key)
                .ToHashSet();
            var unused = texts.Select(t => t.Row)
                .Where(r => r < lastTotal && !filledRows.Contains(r) && !titles.Contains(r) && !sectionTotals.Contains(r))
                .ToHashSet();
            if (hideEmptySections)
            {
                // A section with no material loses its title and TOTAL line too.
                var start = Config.FirstItemRow;
                foreach (var total in new List<int>([.. sectionTotals, lastTotal]))
                {
                    if (!Enumerable.Range(start, Math.Max(0, total - start)).Any(filledRows.Contains))
                        unused.UnionWith(Enumerable.Range(start, total - start + 1));
                    start = total + 1;
                }
            }
            sheet.HideRows(unused);
        }

        // Everything centered, except the item descriptions, which read better left-aligned.
        var aligned = new Dictionary<(int, string), int>();
        sheet.AlignRows(Config.HeaderRow, lastRow,
            (column, row) => column == "B" && row >= Config.FirstItemRow ? "left" : "center",
            (style, horizontal) => AlignedStyle(styles, aligned, style, horizontal));
        sheet.DropCachedFormulaValues();

        WritePackage(zip, output, sheetPath, document, styles);
        return notFound.Keys.ToList();
    }

    /// <summary>Adds a cell format like <paramref name="baseStyle"/> with a custom number format, centered.</summary>
    private static int WithNumberFormat(XDocument styles, int baseStyle, string formatCode)
    {
        var root = styles.Root!;
        var numFmts = root.Element(Main + "numFmts");
        if (numFmts is null)
        {
            numFmts = new XElement(Main + "numFmts");
            root.AddFirst(numFmts);
        }
        var format = numFmts.Elements(Main + "numFmt").FirstOrDefault(n => (string?)n.Attribute("formatCode") == formatCode);
        if (format is null)
        {
            var id = numFmts.Elements(Main + "numFmt")
                .Select(n => (int?)n.Attribute("numFmtId") ?? 0).DefaultIfEmpty(163).Max() + 1;
            format = new XElement(Main + "numFmt", new XAttribute("numFmtId", Math.Max(id, 164)),
                new XAttribute("formatCode", formatCode));
            numFmts.Add(format);
            numFmts.SetAttributeValue("count", numFmts.Elements(Main + "numFmt").Count());
        }

        var cellXfs = root.Element(Main + "cellXfs") ?? throw new InvalidDataException("O modelo não tem cellXfs.");
        var xfs = cellXfs.Elements(Main + "xf").ToList();
        var xf = new XElement(baseStyle < xfs.Count ? xfs[baseStyle] : new XElement(Main + "xf"));
        xf.SetAttributeValue("numFmtId", (string?)format.Attribute("numFmtId"));
        xf.SetAttributeValue("applyNumberFormat", "1");
        // Numbers default to right-aligned, which pushes them to the far edge of the wide column B.
        SetHorizontal(xf, "center");
        cellXfs.Add(xf);
        cellXfs.SetAttributeValue("count", xfs.Count + 1);
        return xfs.Count;
    }

    /// <summary>A cell format like <paramref name="baseStyle"/> with the given alignment; created once per pair.</summary>
    private static int AlignedStyle(XDocument styles, Dictionary<(int, string), int> cache, int baseStyle,
        string horizontal)
    {
        if (cache.TryGetValue((baseStyle, horizontal), out var known))
            return known;
        var cellXfs = styles.Root!.Element(Main + "cellXfs") ?? throw new InvalidDataException("O modelo não tem cellXfs.");
        var xfs = cellXfs.Elements(Main + "xf").ToList();
        var source = baseStyle < xfs.Count ? xfs[baseStyle] : null;
        if ((string?)source?.Element(Main + "alignment")?.Attribute("horizontal") == horizontal)
            return cache[(baseStyle, horizontal)] = baseStyle;

        var xf = source is null
            ? new XElement(Main + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0),
                new XAttribute("fillId", 0), new XAttribute("borderId", 0))
            : new XElement(source);
        SetHorizontal(xf, horizontal);
        cellXfs.Add(xf);
        cellXfs.SetAttributeValue("count", xfs.Count + 1);
        return cache[(baseStyle, horizontal)] = xfs.Count;
    }

    private static void SetHorizontal(XElement xf, string horizontal)
    {
        xf.SetAttributeValue("applyAlignment", "1");
        var alignment = xf.Element(Main + "alignment");
        if (alignment is null)
            xf.AddFirst(alignment = new XElement(Main + "alignment")); // schema: before protection/extLst
        alignment.SetAttributeValue("horizontal", horizontal);
    }

    private static void WritePackage(ZipArchive template, string output, string sheetPath, XDocument sheet,
        XDocument styles)
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
            ["xl/styles.xml"] = Serialize(styles),
            ["[Content_Types].xml"] = Serialize(contentTypes),
            ["xl/_rels/workbook.xml.rels"] = Serialize(rels),
        };

        // Write to a temp file and swap at the end so a half-written spreadsheet is never left behind.
        var fullOutput = Path.GetFullPath(output);
        var folder = Path.GetDirectoryName(fullOutput)!;
        var temp = Path.Combine(folder, $".~{Guid.NewGuid():N}.tmp");
        var name = Path.GetFileName(fullOutput);
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
        catch (Exception e) when (File.Exists(fullOutput)
                                  && (e is UnauthorizedAccessException || e.HResult is SharingViolation or LockViolation))
        {
            throw new AppException($"Não foi possível substituir \"{name}\".\n\n" +
                                   "Se essa planilha estiver aberta no Excel, feche-a e tente de novo. " +
                                   "Ou salve com outro nome.", e);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            throw new AppException($"Não foi possível salvar na pasta:\n{folder}\n\n" +
                                   "A pasta não existe ou você não tem permissão para gravar nela. " +
                                   "Escolha outra pasta.", e);
        }
        catch (IOException e)
        {
            throw new AppException($"Não foi possível salvar \"{name}\".\n\n" +
                                   "Verifique se há espaço em disco e se a pasta está acessível.\n\n" +
                                   $"Detalhe: {e.Message}", e);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Não foi possível apagar o arquivo temporário {0}: {1}", temp, e.Message);
            }
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

        /// <summary>Normalized, non-empty texts of the cells that hold a value (not a formula).</summary>
        public IEnumerable<(string Column, int Row, string Text)> Texts(IReadOnlyList<string> strings)
        {
            foreach (var cell in _sheetData.Elements(Row).Elements(Cell))
            {
                if ((string?)cell.Attribute("r") is not { } cellRef || cell.Element(Formula) is not null)
                    continue;
                var raw = cell.Element(Value)?.Value;
                var text = (string?)cell.Attribute("t") switch
                {
                    "inlineStr" => cell.Element(Main + "is") is { } inline ? RichText(inline) : null,
                    "s" => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var i)
                           && i < strings.Count ? strings[i] : null,
                    _ => raw,
                };
                if (text is null || Config.Normalize(text) is not { Length: > 0 } normalized)
                    continue;
                var (column, row) = SplitRef(cellRef);
                yield return (column, row, normalized);
            }
        }

        /// <summary>Rows whose cell in <paramref name="column"/> is a SUM(...) formula.</summary>
        public IEnumerable<int> SumRows(string column)
        {
            foreach (var cell in _sheetData.Elements(Row).Elements(Cell))
            {
                if ((string?)cell.Attribute("r") is not { } cellRef
                    || cell.Element(Formula)?.Value.TrimStart('=').StartsWith("SUM(", StringComparison.OrdinalIgnoreCase) != true)
                    continue;
                var (col, row) = SplitRef(cellRef);
                if (col == column)
                    yield return row;
            }
        }

        /// <summary>
        /// Sets the horizontal alignment of every cell in rows <paramref name="first"/> to
        /// <paramref name="last"/>, as chosen by <paramref name="alignment"/> from column and row.
        /// </summary>
        public void AlignRows(int first, int last, Func<string, int, string> alignment,
            Func<int, string, int> styleWith)
        {
            foreach (var row in _sheetData.Elements(Row))
            {
                var number = RowNumber(row);
                if (number < first || number > last)
                    continue;
                foreach (var cell in row.Elements(Cell))
                {
                    if ((string?)cell.Attribute("r") is not { } cellRef)
                        continue;
                    var style = int.TryParse((string?)cell.Attribute("s"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var s) ? s : 0;
                    cell.SetAttributeValue("s", styleWith(style, alignment(SplitRef(cellRef).Column, number)));
                }
            }
        }

        /// <summary>Adds a row in its sorted position.</summary>
        private void AddRow(XElement row)
        {
            var number = RowNumber(row);
            var next = _sheetData.Elements(Row).FirstOrDefault(r => RowNumber(r) > number);
            if (next is null)
                _sheetData.Add(row);
            else
                next.AddBeforeSelf(row);
        }

        /// <summary>Inserts a "TOTAL" row, styled like the row above it.</summary>
        public void InsertTotalRow(int rowNumber)
        {
            ShiftRows(rowNumber, 1);
            var above = FindRow(rowNumber - 1);
            XAttribute? StyleOf(string column) =>
                above?.Elements(Cell).FirstOrDefault(c => (string?)c.Attribute("r") == $"{column}{rowNumber - 1}")
                    ?.Attribute("s") is { } s ? new XAttribute(s) : null;

            AddRow(new XElement(Row, new XAttribute("r", rowNumber),
                new XElement(Cell, new XAttribute("r", $"G{rowNumber}"), StyleOf("G"),
                    new XAttribute("t", "inlineStr"), InlineString("TOTAL")),
                new XElement(Cell, new XAttribute("r", $"H{rowNumber}"), StyleOf("H"))));
        }

        private XElement GetOrCreateCell(string cellRef)
        {
            var (column, rowNumber) = SplitRef(cellRef);
            var row = FindRow(rowNumber);
            if (row is null)
            {
                row = new XElement(Row, new XAttribute("r", rowNumber));
                AddRow(row);
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
        public void SetContent(string cellRef, params object[] content)
        {
            var cell = GetOrCreateCell(cellRef);
            cell.Attribute("t")?.Remove();
            cell.RemoveNodes();
            cell.Add(content);
        }

        public int GetStyle(string cellRef) =>
            int.TryParse((string?)GetOrCreateCell(cellRef).Attribute("s"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var style) ? style : 0;

        public void SetStyle(string cellRef, int style) =>
            GetOrCreateCell(cellRef).SetAttributeValue("s", style);

        public void SetText(string cellRef, string text) =>
            SetContent(cellRef, new XAttribute("t", "inlineStr"), InlineString(text));

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
            // New rows copy the style of the row above; an empty row stands in when there is none.
            var template = FindRow(afterRow) ?? new XElement(Row, new XAttribute("r", afterRow));
            ShiftRows(afterRow + 1, materials.Count);
            var i = 0;
            foreach (var (name, material) in materials)
                AddRow(NewRow(template, afterRow + 1 + i++, name, material));
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

        public void HideRows(IReadOnlySet<int> rows)
        {
            foreach (var row in _sheetData.Elements(Row).Where(r => rows.Contains(RowNumber(r))))
                row.SetAttributeValue("hidden", "1");
        }

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
