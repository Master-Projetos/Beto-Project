namespace ListaMateriais.Kmz;

/// <summary>How much of the detail goes away.</summary>
internal enum SimplifyLevel { Little, Medium, Lots }

/// <summary>
/// The features of the original map that look alike: same kind, same folder, same color. Each group
/// becomes one entry of the legend of the simple map (or joins another one with the same name).
/// </summary>
/// <param name="ColorName">Plain name of the original color ("vermelho"), empty when the map gave none.</param>
internal sealed record MapGroup(FeatureKind Kind, string Folder, string ColorName, Color? Color, int Count, string SuggestedName);

/// <summary>What the user decided for a group: the name it gets on the simple map, and whether it shows at all.</summary>
internal sealed record GroupChoice(string Name, bool Show);

/// <summary>A point of the simple map; <paramref name="Count"/> original points were joined into it.</summary>
internal sealed record SimplePlace(GeoPoint Point, int Count);

/// <summary>One entry of the legend: everything drawn in one color under one name.</summary>
internal sealed record SimpleLayer(string Name, Color Color, IReadOnlyList<SimplePlace> Points,
    IReadOnlyList<IReadOnlyList<GeoPoint>> Lines, IReadOnlyList<IReadOnlyList<GeoPoint>> Areas)
{
    /// <summary>What the legend shows next to the name: the kind the layer has most of.</summary>
    public FeatureKind Main =>
        Lines.Count >= Points.Count && Lines.Count >= Areas.Count ? FeatureKind.Line
        : Areas.Count > Points.Count ? FeatureKind.Area : FeatureKind.Point;
}

/// <param name="LineWidth">Thickness of the lines on the map, in pixels.</param>
/// <param name="Outline">Draws a dark edge around each line, so a light color stands out on light ground too.</param>
internal sealed record SimpleMap(string Title, IReadOnlyList<SimpleLayer> Layers, double LineWidth = 6, bool Outline = false)
{
    public int Points => Layers.Sum(layer => layer.Points.Count);
    public int Lines => Layers.Sum(layer => layer.Lines.Count);
    public int Areas => Layers.Sum(layer => layer.Areas.Count);
}

/// <summary>The groups of a map and which group each feature belongs to.</summary>
internal sealed record MapGrouping(IReadOnlyList<MapGroup> Groups, IReadOnlyList<int> GroupOf)
{
    /// <summary>Above this many folder-and-color groups, the folders are ignored: one group per kind and color.</summary>
    private const int MaxGroups = 24;

    public static MapGrouping Of(MapContent content)
    {
        var grouping = Of(content.Features, byFolder: true);
        return grouping.Groups.Count > MaxGroups ? Of(content.Features, byFolder: false) : grouping;
    }

    private static MapGrouping Of(IReadOnlyList<MapFeature> features, bool byFolder)
    {
        var keys = new Dictionary<(FeatureKind, string, string), int>();
        var members = new List<List<MapFeature>>();
        var groupOf = new int[features.Count];
        for (var i = 0; i < features.Count; i++)
        {
            var feature = features[i];
            var key = (feature.Kind, byFolder ? feature.Folder : string.Empty, MapColors.NameOf(feature.Color));
            if (!keys.TryGetValue(key, out var index))
            {
                keys[key] = index = members.Count;
                members.Add([]);
            }
            members[index].Add(feature);
            groupOf[i] = index;
        }

        var groups = keys.OrderBy(entry => entry.Value).Select(entry =>
        {
            var (kind, folder, colorName) = entry.Key;
            var list = members[entry.Value];
            var color = list.Select(f => f.Color).FirstOrDefault(c => c is not null);
            // A folder with more than one color of the same kind needs the color to tell its groups apart.
            var shared = keys.Keys.Count(other => other.Item1 == kind && other.Item2 == folder) > 1;
            return new MapGroup(kind, folder, colorName, color, list.Count, Suggest(kind, folder, colorName, shared));
        }).ToList();
        return new MapGrouping(groups, groupOf);
    }

