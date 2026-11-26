using System.IO.Compression;
using System.Text;
using ListaMateriais.Cad;

namespace ListaMateriais.Tests;

public class UtmTests
{
    // Reference values computed with PROJ (EPSG:31983, 31982 and 32633).
    [Theory]
    [InlineData(23, true, 333000, 7394000, -23.55578235075691, -46.63618561977696)]
    [InlineData(23, true, 500000, 7400000, -23.51019471580234, -45.0)]
    [InlineData(22, true, 400000, 6700000, -29.8264127672116, -52.03500625483298)]
    [InlineData(33, false, 500000, 4649776.224819178, 42.0, 15.0)]
    [InlineData(23, true, 720000, 9000000, -9.041099311003114, -42.9985980805708)]
    public void MatchesProj(int zone, bool south, double e, double n, double lat, double lon)
    {
        var (la, lo) = Utm.ToLatLon(zone, south, e, n);
        Assert.InRange(la, lat - 1e-7, lat + 1e-7);   // about a centimeter
        Assert.InRange(lo, lon - 1e-7, lon + 1e-7);
    }
}

public class DxfReaderTests
{
    private static byte[] Dxf(params (int Code, string Value)[] groups)
    {
        var text = new StringBuilder();
        foreach (var (code, value) in groups)
            text.Append(code).Append('\n').Append(value).Append('\n');
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static (int, string)[] Entities(params (int, string)[][] entities) =>
    [
        (0, "SECTION"), (2, "ENTITIES"), .. entities.SelectMany(e => e), (0, "ENDSEC"), (0, "EOF"),
    ];

    [Fact]
    public void ReadsLinesPolylinesCirclesTextsAndInserts()
    {
        var drawing = DxfReader.Read(Dxf(Entities(
            [(0, "LINE"), (8, "REDE"), (62, "1"), (10, "0"), (20, "0"), (11, "10"), (21, "5")],
            [(0, "LWPOLYLINE"), (8, "REDE"), (70, "1"), (10, "0"), (20, "0"), (10, "10"), (20, "0"), (42, "1"), (10, "10"), (20, "10")],
            [(0, "CIRCLE"), (8, "POSTES"), (10, "5"), (20, "5"), (40, "2")],
            [(0, "TEXT"), (8, "TEXTOS"), (10, "1"), (20, "2"), (1, "Poste 1")],
            [(0, "INSERT"), (8, "POSTES"), (2, "CTO"), (10, "3"), (20, "4"), (50, "90")])));

        Assert.Equal(5, drawing.Model.Count);
        var line = drawing.Model[0];
        Assert.Equal("REDE", line.Layer);
        Assert.Equal(1, line.ColorIndex);
        Assert.Equal(new CadPoint(10, 5), line.Points[1]);
        Assert.True(drawing.Model[1].Closed);
        Assert.True(drawing.Model[1].Points.Count > 4);                    // the bulge became an arc
        Assert.True(drawing.Model[2].Closed);
        Assert.All(drawing.Model[2].Points, p => Assert.Equal(2, Math.Sqrt(Math.Pow(p.X - 5, 2) + Math.Pow(p.Y - 5, 2)), 6));
        Assert.Equal("Poste 1", drawing.Model[3].Text);
        Assert.Equal("CTO", drawing.Model[4].Block);
        Assert.Equal(Math.PI / 2, drawing.Model[4].Rotation, 9);
    }

    [Fact]
    public void RejectsOtherFiles()
    {
        Assert.Throws<AppException>(() => DxfReader.Read(Encoding.UTF8.GetBytes("isto não é um desenho\nnem de longe\n")));
    }
}

public class CadMapTests
{
    private static CadDrawing Sample()
    {
        var drawing = new CadDrawing();
        drawing.Layers["REDE"] = new CadLayer("REDE", 1, null, false);
        var line = new CadEntity { Kind = CadKind.Path, Layer = "REDE" };
        line.Points.Add(new CadPoint(333000, 7394000));
        line.Points.Add(new CadPoint(333100, 7394000));
        drawing.Model.Add(line);
        var label = new CadEntity { Kind = CadKind.Text, Layer = "OUTRA", Text = "Praça", ColorIndex = 3 };
        label.Points.Add(new CadPoint(333050, 7394010));
        drawing.Model.Add(label);
        var insert = new CadEntity { Kind = CadKind.Insert, Layer = "REDE", Block = "POSTE" };
        insert.Points.Add(new CadPoint(333000, 7394000));
        drawing.Model.Add(insert);
        var symbol = new CadEntity { Kind = CadKind.Path, Layer = "0", ColorIndex = 0 };
        symbol.Points.Add(new CadPoint(0, 0));
        symbol.Points.Add(new CadPoint(2, 0));
        drawing.Blocks["POSTE"] = [symbol];
        return drawing;
    }

