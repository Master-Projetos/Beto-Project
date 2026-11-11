using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ListaMateriais.Kmz;

namespace ListaMateriais.Tests;

public class PointTextTests
{
    private static PointList Parse(string text, CoordinateOrder order = CoordinateOrder.LatitudeFirst) => PointText.Parse(text, order);

    [Theory]
    [InlineData("Poste 12, -23.5505, -46.6333", "Poste 12")]
    [InlineData("-23.5505, -46.6333, Poste 12", "Poste 12")]
    [InlineData("Poste 12; -23,5505; -46,6333", "Poste 12")]
    [InlineData("Poste 12, -23,5505, -46,6333", "Poste 12")]
    [InlineData("Rua A, 123, -23.5505, -46.6333", "Rua A, 123")]
    [InlineData("Poste 12 -23.5505 -46.6333", "Poste 12")]
    [InlineData("-23.5505, -46.6333", "Ponto 1")]
    [InlineData("-23,5505, -46,6333", "Ponto 1")]
    [InlineData("-23.5505 -46.6333", "Ponto 1")]
    [InlineData("23.5505° S, 46.6333° W", "Ponto 1")]
    public void Reads_a_single_entry(string text, string name)
    {
        var result = Parse(text);

        Assert.Empty(result.Problems);
        var point = Assert.Single(result.Points);
        Assert.Equal(name, point.Name);
        Assert.Equal(-23.5505, point.Latitude, 6);
        Assert.Equal(-46.6333, point.Longitude, 6);
    }

    [Fact]
    public void Reads_one_point_per_line_and_reports_the_bad_ones()
    {
        var result = Parse("A, -23.5, -46.6\n\nisto não é um ponto\nB, -20, -44");

        Assert.Equal(["A", "B"], result.Points.Select(p => p.Name));
        Assert.StartsWith("Linha 3", Assert.Single(result.Problems));
    }

    [Fact]
    public void Longitude_first_swaps_the_pair()
    {
        var point = Assert.Single(Parse("A, -46.6, -23.5", CoordinateOrder.LongitudeFirst).Points);

        Assert.Equal((-23.5, -46.6), (point.Latitude, point.Longitude));
    }

    [Fact]
    public void A_latitude_off_the_globe_is_a_problem_not_a_point()
    {
        var result = Parse("A, -146.6, -23.5");

        Assert.Empty(result.Points);
        Assert.Contains("ordem", Assert.Single(result.Problems));
    }

    [Theory]
    [InlineData("""{"A": (-23.5, -46.6), 'B': [-20, -44]}""")]
    [InlineData("""pontos = {"A": "-23.5, -46.6", "B": "-20,-44",}""")]
    [InlineData("""{"A": {"lat": -23.5, "lon": -46.6}, "B": {"latitude": "-20", "lng": -44}}""")]
    [InlineData("""[{"nome": "A", "lat": -23.5, "lon": -46.6}, {"name": "B", "y": -20, "x": -44}]""")]
    [InlineData("""[("A", -23.5, -46.6), (-20, -44, "B")]""")]
    [InlineData("""{"nome": ["A", "B"], "lat": [-23.5, -20], "lon": [-46.6, -44]}""")]
    [InlineData("""{(-23.5, -46.6): "A", (-20, -44): "B"}  # comentário""")]
    [InlineData("""{"A": {"coordenadas": (-23.5, -46.6)}, "B": {"Coordenadas": "-20, -44"}}""")]
    public void Reads_python_dicts_and_lists(string text)
    {
        var result = Parse(text);

        Assert.Empty(result.Problems);
        Assert.Equal([new MapPoint("A", -23.5, -46.6), new MapPoint("B", -20, -44)], result.Points);
    }

    [Fact]
    public void A_dict_that_is_one_point_is_read_as_such()
    {
        var point = Assert.Single(Parse("""{"nome": "CTO 1", "latitude": -23.5, "longitude": -46.6}""").Points);

        Assert.Equal(new MapPoint("CTO 1", -23.5, -46.6), point);
    }

    [Fact]
    public void Dict_labels_win_over_the_chosen_order()
    {
        var point = Assert.Single(Parse("""{"A": {"lon": -46.6, "lat": -23.5}}""", CoordinateOrder.LongitudeFirst).Points);

        Assert.Equal((-23.5, -46.6), (point.Latitude, point.Longitude));
    }

    [Theory]
    [InlineData("""{"A": (-23.5, -46.6)""")]
    [InlineData("""{"A": __import__("os")}""")]
    [InlineData("""{A: (-23.5, -46.6)}""")]
    public void A_broken_dict_is_reported_with_its_line(string text)
    {
        var result = Parse(text);

        Assert.Empty(result.Points);
        Assert.Contains("Linha 1", Assert.Single(result.Problems));
    }

    [Fact]
    public void Entries_without_coordinates_are_reported_by_name()
    {
        var result = Parse("""{"A": (-23.5, -46.6), "B": "sem coordenada"}""");

        Assert.Single(result.Points);
        Assert.StartsWith("\"B\"", Assert.Single(result.Problems));
    }
}

