using System.Globalization;

namespace ListaMateriais.Kmz;

internal enum IssueLevel
{
    /// <summary>Almost surely wrong: the project should not go out like this.</summary>
    Error,

    /// <summary>Worth a look; it may be right.</summary>
    Warning,
}

/// <summary>One thing to look at in a network map.</summary>
/// <param name="Problem">Short name of the rule, the same for every issue it finds ("Ponta solta").</param>
/// <param name="Item">Name of the cable or point, as written in the map.</param>
/// <param name="Where">The spot to look at.</param>
/// <param name="Detail">What exactly is wrong, in a sentence.</param>
internal sealed record MapIssue(IssueLevel Level, string Problem, string Item, string Folder, GeoPoint Where, string Detail);

/// <param name="JoinDistance">How close, in meters, a cable end must be to a box or another cable to count as connected.</param>
/// <param name="MaxSpan">Longest stretch, in meters, between two vertices of a cable; null to not check.</param>
/// <param name="MinLength">Cables shorter than this, in meters, are taken as leftovers of drawing.</param>
internal sealed record CheckOptions(double JoinDistance = 10, double? MaxSpan = null, double MinLength = 3);

/// <summary>
/// Reviews a network map before it goes out: cables with no name or drawn twice, loose cable ends, boxes
/// no cable reaches, leftover bits of line, points in the wrong place. Only the geometry and the names are
/// looked at: the maps carry no port counts or cable types that could be checked.
/// </summary>
internal static class MapChecker
{
    public const string Unnamed = "Sem nome";
    public const string RepeatedName = "Nome repetido";
    public const string DrawnTwice = "Desenhado duas vezes";
    public const string SameTrace = "Mesmo traçado";
    public const string LooseEnd = "Ponta solta";
    public const string NoCable = "Ponto sem cabo";
    public const string TooShort = "Cabo muito curto";
    public const string LongSpan = "Vão longo";
    public const string BadCoordinate = "Coordenada errada";
    public const string OutsideBrazil = "Fora do Brasil";

    /// <summary>Two drawings closer than this everywhere are the same trace.</summary>
    private const double SameTraceDistance = 2;

    /// <summary>The names Google Earth gives what was never named, in Portuguese and English.</summary>
    private static readonly string[] DefaultNames =
    [
        "caminho sem título", "ponto sem título", "marcador sem título", "polígono sem título", "trajeto sem título",
        "untitled path", "untitled placemark", "untitled polygon", "untitled",
    ];

    /// <summary>Every issue found, errors first.</summary>
    /// <param name="report">
    /// Which drawings to report on. The others (the existing network around the project, say) still count
    /// as something a cable can connect to, but their own issues are left out. Null reports on all.
    /// </param>
    public static List<MapIssue> Check(MapContent content, CheckOptions options, Func<MapFeature, bool>? report = null)
    {
        report ??= _ => true;
        var all = content.Features;
        var lines = all.Where(f => f.Kind == FeatureKind.Line && f.Points.Count >= 2).Select(f => new Line(f, options.JoinDistance)).ToList();
        var points = new PointGrid(all.Where(f => f.Kind == FeatureKind.Point).Select(f => f.Points[0]), options.JoinDistance);
        var issues = new List<MapIssue>();

        foreach (var feature in all.Where(report))
            CheckCoordinates(feature, issues);
        var valid = all.Where(f => report(f) && f.Points.All(Plausible)).ToList();
        var isValid = new HashSet<MapFeature>(valid, ReferenceEqualityComparer.Instance);

        CheckRepeats(valid, lines, issues);
        CheckNames(valid, issues);
        foreach (var line in lines.Where(l => isValid.Contains(l.Feature)))
        {
            CheckEnds(line, lines, points, options.JoinDistance, issues);
            CheckLength(line, options, issues);
        }
        if (lines.Count > 0)
        {
            foreach (var point in valid.Where(f => f.Kind == FeatureKind.Point))
            {
                if (!lines.Any(l => l.Near(point.Points[0], options.JoinDistance)))
                    issues.Add(Issue(IssueLevel.Warning, NoCable, point, point.Points[0],
                        $"Nenhum cabo passa a menos de {Meters(options.JoinDistance)} deste ponto."));
            }
        }

        return [.. issues
            .OrderBy(i => i.Level)
            .ThenBy(i => i.Problem, StringComparer.CurrentCulture)
            .ThenBy(i => i.Folder, StringComparer.CurrentCulture)
            .ThenBy(i => i.Item, StringComparer.CurrentCulture)];
    }

