using System.Drawing;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ListaMateriais.Kmz;

namespace ListaMateriais.Tests;

public class KmlReaderTests
{
    private const string Busy = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2" xmlns:gx="http://www.google.com/kml/ext/2.2">
          <Document>
            <name>Projeto FTTH</name>
            <Style id="cabo"><LineStyle><color>ff0000ff</color><width>2</width></LineStyle></Style>
            <Style id="cabo-hl"><LineStyle><color>ff00ffff</color></LineStyle></Style>
            <StyleMap id="cabo-map">
              <Pair><key>normal</key><styleUrl>#cabo</styleUrl></Pair>
              <Pair><key>highlight</key><styleUrl>#cabo-hl</styleUrl></Pair>
            </StyleMap>
            <Folder>
              <name>CABOS_AS</name>
              <Placemark>
                <name>CB-AS-12FO-001</name>
                <description><![CDATA[<table><tr><td>Fibras</td><td>12</td></tr></table>]]></description>
                <ExtendedData><Data name="lote"><value>7</value></Data></ExtendedData>
                <styleUrl>#cabo-map</styleUrl>
                <LineString><coordinates>-46.0,-23.0,0 -46.0005,-23.0,0</coordinates></LineString>
              </Placemark>
            </Folder>
            <Folder>
              <name>POSTES</name>
              <Placemark>
                <name>P-0001</name>
                <Style><IconStyle><color>ff00ff00</color></IconStyle></Style>
                <Point><coordinates>-46.1,-23.1</coordinates></Point>
              </Placemark>
              <Placemark>
                <name>Duas coisas</name>
                <MultiGeometry>
                  <Point><coordinates>-46.2,-23.2,0</coordinates></Point>
                  <Polygon><outerBoundaryIs><LinearRing><coordinates>
                    -46.0,-23.0 -46.0,-23.001 -46.001,-23.001 -46.0,-23.0
                  </coordinates></LinearRing></outerBoundaryIs></Polygon>
                </MultiGeometry>
              </Placemark>
            </Folder>
            <GroundOverlay><name>Planta</name></GroundOverlay>
          </Document>
        </kml>
        """;

    private static MapContent Read(string kml) => KmlReader.Read(Encoding.UTF8.GetBytes(kml), "arquivo", "arquivo.kml");

    [Fact]
    public void Reads_points_lines_and_areas_with_their_folder_and_color()
    {
        var map = Read(Busy);

        Assert.Equal("Projeto FTTH", map.Title);
        Assert.Equal(1, map.Skipped);
        Assert.Equal(4, map.Features.Count);

        var cable = map.Features[0];
        Assert.Equal(FeatureKind.Line, cable.Kind);
        Assert.Equal("CABOS_AS", cable.Folder);
        // The style map points to the normal style: red (KML colors are aabbggrr).
        Assert.Equal(Color.FromArgb(255, 255, 0, 0).ToArgb(), cable.Color!.Value.ToArgb());

        var pole = map.Features[1];
        Assert.Equal(FeatureKind.Point, pole.Kind);
        Assert.Equal(-23.1, pole.Points[0].Latitude);
        Assert.Equal(-46.1, pole.Points[0].Longitude);
        Assert.Equal("verde", MapColors.NameOf(pole.Color));

        Assert.Equal([FeatureKind.Point, FeatureKind.Area], map.Features.Skip(2).Select(f => f.Kind));
    }

    [Fact]
    public void Reads_the_first_kml_inside_a_kmz()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("files/foto.jpg");
            using var writer = new StreamWriter(zip.CreateEntry("mapa.kml").Open());
            writer.Write(Busy);
        }

        var map = KmlReader.Read(buffer.ToArray(), "arquivo", "arquivo.kmz");

        Assert.Equal(4, map.Features.Count);
    }

    [Fact]
    public void Refuses_a_file_that_is_not_a_map()
    {
        Assert.Throws<AppException>(() => Read("isto não é xml"));
    }

    [Fact]
    public void Refuses_a_document_type_declaration()
    {
        const string kml = """<?xml version="1.0"?><!DOCTYPE kml [<!ENTITY x "y">]><kml><Document/></kml>""";
        Assert.Throws<AppException>(() => Read(kml));
    }
}

public class MapSimplifierTests
{
    private static GeoPoint P(double latitude, double longitude) => new(latitude, longitude);

    /// <summary>About 1 m of latitude, in degrees.</summary>
    private const double Meter = 1 / 111_320.0;

    [Fact]
    public void Joins_pieces_whose_ends_meet_in_any_direction()
    {
        IReadOnlyList<GeoPoint>[] pieces =
        [
            [P(0, 0), P(10 * Meter, 0)],
            [P(20 * Meter, 0), P(10 * Meter, 0)], // drawn backwards
            [P(20 * Meter, 0), P(30 * Meter, 0)],
            [P(1, 1), P(1.001, 1)], // somewhere else
        ];

        var joined = MapSimplifier.JoinLines(pieces);

        Assert.Equal(2, joined.Count);
        var line = joined.Single(l => l.Count == 4);
        Assert.Equal(0, Math.Min(line[0].Latitude, line[^1].Latitude), 9);
        Assert.Equal(30 * Meter, Math.Max(line[0].Latitude, line[^1].Latitude), 9);
    }

    [Fact]
    public void Keeps_branches_apart()
    {
        // Three cables leave the same box: none of them is the continuation of another.
        IReadOnlyList<GeoPoint>[] pieces =
        [
            [P(0, 0), P(10 * Meter, 0)],
            [P(0, 0), P(-10 * Meter, 0)],
            [P(0, 0), P(0, 10 * Meter)],
        ];

        Assert.Equal(3, MapSimplifier.JoinLines(pieces).Count);
    }

    [Fact]
    public void Straightens_a_line_drawn_with_many_points()
    {
        var wiggly = Enumerable.Range(0, 101)
            .Select(i => P(i * Meter, (i % 2 == 0 ? 0 : 0.5) * Meter)) // zigzag of half a meter
            .ToList();

        var reduced = MapSimplifier.Reduce(wiggly, tolerance: 2);

        Assert.Equal(2, reduced.Count);
        Assert.Equal(wiggly[0], reduced[0]);
        Assert.Equal(wiggly[^1], reduced[^1]);
    }

    [Fact]
    public void Keeps_real_corners()
    {
        var corner = new List<GeoPoint> { P(0, 0), P(100 * Meter, 0), P(100 * Meter, 100 * Meter) };

        Assert.Equal(3, MapSimplifier.Reduce(corner, tolerance: 10).Count);
    }

    [Fact]
    public void Joins_points_that_are_close_and_keeps_the_far_ones()
    {
        var points = new List<GeoPoint>
        {
            P(-23, -46), P(-23 + 10 * Meter, -46), P(-23 + 20 * Meter, -46),
            P(-23.01, -46), // about 1 km away
        };

        var places = MapSimplifier.Cluster(points, distance: 40);

        Assert.Equal(2, places.Count);
        Assert.Equal(3, places[0].Count);
        Assert.Equal(-23 + 10 * Meter, places[0].Point.Latitude, 9);
        Assert.Equal(1, places[1].Count);
    }

    private static MapContent Content(params MapFeature[] features) => new("Mapa", features, 0);

    [Fact]
    public void Groups_by_folder_and_color_with_plain_names()
    {
        var red = Color.FromArgb(255, 200, 0, 0);
        var blue = Color.FromArgb(255, 0, 0, 200);
        var content = Content(
            new MapFeature("CB-1", "Rede / CABOS_AS", FeatureKind.Line, [P(0, 0), P(0.001, 0)], red),
            new MapFeature("CB-2", "Rede / CABOS_AS", FeatureKind.Line, [P(0, 0.01), P(0.001, 0.01)], blue),
            new MapFeature("CB-3", "Rede / CABOS_AS", FeatureKind.Line, [P(0, 0.02), P(0.001, 0.02)], red),
            new MapFeature("X", "", FeatureKind.Point, [P(0, 0)], blue));

        var grouping = MapGrouping.Of(content);

        Assert.Equal(["Cabos as (vermelho)", "Cabos as (azul)", "Pontos azuis"], grouping.Groups.Select(g => g.SuggestedName));
        Assert.Equal([0, 1, 0, 2], grouping.GroupOf);
        Assert.Equal(2, grouping.Groups[0].Count);
    }

    [Fact]
    public void Groups_with_the_same_name_become_one_layer_and_hidden_ones_go_away()
    {
        var content = Content(
            new MapFeature("a", "A", FeatureKind.Point, [P(0, 0)], Color.Red),
            new MapFeature("b", "B", FeatureKind.Point, [P(1, 1)], Color.Blue),
            new MapFeature("c", "C", FeatureKind.Point, [P(2, 2)], Color.Green));
        var grouping = MapGrouping.Of(content);

        var map = MapSimplifier.Simplify(content, grouping,
            [new GroupChoice("Postes", true), new GroupChoice("postes", true), new GroupChoice("C", false)], SimplifyLevel.Medium);

        var layer = Assert.Single(map.Layers);
        Assert.Equal("Postes", layer.Name);
        Assert.Equal(2, layer.Points.Count);
        // It keeps the color of its first group, in the strong version.
        Assert.Equal("vermelho", MapColors.NameOf(layer.Color));
    }

    [Fact]
    public void Cables_only_keeps_every_line_in_one_color_and_drops_the_points()
    {
        var content = Content(
            new MapFeature("FO-12 001", "Rota 01 / Cabos", FeatureKind.Line, [P(0, 0), P(10 * Meter, 0)], Color.Red),
            new MapFeature("FO-12 002", "Rota 02 / Cabos", FeatureKind.Line, [P(10 * Meter, 0), P(20 * Meter, 0)], Color.Blue),
            new MapFeature("Outro", "LAV", FeatureKind.Line, [P(1, 1), P(1.001, 1)], Color.Yellow),
            new MapFeature("CTO 1", "Rota 02 / CTO", FeatureKind.Point, [P(0, 0)], Color.Green),
            new MapFeature("Área", "", FeatureKind.Area, [P(0, 0), P(0, 0.001), P(0.001, 0)], Color.Green));
        var grouping = MapGrouping.Of(content);
        var green = Color.FromArgb(0x43, 0xA0, 0x47);
        var show = grouping.Groups.Select(g => g.Folder != "LAV").ToList();

        var map = MapSimplifier.Cables(content, grouping, show, SimplifyLevel.Little, green, 10);

        var layer = Assert.Single(map.Layers);
        Assert.Equal("Cabos", layer.Name);
        Assert.Equal(green, layer.Color);
        Assert.Empty(layer.Points);
        Assert.Empty(layer.Areas);
        // The two pieces of different colors meet: one cable; the unchecked one is left out.
        Assert.Single(layer.Lines);
        Assert.Equal(10, map.LineWidth);

        var document = SimpleKmzWriter.Document(map, legend: true);
        XNamespace kml = "http://www.opengis.net/kml/2.2";
        Assert.Empty(document.Descendants(kml + "Point"));

        // Each cable is drawn twice: first a wider dark edge, then the color on top of it.
        Assert.True(map.Outline);
        var styles = document.Descendants(kml + "Style").ToDictionary(s => "#" + s.Attribute("id")!.Value);
        var urls = document.Descendants(kml + "Placemark").Select(p => p.Element(kml + "styleUrl")!.Value).ToList();
        Assert.Equal(2, urls.Count);
        Assert.Equal("15", styles[urls[0]].Descendants(kml + "width").Single().Value);
        Assert.Equal("10", styles[urls[1]].Descendants(kml + "width").Single().Value);
        Assert.Equal(SimpleKmzWriter.KmlColor(green, 0xFF), styles[urls[1]].Descendants(kml + "color").First().Value);
    }

    [Fact]
    public void Each_layer_gets_its_own_color_even_when_they_had_the_same()
    {
        var colors = MapColors.Assign(["vermelho", "vermelho", "", "azul"]);

        Assert.Equal(4, colors.Distinct().Count());
        Assert.Equal("vermelho", MapColors.NameOf(colors[0]));
        Assert.Equal("azul", MapColors.NameOf(colors[3]));
    }

    [Theory]
    [InlineData("vermelho", true, "vermelhas")]
    [InlineData("vermelho", false, "vermelhos")]
    [InlineData("azul", true, "azuis")]
    [InlineData("laranja", false, "laranja")]
    [InlineData("marrom", true, "marrons")]
    public void Colors_agree_with_the_noun(string color, bool feminine, string expected)
    {
        Assert.Equal(expected, MapColors.Plural(color, feminine));
    }

    [Theory]
    [InlineData("CABOS_AS", "Cabos as")]
    [InlineData("Postes novos", "Postes novos")]
    [InlineData("rede.kmz", "rede")]
    [InlineData("  CTO  ", "Cto")]
    public void Tidies_folder_names(string folder, string expected)
    {
        Assert.Equal(expected, MapGrouping.Tidy(folder));
    }
}

public class SimpleKmzWriterTests
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";

    [Fact]
    public void Writes_a_map_without_any_technical_content()
    {
        var map = new SimpleMap("Simples",
        [
            new SimpleLayer("Postes", Color.Red, [new SimplePlace(new GeoPoint(-23, -46), 1), new SimplePlace(new GeoPoint(-23.1, -46), 4)], [], []),
            new SimpleLayer("Cabos", Color.Blue, [], [[new GeoPoint(-23, -46), new GeoPoint(-23.01, -46.01)]], []),
        ]);

        using var buffer = new MemoryStream();
        SimpleKmzWriter.Write(buffer, map, legend: true);
        buffer.Position = 0;
        using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("files/legenda.png"));
        Assert.NotNull(zip.GetEntry("files/ponto1.png"));
        Assert.Null(zip.GetEntry("files/ponto2.png")); // the cables have no points

        XDocument document;
        using (var kml = zip.GetEntry("doc.kml")!.Open())
            document = XDocument.Load(kml);
        Assert.Empty(document.Descendants(Kml + "description"));
        Assert.Empty(document.Descendants(Kml + "ExtendedData"));
        Assert.Equal(["Postes", "Cabos"], document.Descendants(Kml + "Folder").Select(f => (string)f.Element(Kml + "name")!));
        Assert.Contains(document.Descendants(Kml + "Placemark"), p => (string)p.Element(Kml + "name")! == "Postes (4)");
        Assert.Single(document.Descendants(Kml + "ScreenOverlay"));

        // Read back by the reader, the simple map is what was written.
        buffer.Position = 0;
        var again = KmlReader.Read(buffer.ToArray(), "x", "x.kmz");
        Assert.Equal(3, again.Features.Count);
        Assert.Equal(1, again.Skipped); // the legend
    }

    [Fact]
    public void Writes_colors_as_kml_wants_them()
    {
        Assert.Equal("ff0000ff", SimpleKmzWriter.KmlColor(Color.FromArgb(255, 0, 0), 0xFF));
    }
}

public class UpdaterTests
{
    [Fact]
    public void Knows_its_own_version()
    {
        Assert.True(Updater.Current > new Version(0, 0, 0));
    }

    [Fact]
    public async Task Asks_github_for_the_latest_version()
    {
        // Reaches GitHub for real: without releases it answers "not found", which means no update.
        try
        {
            var update = await Updater.CheckAsync();
            Assert.True(update is null || update.Version > Updater.Current);
        }
        catch (AppException e) when (e.InnerException is HttpRequestException or TaskCanceledException)
        {
            // Offline: nothing to check here.
        }
    }

    [Theory]
    [InlineData("v2.3.0", "2.3.0")]
    [InlineData("2.3", "2.3.0")]
    [InlineData("V10.0.1-beta", "10.0.1")]
    [InlineData("v2.3.0+abc123", "2.3.0")]
    public void Reads_the_version_of_a_tag(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), Updater.ParseVersion(tag));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    public void Ignores_tags_that_are_not_versions(string tag)
    {
        Assert.Null(Updater.ParseVersion(tag));
    }

    [Theory]
    [InlineData("2.2.0", "2.1.0", true)]
    [InlineData("2.1.1", "2.1.0", true)]
    [InlineData("2.1.0", "2.1.0", false)]
    [InlineData("2.1", "2.1.0.5", false)]
    [InlineData("2.0.9", "2.1.0", false)]
    public void Only_a_higher_version_is_newer(string candidate, string current, bool newer)
    {
        Assert.Equal(newer, Updater.IsNewer(Version.Parse(candidate), Version.Parse(current)));
    }
}