    /// <summary>A name a child can read: the folder, tidied up, or "Linhas vermelhas" when there is no folder.</summary>
    internal static string Suggest(FeatureKind kind, string folder, string colorName, bool withColor)
    {
        var last = folder.Split(" / ").LastOrDefault(part => part.Length > 0);
        if (last is not null && Tidy(last) is { Length: > 0 } name)
            return withColor && colorName.Length > 0 ? $"{name} ({colorName})" : name;
        var noun = kind switch { FeatureKind.Point => "Pontos", FeatureKind.Line => "Linhas", _ => "Áreas" };
        return colorName.Length == 0 ? noun : $"{noun} {MapColors.Plural(colorName, feminine: kind != FeatureKind.Point)}";
    }

    /// <summary>Underscores to spaces, file extensions off, and SHOUTING turned into a normal sentence.</summary>
    internal static string Tidy(string text)
    {
        var name = text.Trim();
        if (name.EndsWith(".kml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".kmz", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        name = string.Join(' ', name.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Any(char.IsLetter) && !name.Any(char.IsLower))
            name = char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
        return name;
    }
}

/// <summary>Turns a busy technical map into a few big, plainly named drawings in a few strong colors.</summary>
internal static class MapSimplifier
{
    /// <param name="Tolerance">How far, in meters, a simplified line may stray from the original.</param>
    /// <param name="Cluster">Points closer than this, in meters, become one; 0 joins only points on the same spot.</param>
    /// <param name="MinLength">Lines shorter than this, in meters, are dropped after the pieces are joined.</param>
    private sealed record Settings(double Tolerance, double Cluster, double MinLength);

    private static Settings For(SimplifyLevel level) => level switch
    {
        SimplifyLevel.Little => new Settings(2, 0, 0),
        SimplifyLevel.Medium => new Settings(8, 40, 5),
        _ => new Settings(25, 150, 20),
    };

    /// <summary>Two line ends closer than this, in meters, are the same spot.</summary>
    private const double Snap = 1.5;

    public static SimpleMap Simplify(MapContent content, MapGrouping grouping, IReadOnlyList<GroupChoice> choices, SimplifyLevel level)
    {
        var settings = For(level);
        // Groups given the same name become one layer; the order is that of the first group of each name.
        var layers = new List<(string Name, List<int> Groups)>();
        for (var g = 0; g < grouping.Groups.Count; g++)
        {
            var choice = choices[g];
            if (!choice.Show)
                continue;
            var name = string.IsNullOrWhiteSpace(choice.Name) ? grouping.Groups[g].SuggestedName : choice.Name.Trim();
            var layer = layers.FindIndex(l => string.Equals(l.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (layer < 0)
                layers.Add((name, [g]));
            else
                layers[layer].Groups.Add(g);
        }

        var colors = MapColors.Assign(layers.Select(layer => PreferredColor(grouping, layer.Groups)).ToList());
        var result = new List<SimpleLayer>();
        for (var l = 0; l < layers.Count; l++)
        {
            var inLayer = new HashSet<int>(layers[l].Groups);
            var features = content.Features.Where((_, i) => inLayer.Contains(grouping.GroupOf[i])).ToList();
            var points = Cluster([.. features.Where(f => f.Kind == FeatureKind.Point).Select(f => f.Points[0])],
                Math.Max(settings.Cluster, Snap));
            var lines = JoinLines([.. features.Where(f => f.Kind == FeatureKind.Line).Select(f => f.Points)])
                .Select(line => Reduce(line, settings.Tolerance))
                .Where(line => line.Count >= 2 && Length(line) >= settings.MinLength)
                .ToList();
            var areas = features.Where(f => f.Kind == FeatureKind.Area)
                .Select(f => Reduce(f.Points, settings.Tolerance))
                .Where(ring => ring.Count >= 3)
                .ToList();
            if (points.Count + lines.Count + areas.Count > 0)
                result.Add(new SimpleLayer(layers[l].Name, colors[l], points, lines, areas));
        }
        return new SimpleMap(content.Title, result);
    }

    /// <summary>
    /// Only the cables: every line of the groups left checked, in one layer of one color. Points (boxes,
    /// CTOs, poles) and areas are left out.
    /// </summary>
    public static SimpleMap Cables(MapContent content, MapGrouping grouping, IReadOnlyList<bool> show, SimplifyLevel level,
        Color color, double lineWidth, string name = "Cabos")
    {
        var settings = For(level);
        var lines = JoinLines([.. content.Features
                .Where((f, i) => f.Kind == FeatureKind.Line && show[grouping.GroupOf[i]])
                .Select(f => f.Points)])
            .Select(line => Reduce(line, settings.Tolerance))
            .Where(line => line.Count >= 2 && Length(line) >= settings.MinLength)
            .ToList<IReadOnlyList<GeoPoint>>();
        return new SimpleMap(content.Title, lines.Count == 0 ? [] : [new SimpleLayer(name, color, [], lines, [])], lineWidth, Outline: true);
    }

    /// <summary>The color name of the biggest group of the layer that had one.</summary>
    private static string PreferredColor(MapGrouping grouping, List<int> groups) =>
        groups.Select(g => grouping.Groups[g]).Where(g => g.ColorName.Length > 0)
            .OrderByDescending(g => g.Count).Select(g => g.ColorName).FirstOrDefault() ?? string.Empty;

    // ---- Points --------------------------------------------------------------------------------

    /// <summary>
    /// Points within <paramref name="distance"/> meters of a group's middle join that group; the group is
    /// drawn once, at its middle. Works on a grid, so it stays fast with many thousands of points.
    /// </summary>
    internal static List<SimplePlace> Cluster(IReadOnlyList<GeoPoint> points, double distance)
    {
        var cells = new Dictionary<(long, long), List<int>>();
        var sums = new List<(double Latitude, double Longitude, int Count)>();
        foreach (var point in points)
        {
            var (x, y) = Meters(point);
            var cell = ((long)Math.Floor(x / distance), (long)Math.Floor(y / distance));
            var best = -1;
            var bestDistance = distance;
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!cells.TryGetValue((cell.Item1 + dx, cell.Item2 + dy), out var nearby))
                    continue;
                foreach (var index in nearby)
                {
                    var sum = sums[index];
                    var middle = new GeoPoint(sum.Latitude / sum.Count, sum.Longitude / sum.Count);
                    var d = Distance(point, middle);
                    if (d <= bestDistance)
                        (best, bestDistance) = (index, d);
                }
            }
            if (best >= 0)
            {
                var sum = sums[best];
                sums[best] = (sum.Latitude + point.Latitude, sum.Longitude + point.Longitude, sum.Count + 1);
                continue;
            }
            if (!cells.TryGetValue(cell, out var list))
                cells[cell] = list = [];
            list.Add(sums.Count);
            sums.Add((point.Latitude, point.Longitude, 1));
        }
        return [.. sums.Select(s => new SimplePlace(new GeoPoint(s.Latitude / s.Count, s.Longitude / s.Count), s.Count))];
    }

    // ---- Lines ---------------------------------------------------------------------------------

    /// <summary>
    /// Puts back together lines drawn in many short pieces: two pieces whose ends meet, where no third
    /// piece meets them, become one line. Crossings and branches stay as they are.
    /// </summary>
    internal static List<List<GeoPoint>> JoinLines(IReadOnlyList<IReadOnlyList<GeoPoint>> lines)
    {
        var ends = new Dictionary<(long, long), List<int>>();
        foreach (var (line, index) in lines.Select((line, index) => (line, index)))
        {
            foreach (var end in new[] { line[0], line[^1] })
            {
                if (!ends.TryGetValue(Key(end), out var list))
                    ends[Key(end)] = list = [];
                list.Add(index);
            }
        }

        var used = new bool[lines.Count];
        var joined = new List<List<GeoPoint>>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (used[i])
                continue;
            used[i] = true;
            var chain = new List<GeoPoint>(lines[i]);
            Extend(chain);
            chain.Reverse();
            Extend(chain);
            joined.Add(chain);
        }
        return joined;

        void Extend(List<GeoPoint> chain)
        {
            while (true)
            {
                var key = Key(chain[^1]);
                if (!ends.TryGetValue(key, out var touching) || touching.Count != 2)
                    return;
                var next = touching.FirstOrDefault(j => !used[j], -1);
                if (next < 0)
                    return;
                used[next] = true;
                var piece = lines[next];
                if (Key(piece[0]) == key)
                    chain.AddRange(piece.Skip(1));
                else
                    chain.AddRange(piece.Reverse().Skip(1));
            }
        }
    }

    /// <summary>The cell of the snapping grid a line end falls in.</summary>
    private static (long, long) Key(GeoPoint point)
    {
        // About 1.1 m per step of latitude; good enough for telling whether two ends were meant to touch.
        const double step = Snap / 111_320;
        return ((long)Math.Round(point.Latitude / step), (long)Math.Round(point.Longitude / step));
    }

    /// <summary>
    /// Douglas–Peucker: keeps only the corners that move the line more than <paramref name="tolerance"/>
    /// meters, so a cable drawn with a point every few meters becomes a handful of straight stretches.
    /// </summary>
    internal static List<GeoPoint> Reduce(IReadOnlyList<GeoPoint> line, double tolerance)
    {
        // Consecutive repeats first: they make no corner.
        var points = new List<GeoPoint>(line.Count);
        foreach (var point in line)
        {
            if (points.Count == 0 || Distance(points[^1], point) > 0.05)
                points.Add(point);
        }
        if (points.Count <= 2 || tolerance <= 0)
            return points;

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var pending = new Stack<(int First, int Last)>();
        pending.Push((0, points.Count - 1));
        while (pending.Count > 0)
        {
            var (first, last) = pending.Pop();
            var farthest = -1;
            var farthestDistance = tolerance;
            for (var i = first + 1; i < last; i++)
            {
                var d = DistanceToSegment(points[i], points[first], points[last]);
                if (d > farthestDistance)
                    (farthest, farthestDistance) = (i, d);
            }
            if (farthest < 0)
                continue;
            keep[farthest] = true;
            pending.Push((first, farthest));
            pending.Push((farthest, last));
        }
        return [.. points.Where((_, i) => keep[i])];
    }

    internal static double Length(IReadOnlyList<GeoPoint> line)
    {
        var length = 0.0;
        for (var i = 1; i < line.Count; i++)
            length += Distance(line[i - 1], line[i]);
        return length;
    }

    // ---- Distances -----------------------------------------------------------------------------

    private const double MetersPerDegree = 111_320;

    /// <summary>Flat x/y in meters; fine for distances of a few kilometers.</summary>
    private static (double X, double Y) Meters(GeoPoint point) =>
        (point.Longitude * MetersPerDegree * Math.Cos(point.Latitude * Math.PI / 180), point.Latitude * MetersPerDegree);

    internal static double Distance(GeoPoint a, GeoPoint b)
    {
        var cos = Math.Cos((a.Latitude + b.Latitude) / 2 * Math.PI / 180);
        var dx = (b.Longitude - a.Longitude) * MetersPerDegree * cos;
        var dy = (b.Latitude - a.Latitude) * MetersPerDegree;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    internal static double DistanceToSegment(GeoPoint point, GeoPoint start, GeoPoint end)
    {
        var cos = Math.Cos(start.Latitude * Math.PI / 180);
        double X(GeoPoint p) => (p.Longitude - start.Longitude) * MetersPerDegree * cos;
        double Y(GeoPoint p) => (p.Latitude - start.Latitude) * MetersPerDegree;
        var (px, py, ex, ey) = (X(point), Y(point), X(end), Y(end));
        var lengthSquared = ex * ex + ey * ey;
        var t = lengthSquared == 0 ? 0 : Math.Clamp((px * ex + py * ey) / lengthSquared, 0, 1);
        var (dx, dy) = (px - t * ex, py - t * ey);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>The few strong colors of the simple map, and the plain names of any color.</summary>
internal static class MapColors
{
    /// <summary>In the order they are handed out; each is easy to tell from the others and from a satellite picture.</summary>
    private static readonly (string Name, Color Color)[] Palette =
    [
        ("vermelho", Color.FromArgb(0xE5, 0x39, 0x35)),
        ("azul", Color.FromArgb(0x1E, 0x88, 0xE5)),
        ("verde", Color.FromArgb(0x43, 0xA0, 0x47)),
        ("laranja", Color.FromArgb(0xFB, 0x8C, 0x00)),
        ("roxo", Color.FromArgb(0x8E, 0x24, 0xAA)),
        ("amarelo", Color.FromArgb(0xFD, 0xD8, 0x35)),
        ("rosa", Color.FromArgb(0xEC, 0x40, 0x7A)),
        ("marrom", Color.FromArgb(0x6D, 0x4C, 0x41)),
        ("preto", Color.FromArgb(0x21, 0x21, 0x21)),
        ("cinza", Color.FromArgb(0x9E, 0x9E, 0x9E)),
    ];

    /// <summary>
    /// One color per layer: the strong version of the color it had when that one is still free, so red
    /// stays red; otherwise the next free color. With more layers than colors, the colors repeat.
    /// </summary>
    public static List<Color> Assign(IReadOnlyList<string> preferred)
    {
        var taken = new bool[Palette.Length];
        var colors = new Color?[preferred.Count];
        for (var i = 0; i < preferred.Count; i++)
        {
            var index = Array.FindIndex(Palette, p => p.Name == preferred[i]);
            if (index >= 0 && !taken[index])
            {
                taken[index] = true;
                colors[i] = Palette[index].Color;
            }
        }
        var next = 0;
        for (var i = 0; i < preferred.Count; i++)
        {
            if (colors[i] is not null)
                continue;
            var free = Array.FindIndex(taken, t => !t);
            if (free < 0)
                free = next++ % Palette.Length;
            else
                taken[free] = true;
            colors[i] = Palette[free].Color;
        }
        return [.. colors.Select(c => c!.Value)];
    }

    /// <summary>
    /// Neon green: the brightest there is, and no satellite picture has it (grass and trees are dull
    /// greens), so the cables stand out on any background. It is only for the one-color map; among the
    /// layer colors it would be too close to the plain green.
    /// </summary>
    public static readonly Color Neon = Color.FromArgb(0x39, 0xFF, 0x14);

    public const string NeonName = "verde neon";

    /// <summary>
    /// The colors, with their names, for the user to pick from: first the light, bright ones made for
    /// one color on top of the satellite picture, then the layer colors.
    /// </summary>
    public static IReadOnlyList<(string Name, Color Color)> Choices { get; } =
    [
        (NeonName, Neon),
        ("amarelo neon", Color.FromArgb(0xFF, 0xF2, 0x00)),
        ("ciano neon", Color.FromArgb(0x00, 0xF0, 0xFF)),
        ("rosa neon", Color.FromArgb(0xFF, 0x2E, 0xD1)),
        ("laranja neon", Color.FromArgb(0xFF, 0x8C, 0x1A)),
        ("branco", Color.White),
        .. Palette,
    ];

    /// <summary>The plain name of a color: "vermelho", "azul"...; empty for none.</summary>
    public static string NameOf(Color? color)
    {
        if (color is not { } c)
            return string.Empty;
        var brightness = c.GetBrightness();
        var saturation = c.GetSaturation();
        if (saturation < 0.18 || brightness > 0.94)
            return brightness > 0.8 ? "branco" : brightness < 0.2 ? "preto" : "cinza";
        if (brightness < 0.1)
            return "preto";
        var hue = c.GetHue();
        return hue switch
        {
            < 12 or >= 340 => "vermelho",
            < 45 => brightness < 0.35 ? "marrom" : "laranja",
            < 68 => "amarelo",
            < 165 => "verde",
            < 255 => "azul",
            < 295 => "roxo",
            _ => "rosa",
        };
    }

    /// <summary>"vermelhas", "azuis", "laranja"...: the color agreeing with a plural noun.</summary>
    public static string Plural(string name, bool feminine) => name switch
    {
        "azul" => "azuis",
        "verde" => "verdes",
        "marrom" => "marrons",
        "laranja" or "rosa" or "cinza" => name,
        _ when feminine && name.EndsWith('o') => name[..^1] + "as",
        _ => name + "s",
    };
}
