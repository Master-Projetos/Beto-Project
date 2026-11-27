namespace ListaMateriais.Cad;

/// <summary>
/// Converts UTM coordinates (meters) to latitude and longitude in degrees, with the Krüger series
/// (millimeter accuracy). SIRGAS 2000 and WGS 84 are treated as the same datum: they differ by less than a meter.
/// Older Brazilian drawings are in SAD 69, some 60 m away; <see cref="Sad69ToSirgas"/> brings them over.
/// </summary>
internal static class Utm
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257222101;       // GRS 80, the ellipsoid of SIRGAS 2000 (WGS 84 differs by 0.1 mm)
    private const double Sad69A = 6378160.0;
    private const double Sad69F = 1 / 298.25;          // the South American 1969 ellipsoid
    private const double K0 = 0.9996;

    /// <summary>An ellipsoid with the terms of the series worked out.</summary>
    private sealed class Ellipsoid
    {
        public Ellipsoid(double a, double f)
        {
            var n = f / (2 - f);
            Rectifying = a / (1 + n) * (1 + n * n / 4 + Math.Pow(n, 4) / 64);
            Beta =
            [
                n / 2 - 2 * n * n / 3 + 37 * Math.Pow(n, 3) / 96 - Math.Pow(n, 4) / 360,
                n * n / 48 + Math.Pow(n, 3) / 15 - 437 * Math.Pow(n, 4) / 1440,
                17 * Math.Pow(n, 3) / 480 - 37 * Math.Pow(n, 4) / 840,
                4397 * Math.Pow(n, 4) / 161280,
            ];
            Delta =
            [
                2 * n - 2 * n * n / 3 - 2 * Math.Pow(n, 3) + 116 * Math.Pow(n, 4) / 45,
                7 * n * n / 3 - 8 * Math.Pow(n, 3) / 5 - 227 * Math.Pow(n, 4) / 45,
                56 * Math.Pow(n, 3) / 15 - 136 * Math.Pow(n, 4) / 35,
                4279 * Math.Pow(n, 4) / 630,
            ];
        }

        public double Rectifying { get; }

        public double[] Beta { get; }

        public double[] Delta { get; }
    }

    private static readonly Ellipsoid Grs80 = new(A, F);
    private static readonly Ellipsoid Sad69 = new(Sad69A, Sad69F);

    /// <param name="zone">UTM zone, 1 to 60.</param>
    /// <param name="south">True for the southern hemisphere (all of Brazil's south of the equator).</param>
    /// <param name="sad69">The coordinates are in SAD 69; the result is still in SIRGAS 2000.</param>
    /// <returns>Latitude and longitude in degrees.</returns>
    public static (double Latitude, double Longitude) ToLatLon(int zone, bool south, double easting, double northing,
        bool sad69 = false)
    {
        var ellipsoid = sad69 ? Sad69 : Grs80;
        var xi = (northing - (south ? 10_000_000.0 : 0.0)) / (K0 * ellipsoid.Rectifying);
        var eta = (easting - 500_000.0) / (K0 * ellipsoid.Rectifying);
        double xiPrime = xi, etaPrime = eta;
        for (var j = 1; j <= 4; j++)
        {
            xiPrime -= ellipsoid.Beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            etaPrime -= ellipsoid.Beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }
        var chi = Math.Asin(Math.Sin(xiPrime) / Math.Cosh(etaPrime));
        var latitude = chi;
        for (var j = 1; j <= 4; j++)
            latitude += ellipsoid.Delta[j - 1] * Math.Sin(2 * j * chi);
        var centralMeridian = (zone * 6 - 183) * Math.PI / 180;
        var longitude = centralMeridian + Math.Atan2(Math.Sinh(etaPrime), Math.Cos(xiPrime));
        var degrees = (Latitude: latitude * 180 / Math.PI, Longitude: longitude * 180 / Math.PI);
        return sad69 ? Sad69ToSirgas(degrees.Latitude, degrees.Longitude) : degrees;
    }

    /// <summary>
    /// SAD 69 to SIRGAS 2000, in degrees, with the official shift of IBGE: the point goes to coordinates from
    /// the center of the Earth, moves, and comes back on the new ellipsoid.
    /// </summary>
    public static (double Latitude, double Longitude) Sad69ToSirgas(double latitude, double longitude)
    {
        var lat = latitude * Math.PI / 180;
        var lon = longitude * Math.PI / 180;
        var e2 = Sad69F * (2 - Sad69F);
        var radius = Sad69A / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
        var x = radius * Math.Cos(lat) * Math.Cos(lon) - 67.35;
        var y = radius * Math.Cos(lat) * Math.Sin(lon) + 3.88;
        var z = radius * (1 - e2) * Math.Sin(lat) - 38.22;

        e2 = F * (2 - F);
        var p = Math.Sqrt(x * x + y * y);
        var result = Math.Atan2(z, p * (1 - e2));
        for (var i = 0; i < 5; i++)
        {
            var sin = Math.Sin(result);
            result = Math.Atan2(z + e2 * A / Math.Sqrt(1 - e2 * sin * sin) * sin, p);
        }
        return (result * 180 / Math.PI, Math.Atan2(y, x) * 180 / Math.PI);
    }
}
