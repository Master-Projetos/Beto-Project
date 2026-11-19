namespace ListaMateriais.Kmz;

/// <summary>A named place on the map, in decimal degrees.</summary>
/// <param name="Description">Shown when the marker is clicked: how the point was found (an address, and how precisely).</param>
internal sealed record MapPoint(string Name, double Latitude, double Longitude, string? Description = null);

/// <summary>Which coordinate comes first when a pair has no "lat"/"lon" labels.</summary>
internal enum CoordinateOrder
{
    LatitudeFirst,
    LongitudeFirst,
}

/// <summary>
/// The points that could be read, the entries that could not (and why), and the typing slips that
/// were corrected on the way (decimal separator missing, latitude and longitude swapped, repeated lines).
/// </summary>
internal sealed record PointList(IReadOnlyList<MapPoint> Points, IReadOnlyList<string> Problems, IReadOnlyList<string> Fixes)
{
    public static readonly PointList Empty = new([], [], []);

    public PointList(IReadOnlyList<MapPoint> points, IReadOnlyList<string> problems) : this(points, problems, []) { }
}

/// <summary>
/// Collects points as they are read. Coordinates off the globe are repaired when the slip is clear
/// (a number typed without its decimal separator, or the pair in the wrong order), using the other
/// points of the list to pick the right reading; only what cannot be repaired with confidence is a problem.
/// </summary>
internal sealed class PointCollector(CoordinateOrder order)
{
    /// <summary>A repaired point further than this from the middle of the others is not trusted.</summary>
    private const double MaxDistanceDegrees = 5;

    private readonly record struct Entry(string Where, string? Name, double Latitude, double Longitude, string? Description = null);

    private readonly List<Entry> _entries = [];
    private readonly List<(int Index, string Text)> _problems = [];
    private readonly List<(int Index, string Text)> _fixes = [];

    public PointList Result => Resolve();

    public void Problem(string where, string what)
    {
        _problems.Add((_entries.Count, $"{where}: {what}"));
    }

    /// <summary>A slip corrected while reading the next entry.</summary>
    public void Fixed(string where, string what)
    {
        _fixes.Add((_entries.Count, $"{where}: {what}"));
    }

    public void AddPair(string where, string? name, double first, double second)
    {
        var (latitude, longitude) = order == CoordinateOrder.LatitudeFirst ? (first, second) : (second, first);
        Add(where, name, latitude, longitude);
    }

