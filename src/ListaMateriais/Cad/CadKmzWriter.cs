using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using ListaMateriais.Kmz;

namespace ListaMateriais.Cad;

/// <summary>Writes a <see cref="CadMap"/> as a KML or KMZ for Google Earth: one folder per layer, with the drawing's colors.</summary>
internal static class CadKmzWriter
{
    private const string IconPath = "files/ponto.png";

    /// <param name="output">A path ending in .kml (plain) or .kmz (zipped, with the marker picture).</param>
    /// <param name="markerPng">The picture that marks points and block references; tinted with each one's color.</param>
    public static void Write(string output, string title, CadMap map, byte[] markerPng)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (output.EndsWith(".kml", StringComparison.OrdinalIgnoreCase))
                    WriteKml(file, title, map, iconHref: null);
                else
                    Write(file, title, map, markerPng);
            }
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

    public static void Write(Stream output, string title, CadMap map, byte[] markerPng)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        // doc.kml goes first: readers take the first .kml of the archive as the document.
        using (var kml = zip.CreateEntry("doc.kml", CompressionLevel.Optimal).Open())
            WriteKml(kml, title, map, IconPath);
        using var picture = zip.CreateEntry(IconPath, CompressionLevel.NoCompression).Open();
        picture.Write(markerPng);
    }

    private static string KmlColor(int rgb, int alpha = 255) =>
        $"{alpha:x2}{rgb & 0xFF:x2}{(rgb >> 8) & 0xFF:x2}{(rgb >> 16) & 0xFF:x2}";

    private static string Number(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);

    internal static void WriteKml(Stream output, string title, CadMap map, string? iconHref)
    {
        using var xml = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false });
        xml.WriteStartDocument();
        xml.WriteStartElement("kml", "http://www.opengis.net/kml/2.2");
        xml.WriteStartElement("Document");
        xml.WriteElementString("name", KmzWriter.Clean(title));

        // One style per color, for lines, points and labels.
        foreach (var rgb in map.Features.Select(f => f.Rgb).Distinct().Order())
        {
            var id = rgb.ToString("x6", CultureInfo.InvariantCulture);
            xml.WriteStartElement("Style");
            xml.WriteAttributeString("id", "l" + id);
            xml.WriteStartElement("LineStyle");
            xml.WriteElementString("color", KmlColor(rgb));
            xml.WriteElementString("width", "2");
            xml.WriteEndElement();
            xml.WriteEndElement();

            xml.WriteStartElement("Style");
            xml.WriteAttributeString("id", "p" + id);
            xml.WriteStartElement("IconStyle");
            xml.WriteElementString("color", KmlColor(rgb));
            xml.WriteElementString("scale", "0.6");
            if (iconHref is not null)
            {
                xml.WriteStartElement("Icon");
                xml.WriteElementString("href", iconHref);
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
            xml.WriteStartElement("LabelStyle");
            xml.WriteElementString("color", KmlColor(rgb));
            xml.WriteElementString("scale", "0.8");
            xml.WriteEndElement();
            xml.WriteEndElement();

            xml.WriteStartElement("Style");
            xml.WriteAttributeString("id", "t" + id);
            xml.WriteStartElement("IconStyle");
            xml.WriteElementString("scale", "0");
            xml.WriteEndElement();
            xml.WriteStartElement("LabelStyle");
            xml.WriteElementString("color", KmlColor(rgb));
            xml.WriteElementString("scale", "0.8");
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        foreach (var layer in map.Features.GroupBy(f => f.Layer))
        {
            xml.WriteStartElement("Folder");
            xml.WriteElementString("name", KmzWriter.Clean(layer.Key));
            foreach (var feature in layer)
            {
                var id = feature.Rgb.ToString("x6", CultureInfo.InvariantCulture);
                xml.WriteStartElement("Placemark");
                if (feature.Name.Length > 0)
                    xml.WriteElementString("name", KmzWriter.Clean(feature.Name));
                if (feature.Kind == GeoKind.Line)
                {
                    xml.WriteElementString("styleUrl", "#l" + id);
                    xml.WriteStartElement("LineString");
                    xml.WriteElementString("tessellate", "1");
                    xml.WriteElementString("coordinates",
                        string.Join(' ', feature.Points.Select(p => $"{Number(p.Longitude)},{Number(p.Latitude)},0")));
                    xml.WriteEndElement();
                }
                else
                {
                    xml.WriteElementString("styleUrl", (feature.Kind == GeoKind.Label ? "#t" : "#p") + id);
                    xml.WriteStartElement("Point");
                    xml.WriteElementString("coordinates", $"{Number(feature.Points[0].Longitude)},{Number(feature.Points[0].Latitude)},0");
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }
}