public class CoordinateTests
{
    [Theory]
    [InlineData("-23.5505", -23.5505)]
    [InlineData("-23,5505", -23.5505)]
    [InlineData("23°33'01.8\"S", -23.5505)]
    [InlineData("46° 38′ 00″ O", -46.633333)]
    [InlineData("S 23.5505", -23.5505)]
    [InlineData("46.6333 L", 46.6333)]
    public void Parses(string text, double expected)
    {
        Assert.True(Coordinate.TryParse(text, out var value));
        Assert.Equal(expected, value, 5);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12 abc")]
    [InlineData("23°75'S")]
    public void Rejects(string text) => Assert.False(Coordinate.TryParse(text, out _));
}

public class KmzWriterTests
{
    [Fact]
    public void Writes_the_kml_and_the_icon_inside_the_zip()
    {
        var icon = new MapIcon([1, 2, 3], AnchorBottom: true, Scale: 1.2);
        using var stream = new MemoryStream();
        KmzWriter.Write(stream, "Postes", [new MapPoint("A & B <1>\u0001", -23.5, -46.6)], icon);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal("doc.kml", zip.Entries[0].FullName);
        using (var picture = zip.GetEntry("files/icone.png")!.Open())
        {
            var bytes = new MemoryStream();
            picture.CopyTo(bytes);
            Assert.Equal(icon.Png, bytes.ToArray());
        }

        using var kml = zip.GetEntry("doc.kml")!.Open();
        XNamespace ns = "http://www.opengis.net/kml/2.2";
        var document = XDocument.Load(kml).Root!.Element(ns + "Document")!;
        Assert.Equal("Postes", document.Element(ns + "name")!.Value);
        var placemark = Assert.Single(document.Elements(ns + "Placemark"));
        Assert.Equal("A & B <1>", placemark.Element(ns + "name")!.Value);
        Assert.Equal("-46.6,-23.5,0", placemark.Descendants(ns + "coordinates").Single().Value);
        Assert.Equal("#" + document.Element(ns + "Style")!.Attribute("id")!.Value, placemark.Element(ns + "styleUrl")!.Value);
        Assert.Equal("files/icone.png", document.Descendants(ns + "href").Single().Value);
        Assert.Equal("0", document.Descendants(ns + "hotSpot").Single().Attribute("y")!.Value);
    }
}

public class SheetPointsTests
{
    [Fact]
    public void Reads_the_columns_chosen_by_the_user()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pontos-{Guid.NewGuid():N}.xlsx");
        try
        {
            File.WriteAllBytes(path, Workbook(
                """<row r="1"><c r="A1" t="inlineStr"><is><t>Nome</t></is></c><c r="B1" t="inlineStr"><is><t>Latitude</t></is></c><c r="C1" t="inlineStr"><is><t>Longitude</t></is></c></row>""" +
                """<row r="2"><c r="A2" t="inlineStr"><is><t>CTO 1</t></is></c><c r="B2"><v>-23.5505</v></c><c r="C2"><v>-46.6333</v></c></row>""" +
                """<row r="4"><c r="A4" t="inlineStr"><is><t>CTO 2</t></is></c><c r="B4" t="inlineStr"><is><t>-20,5</t></is></c><c r="C4" t="inlineStr"><is><t>-44,25</t></is></c></row>""" +
                """<row r="5"><c r="A5" t="inlineStr"><is><t>CTO 3</t></is></c><c r="B5" t="inlineStr"><is><t>?</t></is></c><c r="C5"><v>-44</v></c></row>"""));

            var table = Assert.Single(XlsxTable.Read(path));
            Assert.Equal("Pontos", table.Name);
            Assert.Equal((0, 1, 2), SheetPoints.GuessColumns(table));
            Assert.True(SheetPoints.HasHeader(table, 1, 2));

            var result = SheetPoints.Read(table, 0, 1, 2, hasHeader: true);
            Assert.Equal([new MapPoint("CTO 1", -23.5505, -46.6333), new MapPoint("CTO 2", -20.5, -44.25)], result.Points);
            Assert.StartsWith("Linha 5", Assert.Single(result.Problems));

            Assert.Equal(["Ponto 1", "Ponto 2"], SheetPoints.Read(table, -1, 1, 2, hasHeader: true).Points.Select(p => p.Name));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Column_letters() => Assert.Equal(["A", "Z", "AA", "AB"], new[] { 0, 25, 26, 27 }.Select(SheetTable.ColumnLetter));

    private static byte[] Workbook(string rows)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Add(zip, "xl/workbook.xml",
                """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Pontos" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels",
                """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Add(zip, "xl/worksheets/sheet1.xml",
                $"""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{rows}</sheetData></worksheet>""");
        }
        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var entry = zip.CreateEntry(name).Open();
        entry.Write(Encoding.UTF8.GetBytes(content));
    }
}

public class MixedLinesTests
{
    [Fact]
    public void A_dict_on_its_own_line_is_read_among_plain_lines()
    {
        var result = PointText.Parse("A, -23.5, -46.6\n{\"B\": (-20, -44)}\n(\"C\", -10, -40)\n{quebrado", CoordinateOrder.LatitudeFirst);

        Assert.Equal(["A", "B", "C"], result.Points.Select(p => p.Name));
        Assert.StartsWith("Linha 4: ", Assert.Single(result.Problems));
        Assert.DoesNotContain("Linha 1", result.Problems[0]);
    }
}
