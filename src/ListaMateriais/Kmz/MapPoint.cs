namespace ListaMateriais.Kmz;

/// <summary>A named place on the map, in decimal degrees.</summary>
internal sealed record MapPoint(string Name, double Latitude, double Longitude);

/// <summary>Which coordinate comes first when a pair has no "lat"/"lon" labels.</summary>
internal enum CoordinateOrder
{
    LatitudeFirst,
    LongitudeFirst,
}

/// <summary>The points that could be read and, for each entry that could not, why.</summary>
internal sealed record PointList(IReadOnlyList<MapPoint> Points, IReadOnlyList<string> Problems)
{
    public static readonly PointList Empty = new([], []);
}

/// <summary>Collects points as they are read, naming the unnamed ones and refusing coordinates off the globe.</summary>
internal sealed class PointCollector(CoordinateOrder order)
{
    private readonly List<MapPoint> _points = [];
    private readonly List<string> _problems = [];

    public PointList Result => new(_points, _problems);

    public void Problem(string where, string what) => _problems.Add($"{where}: {what}");

    /// <summary>Adds a pair given in the order chosen by the user.</summary>
    public void AddPair(string where, string? name, double first, double second)
    {
        var (latitude, longitude) = order == CoordinateOrder.LatitudeFirst ? (first, second) : (second, first);
        Add(where, name, latitude, longitude);
    }

    public void Add(string where, string? name, double latitude, double longitude)
    {
        if (Math.Abs(latitude) > 90)
        {
            Problem(where, Math.Abs(longitude) <= 90
                ? $"latitude {latitude:0.######} fora de -90 a 90. Confira a ordem das coordenadas."
                : $"latitude {latitude:0.######} fora de -90 a 90.");
            return;
        }
        if (Math.Abs(longitude) > 180)
        {
            Problem(where, $"longitude {longitude:0.######} fora de -180 a 180.");
            return;
        }
        var label = string.IsNullOrWhiteSpace(name) ? $"Ponto {_points.Count + 1}" : name.Trim();
        _points.Add(new MapPoint(label, latitude, longitude));
    }
}
