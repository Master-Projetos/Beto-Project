using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using ListaMateriais.Kmz;
using static ListaMateriais.OoXml;

namespace ListaMateriais.Planilhas;

/// <summary>Writing the result of a job: cell fills, hidden rows and the filter buttons.</summary>
internal sealed partial class Workbook
{
    private const string StylesPath = "xl/styles.xml";

    /// <summary>
    /// Writes a copy of the workbook with the job done. A sheet that fails is left as it was and reported
    /// in its warnings. Nothing is written when there is nothing to change.
    /// </summary>
    public JobResult Save(Job job, string output)
    {
        if (string.Equals(System.IO.Path.GetFullPath(output), System.IO.Path.GetFullPath(Path), StringComparison.OrdinalIgnoreCase))
            throw new AppException("A planilha original não é alterada.\n\nSalve o resultado com outro nome ou em outra pasta.");

        try
        {
            using var zip = OpenZip(_bytes);
            var results = new List<SheetResult>();
            var replaced = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var styles = TryLoadXml(zip, StylesPath);
            var painter = styles is null ? null : new Painter(styles);
            foreach (var sheet in Chosen(job))
            {
                var plan = PlanSheet(sheet, job);
                if (painter is null && plan.Paint.Count > 0)
                {
                    plan.Paint.Clear();
                    plan.Warnings.Add("o arquivo não tem a parte de estilos; nada foi pintado. Abra e salve no Excel.");
                }
                if (plan.Paint.Count > 0 || plan.Filtered)
                {
                    try
                    {
                        var document = LoadXml(zip, sheet.Path);
                        Apply(document.Root!, plan, painter);
                        replaced[sheet.Path] = Serialize(document);
                    }
#pragma warning disable CA1031 // One bad sheet must not stop the others; the reason is reported and logged.
                    catch (Exception e)
#pragma warning restore CA1031
                    {
                        Trace.TraceWarning("Falha ao alterar a aba {0}: {1}", sheet.Name, e);
                        plan.Paint.Clear();
                        plan.Shown.Clear();
                        plan.Hidden.Clear();
                        plan.Filtered = false;
                        plan.Warnings.Add($"não foi possível alterar; ficou como estava ({e.Message}).");
                    }
                }
                results.Add(plan.Result(sheet.Name));
            }

            var result = new JobResult(results);
            if (!result.Changes)
                return result;
            if (painter is { Used: true })
                replaced[StylesPath] = Serialize(styles!);
            Files.SaveSafely(output, stream =>
            {
                using var target = new ZipArchive(stream, ZipArchiveMode.Create);
                foreach (var entry in zip.Entries)
                {
                    var bytes = replaced.TryGetValue(entry.FullName, out var data) ? data : ReadBytes(entry);
                    using var part = target.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
                    part.Write(bytes);
                }
            });
            return result;
        }
        catch (Exception e) when (e is InvalidDataException or XmlException)
        {
            throw new AppException($"Não foi possível alterar \"{System.IO.Path.GetFileName(Path)}\": " +
                                   $"o arquivo tem um conteúdo inválido.\n\nDetalhe: {e.Message}", e);
        }
    }

    private static void Apply(XElement worksheet, SheetPlan plan, Painter? painter)
    {
        var sheetData = worksheet.Element(Main + "sheetData") ?? throw new InvalidDataException("A aba não tem sheetData.");
        AssignReferences(sheetData);
        var rows = new SortedList<int, XElement>();
        foreach (var row in sheetData.Elements(Main + "row"))
            rows[RowNumber(row)] = row;

        XElement RowAt(int number)
        {
            if (rows.TryGetValue(number, out var row))
                return row;
            row = new XElement(Main + "row", new XAttribute("r", number));
            rows.Add(number, row);
            var index = rows.IndexOfKey(number);
            if (index == 0)
                sheetData.AddFirst(row);
            else
                rows.GetValueAtIndex(index - 1).AddAfterSelf(row);
            return row;
        }

        if (painter is not null)
        {
            var columnStyles = ColumnStyles(worksheet);
            foreach (var line in plan.Paint.GroupBy(p => p.Key.Row).OrderBy(g => g.Key))
                PaintRow(RowAt(line.Key), line.Key, line.Select(p => (p.Key.Column, p.Value)), columnStyles, painter);
        }

        if (!plan.Filtered)
            return;
        foreach (var number in plan.Shown)
        {
            if (rows.TryGetValue(number, out var row))
                row.Attribute("hidden")?.Remove();
        }
        foreach (var number in plan.Hidden)
            RowAt(number).SetAttributeValue("hidden", "1");
        AddAutoFilter(worksheet, plan);
    }

