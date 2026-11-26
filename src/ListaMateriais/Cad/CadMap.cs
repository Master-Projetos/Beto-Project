namespace ListaMateriais.Cad;

/// <summary>How the coordinates of the drawing are to be read.</summary>
/// <param name="Degrees">The drawing is already in longitude and latitude (X = longitude, Y = latitude).</param>
/// <param name="Zone">UTM zone, when the drawing is in UTM.</param>
/// <param name="South">UTM southern hemisphere.</param>
/// <param name="UnitScale">Meters per drawing unit (1 for a drawing in meters, 0.001 for millimeters).</param>
/// <param name="DrawBlocks">Also draws what is inside the block references, not only a marker at each.</param>
internal sealed record CadGeoSettings(bool Degrees, int Zone, bool South, double UnitScale, bool DrawBlocks);

internal enum GeoKind { Line, Point, Label }

/// <summary>One thing of the map, in degrees.</summary>
/// <param name="Rgb">0xRRGGBB.</param>
internal sealed record GeoFeature(GeoKind Kind, string Layer, int Rgb, string Name, IReadOnlyList<(double Longitude, double Latitude)> Points);

/// <summary>The drawing placed on the map: ready to be written as a KML.</summary>
internal sealed class CadMap
{
    public List<GeoFeature> Features { get; } = [];

    public double MinLatitude { get; set; } = double.MaxValue;

    public double MaxLatitude { get; set; } = double.MinValue;

    public double MinLongitude { get; set; } = double.MaxValue;

    public double MaxLongitude { get; set; } = double.MinValue;

    public (double Latitude, double Longitude) Center => ((MinLatitude + MaxLatitude) / 2, (MinLongitude + MaxLongitude) / 2);

    /// <summary>Roughly how far apart the farthest points are, in kilometers.</summary>
    public double ExtentKm
    {
        get
        {
            if (Features.Count == 0)
                return 0;
            var dLat = (MaxLatitude - MinLatitude) * 111.32;
            var dLon = (MaxLongitude - MinLongitude) * 111.32 * Math.Cos(Center.Latitude * Math.PI / 180);
            return Math.Sqrt(dLat * dLat + dLon * dLon);
        }
    }
}

/// <summary>Turns a drawing into a <see cref="CadMap"/>: colors, blocks, layers chosen, and the change of coordinates.</summary>
internal static class CadMapBuilder
{
    private const int MaxFeatures = 1_000_000;
    private const int MaxBlockDepth = 6;

    private readonly record struct Transform(double Tx, double Ty, double Sx, double Sy, double Cos, double Sin, double Bx, double By);

    public static CadMap Build(CadDrawing drawing, CadGeoSettings settings, ISet<string> layers)
    {
        var map = new CadMap();
        var transforms = new List<Transform>();
        foreach (var entity in drawing.Model)
        {
            if (layers.Contains(entity.Layer))
                Emit(drawing, settings, map, entity, transforms, entity.Layer, null, 0);
            if (map.Features.Count > MaxFeatures)
                throw new AppException("O desenho tem elementos demais para um mapa (mais de um milhão). Desmarque camadas que não precisa.");
        }
        return map;
    }

