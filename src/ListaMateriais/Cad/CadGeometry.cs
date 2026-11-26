namespace ListaMateriais.Cad;

/// <summary>Turns the curves of a drawing (arcs, circles, ellipses, splines, polyline bulges) into short straight pieces.</summary>
internal static class CadGeometry
{
    /// <summary>Most points one entity may have; far more means damaged data, and would exhaust the memory.</summary>
    public const int MaxPoints = 500_000;

    /// <summary>Refuses an entity that grew past <see cref="MaxPoints"/>.</summary>
    public static void CheckSize(List<CadPoint> points)
    {
        if (points.Count > MaxPoints)
            throw new InvalidDataException("Elemento grande demais.");
    }

    /// <summary>Largest angle one piece of a curve may cover, in radians (3 degrees: under 4 cm off the curve per 100 m of radius).</summary>
    private const double StepAngle = Math.PI / 60;

    /// <summary>Maps a point of an entity's own plane (the OCS, set by its extrusion direction) to the world's X and Y.</summary>
    public static CadPoint Ocs((double X, double Y, double Z) normal, double x, double y)
    {
        if (normal.X == 0 && normal.Y == 0 && normal.Z >= 0)
            return new CadPoint(x, y);
        var (nx, ny, nz) = Normalize(normal);
        // Arbitrary axis algorithm: the OCS X axis is Wy × N, or Wz × N when N is far from the world Z axis' neighborhood.
        var ax = Math.Abs(nx) < 1.0 / 64 && Math.Abs(ny) < 1.0 / 64
            ? Normalize((nz, 0, -nx))
            : Normalize((-ny, nx, 0));
        // The OCS Y axis is N × X axis.
        var ayX = ny * ax.Z - nz * ax.Y;
        var ayY = nz * ax.X - nx * ax.Z;
        return new CadPoint(x * ax.X + y * ayX, x * ax.Y + y * ayY);
    }

    private static (double X, double Y, double Z) Normalize((double X, double Y, double Z) v)
    {
        var length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return length < 1e-12 ? (0, 0, 1) : (v.X / length, v.Y / length, v.Z / length);
    }