    private static readonly CadGeoSettings Utm23S = new(false, 23, true, 1.0, false);

    [Fact]
    public void PlacesTheDrawingOnTheMapAndKeepsColorsAndLayers()
    {
        var map = CadMapBuilder.Build(Sample(), Utm23S, new HashSet<string> { "REDE", "OUTRA" });
        var line = map.Features.Single(f => f.Kind == GeoKind.Line);
        Assert.Equal(0xFF0000, line.Rgb);                                  // layer REDE is color 1 (red)
        Assert.InRange(line.Points[0].Latitude, -23.5558, -23.5557);
        Assert.InRange(line.Points[0].Longitude, -46.6362, -46.6361);
        Assert.Equal("Praça", map.Features.Single(f => f.Kind == GeoKind.Label).Name);
        Assert.Equal("POSTE", map.Features.Single(f => f.Kind == GeoKind.Point).Name);
        Assert.InRange(map.ExtentKm, 0.05, 0.5);
    }

    [Fact]
    public void LeavesOutUncheckedLayers()
    {
        var map = CadMapBuilder.Build(Sample(), Utm23S, new HashSet<string> { "OUTRA" });
        Assert.Single(map.Features);
    }

    [Fact]
    public void DrawsBlockContentsWhenAsked()
    {
        var map = CadMapBuilder.Build(Sample(), Utm23S with { DrawBlocks = true }, new HashSet<string> { "REDE" });
        // The block's line comes in by the insert, in the insert's layer and (by block) color.
        Assert.Equal(2, map.Features.Count(f => f.Kind == GeoKind.Line));
    }

    [Fact]
    public void PlacesABlockByItsBasePointScaleAndRotation()
    {
        var drawing = new CadDrawing();
        var insert = new CadEntity { Kind = CadKind.Insert, Layer = "L", Block = "B", ScaleX = 2, ScaleY = 2, Rotation = Math.PI / 2 };
        insert.Points.Add(new CadPoint(333000, 7394000));
        drawing.Model.Add(insert);
        // A block drawn around (100, 100) whose base point is (100, 100): that point lands on the insertion point.
        var line = new CadEntity { Kind = CadKind.Path, Layer = "L" };
        line.Points.Add(new CadPoint(100, 100));
        line.Points.Add(new CadPoint(110, 100));
        drawing.Blocks["B"] = [line];
        drawing.BlockBases["B"] = new CadPoint(100, 100);

        var map = CadMapBuilder.Build(drawing, Utm23S with { DrawBlocks = true }, new HashSet<string> { "L" });

        var drawn = map.Features.Single(f => f.Kind == GeoKind.Line);
        var (lat0, lon0) = Utm.ToLatLon(23, true, 333000, 7394000);
        var (lat1, lon1) = Utm.ToLatLon(23, true, 333000, 7394020);      // 10 units × scale 2, turned 90° = 20 m north
        Assert.Equal(lon0, drawn.Points[0].Longitude, 7);
        Assert.Equal(lat0, drawn.Points[0].Latitude, 7);
        Assert.Equal(lon1, drawn.Points[1].Longitude, 7);
        Assert.Equal(lat1, drawn.Points[1].Latitude, 7);
    }

