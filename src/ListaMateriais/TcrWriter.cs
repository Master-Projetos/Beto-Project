using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static ListaMateriais.OoXml;

namespace ListaMateriais;

/// <summary>
/// Fills the TCR Word template (study summary, area study, scope and cost) with the data of the
/// project report. The Excel objects of the template only redraw when opened in Word, so the
/// material lists and the cost summary are replaced by regular Word tables, visible right away.
/// </summary>
internal enum ReportKind { Tcr, Tce }

internal static class TcrWriter
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly XNamespace W = Word;

    private const string DocumentPart = "word/document.xml";
    private const string ChartPart = "word/charts/chart1.xml";
    private const string ChartWorkbookPart = "word/embeddings/Microsoft_Excel_Worksheet.xlsx";

    /// <summary>Totals shown in the summary table, the chart and the text.</summary>
    private sealed record Costs(double Materials, double Regional, double Outsourced, double SafetyPercent)
    {
        public double Labor => Regional + Outsourced;
        public double Capex => Materials + Labor;
        public double Safety => Math.Round(Capex * SafetyPercent / 100, 2);
        public double Total => Capex + Safety;
    }

    /// <summary>Writes the filled TCR to <paramref name="output"/> and returns what could not be filled.</summary>
    public static (IReadOnlyList<string> Warnings, IReadOnlyList<int> Objects) Fill(byte[] template,
        ProjectReport report, IReadOnlyList<MaterialTable> tables, string output, ReportKind kind,
        Func<IReadOnlyList<Placeholder>, Dictionary<int, string>?>? ask = null)
    {
        var warnings = new List<string>();
        var changed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using var source = new ZipArchive(new MemoryStream(template, writable: false), ZipArchiveMode.Read);

        var costs = new Costs(
            tables.Sum(t => Subtotal(t)),
            report.RegionalLabor ?? 0,
            report.OutsourcedLabor ?? 0,
            report.SafetyPercent ?? 5);

        var document = LoadXml(source, DocumentPart);
        FillText(document, report, costs, kind, warnings);
        if (report.Map is { Length: > 0 } map)
            ReplaceMap(source, document, map, changed, warnings);
        if (ask is not null)
            FillPlaceholders(document, ask);
        var objects = FillObjects(source, document, tables, costs, changed, warnings);
        changed[DocumentPart] = Serialize(document);
        UpdateChart(source, changed, costs, warnings);

        var temp = output + ".tmp";
        using (var file = File.Create(temp))
        {
            using var result = new ZipArchive(file, ZipArchiveMode.Create);
            foreach (var entry in source.Entries)
            {
                var target = result.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var stream = target.Open();
                if (changed.TryGetValue(entry.FullName, out var content))
                    stream.Write(content);
                else
                {
                    using var original = entry.Open();
                    original.CopyTo(stream);
                }
            }
        }
        File.Move(temp, output, overwrite: true);
        return (warnings, objects);
    }

    private static double Subtotal(MaterialTable table) =>
        table.Items.Sum(o => Math.Round(o.Material.Quantity * o.Material.UnitPrice, 2));

    // ---- text and fields -------------------------------------------------------------------

    private static void FillText(XDocument document, ProjectReport report, Costs costs, ReportKind kind,
        List<string> warnings)
    {
        var body = document.Root!.Element(W + "body")!;
        var tables = body.Descendants(W + "tbl").ToList();
        var requested = report.NewPorts;

        if (report.Author is { } author)
            SetParagraph(body, "Nome do projetista", author, warnings);
        else
            warnings.Add("Projetista não encontrado no PDF.");

        if (report.Code is { Length: > 0 } code)
            SetParagraph(body, kind == ReportKind.Tce ? "TCE-" : "TCR-", code, warnings);

        if (report.Date is { } date)
            SetDate(body, date, warnings);

        // "Resumo do estudo" table and the area study share the first table.
        var common = new (string Label, string? Value)[]
        {
            ("Cidade", report.City),
            ("Data da solicitação", report.Date),
            ("Lançamento estimado", report.LaunchMeters is { } m ? $"{Count(m)} m" : null),
            ("CAPEX estimado", Money(costs.Capex)),
            ("Coef. Segurança", Money(costs.Safety)),
            ("CAPEX + Coef. Segurança", Money(costs.Total)),
            ("Prazo estimado da obra", report.Days is { } d ? $"{Count(d)} dias" : null),
            ("Mão de obra", LaborKind(costs)),
        };
        (string Label, string? Value)[] fields = common;
        if (kind == ReportKind.Tcr)
            fields =
            [
                ("Portas solicitadas", Count(requested)),
                ("Valor por Porta", requested is > 0 ? Money(costs.Total / requested.Value) : null),
                ("Portas estimadas", Count(requested)),
                .. common,
                ("BAIRRO", report.Neighborhood),
                ("HP", Count(report.Houses)),
                ("HC", Count(report.ExistingPorts)),
                ("TAXA DE PENETRAÇÃO", report.PenetrationRate),
            ];
        foreach (var (label, value) in fields)
        {
            if (value is null)
                warnings.Add($"\"{label}\" não foi encontrado no PDF; ficou como no modelo.");
            else if (!tables.Any(t => FillRow(t, label, value)))
                warnings.Add($"O campo \"{label}\" não foi encontrado no modelo do Word.");
        }

        var posts = tables.Count > 1 ? tables[1] : null;
        if (Count(report.Posts) is { } postCount && posts is not null)
            FillRow(posts, "Quantidade de Postes", postCount);

        if (kind == ReportKind.Tcr && report.Objective is { Length: > 0 } objective)
            SetParagraph(body, "Expansão FTTH no bairro", Capitalize(objective), warnings);
        if (report.Scope is { Length: > 0 } scope)
            SetParagraph(body, "Estima-se um l", Capitalize(scope), warnings);

        if (kind == ReportKind.Tce)
            FillClient(body, report, warnings);

        SetInvestment(body, costs.Total, warnings);
        if (report.City is not null)
            SetParagraph(body, "Área solicitada:",
                $"Área solicitada: {report.City}{(report.Neighborhood is { } n ? " - " + n : "")}", warnings);
    }

    /// <summary>The "Objetivo", the service list and the POP of the TCE, from the client and what the user typed.</summary>
    private static void FillClient(XElement body, ProjectReport report, List<string> warnings)
    {
        var client = report.Client;
        if (client?.Name is { Length: > 0 } name)
        {
            var text = $"O Objetivo desse levantamento de custo é para o atendimento Classe B2B a empresa {name}";
            if (client.Address is { Length: > 0 })
                text += $", localizada no endereço: {client.Address}";
            text += ".";
            if (client.Coordinates is { Length: > 0 })
                text += $" Coordenadas ({client.Coordinates})";
            text += client.Network is { Length: > 0 } n ? $" O cliente pode ser atendido na Rede {n}." : string.Empty;
            SetParagraph(body, "O Objetivo desse levantamento", text, warnings);
        }
        else
            warnings.Add("O cliente B2B não foi informado; o \"Objetivo\" ficou como no modelo.");

        var lines = new (string Label, string? Value)[]
        {
            ("Produto:", client?.Service),
            ("Velocidade:", client?.Bandwidth),
            ("Prazo contratual:", report.ContractTerm),
            ("Vendedor:", client?.Seller),
            ("Bloco IP:", client?.Ip),
            ("POP de atendimento:", report.Pop),
            ("Equipamento de atendimento:", report.Equipment),
            ("Tipo de Rede:", client?.Network),
        };
        foreach (var (label, value) in lines)
        {
            if (value is { Length: > 0 })
                SetParagraph(body, label, $"{label} {value}", warnings);
        }
        if (report.Pop is { Length: > 0 } pop)
        {
            var row = body.Descendants(W + "tr").FirstOrDefault(r =>
                r.Elements(W + "tc").FirstOrDefault() is { } first && Key(Plain(first)) == "POP");
            if (row?.Elements(W + "tc").Skip(1).FirstOrDefault()?.Element(W + "p") is { } paragraph)
                SetText(paragraph, pop);
        }
    }

    private static string LaborKind(Costs costs) => (costs.Regional > 0, costs.Outsourced > 0) switch
    {
        (true, true) => "Regional e Terceirizada",
        (true, false) => "Regional",
        (false, true) => "Terceirizada",
        _ => "Sem mão de obra",
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpper(text[0], PtBr) + text[1..];

    private static string? Count(double? value) => value?.ToString("#,##0.##", PtBr);

    private static string Money(double value) => "R$ " + value.ToString("N2", PtBr);

    private static string Plain(XElement element) =>
        string.Concat(element.Descendants(W + "t").Select(t => t.Value));

    /// <summary>Upper case, no trailing colon, single spaces: how labels are compared.</summary>
    private static string Key(string text) =>
        Regex.Replace(text, @"\s+", " ").Trim().TrimEnd(':').Trim().ToUpperInvariant();

    /// <summary>Puts <paramref name="value"/> in the cell after the one labelled <paramref name="label"/>.</summary>
    private static bool FillRow(XElement table, string label, string value)
    {
        var wanted = Key(label);
        foreach (var row in table.Descendants(W + "tr"))
        {
            var cells = row.Elements(W + "tc").ToList();
            var at = cells.FindIndex(c => Key(Plain(c)).StartsWith(wanted, StringComparison.Ordinal)
                                         && Key(Plain(c)).Length <= wanted.Length + 6);
            if (at < 0 || at + 1 >= cells.Count)
                continue;
            if (cells[at + 1].Element(W + "p") is { } paragraph)
            {
                SetText(paragraph, value);
                return true;
            }
        }
        return false;
    }

    private static void SetParagraph(XElement body, string startsWith, string text, List<string> warnings)
    {
        var paragraph = body.Descendants(W + "p")
            .FirstOrDefault(p => Plain(p).TrimStart().StartsWith(startsWith, StringComparison.Ordinal));
        if (paragraph is null)
            warnings.Add($"O trecho \"{startsWith}...\" não foi encontrado no modelo do Word.");
        else
            SetText(paragraph, text);
    }

    /// <summary>Replaces the paragraph's text keeping the formatting of its first run, without the placeholder highlight.</summary>
    private static void SetText(XElement paragraph, string text)
    {
        var first = paragraph.Elements(W + "r").FirstOrDefault(r => r.Element(W + "t") is not null)
                    ?? paragraph.Elements(W + "r").FirstOrDefault();
        var properties = first?.Element(W + "rPr") ?? paragraph.Element(W + "pPr")?.Element(W + "rPr");
        var runProperties = properties is null ? null : new XElement(properties);
        runProperties?.Elements(W + "highlight").Remove();

        paragraph.Elements(W + "r").Remove();
        var run = new XElement(W + "r");
        if (runProperties is { HasElements: true })
            run.Add(runProperties);
        run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));
        paragraph.Add(run);
    }

    /// <summary>The "R$" run left highlighted at the end of the cost sentence gets the total.</summary>
    private static void SetInvestment(XElement body, double total, List<string> warnings)
    {
        var run = body.Descendants(W + "r").FirstOrDefault(r =>
            Plain(r) == "R$" && r.Element(W + "rPr")?.Element(W + "highlight") is not null);
        if (run is null)
        {
            warnings.Add("O valor do investimento no texto não foi encontrado no modelo do Word.");
            return;
        }
        run.Element(W + "rPr")!.Element(W + "highlight")!.Remove();
        run.Element(W + "t")!.Value = Money(total) + ".";
    }

    private static void SetDate(XElement body, string date, List<string> warnings)
    {
        var field = body.Descendants(W + "sdt").FirstOrDefault(s => s.Element(W + "sdtPr")?.Element(W + "date") is not null);
        if (field is null || !DateTime.TryParseExact(date, "dd/MM/yyyy", PtBr, DateTimeStyles.None, out var parsed))
        {
            warnings.Add("O campo de data do modelo não foi preenchido.");
            return;
        }
        field.Element(W + "sdtPr")!.Element(W + "date")!
            .SetAttributeValue(W + "fullDate", parsed.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture));
        field.Element(W + "sdtPr")!.Elements(W + "showingPlcHdr").Remove();
        if (field.Element(W + "sdtContent")?.Element(W + "r") is { } run)
        {
            run.Element(W + "rPr")?.Elements(W + "highlight").Remove();
            run.Element(W + "t")!.Value = date;
        }
    }

    // ---- route map picture ---------------------------------------------------------------------

    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";

    /// <summary>
    /// Swaps the picture of the template's map (the inline picture, not the chart) for the one of the PDF,
    /// keeping the template's height and adjusting the width to the new proportions.
    /// </summary>
    private static void ReplaceMap(ZipArchive zip, XDocument document, byte[] jpeg,
        Dictionary<string, byte[]> changed, List<string> warnings)
    {
        var targets = RelationshipTargets(zip);
        var blip = document.Descendants(A + "blip").FirstOrDefault(b =>
            (string?)b.Attribute(OfficeRels + "embed") is { } id && targets.ContainsKey(id));
        var drawing = blip?.Ancestors(W + "drawing").FirstOrDefault();
        if (blip is null || drawing is null)
        {
            warnings.Add("A imagem do mapa não foi encontrada no modelo do Word.");
            return;
        }
        System.Drawing.Size size;
        using (var image = System.Drawing.Image.FromStream(new MemoryStream(jpeg)))
            size = image.Size;

        var part = "word/" + targets[(string)blip.Attribute(OfficeRels + "embed")!];
        if (drawing.Descendants(Wp + "extent").FirstOrDefault() is not { } extent)
        {
            warnings.Add("O tamanho da imagem do mapa não foi encontrado no modelo do Word.");
            return;
        }
        var height = long.Parse((string)extent.Attribute("cy")!, CultureInfo.InvariantCulture);
        var width = (long)Math.Round(height * (double)size.Width / size.Height);
        extent.SetAttributeValue("cx", width);
        foreach (var ext in drawing.Descendants(A + "ext").Where(e => e.Attribute("cx") is not null))
        {
            ext.SetAttributeValue("cx", width);
            ext.SetAttributeValue("cy", height);
        }
        changed[part] = jpeg;
    }

    // ---- highlighted spots nothing filled ---------------------------------------------------

    /// <summary>
    /// Highlighted runs left in the body (adjacent ones count as one spot). The user is asked for them;
    /// what is typed replaces the highlighted text, the rest stays highlighted.
    /// </summary>
    private static void FillPlaceholders(XDocument document, Func<IReadOnlyList<Placeholder>, Dictionary<int, string>?> ask)
    {
        var spots = new List<(Placeholder Info, List<XElement> Runs)>();
        foreach (var paragraph in document.Root!.Element(W + "body")!.Descendants(W + "p"))
        {
            var all = paragraph.Descendants(W + "r").ToList();
            List<XElement>? group = null;

            void Close()
            {
                if (group is null)
                    return;
                var first = all.IndexOf(group[0]);
                var last = all.IndexOf(group[^1]);
                var before = string.Concat(all.Take(first).Select(Plain));
                var after = string.Concat(all.Skip(last + 1).Select(Plain));
                var current = string.Concat(all.Skip(first).Take(last - first + 1).Select(Plain));
                spots.Add((new Placeholder(spots.Count, before, current, after), group));
                group = null;
            }

            foreach (var run in all)
            {
                if (run.Element(W + "rPr")?.Element(W + "highlight") is not null && Plain(run).Trim().Length > 0)
                    (group ??= []).Add(run);
                else if (group is null || Plain(run).Length > 0)
                    Close();
            }
            Close();
        }
        if (spots.Count == 0)
            return;

        var values = ask(spots.Select(s => s.Info).ToList());
        if (values is null)
            return;
        foreach (var (info, runs) in spots)
        {
            if (!values.TryGetValue(info.Id, out var text))
                continue;
            var run = runs[0];
            run.Element(W + "rPr")?.Element(W + "highlight")?.Remove();
            foreach (var t in run.Elements(W + "t").Skip(1))
                t.Remove();
            var node = run.Element(W + "t");
            if (node is null)
                run.Add(node = new XElement(W + "t"));
            node.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            node.Value = text;
            foreach (var other in runs.Skip(1))
                other.Remove();
        }
    }

    // ---- Excel objects (filled in place) ----------------------------------------------------

    /// <summary>Which summary line of the cost workbook a material workbook feeds.</summary>
    private enum Group { ClassL, ClassF, DataCenter }

    /// <summary>Material workbooks of the template: title of the sheet, PDF categories that go in it, summary line.</summary>
    private static readonly (Regex Title, Regex Category, Group Group)[] Slots =
    [
        (new Regex("FERRAGEN", Opts), new Regex("FERRAGEN", Opts), Group.ClassL),
        (new Regex("CABOS", Opts), new Regex("CABO|LAN[CÇ]AMENTO", Opts), Group.ClassL),
        (new Regex("CAIXAS|SPLITTER", Opts), new Regex("FUS[AÃ]O|CAIXA|SPLITTER", Opts), Group.ClassF),
        (new Regex("PASSIVOS", Opts), new Regex(@"\bPASSIVO", Opts), Group.DataCenter),
        (new Regex("ATIVOS", Opts), new Regex(@"\bATIVO", Opts), Group.DataCenter),
        (new Regex("INSTALA[CÇ][AÃ]O CLIENTE", Opts), new Regex("INSTALA[CÇ][AÃ]O|CLIENTE", Opts), Group.ClassF),
    ];

    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>
    /// Fills the Excel objects embedded in the document (quantities, prices and the cached results),
    /// so the tables stay the template's own. Returns the objects changed, by position in the document.
    /// </summary>
    private static List<int> FillObjects(ZipArchive zip, XDocument document, IReadOnlyList<MaterialTable> tables,
        Costs costs, Dictionary<string, byte[]> changed, List<string> warnings)
    {
        var targets = RelationshipTargets(zip);
        var objectNodes = document.Descendants(W + "object").ToList();
        var order = objectNodes
            .Select(o => o.Descendants().FirstOrDefault(e => e.Name.LocalName == "OLEObject")?.Attribute(OfficeRels + "id")?.Value)
            .Select(id => id is not null && targets.TryGetValue(id, out var t) && t.StartsWith("embeddings/", StringComparison.Ordinal)
                ? "word/" + t : null)
            .ToList();

        var remaining = tables.ToList();
        var groupTotals = new Dictionary<Group, double>();
        var touched = new List<int>();
        string? summary = null;

        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] is not { } part || zip.GetEntry(part) is not { } entry)
                continue;
            var bytes = ReadBytes(entry);
            var title = Sheet.TitleOf(bytes);
            if (title is null)
                continue;
            if (title.Contains("CUSTO ESTIMADO", StringComparison.OrdinalIgnoreCase)
                && Sheet.HasLabel(bytes, "INVESTIMENTO") && !Sheet.HasLabel(bytes, "ECONOMIA"))
            {
                summary = part;
                touched.Add(i);
                continue;
            }
            var slot = Array.FindIndex(Slots, s => s.Title.IsMatch(title));
            if (slot < 0)
                continue;
            var mine = remaining.Where(t => Slots[slot].Category.IsMatch(t.Title)).ToList();
            remaining.RemoveAll(mine.Contains);
            if (mine.Count == 0)
                continue;
            var (filled, total, added, addedHeight) = Sheet.FillMaterials(bytes, mine.SelectMany(t => t.Items));
            changed[part] = filled;
            if (addedHeight > 0)
                Grow(objectNodes[i], addedHeight);
            touched.Add(i);
            groupTotals[Slots[slot].Group] = groupTotals.GetValueOrDefault(Slots[slot].Group) + total;
            if (added.Count > 0)
                warnings.Add($"{added.Count} item(ns) não estavam na tabela \"{title}\" do modelo e foram acrescentados no fim dela: " +
                             string.Join("; ", added.Take(5)) + (added.Count > 5 ? "..." : "."));
        }

        foreach (var table in remaining)
            warnings.Add($"A categoria \"{table.Title}\" não tem tabela no modelo do Word; não foi incluída.");

        if (summary is null)
            warnings.Add("A tabela de custo estimado não foi encontrada no modelo do Word.");
        else
            changed[summary] = Sheet.FillSummary(ReadBytes(zip.GetEntry(summary)!), new Dictionary<string, double>
            {
                ["MATERIAL CLASSE \"L\""] = groupTotals.GetValueOrDefault(Group.ClassL),
                ["MATERIAL CLASSE \"F\""] = groupTotals.GetValueOrDefault(Group.ClassF),
                ["MATERIAL DATA CENTER"] = groupTotals.GetValueOrDefault(Group.DataCenter),
                ["MÃO DE OBRA REGIONAL"] = costs.Regional,
                ["MÃO DE OBRA CLASSE \"L\""] = costs.Outsourced,
            });
        return touched;
    }

    /// <summary>Makes an Excel object taller (points) to show the rows added to its workbook.</summary>
    private static void Grow(XElement obj, double points)
    {
        if (obj.Descendants().FirstOrDefault(e => e.Name.LocalName == "shape" && e.Attribute("style") is not null) is { } shape)
        {
            var style = (string)shape.Attribute("style")!;
            var match = Regex.Match(style, @"height:([\d.]+)pt");
            if (match.Success)
            {
                var height = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + points;
                shape.SetAttributeValue("style", style.Replace(match.Value,
                    "height:" + height.ToString("0.##", CultureInfo.InvariantCulture) + "pt"));
            }
        }
        if (double.TryParse((string?)obj.Attribute(W + "dyaOrig"), NumberStyles.Float, CultureInfo.InvariantCulture, out var twips))
            obj.SetAttributeValue(W + "dyaOrig", (long)Math.Round(twips + points * 20));
    }

    private static Dictionary<string, string> RelationshipTargets(ZipArchive zip)
    {
        var rels = LoadXml(zip, "word/_rels/document.xml.rels");
        return rels.Root!.Elements(PackageRels + "Relationship")
            .Where(r => (string?)r.Attribute("Id") is not null && (string?)r.Attribute("Target") is not null)
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!, StringComparer.Ordinal);
    }

    // ---- chart -------------------------------------------------------------------------------

    /// <summary>
    /// The pie chart (material, regional labor, outsourced labor) is drawn from the values cached in
    /// the chart part; the embedded workbook is kept in sync so editing the chart in Word shows the same numbers.
    /// </summary>
    private static void UpdateChart(ZipArchive zip, Dictionary<string, byte[]> changed, Costs costs, List<string> warnings)
    {
        double[] values = [costs.Materials, costs.Regional, costs.Outsourced];
        if (TryLoadXml(zip, ChartPart) is not { } chart)
        {
            warnings.Add("O gráfico de custos não foi encontrado no modelo do Word.");
            return;
        }
        XNamespace c = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        var points = chart.Descendants(c + "val").Descendants(c + "numCache").Elements(c + "pt").ToList();
        foreach (var point in points)
        {
            if (int.TryParse((string?)point.Attribute("idx"), NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < values.Length)
                point.Element(c + "v")!.Value = Number(values[i]);
        }
        changed[ChartPart] = Serialize(chart);

        if (zip.GetEntry(ChartWorkbookPart) is not { } entry)
            return;
        const string sheet = "xl/worksheets/sheet1.xml";
        using var source = new ZipArchive(new MemoryStream(ReadBytes(entry), writable: false), ZipArchiveMode.Read);
        var sheetXml = LoadXml(source, sheet);
        for (var i = 0; i < values.Length; i++)
        {
            var cell = sheetXml.Descendants(Main + "c").FirstOrDefault(x => (string?)x.Attribute("r") == $"B{i + 2}");
            if (cell?.Element(Main + "v") is { } v)
                v.Value = Number(values[i]);
        }

        using var workbook = new MemoryStream();
        using (var result = new ZipArchive(workbook, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var part in source.Entries)
            {
                using var stream = result.CreateEntry(part.FullName, CompressionLevel.Optimal).Open();
                if (part.FullName == sheet)
                    stream.Write(Serialize(sheetXml));
                else
                {
                    using var original = part.Open();
                    original.CopyTo(stream);
                }
            }
        }
        changed[ChartWorkbookPart] = workbook.ToArray();
    }
}