    public static bool IsUnnamed(string name)
    {
        var text = name.Trim().ToLower(CultureInfo.GetCultureInfo("pt-BR"));
        return text.Length == 0 || DefaultNames.Any(d => text == d || text.StartsWith(d + " ", StringComparison.Ordinal));
    }

    private static string What(MapFeature feature) => feature.Kind switch
    {
        FeatureKind.Line => "cabo",
        FeatureKind.Point => "ponto",
        _ => "área",
    };

    private static MapIssue Issue(IssueLevel level, string problem, MapFeature feature, GeoPoint where, string detail) =>
        new(level, problem, feature.Name.Trim().Length == 0 ? "(sem nome)" : feature.Name.Trim(), feature.Folder, where, detail);

    // ---- Coordinates ---------------------------------------------------------------------------

    private static bool Plausible(GeoPoint p) =>
        Math.Abs(p.Latitude) <= 90 && Math.Abs(p.Longitude) <= 180 && (Math.Abs(p.Latitude) > 0.01 || Math.Abs(p.Longitude) > 0.01);

    /// <summary>A generous box around Brazil, islands included.</summary>
    private static bool InBrazil(GeoPoint p) => p.Latitude is >= -34.5 and <= 6 && p.Longitude is >= -74.5 and <= -28;

    private static void CheckCoordinates(MapFeature feature, List<MapIssue> issues)
    {
        if (feature.Points.Where(p => !Plausible(p)).Take(1).ToList() is [var bad])
        {
            issues.Add(Issue(IssueLevel.Error, BadCoordinate, feature, bad,
                $"O {What(feature)} tem um ponto em {bad.Latitude.ToString("0.#####", CultureInfo.InvariantCulture)}, " +
                $"{bad.Longitude.ToString("0.#####", CultureInfo.InvariantCulture)}: coordenada zerada ou inválida."));
            return;
        }
        if (feature.Points.Where(p => !InBrazil(p)).Take(1).ToList() is [var away])
        {
            var swapped = InBrazil(new GeoPoint(away.Longitude, away.Latitude));
            issues.Add(Issue(IssueLevel.Warning, OutsideBrazil, feature, away,
                swapped ? "Parece que a latitude e a longitude foram trocadas." : $"O {What(feature)} está fora do Brasil."));
        }
    }

    // ---- Names ---------------------------------------------------------------------------------

    private static void CheckNames(List<MapFeature> features, List<MapIssue> issues)
    {
        foreach (var feature in features.Where(f => IsUnnamed(f.Name)))
        {
            var detail = feature.Kind == FeatureKind.Line
                ? $"Cabo de {Meters(MapSimplifier.Length(feature.Points))} sem nome (ficou o nome padrão do Google Earth)."
                : $"O {What(feature)} ficou sem nome.";
            issues.Add(Issue(IssueLevel.Warning, Unnamed, feature, Middle(feature.Points), detail));
        }

        var key = CultureInfo.GetCultureInfo("pt-BR");
        foreach (var group in features.Where(f => !IsUnnamed(f.Name))
                     .GroupBy(f => (f.Kind, Name: string.Join(' ', f.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLower(key)))
                     .Where(g => g.Count() > 1))
        {
            // Pieces of one cable drawn one after the other may share the name; only separate drawings count.
            var copies = group.ToList();
            if (group.Key.Kind == FeatureKind.Line && Connected(copies))
                continue;
            // A copy on top of the other was already reported as drawn twice.
            if (copies.Count == 2 && issues.Any(i => i.Problem == DrawnTwice && copies.Any(c => c.Points.Contains(i.Where) || Middle(c.Points) == i.Where)))
                continue;
            var first = copies[0];
            var folders = copies.Select(f => f.Folder).Distinct().ToList();
            issues.Add(Issue(IssueLevel.Warning, RepeatedName, first, Middle(first.Points),
                $"{copies.Count} {(group.Key.Kind == FeatureKind.Line ? "cabos" : "pontos")} com o mesmo nome" +
                (folders.Count > 1 ? $", nas pastas: {string.Join("; ", folders.Select(f => f.Length == 0 ? "(sem pasta)" : f))}." : ".")));
        }
    }

    /// <summary>True when the lines join end to end into one chain.</summary>
    private static bool Connected(List<MapFeature> lines)
    {
        var joined = MapSimplifier.JoinLines([.. lines.Select(l => l.Points)]);
        return joined.Count == 1;
    }

    // ---- Repeated drawings ---------------------------------------------------------------------

    private static void CheckRepeats(List<MapFeature> features, List<Line> lines, List<MapIssue> issues)
    {
        var reported = new HashSet<MapFeature>(features, ReferenceEqualityComparer.Instance);
        // A CEO and a CTO on the same pole are normal; the same box twice is not: only points of one name are compared.
        foreach (var named in features.Where(f => f.Kind == FeatureKind.Point && !IsUnnamed(f.Name))
                     .GroupBy(f => f.Name.Trim(), StringComparer.CurrentCultureIgnoreCase))
        {
            var points = named.ToList();
            for (var i = 0; i < points.Count; i++)
            {
                for (var j = i + 1; j < points.Count; j++)
                {
                    if (MapSimplifier.Distance(points[i].Points[0], points[j].Points[0]) <= SameTraceDistance)
                        issues.Add(Issue(IssueLevel.Error, DrawnTwice, points[j], points[j].Points[0],
                            "Há dois pontos com este nome no mesmo lugar: um deles é cópia" +
                            (points[i].Folder == points[j].Folder ? "." : $" (o outro está em \"{points[i].Folder}\").")));
                }
            }
        }

        var mine = lines.Where(l => reported.Contains(l.Feature)).ToList();
        for (var i = 0; i < mine.Count; i++)
        {
            for (var j = i + 1; j < mine.Count; j++)
            {
                var (a, b) = (mine[i], mine[j]);
                if (!a.Box.Overlaps(b.Box) || !a.Covers(b) || !b.Covers(a))
                    continue;
                if (SameName(a.Feature, b.Feature) || IsUnnamed(a.Feature.Name) || IsUnnamed(b.Feature.Name))
                    issues.Add(Issue(IssueLevel.Error, DrawnTwice, b.Feature, Middle(b.Feature.Points),
                        "O cabo está desenhado duas vezes, uma por cima da outra" +
                        (a.Feature.Folder == b.Feature.Folder ? "." : $" (a outra está em \"{a.Feature.Folder}\").")));
                else
                    issues.Add(Issue(IssueLevel.Warning, SameTrace, b.Feature, Middle(b.Feature.Points),
                        $"Passa exatamente por cima de \"{a.Feature.Name.Trim()}\". Se forem dois cabos de verdade, está certo; " +
                        "se for cópia, apague um."));
            }
        }
    }

    private static bool SameName(MapFeature a, MapFeature b) =>
        !IsUnnamed(a.Name) && string.Equals(a.Name.Trim(), b.Name.Trim(), StringComparison.CurrentCultureIgnoreCase);

    // ---- Cable ends and lengths ----------------------------------------------------------------

    private static void CheckEnds(Line line, List<Line> lines, PointGrid points, double distance, List<MapIssue> issues)
    {
        var drawing = line.Feature.Points;
        var length = MapSimplifier.Length(drawing);
        // A ring closes on itself.
        if (MapSimplifier.Distance(drawing[0], drawing[^1]) <= distance && length > distance * 2)
            return;
        foreach (var (end, which) in new[] { (drawing[0], "O início"), (drawing[^1], "O fim") })
        {
            var connected = points.AnyWithin(end, distance)
                            || lines.Any(other => other != line && other.Near(end, distance));
            if (!connected)
                issues.Add(Issue(IssueLevel.Warning, LooseEnd, line.Feature, end,
                    $"{which} do cabo não chega a nenhuma caixa, ponto ou outro cabo (nada a menos de {Meters(distance)})."));
        }
    }

    private static void CheckLength(Line line, CheckOptions options, List<MapIssue> issues)
    {
        var drawing = line.Feature.Points;
        var length = MapSimplifier.Length(drawing);
        if (length < options.MinLength)
            issues.Add(Issue(IssueLevel.Warning, TooShort, line.Feature, drawing[0],
                $"Tem só {Meters(length)}: deve ser sobra de desenho."));

        if (options.MaxSpan is not { } max)
            return;
        var spans = Enumerable.Range(1, drawing.Count - 1)
            .Select(i => (Length: MapSimplifier.Distance(drawing[i - 1], drawing[i]), Index: i))
            .Where(s => s.Length > max)
            .ToList();
        if (spans.Count == 0)
            return;
        var longest = spans.MaxBy(s => s.Length);
        var a = drawing[longest.Index - 1];
        var b = drawing[longest.Index];
        issues.Add(Issue(IssueLevel.Warning, LongSpan, line.Feature, new GeoPoint((a.Latitude + b.Latitude) / 2, (a.Longitude + b.Longitude) / 2),
            (spans.Count == 1 ? "1 vão" : $"{spans.Count} vãos") + $" acima de {Meters(max)}; o maior tem {Meters(longest.Length)}."));
    }

    // ---- Helpers -------------------------------------------------------------------------------

    internal static string Meters(double meters) =>
        meters >= 1000 ? (meters / 1000).ToString("0.0#", CultureInfo.GetCultureInfo("pt-BR")) + " km"
        : meters >= 10 ? meters.ToString("N0", CultureInfo.GetCultureInfo("pt-BR")) + " m"
        : meters.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) + " m";

    /// <summary>The vertex in the middle of the drawing: a spot that is on it.</summary>
    private static GeoPoint Middle(IReadOnlyList<GeoPoint> points) => points[points.Count / 2];

    /// <summary>Points sorted into square cells as wide as the search distance, so a search looks at 9 cells only.</summary>
    private sealed class PointGrid
    {
        private readonly Dictionary<(long, long), List<GeoPoint>> _cells = [];
        private readonly double _cellLatitude;
        private readonly double _cellLongitude;

        public PointGrid(IEnumerable<GeoPoint> points, double distance)
        {
            var all = points.ToList();
            var latitude = all.Count == 0 ? 0 : all.Average(p => Math.Clamp(p.Latitude, -80, 80));
            _cellLatitude = Math.Max(distance, 1) / 111_320;
            _cellLongitude = _cellLatitude / Math.Max(0.2, Math.Cos(latitude * Math.PI / 180));
            foreach (var point in all)
            {
                var key = Key(point);
                if (!_cells.TryGetValue(key, out var cell))
                    _cells[key] = cell = [];
                cell.Add(point);
            }
        }

        private (long, long) Key(GeoPoint p) => ((long)Math.Floor(p.Latitude / _cellLatitude), (long)Math.Floor(p.Longitude / _cellLongitude));

        public bool AnyWithin(GeoPoint point, double distance)
        {
            var (row, column) = Key(point);
            // The cells are at least as wide as the distance, except far from the equator where they are narrower.
            var reach = (int)Math.Ceiling(distance / 111_320 / _cellLatitude) + 1;
            for (var r = row - reach; r <= row + reach; r++)
            {
                for (var c = column - reach; c <= column + reach; c++)
                {
                    if (_cells.TryGetValue((r, c), out var cell) && cell.Any(p => MapSimplifier.Distance(p, point) <= distance))
                        return true;
                }
            }
            return false;
        }
    }

    private readonly record struct Box(double South, double West, double North, double East)
    {
        public bool Contains(GeoPoint p) => p.Latitude >= South && p.Latitude <= North && p.Longitude >= West && p.Longitude <= East;

        public bool Overlaps(Box o) => South <= o.North && o.South <= North && West <= o.East && o.West <= East;
    }

    /// <summary>A cable with its bounding box grown by the join distance, to skip the far ones quickly.</summary>
    private sealed class Line
    {
        public Line(MapFeature feature, double margin)
        {
            Feature = feature;
            margin = Math.Max(margin, SameTraceDistance);
            var points = feature.Points;
            var latitude = points.Average(p => p.Latitude);
            var dLat = margin / 111_320;
            var dLon = margin / (111_320 * Math.Max(0.2, Math.Cos(latitude * Math.PI / 180)));
            Box = new Box(points.Min(p => p.Latitude) - dLat, points.Min(p => p.Longitude) - dLon,
                points.Max(p => p.Latitude) + dLat, points.Max(p => p.Longitude) + dLon);
        }

        public MapFeature Feature { get; }

        public Box Box { get; }

        public double DistanceTo(GeoPoint point)
        {
            var points = Feature.Points;
            var best = double.MaxValue;
            for (var i = 1; i < points.Count; i++)
                best = Math.Min(best, MapSimplifier.DistanceToSegment(point, points[i - 1], points[i]));
            return best;
        }

        public bool Near(GeoPoint point, double distance) => Box.Contains(point) && DistanceTo(point) <= distance;

        /// <summary>True when every vertex of the other line lies on this one.</summary>
        public bool Covers(Line other) => other.Feature.Points.All(p => Box.Contains(p) && DistanceTo(p) <= SameTraceDistance);
    }
}