    /// <summary>The angle swept going counterclockwise from <paramref name="start"/> to <paramref name="end"/>, in (0, 2π].</summary>
    private static double Sweep(double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || Math.Abs(start) > 1e6 || Math.Abs(end) > 1e6)
            throw new InvalidDataException("Ângulo inválido.");
        var sweep = (end - start) % (2 * Math.PI);
        return sweep <= 1e-12 ? sweep + 2 * Math.PI : sweep;
    }

    /// <summary>Points of an arc from <paramref name="start"/> to <paramref name="end"/> (radians, counterclockwise).</summary>
    public static void Arc(List<CadPoint> into, Func<double, double, CadPoint> toWorld, double cx, double cy, double radius,
        double start, double end)
    {
        var sweep = Sweep(start, end);
        var steps = Math.Clamp((int)Math.Ceiling(sweep / StepAngle), 2, 720);
        for (var i = 0; i <= steps; i++)
        {
            var a = start + sweep * i / steps;
            into.Add(toWorld(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
        }
    }

    /// <summary>Adds the curve of a polyline segment with a bulge (tangent of a quarter of the arc's angle), excluding the start point.</summary>
    public static void BulgeSegment(List<CadPoint> into, double x1, double y1, double x2, double y2, double bulge,
        Func<double, double, CadPoint> toWorld)
    {
        if (Math.Abs(bulge) < 1e-9)
        {
            into.Add(toWorld(x2, y2));
            return;
        }
        var dx = x2 - x1;
        var dy = y2 - y1;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-12)
        {
            into.Add(toWorld(x2, y2));
            return;
        }
        var theta = 4 * Math.Atan(bulge);                        // signed included angle
        var radius = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
        // Center: from the chord's middle, perpendicular to it, at the distance given by the radius and the angle.
        var mx = (x1 + x2) / 2;
        var my = (y1 + y2) / 2;
        var offset = radius * Math.Cos(theta / 2);
        var sign = bulge > 0 ? 1 : -1;
        var cx = mx - sign * offset * dy / chord;
        var cy = my + sign * offset * dx / chord;
        var a1 = Math.Atan2(y1 - cy, x1 - cx);
        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(theta) / StepAngle), 2, 720);
        for (var i = 1; i <= steps; i++)
        {
            var a = a1 + theta * i / steps;
            into.Add(i == steps ? toWorld(x2, y2) : toWorld(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a)));
        }
    }

    /// <summary>A (partial) ellipse; angles are the parameters of the ellipse, not true angles.</summary>
    public static void Ellipse(List<CadPoint> into, double cx, double cy, double majorX, double majorY, double ratio,
        double start, double end)
    {
        var sweep = Sweep(start, end);
        var steps = Math.Clamp((int)Math.Ceiling(sweep / StepAngle), 8, 720);
        // The minor axis is the major one turned a quarter and shortened by the ratio.
        var minorX = -majorY * ratio;
        var minorY = majorX * ratio;
        for (var i = 0; i <= steps; i++)
        {
            var t = start + sweep * i / steps;
            into.Add(new CadPoint(cx + majorX * Math.Cos(t) + minorX * Math.Sin(t),
                cy + majorY * Math.Cos(t) + minorY * Math.Sin(t)));
        }
    }

    /// <summary>Evaluates a (rational) B-spline from its control points and knots.</summary>
    public static List<CadPoint> Spline(int degree, IReadOnlyList<double> knots, IReadOnlyList<CadPoint> controls,
        IReadOnlyList<double>? weights)
    {
        var result = new List<CadPoint>();
        var n = controls.Count;
        // Real splines are of degree 1 to 5; anything far above is damaged data, and would take forever to evaluate.
        if (degree < 1 || degree > 25 || n <= degree || knots.Count < n + degree + 1)
        {
            result.AddRange(controls);
            return result;
        }
        var from = knots[degree];
        var to = knots[n];
        if (to <= from)
        {
            result.AddRange(controls);
            return result;
        }
        var samples = Math.Clamp(n * 12, 24, 1500);
        for (var i = 0; i <= samples; i++)
        {
            var u = i == samples ? to : from + (to - from) * i / samples;
            result.Add(DeBoor(degree, knots, controls, weights, u));
        }
        return result;
    }

    private static CadPoint DeBoor(int degree, IReadOnlyList<double> knots, IReadOnlyList<CadPoint> controls,
        IReadOnlyList<double>? weights, double u)
    {
        var n = controls.Count;
        var span = degree;
        while (span < n - 1 && u >= knots[span + 1])
            span++;
        var x = new double[degree + 1];
        var y = new double[degree + 1];
        var w = new double[degree + 1];
        for (var j = 0; j <= degree; j++)
        {
            var index = span - degree + j;
            var weight = weights is { Count: > 0 } && index < weights.Count && weights[index] > 0 ? weights[index] : 1.0;
            x[j] = controls[index].X * weight;
            y[j] = controls[index].Y * weight;
            w[j] = weight;
        }
        for (var r = 1; r <= degree; r++)
        {
            for (var j = degree; j >= r; j--)
            {
                var left = knots[span - degree + j];
                var right = knots[span + 1 + j - r];
                var denominator = right - left;
                var alpha = denominator == 0 ? 0 : (u - left) / denominator;
                x[j] = (1 - alpha) * x[j - 1] + alpha * x[j];
                y[j] = (1 - alpha) * y[j - 1] + alpha * y[j];
                w[j] = (1 - alpha) * w[j - 1] + alpha * w[j];
            }
        }
        return w[degree] == 0 ? new CadPoint(x[degree], y[degree]) : new CadPoint(x[degree] / w[degree], y[degree] / w[degree]);
    }
}
