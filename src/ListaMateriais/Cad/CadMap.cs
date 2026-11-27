namespace ListaMateriais.Cad;

/// <summary>How the coordinates of the drawing are to be read.</summary>
/// <param name="Degrees">The drawing is already in longitude and latitude (X = longitude, Y = latitude).</param>
/// <param name="Zone">UTM zone, when the drawing is in UTM.</param>
/// <param name="South">UTM southern hemisphere.</param>
/// <param name="UnitScale">Meters per drawing unit (1 for a drawing in meters, 0.001 for millimeters).</param>
/// <param name="DrawBlocks">Also draws what is inside the block references, not only a marker at each.</param>
/// <param name="Sad69">The drawing is in the old SAD 69 datum, and is moved to SIRGAS 2000.</param>
internal sealed record CadGeoSettings(bool Degrees, int Zone, bool South, double UnitScale, bool DrawBlocks, bool Sad69 = false);

internal enum GeoKind { Line, Point, Label }

/// <summary>One thing of the map, in degrees.</summary>
/// <param name="Rgb">0xRRGGBB.</param>
internal sealed record GeoFeature(GeoKind Kind, string Layer, int Rgb, string Name, IReadOnlyList<(double Longitude, double Latitude)> Points)
{
    /// <summary>The data of a block reference, shown when its marker is clicked.</summary>
    public IReadOnlyList<(string Tag, string Value)> Data { get; init; } = [];
}

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

    /// <summary>Meters from its insertion point beyond which a block's drawing is taken to be somewhere else.</summary>
    private const double FarFromInsertion = 1000;

    private readonly record struct Transform(double Tx, double Ty, double Sx, double Sy, double Cos, double Sin, double Bx, double By);

    public static CadMap Build(CadDrawing drawing, CadGeoSettings settings, ISet<string> layers)
    {
        var map = new CadMap();
        var centers = new Dictionary<string, CadPoint?>(StringComparer.OrdinalIgnoreCase);
        var transforms = new List<Transform>();
        foreach (var entity in drawing.Model)
        {
            if (layers.Contains(entity.Layer))
                Emit(drawing, settings, map, centers, entity, transforms, entity.Layer, null, 0);
            if (map.Features.Count > MaxFeatures)
                throw new AppException("O desenho tem elementos demais para um mapa (mais de um milhão). Desmarque camadas que não precisa.");
        }
        return map;
    }

    private static void Emit(CadDrawing drawing, CadGeoSettings settings, CadMap map, Dictionary<string, CadPoint?> centers,
        CadEntity entity, List<Transform> transforms, string layer, int? inheritedColor, int depth)
    {
        var color = ColorOf(drawing, entity, layer, inheritedColor);
        switch (entity.Kind)
        {
            case CadKind.Insert:
                if (depth == 0)
                {
                    var marker = MarkerOf(drawing, settings, entity, centers);
                    Add(map, settings, new GeoFeature(GeoKind.Point, layer, color, NameOf(entity), [(marker.X, marker.Y)])
                    {
                        Data = entity.Attributes.Count == 0 ? [] : [("BLOCO", entity.Block), .. entity.Attributes],
                    });
                }
                if (settings.DrawBlocks && depth < MaxBlockDepth && drawing.Blocks.TryGetValue(entity.Block, out var content))
                {
                    var inner = new List<Transform>(transforms) { TransformOf(drawing, entity) };
                    foreach (var child in content)
                        Emit(drawing, settings, map, centers, child, inner, child.Layer == "0" ? layer : child.Layer, color, depth + 1);
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

    private static Transform TransformOf(CadDrawing drawing, CadEntity insert)
    {
        drawing.BlockBases.TryGetValue(insert.Block, out var blockBase);
        return new Transform(insert.Points[0].X, insert.Points[0].Y, insert.ScaleX, insert.ScaleY,
            Math.Cos(insert.Rotation), Math.Sin(insert.Rotation), blockBase.X, blockBase.Y);
    }

    /// <summary>
    /// Where a block reference of the model shows on the drawing. That is its insertion point, except for blocks
    /// made from a drawing at its own coordinates: their content lands far from the insertion point (which may
    /// then be anywhere, even outside the world), so the middle of the content is used.
    /// </summary>
    /// <param name="centers">The middle of each block already looked at.</param>
    public static CadPoint MarkerOf(CadDrawing drawing, CadGeoSettings settings, CadEntity insert,
        Dictionary<string, CadPoint?> centers)
    {
        if (!centers.TryGetValue(insert.Block, out var center))
            centers[insert.Block] = center = CenterOf(drawing, insert.Block);
        if (center is not { } middle)
            return insert.Points[0];
        var placed = Apply(middle, [TransformOf(drawing, insert)]);
        // A degree is about 111 km.
        var far = settings.Degrees ? FarFromInsertion / 111_000 : FarFromInsertion;
        var distance = double.Hypot(placed.X - insert.Points[0].X, placed.Y - insert.Points[0].Y) * settings.UnitScale;
        return distance > far ? placed : insert.Points[0];
    }

    private static CadPoint? CenterOf(CadDrawing drawing, string block)
    {
        if (!drawing.Blocks.TryGetValue(block, out var content))
            return null;
        var points = content.SelectMany(e => e.Points).ToList();
        return points.Count == 0
            ? null
            : new CadPoint((points.Min(p => p.X) + points.Max(p => p.X)) / 2, (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2);
    }

    /// <summary>The name of a block reference's marker: the block and its first value ("EQCXT1 · NUMERO 30").</summary>
    private static string NameOf(CadEntity insert) =>
        insert.Attributes is [var (tag, value), ..] ? $"{insert.Block} · {tag} {value}" : insert.Block;

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
            (lat, lon) = ToLatLon(settings, x, y);
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

    /// <summary>A point of the drawing in degrees of latitude and longitude.</summary>
    public static (double Latitude, double Longitude) ToLatLon(CadGeoSettings settings, double x, double y)
    {
        if (!settings.Degrees)
            return Utm.ToLatLon(settings.Zone, settings.South, x * settings.UnitScale, y * settings.UnitScale, settings.Sad69);
        var (lat, lon) = (y * settings.UnitScale, x * settings.UnitScale);
        return settings.Sad69 ? Utm.Sad69ToSirgas(lat, lon) : (lat, lon);
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
