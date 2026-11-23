using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml.Linq;
using ListaMateriais.Kmz;

namespace ListaMateriais.Deck;

/// <summary>
/// Writes the presentation as a .pptx. With a template, its master, theme, layouts and pictures are kept (the client's
/// look) and its slides are replaced; without one, a plain look is built. Every chart is drawn with shapes, as in the
/// example deck, so it looks the same in any PowerPoint and can be retouched by hand.
/// </summary>
internal static class PptxWriter
{
    private const long Emu = 914400;
    private const double SlideWidth = 13.333;

    private const string Navy = "082B5C";
    private const string Dark = "002060";
    private const string Green = "70AD47";
    private const string Orange = "ED7D31";
    private const string Ink = "2D2D2D";
    private const string Grey = "696969";
    private const string Track = "EBEDF0";
    private const string PanelFill = "DDEBF7";
    private const string PanelLine = "225B97";
    private const string Soft = "8FA6C8";

    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string TypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string SlideType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide";
    private const string LayoutType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout";
    private const string SlideContent = "application/vnd.openxmlformats-officedocument.presentationml.slide+xml";

    public static void Write(string output, DeckModel deck, byte[]? template)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Write(file, deck, template);
            File.Move(temp, output, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            KmzWriter.TryDelete(temp);
            throw new AppException($"Não foi possível salvar \"{Path.GetFileName(output)}\" em:\n{folder}\n\n" +
                                   "Se o arquivo estiver aberto no PowerPoint, feche-o e tente de novo.\n\n" +
                                   $"Detalhe: {e.Message}", e);
        }
    }

    public static void Write(Stream output, DeckModel deck, byte[]? template)
    {
        var branded = template is not null;
        var slides = deck.Slides.Select(s => Slide(s, branded)).ToList();
        if (template is not null)
            FromTemplate(output, template, deck.Title, slides);
        else
            Plain(output, deck.Title, slides);
    }

    // ---- Slides ------------------------------------------------------------------------------------

    private static string Slide(DeckSlide slide, bool branded)
    {
        var s = new Shapes();
        if (!branded)
        {
            // The plain look: a navy band on top and a thin line at the bottom.
            s.Rect(0, 0, SlideWidth, 0.14, Navy);
            s.Rect(0.6, 7.18, SlideWidth - 1.2, 0.02, Soft);
        }
        switch (slide)
        {
            case CoverSlide cover:
                Cover(s, cover);
                break;
            case OverviewSlide overview:
                Title(s, overview.Title);
                Overview(s, overview);
                Banner(s, overview.Conclusion);
                break;
            case ChartSlide chart:
                Title(s, chart.Title);
                Chart(s, chart);
                PanelBox(s, chart.Panel);
                Banner(s, chart.Conclusion);
                break;
            case SummarySlide summary:
                Title(s, summary.Title);
                Summary(s, summary);
                Banner(s, summary.Conclusion);
                break;
        }
        return $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sld xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}"><p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>{s}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>""";
    }

    private static void Title(Shapes s, string title) =>
        s.Text(0.6, 0.28, 12.0, 0.8, title, 30, Navy, bold: true, minSize: 18);

    private static void Banner(Shapes s, string text)
    {
        s.Box(0.72, 5.82, 11.9, 0.9, Dark, radius: 0.2, shadow: true);
        s.Text(1.0, 5.86, 11.34, 0.82, text, 16, "FFFFFF", bold: true, align: "ctr", anchor: "ctr", wrap: true, minSize: 11);
    }

    private static void Cover(Shapes s, CoverSlide cover)
    {
        s.Rect(0.9, 2.05, 1.2, 0.09, Navy);
        s.Text(0.9, 2.3, 11.5, 1.5, cover.Title, 44, Navy, bold: true, wrap: true, minSize: 24);
        s.Text(0.9, 3.85, 11.5, 0.6, cover.Subtitle, 20, Grey, minSize: 12);
        s.Text(0.9, 4.5, 11.5, 0.4, cover.Footer, 12, Grey, minSize: 9);
    }

    private static void Overview(Shapes s, OverviewSlide overview)
    {
        var count = Math.Max(1, overview.Kpis.Count);
        const double left = 0.75, width = 11.85, gap = 0.3;
        var cardWidth = (width - gap * (count - 1)) / count;
        for (var i = 0; i < overview.Kpis.Count; i++)
        {
            var kpi = overview.Kpis[i];
            var x = left + i * (cardWidth + gap);
            s.Box(x, 1.35, cardWidth, 1.6, "FFFFFF", line: "CDD7E1", radius: 0.12, shadow: true);
            s.Rect(x + 0.25, 1.35, cardWidth - 0.5, 0.06, Color(kpi.Tone));
            s.Text(x + 0.1, 1.55, cardWidth - 0.2, 0.8, kpi.Value, 34, Color(kpi.Tone), bold: true, align: "ctr", minSize: 16);
            s.Text(x + 0.1, 2.35, cardWidth - 0.2, 0.45, kpi.Label, 13, Grey, align: "ctr", minSize: 9);
        }
        s.Text(0.78, 3.2, 6, 0.4, "Destaques", 16, Navy, bold: true);
        var y = 3.7;
        var rowHeight = Math.Min(0.52, 2.0 / Math.Max(1, overview.Points.Count));
        foreach (var point in overview.Points)
        {
            s.Ellipse(0.85, y + rowHeight / 2 - 0.06, 0.12, 0.12, Navy);
            s.Text(1.12, y, 11.4, rowHeight, point, 15, Ink, anchor: "ctr", wrap: true, minSize: 10);
            y += rowHeight;
        }
    }

    private static void Summary(Shapes s, SummarySlide summary)
    {
        var count = Math.Max(1, summary.Points.Count);
        var rowHeight = Math.Min(0.85, 4.2 / count);
        var y = 1.3;
        for (var i = 0; i < summary.Points.Count; i++)
        {
            s.Box(0.75, y + 0.08, 11.9, rowHeight - 0.16, i % 2 == 0 ? "F3F6FA" : "FFFFFF", radius: 0.15);
            s.Ellipse(0.95, y + rowHeight / 2 - 0.22, 0.44, 0.44, Navy);
            s.Text(0.95, y + rowHeight / 2 - 0.22, 0.44, 0.44, (i + 1).ToString(CultureInfo.InvariantCulture), 14, "FFFFFF", bold: true, align: "ctr", anchor: "ctr");
            s.Text(1.6, y + 0.1, 10.9, rowHeight - 0.2, summary.Points[i], 15, Ink, anchor: "ctr", wrap: true, minSize: 10);
            y += rowHeight;
        }
    }

    // ---- Charts ------------------------------------------------------------------------------------

    private static void Chart(Shapes s, ChartSlide chart)
    {
        s.Text(0.75, 1.2, 5.8, 0.45, chart.Caption, 16, Navy, bold: true, minSize: 11);
        if (chart.Bars.Count == 0)
            return;
        var max = chart.Max ?? chart.Bars.Max(b => b.Value + b.Pending);
        if (max <= 0)
            max = 1;
        if (chart.Vertical)
            Columns(s, chart.Bars, max);
        else
            Bars(s, chart.Bars, max);
    }

    /// <summary>Horizontal bars, as in the example: label, a grey track, the done part in navy and the pending part in orange.</summary>
    private static void Bars(Shapes s, IReadOnlyList<BarItem> bars, double max)
    {
        const double left = 0.75, right = 6.55, top = 1.75, bottom = 5.55;
        var labelWidth = Math.Clamp(bars.Max(b => Measure.Width(b.Label, 12, true)) + 0.15, 0.7, 2.1);
        var trackLeft = left + labelWidth;
        var trackWidth = right - trackLeft;
        var row = Math.Min(0.72, (bottom - top) / bars.Count);
        var height = Math.Min(0.46, row * 0.66);
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var y = top + i * row + (row - height) / 2;
            s.Text(left, y, labelWidth - 0.1, height, bar.Label, 12, Navy, bold: true, anchor: "ctr", minSize: 8);
            s.Box(trackLeft, y, trackWidth, height, Track, radius: 0.25, shadow: true);
            var done = Math.Max(0, bar.Value) / max * trackWidth;
            var pending = Math.Max(0, bar.Pending) / max * trackWidth;
            var doneWidth = Math.Max(done, 0.12);
            s.Box(trackLeft, y, doneWidth, height, Dark, line: Green, radius: 0.25);
            if (pending > 0.01)
                s.Box(trackLeft + doneWidth, y, Math.Max(pending, 0.1), height, Orange, radius: 0.25);
            var end = trackLeft + doneWidth + (pending > 0.01 ? Math.Max(pending, 0.1) : 0);
            var textWidth = Measure.Width(bar.Text, 11, true) + 0.2;
            if (textWidth <= doneWidth - 0.05)
                s.Text(trackLeft + doneWidth - textWidth - 0.02, y, textWidth, height, bar.Text, 11, "FFFFFF", bold: true, align: "r", anchor: "ctr");
            else if (pending <= 0.01)
                s.Text(end + 0.05, y, Math.Max(0.3, right - end - 0.05), height, bar.Text, 11, Navy, bold: true, anchor: "ctr", minSize: 8);
            if (pending > 0.01 && bar.PendingText is { } pendingText)
            {
                // Only when it fits: the panel tells the leader's pending part anyway.
                var label = (textWidth <= doneWidth - 0.05 ? string.Empty : bar.Text + " · ") + pendingText;
                var room = right - end - 0.1;
                if (Measure.Width(label, 9, true) <= room)
                    s.Text(end + 0.05, y, room, height, label, 11, Orange, bold: true, anchor: "ctr", minSize: 9);
                else if (textWidth > doneWidth - 0.05 && Measure.Width(bar.Text, 9, true) <= room)
                    s.Text(end + 0.05, y, room, height, bar.Text, 11, Navy, bold: true, anchor: "ctr", minSize: 9);
            }
        }
    }

    /// <summary>Columns for months: the value above, the month below; a month not over yet is lighter.</summary>
    private static void Columns(Shapes s, IReadOnlyList<BarItem> bars, double max)
    {
        const double left = 0.8, right = 6.5, top = 1.85, baseLine = 5.25;
        var slot = (right - left) / bars.Count;
        var width = Math.Min(0.7, slot * 0.62);
        s.Rect(left - 0.05, baseLine, right - left + 0.1, 0.015, "CDD7E1");
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var x = left + i * slot + (slot - width) / 2;
            var height = Math.Max(0.04, bar.Value / max * (baseLine - top - 0.4));
            var partial = bar.Label.EndsWith('*');
            s.Box(x, baseLine - height, width, height, partial ? Soft : Dark, radius: 0.12);
            s.Text(left + i * slot, baseLine - height - 0.34, slot, 0.3, bar.Text, 10, Navy, bold: true, align: "ctr", minSize: 6);
            s.Text(left + i * slot, baseLine + 0.04, slot, 0.3, bar.Label, 10, Grey, align: "ctr", minSize: 6);
        }
    }

    private static void PanelBox(Shapes s, Panel panel)
    {
        const double x = 6.85, y = 1.2, w = 5.8, h = 4.35;
        s.Box(x, y, w, h, PanelFill, line: PanelLine, radius: 0.12);
        s.Text(x + 0.25, y + 0.2, w - 0.5, 0.55, panel.Title, 20, Navy, bold: true, align: "ctr", minSize: 12);
        var count = Math.Max(1, panel.Kpis.Count);
        var column = (w - 0.4) / count;
        for (var i = 0; i < panel.Kpis.Count; i++)
        {
            var kpi = panel.Kpis[i];
            s.Text(x + 0.2 + i * column, y + 0.85, column, 0.7, kpi.Value, 30, Color(kpi.Tone), bold: true, align: "ctr", minSize: 14);
            s.Text(x + 0.2 + i * column, y + 1.5, column, 0.35, kpi.Label, 11, Grey, align: "ctr", minSize: 8);
        }
        var next = y + 2.0;
        if (panel.Progress is { } progress)
        {
            var barX = x + 0.35;
            var barW = w - 0.7;
            var fill = Math.Clamp(progress, 0, 1) * barW;
            if (panel.ProgressLabel is { } label)
                s.Text(barX, next, barW, 0.3, label, 11, Navy, bold: true, minSize: 8);
            s.Box(barX, next + 0.32, barW, 0.3, "FFFFFF", line: "CDD7E1", radius: 0.2, shadow: true);
            if (fill > 0.02)
                s.Box(barX, next + 0.32, Math.Max(fill, 0.1), 0.3, Dark, line: Green, radius: 0.2);
            if (panel.RestLabel is { } rest && barW - fill > Measure.Width(rest, 10, true) + 0.2)
                s.Text(barX + fill + 0.05, next + 0.32, barW - fill - 0.1, 0.3, rest, 10, Orange, bold: true, align: "ctr", anchor: "ctr");
            next += 0.85;
        }
        if (panel.Alert is { } alert)
        {
            var cardY = Math.Max(next, y + 2.45);
            var cardH = y + h - 0.2 - cardY;
            s.Box(x + 0.3, cardY, w - 0.6, cardH, "FFFFFF", line: Orange, radius: 0.12, shadow: true);
            var headlineWidth = Math.Min(Measure.Width(alert.Headline, 18, true), w - 1.5);
            var iconX = x + w / 2 - headlineWidth / 2 - 0.42;
            s.Shape("triangle", iconX, cardY + 0.2, 0.3, 0.27, Orange);
            s.Text(iconX + 0.1, cardY + 0.22, 0.1, 0.22, "!", 10, "FFFFFF", bold: true, align: "ctr");
            s.Text(x + 0.45, cardY + 0.12, w - 0.9, 0.45, alert.Headline, 18, Orange, bold: true, align: "ctr", minSize: 11);
            s.Text(x + 0.45, cardY + 0.58, w - 0.9, cardH - 0.65, alert.Text, 11, Ink, align: "ctr", wrap: true, minSize: 8);
        }
    }

    private static string Color(Tone tone) => tone switch
    {
        Tone.Green => Green,
        Tone.Orange => Orange,
        Tone.Muted => Grey,
        _ => Navy,
    };

    /// <summary>The shapes of one slide, as DrawingML.</summary>
    private sealed class Shapes
    {
        private readonly StringBuilder _xml = new();
        private int _id = 2;

        public override string ToString() => _xml.ToString();

        public void Rect(double x, double y, double w, double h, string fill) => Shape("rect", x, y, w, h, fill);

        public void Ellipse(double x, double y, double w, double h, string fill) => Shape("ellipse", x, y, w, h, fill);

        public void Shape(string geometry, double x, double y, double w, double h, string fill) =>
            Add(geometry, x, y, w, h, fill, null, null, false, null);

        /// <param name="radius">Corner roundness, 0–0.5 of the shorter side.</param>
        public void Box(double x, double y, double w, double h, string fill, string? line = null, double radius = 0.15, bool shadow = false) =>
            Add("roundRect", x, y, w, h, fill, line, radius, shadow, null);

        /// <summary>A text box; the size goes down to <paramref name="minSize"/> until the text fits, then the text is cut.</summary>
        public void Text(double x, double y, double w, double h, string text, double size, string color, bool bold = false,
            string align = "l", string anchor = "t", bool wrap = false, double minSize = 0)
        {
            var (fitted, points) = Measure.Fit(text, size, bold, w - 0.1, h, wrap, minSize <= 0 ? size : minSize);
            var body = $"""<p:txBody><a:bodyPr wrap="{(wrap ? "square" : "none")}" lIns="45720" tIns="18288" rIns="45720" bIns="18288" anchor="{anchor}"><a:noAutofit/></a:bodyPr><a:lstStyle/><a:p><a:pPr algn="{align}"/><a:r><a:rPr lang="pt-BR" sz="{(int)Math.Round(points * 100)}" b="{(bold ? 1 : 0)}" dirty="0"><a:solidFill><a:srgbClr val="{color}"/></a:solidFill></a:rPr><a:t>{SecurityElement.Escape(KmzWriter.Clean(fitted))}</a:t></a:r></a:p></p:txBody>""";
            Add("rect", x, y, w, h, null, null, null, false, body, textBox: true);
        }

        private void Add(string geometry, double x, double y, double w, double h, string? fill, string? line, double? radius, bool shadow, string? body, bool textBox = false)
        {
            var id = _id++;
            var adjust = radius is { } r ? $"""<a:avLst><a:gd name="adj" fmla="val {(int)(Math.Clamp(r, 0, 0.5) * 100000)}"/></a:avLst>""" : "<a:avLst/>";
            var fillXml = fill is null ? "<a:noFill/>" : $"""<a:solidFill><a:srgbClr val="{fill}"/></a:solidFill>""";
            var lineXml = line is null ? "<a:ln><a:noFill/></a:ln>" : $"""<a:ln w="12700"><a:solidFill><a:srgbClr val="{line}"/></a:solidFill></a:ln>""";
            var effect = shadow ? """<a:effectLst><a:outerShdw blurRad="63500" dist="25400" dir="5400000" algn="t" rotWithShape="0"><a:prstClr val="black"><a:alpha val="22000"/></a:prstClr></a:outerShdw></a:effectLst>""" : string.Empty;
            _xml.Append(CultureInfo.InvariantCulture,
                $"""<p:sp><p:nvSpPr><p:cNvPr id="{id}" name="{(textBox ? "Texto" : "Forma")} {id}"/><p:cNvSpPr{(textBox ? " txBox=\"1\"" : string.Empty)}/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="{E(x)}" y="{E(y)}"/><a:ext cx="{E(Math.Max(w, 0.01))}" cy="{E(Math.Max(h, 0.01))}"/></a:xfrm><a:prstGeom prst="{geometry}">{adjust}</a:prstGeom>{fillXml}{lineXml}{effect}</p:spPr>{body ?? """<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang="pt-BR" dirty="0"/></a:p></p:txBody>"""}</p:sp>""");
        }

        private static long E(double inches) => (long)Math.Round(inches * Emu);
    }

    // ---- Packages ----------------------------------------------------------------------------------

    /// <summary>The template with its slides replaced, and without what would tell about the old content (thumbnail, titles).</summary>
    private static void FromTemplate(Stream output, byte[] template, string title, List<string> slides)
    {
        using var source = new ZipArchive(new MemoryStream(template, writable: false), ZipArchiveMode.Read);
        XDocument Load(string name) => XDocument.Load(source.GetEntry(name)?.Open() ?? throw new AppException($"O modelo de apresentação está incompleto (falta {name})."));
        XNamespace rel = RelNs, p = P, r = R, types = TypesNs;

        var root = Load("_rels/.rels");
        var mainPart = root.Root!.Elements(rel + "Relationship")
            .First(e => ((string?)e.Attribute("Type"))!.EndsWith("/officeDocument", StringComparison.Ordinal)).Attribute("Target")!.Value.TrimStart('/');
        var mainFolder = mainPart.Contains('/', StringComparison.Ordinal) ? mainPart[..mainPart.LastIndexOf('/')] : string.Empty;
        var mainRelsName = $"{mainFolder}/_rels/{Path.GetFileName(mainPart)}.rels";
        var presentation = Load(mainPart);
        var presentationRels = Load(mainRelsName);

        // The layout of the template's first slide is the one with the look; without slides, a blank layout.
        var slideRels = presentationRels.Root!.Elements(rel + "Relationship").Where(e => (string?)e.Attribute("Type") == SlideType).ToList();
        var firstSlideId = presentation.Root!.Element(p + "sldIdLst")?.Elements(p + "sldId").FirstOrDefault()?.Attribute(r + "id")?.Value;
        var firstSlide = slideRels.FirstOrDefault(e => e.Attribute("Id")!.Value == firstSlideId) ?? slideRels.FirstOrDefault();
        string? layout = null;
        if (firstSlide is not null)
        {
            var slidePath = Resolve(mainFolder, firstSlide.Attribute("Target")!.Value);
            var slideRelsPath = $"{Path.GetDirectoryName(slidePath)!.Replace('\\', '/')}/_rels/{Path.GetFileName(slidePath)}.rels";
            if (source.GetEntry(slideRelsPath) is not null)
            {
                var target = Load(slideRelsPath).Root!.Elements(rel + "Relationship").FirstOrDefault(e => (string?)e.Attribute("Type") == LayoutType)?.Attribute("Target")?.Value;
                if (target is not null)
                    layout = Resolve(Path.GetDirectoryName(slidePath)!.Replace('\\', '/'), target);
            }
        }
        layout ??= source.Entries.Select(e => e.FullName).Where(n => n.StartsWith($"{mainFolder}/slideLayouts/slideLayout", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal))
            .OrderBy(n => source.GetEntry(n)!.Open().ReadToEndText().Contains("type=\"blank\"", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new AppException("O modelo de apresentação não tem nenhum layout de slide.");

        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "[Content_Types].xml", "_rels/.rels", mainPart, mainRelsName, "docProps/app.xml", "docProps/core.xml", "docProps/thumbnail.jpeg",
        };
        bool Old(string name) => dropped.Contains(name) || name.StartsWith($"{mainFolder}/slides/", StringComparison.OrdinalIgnoreCase)
                                 || name.StartsWith($"{mainFolder}/notesSlides/", StringComparison.OrdinalIgnoreCase)
                                 || name.StartsWith($"{mainFolder}/comments/", StringComparison.OrdinalIgnoreCase)
                                 || name.StartsWith("docProps/thumbnail", StringComparison.OrdinalIgnoreCase);

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var entry in source.Entries.Where(e => !Old(e.FullName) && !e.FullName.EndsWith('/')))
        {
            using var from = entry.Open();
            using var to = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal).Open();
            from.CopyTo(to);
        }

        // Content types: the old slides, notes and properties out; the new ones in.
        var contentTypes = Load("[Content_Types].xml");
        contentTypes.Root!.Elements(types + "Override")
            .Where(e => e.Attribute("PartName")!.Value.TrimStart('/') is var part && Old(part) && part != mainPart)
            .Remove();
        for (var i = 0; i < slides.Count; i++)
            contentTypes.Root.Add(new XElement(types + "Override", new XAttribute("PartName", $"/{mainFolder}/slides/slide{i + 1}.xml"), new XAttribute("ContentType", SlideContent)));
        AddProperties(contentTypes);
        Save(zip, "[Content_Types].xml", contentTypes);

        Save(zip, "_rels/.rels", PackageRels(mainPart));

        presentationRels.Root.Elements(rel + "Relationship").Where(e => (string?)e.Attribute("Type") == SlideType).Remove();
        var slideList = new XElement(p + "sldIdLst");
        for (var i = 0; i < slides.Count; i++)
        {
            var id = $"rIdDeck{i + 1}";
            presentationRels.Root.Add(new XElement(rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", SlideType), new XAttribute("Target", $"slides/slide{i + 1}.xml")));
            slideList.Add(new XElement(p + "sldId", new XAttribute("id", 256 + i), new XAttribute(r + "id", id)));
        }
        Save(zip, mainRelsName, presentationRels);
        // Sections and custom shows point at the old slides.
        presentation.Root.Descendants().Where(e => e.Name.LocalName is "sectionLst" or "custShowLst").ToList().ForEach(e => e.Remove());
        presentation.Root.Elements(p + "sldIdLst").Remove();
        // The schema wants the slide list right after the master lists.
        presentation.Root.Elements().Last(e => e.Name == p + "sldMasterIdLst" || e.Name == p + "notesMasterIdLst" || e.Name == p + "handoutMasterIdLst")
            .AddAfterSelf(slideList);
        Save(zip, mainPart, presentation);

        var layoutFromSlides = "../" + layout[(mainFolder.Length + 1)..];
        for (var i = 0; i < slides.Count; i++)
        {
            Text(zip, $"{mainFolder}/slides/slide{i + 1}.xml", slides[i]);
            Text(zip, $"{mainFolder}/slides/_rels/slide{i + 1}.xml.rels",
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="{LayoutType}" Target="{layoutFromSlides}"/></Relationships>""");
        }
        Properties(zip, title, slides.Count);
    }

    /// <summary>A complete plain package: theme, master, one blank layout and the slides.</summary>
    private static void Plain(Stream output, string title, List<string> slides)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var overrides = new StringBuilder();
        for (var i = 0; i < slides.Count; i++)
            overrides.Append(CultureInfo.InvariantCulture, $"""<Override PartName="/ppt/slides/slide{i + 1}.xml" ContentType="{SlideContent}"/>""");
        Text(zip, "[Content_Types].xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="{TypesNs}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/><Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/><Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/><Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/><Override PartName="/ppt/presProps.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presProps+xml"/><Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/><Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>{overrides}</Types>""");
        Save(zip, "_rels/.rels", PackageRels("ppt/presentation.xml"));

        var ids = new StringBuilder();
        var rels = new StringBuilder();
        for (var i = 0; i < slides.Count; i++)
        {
            ids.Append(CultureInfo.InvariantCulture, $"""<p:sldId id="{256 + i}" r:id="rId{i + 10}"/>""");
            rels.Append(CultureInfo.InvariantCulture, $"""<Relationship Id="rId{i + 10}" Type="{SlideType}" Target="slides/slide{i + 1}.xml"/>""");
        }
        Text(zip, "ppt/presentation.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentation xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}" saveSubsetFonts="1"><p:sldMasterIdLst><p:sldMasterId id="2147483648" r:id="rId1"/></p:sldMasterIdLst><p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx="12192000" cy="6858000"/><p:notesSz cx="6858000" cy="9144000"/></p:presentation>""");
        Text(zip, "ppt/_rels/presentation.xml.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="slideMasters/slideMaster1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="theme/theme1.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/presProps" Target="presProps.xml"/>{rels}</Relationships>""");
        Text(zip, "ppt/presProps.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:presentationPr xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}"/>""");
        Text(zip, "ppt/slideMasters/slideMaster1.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldMaster xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}"><p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val="FFFFFF"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld><p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/><p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst><p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz="3200"><a:latin typeface="+mj-lt"/></a:defRPr></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr><a:defRPr sz="1800"><a:latin typeface="+mn-lt"/></a:defRPr></a:lvl1pPr></p:bodyStyle><p:otherStyle><a:lvl1pPr><a:defRPr sz="1800"><a:latin typeface="+mn-lt"/></a:defRPr></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>""");
        Text(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="{LayoutType}" Target="../slideLayouts/slideLayout1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="../theme/theme1.xml"/></Relationships>""");
        Text(zip, "ppt/slideLayouts/slideLayout1.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><p:sldLayout xmlns:a="{A}" xmlns:r="{R}" xmlns:p="{P}" type="blank" preserve="1"><p:cSld name="Em branco"><p:spTree><p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr/></p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>""");
        Text(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="../slideMasters/slideMaster1.xml"/></Relationships>""");
        Text(zip, "ppt/theme/theme1.xml", Theme);
        for (var i = 0; i < slides.Count; i++)
        {
            Text(zip, $"ppt/slides/slide{i + 1}.xml", slides[i]);
            Text(zip, $"ppt/slides/_rels/slide{i + 1}.xml.rels", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="{LayoutType}" Target="../slideLayouts/slideLayout1.xml"/></Relationships>""");
        }
        Properties(zip, title, slides.Count);
    }

    private static string Theme => $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><a:theme xmlns:a="{A}" name="Preguiça"><a:themeElements><a:clrScheme name="Preguiça"><a:dk1><a:srgbClr val="{Ink}"/></a:dk1><a:lt1><a:srgbClr val="FFFFFF"/></a:lt1><a:dk2><a:srgbClr val="{Navy}"/></a:dk2><a:lt2><a:srgbClr val="{Track}"/></a:lt2><a:accent1><a:srgbClr val="{Dark}"/></a:accent1><a:accent2><a:srgbClr val="{Orange}"/></a:accent2><a:accent3><a:srgbClr val="{Green}"/></a:accent3><a:accent4><a:srgbClr val="{PanelLine}"/></a:accent4><a:accent5><a:srgbClr val="{Soft}"/></a:accent5><a:accent6><a:srgbClr val="{Grey}"/></a:accent6><a:hlink><a:srgbClr val="{PanelLine}"/></a:hlink><a:folHlink><a:srgbClr val="{Grey}"/></a:folHlink></a:clrScheme><a:fontScheme name="Preguiça"><a:majorFont><a:latin typeface="Segoe UI Semibold"/><a:ea typeface=""/><a:cs typeface=""/></a:majorFont><a:minorFont><a:latin typeface="Segoe UI"/><a:ea typeface=""/><a:cs typeface=""/></a:minorFont></a:fontScheme><a:fmtScheme name="Preguiça"><a:fillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:fillStyleLst><a:lnStyleLst><a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln><a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln></a:lnStyleLst><a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst><a:bgFillStyleLst><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>""";

    private static void AddProperties(XDocument contentTypes)
    {
        XNamespace types = TypesNs;
        contentTypes.Root!.Add(
            new XElement(types + "Override", new XAttribute("PartName", "/docProps/core.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.core-properties+xml")),
            new XElement(types + "Override", new XAttribute("PartName", "/docProps/app.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.extended-properties+xml")));
    }

    private static XDocument PackageRels(string mainPart) => XDocument.Parse($"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{RelNs}"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="{mainPart}"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/></Relationships>""");

    private static void Properties(ZipArchive zip, string title, int slides)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        Text(zip, "docProps/core.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><dc:title>{SecurityElement.Escape(KmzWriter.Clean(title))}</dc:title><dc:creator>Preguiça App</dc:creator><dcterms:created xsi:type="dcterms:W3CDTF">{now}</dcterms:created><dcterms:modified xsi:type="dcterms:W3CDTF">{now}</dcterms:modified></cp:coreProperties>""");
        Text(zip, "docProps/app.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"><Application>Microsoft Office PowerPoint</Application><Slides>{slides}</Slides></Properties>""");
    }

    private static string Resolve(string folder, string target)
    {
        if (target.StartsWith('/'))
            return target.TrimStart('/');
        var parts = (folder.Length == 0 ? [] : folder.Split('/')).ToList();
        foreach (var piece in target.Split('/'))
        {
            if (piece == "..")
                parts.RemoveAt(parts.Count - 1);
            else if (piece != ".")
                parts.Add(piece);
        }
        return string.Join('/', parts);
    }

    private static void Save(ZipArchive zip, string name, XDocument document)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        document.Declaration ??= new XDeclaration("1.0", "UTF-8", "yes");
        document.Save(stream, SaveOptions.DisableFormatting);
    }

    private static void Text(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(new UTF8Encoding(false).GetBytes(text));
    }

    private static string ReadToEndText(this Stream stream)
    {
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Text sizes, measured with GDI: PowerPoint does not shrink text to fit unless the user edits it, so it must fit
/// when written. Segoe UI is measured (always installed) with a margin, since the template's font may differ.
/// </summary>
internal static class Measure
{
    private static readonly Lock Sync = new();
    private static readonly Bitmap Canvas = new(1, 1);
    private static readonly Graphics Surface = CreateSurface();

    private static Graphics CreateSurface()
    {
        var g = Graphics.FromImage(Canvas);
        g.PageUnit = GraphicsUnit.Point;
        return g;
    }

    /// <summary>Width in inches of one line of text.</summary>
    public static double Width(string text, double points, bool bold)
    {
        lock (Sync)
        {
            using var font = new Font("Segoe UI", (float)points, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
            return Surface.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width / 72.0 * 1.08;
        }
    }

    /// <summary>The text and the size that fit the box: smaller font first, down to the minimum, then the text cut with "…".</summary>
    public static (string Text, double Points) Fit(string text, double points, bool bold, double width, double height, bool wrap, double minPoints)
    {
        var size = points;
        while (true)
        {
            if (Fits(text, size, bold, width, height, wrap))
                return (text, size);
            if (size - 1 < minPoints)
                break;
            size -= 1;
        }
        size = Math.Max(minPoints, 1);
        var cut = text;
        while (cut.Length > 1 && !Fits(cut + "…", size, bold, width, height, wrap))
            cut = cut[..^1].TrimEnd();
        return (cut.Length < text.Length ? cut + "…" : cut, size);
    }

    private static bool Fits(string text, double points, bool bold, double width, double height, bool wrap)
    {
        var line = points * 1.2 / 72.0;
        if (!wrap)
            return Width(text, points, bold) <= width && line <= height + 0.08;
        // Wrapped: count the lines word by word.
        var lines = 1;
        var current = 0.0;
        var space = Width(" ", points, bold);
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = Width(word, points, bold);
            if (w > width)
                return false;
            if (current > 0 && current + space + w > width)
            {
                lines++;
                current = w;
            }
            else
            {
                current += (current > 0 ? space : 0) + w;
            }
        }
        return lines * line <= height + 0.02;
    }
}