    [Fact]
    public void WritesAKmzWithOneFolderPerLayer()
    {
        var map = CadMapBuilder.Build(Sample(), Utm23S, new HashSet<string> { "REDE", "OUTRA" });
        using var stream = new MemoryStream();
        CadKmzWriter.Write(stream, "Teste & cia", map, [1, 2, 3]);
        stream.Position = 0;
        using var zip = new ZipArchive(stream);
        Assert.Equal("doc.kml", zip.Entries[0].FullName);
        using var kml = new StreamReader(zip.GetEntry("doc.kml")!.Open());
        var text = kml.ReadToEnd();
        Assert.Contains("<name>REDE</name>", text);
        Assert.Contains("<name>OUTRA</name>", text);
        Assert.Contains("<name>Teste &amp; cia</name>", text);
        Assert.Contains("<color>ff0000ff</color>", text);                  // red as aabbggrr
        Assert.NotNull(zip.GetEntry("files/ponto.png"));
    }

    [Fact]
    public void RejectsDrawingsOutsideTheWorld()
    {
        var map = CadMapBuilder.Build(Sample(), new CadGeoSettings(true, 23, true, 1.0, false), new HashSet<string> { "REDE" });
        Assert.Empty(map.Features);                                        // X = 333000 is not a longitude
    }
}

/// <summary>
/// Reads real DWG files of each AutoCAD version. The files are not kept in the repository (they belong to the
/// LibreDWG project, under another license): set DWG_SAMPLES to a folder with example_2000.dwg ... example_2018.dwg
/// to run these; without it they pass without checking anything.
/// </summary>
public class DwgReaderTests
{
    private static string? Samples => Environment.GetEnvironmentVariable("DWG_SAMPLES") is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;

    [Theory]
    [InlineData("2000")]
    [InlineData("2004")]
    [InlineData("2010")]
    [InlineData("2013")]
    [InlineData("2018")]
    public void ReadsEveryVersionTheSame(string version)
    {
        if (Samples is not { } dir)
            return;
        var drawing = DwgReader.Read(Path.Combine(dir, $"example_{version}.dwg"));
        Assert.Equal(25, drawing.Model.Count(e => e.Kind == CadKind.Path));
        Assert.Equal(1, drawing.Model.Count(e => e.Kind == CadKind.Point));
        Assert.Equal(3, drawing.Model.Count(e => e.Kind == CadKind.Text));
        Assert.Equal(10, drawing.Model.Count(e => e.Kind == CadKind.Insert));
        Assert.Equal(0, drawing.Unreadable);
        Assert.Contains(drawing.Layers.Keys, name => name == "Tavolo 3");
        Assert.Contains(drawing.Model, e => e.Text.StartsWith("Teksto granda", StringComparison.Ordinal));
        Assert.Equal(13, drawing.Blocks["CIRKLO_PUNKTOJ"].Count);
        // One of the lines of the drawing, to the centimeter.
        Assert.Contains(drawing.Model, e => e.Kind == CadKind.Path && e.Points.Count == 2
            && Math.Abs(e.Points[0].X - 370.86) < 0.01 && Math.Abs(e.Points[0].Y - 730.63) < 0.01);
    }

    [Fact]
    public void AGarbledFileFailsWithAMessageNotACrash()
    {
        if (Samples is not { } dir)
            return;
        var original = File.ReadAllBytes(Path.Combine(dir, "example_2018.dwg"));
        var random = new Random(7);
        for (var round = 0; round < 300; round++)
        {
            var bytes = (byte[])original.Clone();
            for (var i = 0; i < 20; i++)
                bytes[random.Next(0x80, bytes.Length)] ^= (byte)random.Next(1, 256);
            try
            {
                DwgReader.Read(bytes);
            }
            catch (AppException)
            {
                // The expected way to refuse a damaged file.
            }
        }
    }

    [Fact]
    public void RefusesOtherFilesAndOldVersions()
    {
        Assert.Throws<AppException>(() => DwgReader.Read(new byte[300]));
        var old = new byte[400];
        Encoding.ASCII.GetBytes("AC1009").CopyTo(old, 0);
        Assert.Contains("antiga", Assert.Throws<AppException>(() => DwgReader.Read(old)).Message, StringComparison.Ordinal);
        var truncated = new byte[400];
        Encoding.ASCII.GetBytes("AC1032").CopyTo(truncated, 0);
        Assert.Throws<AppException>(() => DwgReader.Read(truncated));
    }
}
