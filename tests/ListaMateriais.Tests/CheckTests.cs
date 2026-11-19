using System.IO.Compression;
using System.Xml.Linq;
using ListaMateriais.Kmz;

namespace ListaMateriais.Tests;

public class MapCheckerTests
{
    private const double Meter = 1 / 111_320.0;
    private const double Lavras = -21.24;
    private const double West = -45.0;

    /// <summary>A spot so many meters north and east of a point in Lavras.</summary>
    private static GeoPoint At(double north, double east = 0) =>
        new(Lavras + north * Meter, West + east * Meter / Math.Cos(Lavras * Math.PI / 180));

    private static MapFeature Cable(string name, string folder, params GeoPoint[] points) => new(name, folder, FeatureKind.Line, points, null);

    private static MapFeature Box(string name, string folder, GeoPoint where) => new(name, folder, FeatureKind.Point, [where], null);

    private static List<MapIssue> Check(CheckOptions? options = null, Func<MapFeature, bool>? report = null, params MapFeature[] features) =>
        MapChecker.Check(new MapContent("Projeto", features, 0), options ?? new CheckOptions(), report);

    [Fact]
    public void A_clean_network_has_nothing_to_report()
    {
        var issues = Check(null, null,
            Box("CEO-01", "Nova / CEO", At(0)),
            Cable("FO-06 A", "Nova / Cabos", At(0), At(60), At(120)),
            Box("CTO-01", "Nova / CTO", At(120)),
            // Starts on the middle of the first cable: a branch is connected.
            Cable("FO-06 B", "Nova / Cabos", At(60, 3), At(60, 90)),
            Box("CTO-02", "Nova / CTO", At(60, 90)));

        Assert.Empty(issues);
    }

    [Fact]
    public void Finds_loose_ends_and_points_no_cable_reaches()
    {
        var issues = Check(null, null,
            Box("CEO-01", "CEO", At(0)),
            Cable("FO-06", "Cabos", At(0), At(100)),
            Box("CTO perdida", "CTO", At(300)));

        var loose = Assert.Single(issues, i => i.Problem == MapChecker.LooseEnd);
        Assert.Equal("FO-06", loose.Item);
        Assert.Equal(At(100), loose.Where);
        Assert.Contains("fim", loose.Detail, StringComparison.Ordinal);
        Assert.Equal("CTO perdida", Assert.Single(issues, i => i.Problem == MapChecker.NoCable).Item);
    }

    [Fact]
    public void Folders_left_out_still_connect_but_are_not_reported()
    {
        var existing = Cable("BACKBONE", "LAV", At(0, -500), At(0, 500));
        var issues = Check(null, f => f.Folder.StartsWith("Nova", StringComparison.Ordinal),
            existing,
            Cable("FO-06", "Nova / Cabos", At(0), At(80)),
            Box("CTO-01", "Nova / CTO", At(80)));

        // The backbone's own ends are loose, but it was not asked about; the new cable reaches it.
        Assert.Empty(issues);
    }

    [Fact]
    public void Unnamed_drawings_are_reported_with_their_length()
    {
        var issues = Check(null, null,
            Box("CEO-01", "CEO", At(0)),
            Cable("Caminho sem título", "Cabos", At(0), At(150)),
            Box("Untitled Placemark", "CTO", At(150)));

        var unnamed = issues.Where(i => i.Problem == MapChecker.Unnamed).ToList();
        Assert.Equal(2, unnamed.Count);
        Assert.Contains(unnamed, i => i.Detail.Contains("150 m", StringComparison.Ordinal));
        Assert.True(MapChecker.IsUnnamed("  CAMINHO SEM TÍTULO "));
        Assert.False(MapChecker.IsUnnamed("Caminho 01"));
    }

    [Fact]
    public void A_cable_drawn_twice_is_an_error_and_two_named_cables_on_one_trace_a_warning()
    {
        var issues = Check(null, null,
            Box("A", "", At(0)), Box("B", "", At(200)),
            Cable("FO-12", "Rota 01", At(0), At(100), At(200)),
            // The copy runs the other way and has one vertex less; it is still the same trace.
            Cable("FO-12", "Rota 02", At(200), At(0)),
            Cable("FO-24", "Rota 02", At(0.5, 1), At(199.5, 1)));

        var twice = Assert.Single(issues, i => i.Problem == MapChecker.DrawnTwice);
        Assert.Equal(IssueLevel.Error, twice.Level);
        Assert.Equal(2, issues.Count(i => i.Problem == MapChecker.SameTrace));
        Assert.Equal(IssueLevel.Error, issues[0].Level);
    }

