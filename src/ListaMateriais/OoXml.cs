using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ListaMateriais;

internal sealed record SheetInfo(string Name, string Path);

/// <summary>
/// Safe reading of Office Open XML packages: no DTD, no external resolution
/// and a decompressed size limit to guard against zip bombs.
/// </summary>
internal static class OoXml
{
    public const long MaxPartBytes = 100L * 1024 * 1024;

    public static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static readonly XNamespace PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    public static readonly XNamespace OfficeRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxPartBytes,
    };

    public static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxPartBytes)
            throw new InvalidDataException($"Parte muito grande no pacote: {entry.FullName}");

        using var source = entry.Open();
        using var buffer = new MemoryStream((int)Math.Max(entry.Length, 0));
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(chunk)) > 0)
        {
            total += read;
            if (total > MaxPartBytes)
                throw new InvalidDataException($"Parte muito grande no pacote: {entry.FullName}");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static XDocument? TryLoadXml(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null)
            return null;
        using var stream = new MemoryStream(ReadBytes(entry), writable: false);
        using var reader = XmlReader.Create(stream, ReaderSettings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    public static XDocument LoadXml(ZipArchive zip, string name) =>
        TryLoadXml(zip, name) ?? throw new InvalidDataException($"Parte ausente no pacote: {name}");

    public static byte[] Serialize(XDocument document)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false };
        using (var writer = XmlWriter.Create(stream, settings))
            document.Save(writer);
        return stream.ToArray();
    }

    public static IReadOnlyList<SheetInfo> Sheets(ZipArchive zip)
    {
        var workbook = LoadXml(zip, "xl/workbook.xml");
        var rels = LoadXml(zip, "xl/_rels/workbook.xml.rels");

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in rels.Root!.Elements(PackageRels + "Relationship"))
        {
            if ((string?)rel.Attribute("Id") is { } id && (string?)rel.Attribute("Target") is { } target)
                targets.TryAdd(id, target);
        }

        var sheets = new List<SheetInfo>();
        foreach (var sheet in workbook.Root!.Descendants(Main + "sheet"))
        {
            var name = (string?)sheet.Attribute("name");
            var relId = (string?)sheet.Attribute(OfficeRels + "id");
            if (name is null || relId is null || !targets.TryGetValue(relId, out var target))
                continue;
            var path = target.TrimStart('/');
            if (path.StartsWith("xl/", StringComparison.Ordinal))
                path = path[3..];
            sheets.Add(new SheetInfo(name, "xl/" + path));
        }
        return sheets;
    }

    public static IReadOnlyList<string> SharedStrings(ZipArchive zip)
    {
        var document = TryLoadXml(zip, "xl/sharedStrings.xml");
        if (document is null)
            return [];
        return document.Root!.Elements(Main + "si").Select(RichText).ToList();
    }

    /// <summary>Concatenates the &lt;t&gt; runs of rich text, skipping phonetic runs (rPh).</summary>
    public static string RichText(XElement container) =>
        string.Concat(container.Descendants(Main + "t")
            .Where(t => t.Parent?.Name != Main + "rPh")
            .Select(t => t.Value));

    public static (string Column, int Row) SplitRef(string cellRef)
    {
        var i = 0;
        while (i < cellRef.Length && char.IsAsciiLetterUpper(cellRef[i]))
            i++;
        if (i == 0 || i > 3 || !int.TryParse(cellRef.AsSpan(i), NumberStyles.None,
                CultureInfo.InvariantCulture, out var row) || row < 1)
            throw new InvalidDataException($"Referência de célula inválida: {cellRef}");
        return (cellRef[..i], row);
    }

    public static int ColumnIndex(string column)
    {
        var index = 0;
        foreach (var letter in column)
            index = index * 26 + letter - 'A' + 1;
        return index;
    }

    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
