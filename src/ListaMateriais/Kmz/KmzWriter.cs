using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ListaMateriais.Kmz;

/// <summary>The markers the app can draw.</summary>
internal enum MarkerShape { Pin, Circle, Square, Triangle, Diamond, Star }

/// <summary>The picture of the placemarks, stored inside the KMZ so it shows on any computer.</summary>
/// <param name="Png">PNG bytes.</param>
/// <param name="AnchorBottom">True when the bottom middle of the picture marks the spot (a pin); false for its center.</param>
/// <param name="Scale">Size on the map; 1 is the picture's own size.</param>
internal sealed record MapIcon(byte[] Png, bool AnchorBottom, double Scale = 1.0);

/// <summary>Writes a KMZ (zipped KML) with one placemark per point, for Google Earth and Google Maps.</summary>
internal static class KmzWriter
{
    private const string IconPath = "files/icone.png";
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    public static void Write(string output, string title, IReadOnlyList<MapPoint> points, MapIcon icon)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Write(file, title, points, icon);
            File.Move(temp, output, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new AppException($"Não foi possível salvar \"{Path.GetFileName(output)}\" em:\n{folder}\n\n" +
                                   "Se o arquivo estiver aberto no Google Earth, feche-o e tente de novo.\n\n" +
                                   $"Detalhe: {e.Message}", e);
        }
    }

    public static void Write(Stream output, string title, IReadOnlyList<MapPoint> points, MapIcon icon)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        // doc.kml goes first: readers take the first .kml of the archive as the document.
        using (var kml = zip.CreateEntry("doc.kml", CompressionLevel.Optimal).Open())
        using (var writer = XmlWriter.Create(kml, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            Document(title, points, icon).Save(writer);
        using (var picture = zip.CreateEntry(IconPath, CompressionLevel.NoCompression).Open())
            picture.Write(icon.Png);
    }

    internal static XDocument Document(string title, IReadOnlyList<MapPoint> points, MapIcon icon)
    {
        const string styleId = "ponto";
        var hotSpot = new XElement(Kml + "hotSpot",
            new XAttribute("x", "0.5"), new XAttribute("y", icon.AnchorBottom ? "0" : "0.5"),
            new XAttribute("xunits", "fraction"), new XAttribute("yunits", "fraction"));

        var document = new XElement(Kml + "Document",
            new XElement(Kml + "name", Clean(title)),
            new XElement(Kml + "Style", new XAttribute("id", styleId),
                new XElement(Kml + "IconStyle",
                    new XElement(Kml + "scale", Number(icon.Scale)),
                    new XElement(Kml + "Icon", new XElement(Kml + "href", IconPath)),
                    hotSpot)));

        foreach (var point in points)
        {
            document.Add(new XElement(Kml + "Placemark",
                new XElement(Kml + "name", Clean(point.Name)),
                new XElement(Kml + "styleUrl", "#" + styleId),
                new XElement(Kml + "Point",
                    // KML wants longitude first.
                    new XElement(Kml + "coordinates", $"{Number(point.Longitude)},{Number(point.Latitude)},0"))));
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(Kml + "kml", document));
    }

    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    /// <summary>Drops the control characters XML cannot hold (they come along when pasting from other programs).</summary>
    private static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                clean.Append(text[i]).Append(text[++i]);
            else if (XmlConvert.IsXmlChar(text[i]))
                clean.Append(text[i]);
        }
        return clean.ToString();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing else to do: the temporary file stays behind.
        }
    }
}
