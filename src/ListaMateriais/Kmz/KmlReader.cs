using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace ListaMateriais.Kmz;

/// <summary>What a placemark draws on the map.</summary>
internal enum FeatureKind { Point, Line, Area }

/// <summary>A place on the map, in decimal degrees.</summary>
internal readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>
/// One drawing of a map read from a KML: a point, a line or an area (its outer edge), with the folder it
/// was in and the color it was drawn with. A placemark with several drawings becomes several features.
/// </summary>
/// <param name="Folder">Folder names from the top, joined by " / "; empty at the top of the document.</param>
/// <param name="Color">The color the original map used for it; null when it had none of its own.</param>
internal sealed record MapFeature(string Name, string Folder, FeatureKind Kind, IReadOnlyList<GeoPoint> Points, Color? Color);

/// <summary>The drawings of a map file, and how many parts of it were left out because they are not drawings.</summary>
internal sealed record MapContent(string Title, IReadOnlyList<MapFeature> Features, int Skipped);

/// <summary>
/// Reads the points, lines and areas of a KMZ or KML made by any program (Google Earth, My Maps, QGIS,
/// design tools). Only what is drawn is kept: names, folders and colors; descriptions, tables of data,
/// photos, 3D models and links to other files are left out.
/// </summary>
internal static class KmlReader
{
    public static MapContent Read(string path)
    {
        byte[] bytes;
        try
        {
            bytes = Files.ReadAllBytesShared(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new AppException($"Não foi possível abrir \"{Path.GetFileName(path)}\".\n\nDetalhe: {e.Message}", e);
        }
        return Read(bytes, Path.GetFileNameWithoutExtension(path), Path.GetFileName(path));
    }

    /// <param name="fallbackTitle">Title when the document has no name of its own.</param>
    /// <param name="fileName">For the messages.</param>
    public static MapContent Read(byte[] bytes, string fallbackTitle, string fileName)
    {
        XDocument document;
        try
        {
            using var kml = OpenKml(bytes, fileName);
            using var reader = XmlReader.Create(kml, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true,
            });
            document = XDocument.Load(reader);
        }
        catch (Exception e) when (e is XmlException or InvalidDataException)
        {
            throw new AppException($"\"{fileName}\" não é um mapa KMZ/KML válido.\n\nDetalhe: {e.Message}", e);
        }

        var root = document.Root ?? throw new AppException($"\"{fileName}\" está vazio.");
        var styles = Styles(root);
        var features = new List<MapFeature>();
        var skipped = 0;
        var top = Child(root, "Document") ?? root;
        Walk(top, "", styles, features, ref skipped);
        var title = Text(Child(top, "name"));
        return new MapContent(string.IsNullOrWhiteSpace(title) ? fallbackTitle : title.Trim(), features, skipped);
    }

    /// <summary>The KML itself, or the first .kml inside the KMZ (that is the one readers take as the document).</summary>
    private static Stream OpenKml(byte[] bytes, string fileName)
    {
        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K')
            return new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                    ?? throw new AppException($"\"{fileName}\" não tem nenhum mapa (.kml) dentro.");
        var copy = new MemoryStream();
        using (var stream = entry.Open())
            stream.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    // ---- Styles --------------------------------------------------------------------------------

    /// <summary>The colors a style draws with: lines, area fill and icon.</summary>
    private sealed record StyleColors(Color? Line, Color? Fill, Color? Icon)
    {
        public static readonly StyleColors None = new(null, null, null);

        /// <summary>The colors of <paramref name="over"/>, falling back to these where it has none.</summary>
        public StyleColors With(StyleColors over) => new(over.Line ?? Line, over.Fill ?? Fill, over.Icon ?? Icon);
    }

    /// <summary>Every shared style of the document by id, with style maps resolved to their normal look.</summary>
    private static Dictionary<string, StyleColors> Styles(XElement root)
    {
        var styles = new Dictionary<string, StyleColors>(StringComparer.Ordinal);
        foreach (var style in root.Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            if (style.Attribute("id")?.Value is { Length: > 0 } id)
                styles[id] = StyleOf(style);
        }
        // Style maps may point to other style maps; a few rounds settle any reasonable chain.
        var maps = root.Descendants().Where(e => e.Name.LocalName == "StyleMap" && e.Attribute("id") is not null).ToList();
        for (var round = 0; round < 3; round++)
        {
            foreach (var map in maps)
            {
                var normal = map.Elements().Where(e => e.Name.LocalName == "Pair")
                    .FirstOrDefault(pair => Text(Child(pair, "key"))?.Trim() == "normal");
                if (normal is null)
                    continue;
                var colors = StyleColors.None;
                if (Text(Child(normal, "styleUrl")) is { } url && styles.TryGetValue(Id(url), out var linked))
                    colors = linked;
                if (Child(normal, "Style") is { } inline)
                    colors = colors.With(StyleOf(inline));
                styles[map.Attribute("id")!.Value] = colors;
            }
        }
        return styles;
    }

    private static StyleColors StyleOf(XElement style) => new(
        ColorOf(Child(style, "LineStyle")),
        Child(style, "PolyStyle") is { } poly && Text(Child(poly, "fill"))?.Trim() is not ("0" or "false") ? ColorOf(poly) : null,
        ColorOf(Child(style, "IconStyle")));

    private static Color? ColorOf(XElement? substyle) =>
        Text(Child(substyle, "color")) is { } text ? ParseColor(text) : null;

    /// <summary>A KML color: aabbggrr in hexadecimal.</summary>
    internal static Color? ParseColor(string text)
    {
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 6)
            hex = "ff" + hex;
        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return null;
        return Color.FromArgb((int)(value >> 24), (int)(value & 0xFF), (int)((value >> 8) & 0xFF), (int)((value >> 16) & 0xFF));
    }