    private static void Emit(CadDrawing drawing, CadGeoSettings settings, CadMap map, CadEntity entity, List<Transform> transforms,
        string layer, int? inheritedColor, int depth)
    {
        var color = ColorOf(drawing, entity, layer, inheritedColor);
        switch (entity.Kind)
        {
            case CadKind.Insert:
                var origin = Apply(entity.Points[0], transforms);
                if (depth == 0)
                    Add(map, settings, new GeoFeature(GeoKind.Point, layer, color, entity.Block, [(origin.X, origin.Y)]));
                if (settings.DrawBlocks && depth < MaxBlockDepth && drawing.Blocks.TryGetValue(entity.Block, out var content))
                {
                    var cos = Math.Cos(entity.Rotation);
                    var sin = Math.Sin(entity.Rotation);
                    drawing.BlockBases.TryGetValue(entity.Block, out var blockBase);
                    var inner = new List<Transform>(transforms)
                    {
                        new(entity.Points[0].X, entity.Points[0].Y, entity.ScaleX, entity.ScaleY, cos, sin, blockBase.X, blockBase.Y),
                    };
                    foreach (var child in content)
                        Emit(drawing, settings, map, child, inner, child.Layer == "0" ? layer : child.Layer, color, depth + 1);
                }
                break;
            case CadKind.Path when entity.Points.Count >= 2:
                var points = entity.Points.Select(p => Apply(p, transforms)).ToList();
                if (entity.Closed && (points[0].X != points[^1].X || points[0].Y != points[^1].Y))
                    points.Add(points[0]);
                Add(map, settings, new GeoFeature(GeoKind.Line, layer, color, string.Empty, [.. points.Select(p => (p.X, p.Y))]));
                break;
            case CadKind.Point when entity.Points.Count == 1 && depth == 0:
                var at = Apply(entity.Points[0], transforms);
                Add(map, settings, new GeoFeature(GeoKind.Point, layer, color, string.Empty, [(at.X, at.Y)]));
                break;
            case CadKind.Text when entity.Points.Count == 1 && entity.Text.Length > 0:
                var position = Apply(entity.Points[0], transforms);
                Add(map, settings, new GeoFeature(GeoKind.Label, layer, color, entity.Text, [(position.X, position.Y)]));
                break;
        }
    }

    /// <summary>Takes a point of a block (or of the model) through the block references that hold it, outermost last.</summary>
    private static CadPoint Apply(CadPoint point, List<Transform> transforms)
    {
        var x = point.X;
        var y = point.Y;
        for (var i = transforms.Count - 1; i >= 0; i--)
        {
            var t = transforms[i];
            var sx = (x - t.Bx) * t.Sx;
            var sy = (y - t.By) * t.Sy;
            x = t.Tx + sx * t.Cos - sy * t.Sin;
            y = t.Ty + sx * t.Sin + sy * t.Cos;
        }
        return new CadPoint(x, y);
    }

    /// <summary>Converts the points to degrees and keeps the feature.</summary>
    private static void Add(CadMap map, CadGeoSettings settings, GeoFeature raw)
    {
        var converted = new List<(double, double)>(raw.Points.Count);
        foreach (var (x, y) in raw.Points)
        {
            double lat, lon;
            if (settings.Degrees)
            {
                lon = x * settings.UnitScale;
                lat = y * settings.UnitScale;
            }
            else
            {
                (lat, lon) = Utm.ToLatLon(settings.Zone, settings.South, x * settings.UnitScale, y * settings.UnitScale);
            }
            if (!double.IsFinite(lat) || !double.IsFinite(lon) || lat is < -90 or > 90 || lon is < -180 or > 180)
                continue;
            converted.Add((lon, lat));
            map.MinLatitude = Math.Min(map.MinLatitude, lat);
            map.MaxLatitude = Math.Max(map.MaxLatitude, lat);
            map.MinLongitude = Math.Min(map.MinLongitude, lon);
            map.MaxLongitude = Math.Max(map.MaxLongitude, lon);
        }
        if (converted.Count == 0 || (raw.Kind == GeoKind.Line && converted.Count < 2))
            return;
        map.Features.Add(raw with { Points = converted });
    }

    private static int ColorOf(CadDrawing drawing, CadEntity entity, string layer, int? inherited)
    {
        if (entity.Rgb is { } rgb)
            return rgb;
        if (entity.ColorIndex == 0 && inherited is { } parent)
            return parent;
        if (entity.ColorIndex is >= 1 and <= 255)
            return AciColors.Rgb(entity.ColorIndex);
        if (drawing.Layers.TryGetValue(layer, out var info))
            return info.Rgb ?? AciColors.Rgb(info.ColorIndex);
        return AciColors.Rgb(7);
    }
}