    private static void PaintRow(XElement row, int number, IEnumerable<(int Column, Color? Fill)> cells,
        IReadOnlyList<(int First, int Last, int Style)> columnStyles, Painter painter)
    {
        var existing = new SortedList<int, XElement>();
        foreach (var cell in row.Elements(Main + "c"))
            existing[ColumnIndex(SplitRef((string)cell.Attribute("r")!).Column) - 1] = cell;
        var rowStyle = row.Attribute("customFormat")?.Value is "1" or "true" ? (int?)row.Attribute("s") : null;

        foreach (var (column, fill) in cells.OrderBy(c => c.Column))
        {
            if (!existing.TryGetValue(column, out var cell))
            {
                // A new cell starts with the look the empty spot had: that of its row or column.
                var inherited = rowStyle
                                ?? columnStyles.Where(c => column + 1 >= c.First && column + 1 <= c.Last)
                                    .Select(c => c.Style).FirstOrDefault();
                cell = new XElement(Main + "c", new XAttribute("r", $"{SheetTable.ColumnLetter(column)}{number}"),
                    new XAttribute("s", inherited));
                existing.Add(column, cell);
                var index = existing.IndexOfKey(column);
                if (index > 0)
                    existing.GetValueAtIndex(index - 1).AddAfterSelf(cell);
                else if (existing.Count > 1)
                    existing.GetValueAtIndex(1).AddBeforeSelf(cell);
                else
                    row.AddFirst(cell);
                // "spans" is only a hint about the cells of the row, and may no longer hold.
                row.Attribute("spans")?.Remove();
            }
            cell.SetAttributeValue("s", painter.Style((int?)cell.Attribute("s") ?? 0, fill));
        }
    }

    private static List<(int First, int Last, int Style)> ColumnStyles(XElement worksheet) =>
        [.. worksheet.Elements(Main + "cols").Elements(Main + "col")
            .Select(c => (First: (int?)c.Attribute("min") ?? 0, Last: (int?)c.Attribute("max") ?? 0, Style: (int?)c.Attribute("style") ?? 0))
            .Where(c => c.Style > 0)];

    /// <summary>
    /// Puts Excel's filter buttons on the header. Left alone when the sheet already has a filter or a
    /// table (which brings its own, and two of them make Excel call the file damaged).
    /// </summary>
    private static void AddAutoFilter(XElement worksheet, SheetPlan plan)
    {
        if (plan.Columns.Count == 0 || worksheet.Element(Main + "autoFilter") is not null
            || worksheet.Element(Main + "tableParts") is not null)
            return;
        var first = SheetTable.ColumnLetter(plan.Columns[0].Index);
        var last = SheetTable.ColumnLetter(plan.Columns[^1].Index);
        // Schema order: autoFilter comes right after sheetData and the protection elements.
        var anchor = worksheet.Elements().Last(e => e.Name.LocalName is "sheetData" or "sheetCalcPr" or "sheetProtection"
            or "protectedRanges" or "scenarios");
        anchor.AddAfterSelf(new XElement(Main + "autoFilter",
            new XAttribute("ref", string.Create(CultureInfo.InvariantCulture, $"{first}{plan.HeaderRow}:{last}{plan.LastRow}"))));
    }

    /// <summary>Adds to the styles the fills and cell formats the painted cells need, once each.</summary>
    private sealed class Painter
    {
        private readonly XElement _fills;
        private readonly XElement _cellXfs;
        private readonly List<XElement> _formats;
        private readonly Dictionary<int, int> _fillIds = [];
        private readonly Dictionary<(int Style, int Fill), int> _styles = [];
        private int _fillCount;

        /// <summary>True once the styles were changed and have to be saved.</summary>
        public bool Used { get; private set; }

        public Painter(XDocument styles)
        {
            var root = styles.Root!;
            _fills = root.Element(Main + "fills") ?? throw new InvalidDataException("A planilha não tem a lista de preenchimentos.");
            _cellXfs = root.Element(Main + "cellXfs") ?? throw new InvalidDataException("A planilha não tem cellXfs.");
            _formats = [.. _cellXfs.Elements(Main + "xf")];
            _fillCount = _fills.Elements(Main + "fill").Count();
        }

        /// <summary>A cell format like <paramref name="baseStyle"/> with the given fill (null: no fill).</summary>
        public int Style(int baseStyle, Color? color)
        {
            var fill = color is { } c ? FillId(c) : 0;
            if (_styles.TryGetValue((baseStyle, fill), out var known))
                return known;

            var source = baseStyle >= 0 && baseStyle < _formats.Count ? _formats[baseStyle] : null;
            if (source is not null && ((int?)source.Attribute("fillId") ?? 0) == fill)
                return _styles[(baseStyle, fill)] = baseStyle;

            var xf = source is null
                ? new XElement(Main + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("borderId", 0))
                : new XElement(source);
            xf.SetAttributeValue("fillId", fill);
            xf.SetAttributeValue("applyFill", "1");
            _cellXfs.Add(xf);
            _formats.Add(xf);
            _cellXfs.SetAttributeValue("count", _formats.Count);
            Used = true;
            return _styles[(baseStyle, fill)] = _formats.Count - 1;
        }

        private int FillId(Color color)
        {
            var argb = color.ToArgb() | unchecked((int)0xFF000000);
            if (_fillIds.TryGetValue(argb, out var id))
                return id;
            // Excel keeps the first two fills for itself: none and gray125.
            while (_fillCount < 2)
                Add(new XElement(Main + "patternFill", new XAttribute("patternType", _fillCount == 0 ? "none" : "gray125")));
            Add(new XElement(Main + "patternFill", new XAttribute("patternType", "solid"),
                new XElement(Main + "fgColor", new XAttribute("rgb", argb.ToString("X8", CultureInfo.InvariantCulture))),
                new XElement(Main + "bgColor", new XAttribute("indexed", 64))));
            Used = true;
            return _fillIds[argb] = _fillCount - 1;
        }

        private void Add(XElement pattern)
        {
            _fills.Add(new XElement(Main + "fill", pattern));
            _fills.SetAttributeValue("count", ++_fillCount);
        }
    }
}
