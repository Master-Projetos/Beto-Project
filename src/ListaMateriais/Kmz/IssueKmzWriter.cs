using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ListaMateriais.Kmz;

/// <summary>
/// Writes the issues of a map check as markers: red for errors, yellow for warnings, one folder per kind
/// of problem. Opened in Google Earth together with the original map, each marker sits on what to fix.
/// </summary>
internal static class IssueKmzWriter
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    public static readonly Color ErrorColor = Color.FromArgb(0xE5, 0x39, 0x35);
    public static readonly Color WarningColor = Color.FromArgb(0xFF, 0xC1, 0x07);

    public static void Write(string output, string title, IReadOnlyList<MapIssue> issues)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Write(file, title, issues);
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

    public static void Write(Stream output, string title, IReadOnlyList<MapIssue> issues)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        using (var kml = zip.CreateEntry("doc.kml", CompressionLevel.Optimal).Open())
        using (var writer = XmlWriter.Create(kml, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            Document(title, issues).Save(writer);
        foreach (var (level, color) in new[] { (IssueLevel.Error, ErrorColor), (IssueLevel.Warning, WarningColor) })
        {
            using var bitmap = MarkerIcon.Draw(MarkerShape.Circle, color, 64);
            using var picture = zip.CreateEntry(IconPath(level), CompressionLevel.NoCompression).Open();
            picture.Write(MarkerIcon.Png(bitmap));
        }
    }

    private static string IconPath(IssueLevel level) => level == IssueLevel.Error ? "files/erro.png" : "files/aviso.png";

    internal static XDocument Document(string title, IReadOnlyList<MapIssue> issues)
    {
        var document = new XElement(Kml + "Document", new XElement(Kml + "name", KmzWriter.Clean(title)), new XElement(Kml + "open", 1));
        foreach (var level in new[] { IssueLevel.Error, IssueLevel.Warning })
        {
            document.Add(new XElement(Kml + "Style", new XAttribute("id", level == IssueLevel.Error ? "erro" : "aviso"),
                new XElement(Kml + "IconStyle", new XElement(Kml + "scale", "1.1"),
                    new XElement(Kml + "Icon", new XElement(Kml + "href", IconPath(level))),
                    new XElement(Kml + "hotSpot", new XAttribute("x", "0.5"), new XAttribute("y", "0.5"),
                        new XAttribute("xunits", "fraction"), new XAttribute("yunits", "fraction"))),
                new XElement(Kml + "LabelStyle", new XElement(Kml + "scale", "0.8")),
                new XElement(Kml + "BalloonStyle", new XElement(Kml + "text", "<b>$[name]</b><br/><br/>$[description]"))));
        }
        foreach (var group in issues.GroupBy(i => (i.Level, i.Problem)))
        {
            var label = $"{(group.Key.Level == IssueLevel.Error ? "Erro" : "Aviso")}: {group.Key.Problem} ({group.Count()})";
            var folder = new XElement(Kml + "Folder", new XElement(Kml + "name", KmzWriter.Clean(label)));
            foreach (var issue in group)
            {
                var description = $"{issue.Detail}\n\nItem: {issue.Item}" + (issue.Folder.Length > 0 ? $"\nPasta: {issue.Folder}" : "");
                folder.Add(new XElement(Kml + "Placemark",
                    new XElement(Kml + "name", KmzWriter.Clean($"{issue.Problem}: {issue.Item}")),
                    new XElement(Kml + "description", KmzWriter.Clean(description).Replace("\n", "<br/>", StringComparison.Ordinal)),
                    new XElement(Kml + "styleUrl", issue.Level == IssueLevel.Error ? "#erro" : "#aviso"),
                    new XElement(Kml + "Point", new XElement(Kml + "coordinates",
                        $"{Number(issue.Where.Longitude)},{Number(issue.Where.Latitude)},0"))));
            }
            document.Add(folder);
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), new XElement(Kml + "kml", document));
    }

    private static string Number(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);
}