    private static string Id(string url)
    {
        var hash = url.LastIndexOf('#');
        return (hash >= 0 ? url[(hash + 1)..] : url).Trim();
    }

    // ---- Features ------------------------------------------------------------------------------

    private static void Walk(XElement container, string folder, Dictionary<string, StyleColors> styles,
        List<MapFeature> features, ref int skipped)
    {
        foreach (var element in container.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "Folder":
                case "Document":
                    var name = Text(Child(element, "name"))?.Trim();
                    var path = string.IsNullOrEmpty(name) ? folder : folder.Length == 0 ? name : $"{folder} / {name}";
                    Walk(element, path, styles, features, ref skipped);
                    break;
                case "Placemark":
                    var before = features.Count;
                    Placemark(element, folder, styles, features);
                    if (features.Count == before)
                        skipped++;
                    break;
                case "GroundOverlay":
                case "ScreenOverlay":
                case "PhotoOverlay":
                case "NetworkLink":
                    skipped++;
                    break;
            }
        }
    }

    private static void Placemark(XElement placemark, string folder, Dictionary<string, StyleColors> styles, List<MapFeature> features)
    {
        var colors = StyleColors.None;
        if (Text(Child(placemark, "styleUrl")) is { } url && styles.TryGetValue(Id(url), out var shared))
            colors = shared;
        if (Child(placemark, "Style") is { } inline)
            colors = colors.With(StyleOf(inline));
        var name = Text(Child(placemark, "name"))?.Trim() ?? string.Empty;

        foreach (var geometry in placemark.Elements())
            Geometry(geometry, name, folder, colors, features);
    }

    private static void Geometry(XElement geometry, string name, string folder, StyleColors colors, List<MapFeature> features)
    {
        switch (geometry.Name.LocalName)
        {
            case "Point":
                if (Coordinates(geometry) is { Count: > 0 } point)
                    features.Add(new MapFeature(name, folder, FeatureKind.Point, [point[0]], colors.Icon));
                break;
            case "LineString":
            case "LinearRing":
                if (Coordinates(geometry) is { Count: >= 2 } line)
                    features.Add(new MapFeature(name, folder, FeatureKind.Line, line, colors.Line));
                break;
            case "Polygon":
                var outer = geometry.Elements().FirstOrDefault(e => e.Name.LocalName == "outerBoundaryIs");
                var ring = outer?.Descendants().FirstOrDefault(e => e.Name.LocalName == "LinearRing");
                if (ring is not null && Coordinates(ring) is { Count: >= 3 } edge)
                    features.Add(new MapFeature(name, folder, FeatureKind.Area, edge, colors.Fill ?? colors.Line));
                break;
            case "MultiGeometry":
            case "MultiTrack":
                foreach (var part in geometry.Elements())
                    Geometry(part, name, folder, colors, features);
                break;
            case "Track":
                var track = geometry.Elements().Where(e => e.Name.LocalName == "coord")
                    .Select(e => Point(e.Value, ' ')).OfType<GeoPoint>().ToList();
                if (track.Count >= 2)
                    features.Add(new MapFeature(name, folder, FeatureKind.Line, track, colors.Line));
                break;
        }
    }

    /// <summary>The "longitude,latitude[,altitude]" tuples of a geometry, separated by spaces or line breaks.</summary>
    private static List<GeoPoint> Coordinates(XElement geometry)
    {
        var points = new List<GeoPoint>();
        if (Text(Child(geometry, "coordinates")) is not { } text)
            return points;
        foreach (var tuple in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Point(tuple, ',') is { } point)
                points.Add(point);
        }
        return points;
    }

    private static GeoPoint? Point(string tuple, char separator)
    {
        var parts = tuple.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            || Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
            return null;
        return new GeoPoint(latitude, longitude);
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Text(XElement? element) => element?.Value;
}