    public void Add(string where, string? name, double latitude, double longitude, string? description = null)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
            Problem(where, "a coordenada não é um número.");
        else
            _entries.Add(new Entry(where, name, latitude, longitude, description));
    }

    private PointList Resolve()
    {
        var inRange = _entries.Where(entry => Valid(entry.Latitude, entry.Longitude)).ToList();
        (double Latitude, double Longitude)? middle = inRange.Count >= 3
            ? (Median(inRange.Select(entry => entry.Latitude)), Median(inRange.Select(entry => entry.Longitude)))
            : null;

        var points = new List<MapPoint>();
        var problems = new List<(int Index, string Text)>(_problems);
        var fixes = new List<(int Index, string Text)>(_fixes);
        var seen = new HashSet<(string?, double, double)>();

        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            var (latitude, longitude) = (entry.Latitude, entry.Longitude);

            if (!Valid(latitude, longitude) || (middle is { } center && IsSwapCloser(latitude, longitude, center)))
            {
                if (Repair(latitude, longitude, middle) is not { } repaired)
                {
                    problems.Add((i, $"{entry.Where}: {Describe(latitude, longitude)}"));
                    continue;
                }
                fixes.Add((i, $"{entry.Where}: {repaired.Why}"));
                (latitude, longitude) = (repaired.Latitude, repaired.Longitude);
            }

            var name = string.IsNullOrWhiteSpace(entry.Name) ? null : entry.Name.Trim();
            if (!seen.Add((name, latitude, longitude)))
            {
                fixes.Add((i, $"{entry.Where}: repetida, ignorada."));
                continue;
            }
            points.Add(new MapPoint(name ?? $"Ponto {points.Count + 1}", latitude, longitude, entry.Description));
        }

        return new PointList(points, [.. problems.OrderBy(problem => problem.Index).Select(problem => problem.Text)],
            [.. fixes.OrderBy(fix => fix.Index).Select(fix => fix.Text)]);
    }

    private static bool Valid(double latitude, double longitude) => Math.Abs(latitude) <= 90 && Math.Abs(longitude) <= 180;

    /// <summary>
    /// A pair that is valid as typed but lands far from the rest, while the swapped pair lands among them:
    /// the columns were inverted on that line.
    /// </summary>
    private static bool IsSwapCloser(double latitude, double longitude, (double Latitude, double Longitude) center) =>
        Math.Abs(longitude) <= 90
        && Distance(latitude, longitude, center) > MaxDistanceDegrees
        && Distance(longitude, latitude, center) <= MaxDistanceDegrees;

    /// <summary>
    /// The most likely reading of a broken pair: each number with its decimal separator put back, the
    /// pair as typed or swapped. With other points to compare, the reading nearest to them wins (and must
    /// be near); without them, only a single obvious reading is accepted.
    /// </summary>
    private static (double Latitude, double Longitude, string Why)? Repair(double latitude, double longitude,
        (double Latitude, double Longitude)? center)
    {
        var readings = new List<(double Latitude, double Longitude, int Changes, bool Swapped)>();
        foreach (var swapped in new[] { false, true })
        {
            var (first, second) = swapped ? (longitude, latitude) : (latitude, longitude);
            foreach (var (lat, latShifts) in Readings(first, 90))
            foreach (var (lon, lonShifts) in Readings(second, 180))
                readings.Add((lat, lon, (latShifts > 0 ? 1 : 0) + (lonShifts > 0 ? 1 : 0) + (swapped ? 1 : 0), swapped));
        }
        if (readings.Count == 0)
            return null;

        (double Latitude, double Longitude, int Changes, bool Swapped) best;
        if (center is { } middle)
        {
            best = readings.MinBy(reading => Distance(reading.Latitude, reading.Longitude, middle));
            if (Distance(best.Latitude, best.Longitude, middle) > MaxDistanceDegrees)
                return null;
        }
        else
        {
            // Alone, a reading is trusted only when it is the single one with the fewest changes.
            var fewest = readings.Min(reading => reading.Changes);
            var simplest = readings.Where(reading => reading.Changes == fewest).ToList();
            if (simplest.Count != 1)
                return null;
            best = simplest[0];
        }

        var why = new List<string>();
        if (best.Swapped)
            why.Add("latitude e longitude estavam trocadas");
        var (typedLatitude, typedLongitude) = best.Swapped ? (longitude, latitude) : (latitude, longitude);
        if (typedLatitude != best.Latitude)
            why.Add($"latitude {Number(typedLatitude)} lida como {Number(best.Latitude)}");
        if (typedLongitude != best.Longitude)
            why.Add($"longitude {Number(typedLongitude)} lida como {Number(best.Longitude)}");
        return (best.Latitude, best.Longitude, Capitalize(string.Join("; ", why)) + ".");
    }

    /// <summary>
    /// The value as typed when it fits; otherwise, for a whole number (the decimal separator was lost),
    /// each placement of the separator that fits, from the most to the fewest digits before it.
    /// </summary>
    private static IEnumerable<(double Value, int Shifts)> Readings(double value, double limit)
    {
        if (Math.Abs(value) <= limit)
        {
            yield return (value, 0);
            yield break;
        }
        if (Math.Abs(value - Math.Round(value)) > 1e-9)
            yield break;
        var shifted = value;
        for (var shifts = 1; shifts <= 12; shifts++)
        {
            shifted /= 10;
            if (Math.Abs(shifted) < 1)
                yield break;
            if (Math.Abs(shifted) <= limit)
                yield return (Math.Round(shifted, 10), shifts);
        }
    }

    private static string Describe(double latitude, double longitude) =>
        Math.Abs(latitude) > 90 && Math.Abs(longitude) <= 90 && Math.Abs(latitude) <= 180
            ? $"latitude {Number(latitude)} fora do limite; confira a ordem das coordenadas."
            : Math.Abs(latitude) > 90
                ? $"latitude {Number(latitude)} fora de -90 a 90."
                : Math.Abs(longitude) > 180
                    ? $"longitude {Number(longitude)} fora de -180 a 180."
                    : $"latitude {Number(latitude)}, longitude {Number(longitude)} fica longe dos demais pontos.";

    private static double Distance(double latitude, double longitude, (double Latitude, double Longitude) center) =>
        Math.Max(Math.Abs(latitude - center.Latitude), Math.Abs(longitude - center.Longitude));

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    private static string Number(double value) => value.ToString("0.######", System.Globalization.CultureInfo.CurrentCulture);

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
