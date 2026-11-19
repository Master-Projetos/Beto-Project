using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ListaMateriais.Kmz;

/// <summary>
/// Writes the simple map: one folder per legend entry, thick lines, big round markers without labels,
/// no descriptions, and the legend itself drawn on the screen corner of Google Earth.
/// </summary>
internal static class SimpleKmzWriter
{
    private const string LegendPath = "files/legenda.png";
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    public static void Write(string output, SimpleMap map, bool legend)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Write(file, map, legend);
            File.Move(temp, output, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            KmzWriter.TryDelete(temp);
            throw new AppException($"Não foi possível salvar \"{Path.GetFileName(output)}\" em:\n{folder}\n\n" +
                                   "Se o arquivo estiver aberto no Google Earth, feche-o e tente de novo.\n\n" +
                                   $"Detalhe: {e.Message}", e);
        }
    }

    public static void Write(Stream output, SimpleMap map, bool legend)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        using (var kml = zip.CreateEntry("doc.kml", CompressionLevel.Optimal).Open())
        using (var writer = XmlWriter.Create(kml, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            Document(map, legend).Save(writer);
        for (var i = 0; i < map.Layers.Count; i++)
        {
            if (map.Layers[i].Points.Count == 0)
                continue;
            using var bitmap = MarkerIcon.Draw(MarkerShape.Circle, map.Layers[i].Color, 64);
            using var picture = zip.CreateEntry(IconPath(i), CompressionLevel.NoCompression).Open();
            picture.Write(MarkerIcon.Png(bitmap));
        }
        if (legend && map.Layers.Count > 0)
        {
            using var bitmap = Legend(map);
            using var picture = zip.CreateEntry(LegendPath, CompressionLevel.NoCompression).Open();
            picture.Write(MarkerIcon.Png(bitmap));
        }
    }

    private static string IconPath(int layer) => $"files/ponto{layer + 1}.png";

    internal static XDocument Document(SimpleMap map, bool legend)
    {
        var document = new XElement(Kml + "Document", new XElement(Kml + "name", KmzWriter.Clean(map.Title)),
            new XElement(Kml + "open", 1));
        for (var i = 0; i < map.Layers.Count; i++)
        {
            document.Add(Style(i, map.Layers[i], map.LineWidth));
            if (map.Outline && map.Layers[i].Lines.Count > 0)
                document.Add(OutlineStyle(i, map.LineWidth));
        }
        if (legend && map.Layers.Count > 0)
            document.Add(LegendOverlay());

        for (var i = 0; i < map.Layers.Count; i++)
        {
            var layer = map.Layers[i];
            var style = "#camada" + (i + 1);
            var folder = new XElement(Kml + "Folder", new XElement(Kml + "name", KmzWriter.Clean(layer.Name)));
            foreach (var place in layer.Points)
            {
                var name = place.Count > 1 ? $"{layer.Name} ({place.Count})" : layer.Name;
                folder.Add(Placemark(name, style, new XElement(Kml + "Point", Coordinates([place.Point]))));
            }
            // The dark edge goes first so that Google Earth draws the color on top of it.
            if (map.Outline)
            {
                foreach (var line in layer.Lines)
                {
                    folder.Add(Placemark(layer.Name, style + OutlineSuffix, new XElement(Kml + "LineString",
                        new XElement(Kml + "tessellate", 1), Coordinates(line))));
                }
            }
            foreach (var line in layer.Lines)
            {
                folder.Add(Placemark(layer.Name, style, new XElement(Kml + "LineString",
                    new XElement(Kml + "tessellate", 1), Coordinates(line))));
            }
            foreach (var area in layer.Areas)
            {
                var ring = area[0] == area[^1] ? area : [.. area, area[0]];
                folder.Add(Placemark(layer.Name, style, new XElement(Kml + "Polygon",
                    new XElement(Kml + "tessellate", 1),
                    new XElement(Kml + "outerBoundaryIs", new XElement(Kml + "LinearRing", Coordinates(ring))))));
            }
            document.Add(folder);
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(Kml + "kml", document));
    }

    private static XElement Placemark(string name, string style, XElement geometry) =>
        new(Kml + "Placemark", new XElement(Kml + "name", KmzWriter.Clean(name)), new XElement(Kml + "styleUrl", style), geometry);

    private static XElement Style(int index, SimpleLayer layer, double lineWidth) =>
        new(Kml + "Style", new XAttribute("id", "camada" + (index + 1)),
            new XElement(Kml + "IconStyle",
                new XElement(Kml + "scale", "1.2"),
                new XElement(Kml + "Icon", new XElement(Kml + "href", IconPath(index))),
                new XElement(Kml + "hotSpot", new XAttribute("x", "0.5"), new XAttribute("y", "0.5"),
                    new XAttribute("xunits", "fraction"), new XAttribute("yunits", "fraction"))),
            // The names would cover the map; they show when a marker is clicked, and the legend says what each color is.
            new XElement(Kml + "LabelStyle", new XElement(Kml + "scale", "0")),
            new XElement(Kml + "LineStyle", new XElement(Kml + "color", KmlColor(layer.Color, 0xFF)), new XElement(Kml + "width", Number(lineWidth))),
            new XElement(Kml + "PolyStyle", new XElement(Kml + "color", KmlColor(layer.Color, 0x66))),
            new XElement(Kml + "BalloonStyle", new XElement(Kml + "text", "<b>$[name]</b>")));

    private const string OutlineSuffix = "-contorno";

    /// <summary>Dark and a bit wider than the line: it shows as a thin edge on each side of it.</summary>
    internal static Color OutlineColor => Color.FromArgb(0xD9, 0x10, 0x10, 0x10);

    internal static double OutlineWidth(double lineWidth) => lineWidth + Math.Max(4, Math.Round(lineWidth * 0.5));

    private static XElement OutlineStyle(int index, double lineWidth) =>
        new(Kml + "Style", new XAttribute("id", "camada" + (index + 1) + OutlineSuffix),
            new XElement(Kml + "LabelStyle", new XElement(Kml + "scale", "0")),
            new XElement(Kml + "LineStyle", new XElement(Kml + "color", KmlColor(OutlineColor, OutlineColor.A)),
                new XElement(Kml + "width", Number(OutlineWidth(lineWidth)))),
            new XElement(Kml + "BalloonStyle", new XElement(Kml + "text", "<b>$[name]</b>")));

    private static XElement LegendOverlay() =>
        new(Kml + "ScreenOverlay",
            new XElement(Kml + "name", "Legenda"),
            new XElement(Kml + "Icon", new XElement(Kml + "href", LegendPath)),
            Xy("overlayXY", 0, 1), Xy("screenXY", 0.01, 0.98),
            new XElement(Kml + "size", new XAttribute("x", "-1"), new XAttribute("y", "-1"),
                new XAttribute("xunits", "pixels"), new XAttribute("yunits", "pixels")));

    private static XElement Xy(string name, double x, double y) =>
        new(Kml + name, new XAttribute("x", Number(x)), new XAttribute("y", Number(y)),
            new XAttribute("xunits", "fraction"), new XAttribute("yunits", "fraction"));

    private static XElement Coordinates(IEnumerable<GeoPoint> points) =>
        new(Kml + "coordinates", string.Join(' ', points.Select(p => $"{Number(p.Longitude)},{Number(p.Latitude)},0")));

    /// <summary>KML writes colors as aabbggrr.</summary>
    internal static string KmlColor(Color color, int alpha) => $"{alpha:x2}{color.B:x2}{color.G:x2}{color.R:x2}";

    private static string Number(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);

    // ---- Legend --------------------------------------------------------------------------------

    /// <summary>A white card with one row per layer: its color drawn as it is on the map, then its name.</summary>
    internal static Bitmap Legend(SimpleMap map)
    {
        const int pad = 14, row = 34, swatch = 34, gap = 10;
        using var titleFont = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
        using var font = new Font("Segoe UI", 15, FontStyle.Regular, GraphicsUnit.Pixel);
        int width;
        using (var measure = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(measure))
        {
            var widest = map.Layers.Max(layer => g.MeasureString(layer.Name, font).Width);
            width = (int)Math.Ceiling(Math.Max(g.MeasureString("Legenda", titleFont).Width, swatch + gap + widest)) + pad * 2;
        }
        width = Math.Min(width, 420);
        var height = pad * 2 + 26 + row * map.Layers.Count;

        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var card = Ui.Rounded(new RectangleF(0.5f, 0.5f, width - 1, height - 1), 10))
            using (var fill = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            using (var edge = new Pen(Color.FromArgb(120, 0, 0, 0)))
            {
                g.FillPath(fill, card);
                g.DrawPath(edge, card);
            }
            using var text = new SolidBrush(Color.FromArgb(0x21, 0x21, 0x21));
            g.DrawString("Legenda", titleFont, text, pad, pad - 2);
            var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            for (var i = 0; i < map.Layers.Count; i++)
            {
                var layer = map.Layers[i];
                var top = pad + 26 + i * row;
                var box = new RectangleF(pad, top + (row - swatch) / 2f + 4, swatch, swatch - 8);
                DrawSwatch(g, layer, box, (float)Math.Clamp(map.LineWidth, 4, 12), map.Outline);
                g.DrawString(layer.Name, font, text, new RectangleF(pad + swatch + gap, top, width - pad * 2 - swatch - gap, row), format);
            }
        }
        return bitmap;
    }

    private static void DrawSwatch(Graphics g, SimpleLayer layer, RectangleF box, float lineWidth, bool outline)
    {
        using var brush = new SolidBrush(layer.Color);
        switch (layer.Main)
        {
            case FeatureKind.Line:
                if (outline)
                {
                    using var edge = new Pen(OutlineColor, (float)OutlineWidth(lineWidth)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(edge, box.Left + 3, box.Top + box.Height / 2, box.Right - 3, box.Top + box.Height / 2);
                }
                using (var pen = new Pen(layer.Color, lineWidth) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, box.Left + 3, box.Top + box.Height / 2, box.Right - 3, box.Top + box.Height / 2);
                break;
            case FeatureKind.Area:
                using (var soft = new SolidBrush(Color.FromArgb(0x66, layer.Color)))
                using (var pen = new Pen(layer.Color, 3))
                {
                    g.FillRectangle(soft, box);
                    g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                }
                break;
            default:
                var side = Math.Min(box.Width, box.Height) + 2;
                var circle = new RectangleF(box.Left + (box.Width - side) / 2, box.Top + (box.Height - side) / 2, side, side);
                g.FillEllipse(brush, circle);
                using (var ring = new Pen(Color.White, 2))
                    g.DrawEllipse(ring, circle);
                break;
        }
    }
}