    [Fact]
    public void The_same_box_twice_is_an_error_but_two_boxes_on_one_pole_are_fine()
    {
        var issues = Check(null, null,
            Cable("FO-06", "Cabos", At(0), At(100)),
            Box("CEO-01", "Rota 01", At(0)),
            Box("CEO-01", "Rota 02", At(0.5)),
            Box("CTO-01", "Rota 01", At(100)),
            Box("CEO-07", "Rota 01", At(100)));

        var twice = Assert.Single(issues, i => i.Problem == MapChecker.DrawnTwice);
        Assert.Equal("CEO-01", twice.Item);
        // The repeated name is the copy itself; it is not counted again.
        Assert.DoesNotContain(issues, i => i.Problem == MapChecker.RepeatedName);
    }

    [Fact]
    public void Pieces_of_one_cable_may_share_its_name()
    {
        var issues = Check(null, null,
            Box("A", "", At(0)), Box("B", "", At(200)),
            Cable("FO-12", "Cabos", At(0), At(100)),
            Cable("FO-12", "Cabos", At(100), At(200)),
            Box("Poste", "", At(0, 300)), Box("Poste", "", At(0, 600)),
            Cable("Ramal", "", At(0, 300), At(0, 600)));

        var repeated = Assert.Single(issues, i => i.Problem == MapChecker.RepeatedName);
        Assert.Equal("Poste", repeated.Item);
    }

    [Fact]
    public void Finds_leftover_bits_long_spans_and_wrong_coordinates()
    {
        var issues = Check(new CheckOptions(MaxSpan: 80), null,
            Box("A", "", At(0)), Box("B", "", At(250)),
            Cable("FO-06", "", At(0), At(50), At(250)),
            Cable("Sobra", "", At(250), At(251)),
            Box("Zerado", "", new GeoPoint(0, 0)),
            Box("Trocado", "", new GeoPoint(West, Lavras)));

        var span = Assert.Single(issues, i => i.Problem == MapChecker.LongSpan);
        Assert.Contains("200 m", span.Detail, StringComparison.Ordinal);
        Assert.Equal("Sobra", Assert.Single(issues, i => i.Problem == MapChecker.TooShort).Item);
        Assert.Equal("Zerado", Assert.Single(issues, i => i.Problem == MapChecker.BadCoordinate).Item);
        var swapped = Assert.Single(issues, i => i.Problem == MapChecker.OutsideBrazil);
        Assert.Contains("trocadas", swapped.Detail, StringComparison.Ordinal);
        // A point with no coordinate is not also reported as far from the cables.
        Assert.DoesNotContain(issues, i => i.Problem == MapChecker.NoCable && i.Item == "Zerado");
    }

    [Fact]
    public void Long_spans_are_not_checked_unless_asked()
    {
        var issues = Check(null, null, Box("A", "", At(0)), Box("B", "", At(500)), Cable("FO", "", At(0), At(500)));
        Assert.Empty(issues);
    }
}

public class IssueKmzWriterTests
{
    [Fact]
    public void Writes_one_marker_per_issue_in_a_folder_per_problem()
    {
        MapIssue[] issues =
        [
            new(IssueLevel.Error, MapChecker.DrawnTwice, "FO-12", "Rota 01", new GeoPoint(-21.24, -45.0), "Copia & cola"),
            new(IssueLevel.Warning, MapChecker.LooseEnd, "FO-06", "Rota 02", new GeoPoint(-21.25, -45.01), "O fim do cabo..."),
            new(IssueLevel.Warning, MapChecker.LooseEnd, "FO-07", "Rota 02", new GeoPoint(-21.26, -45.02), "O início do cabo..."),
        ];
        using var stream = new MemoryStream();
        IssueKmzWriter.Write(stream, "Projeto - conferência", issues);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("files/erro.png"));
        Assert.NotNull(zip.GetEntry("files/aviso.png"));
        using var kml = zip.GetEntry("doc.kml")!.Open();
        var document = XDocument.Load(kml);
        XNamespace ns = "http://www.opengis.net/kml/2.2";
        var folders = document.Descendants(ns + "Folder").Select(f => f.Element(ns + "name")!.Value).ToList();
        Assert.Equal(["Erro: Desenhado duas vezes (1)", "Aviso: Ponta solta (2)"], folders);
        var first = document.Descendants(ns + "Placemark").First();
        Assert.Equal("#erro", first.Element(ns + "styleUrl")!.Value);
        Assert.Equal("-45,-21.24,0", first.Descendants(ns + "coordinates").Single().Value);
        Assert.Contains("Copia & cola", first.Element(ns + "description")!.Value, StringComparison.Ordinal);
    }
}
