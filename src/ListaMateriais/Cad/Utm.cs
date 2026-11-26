namespace ListaMateriais.Cad;

/// <summary>
/// Converts UTM coordinates (meters) to latitude and longitude in degrees, with the Krüger series
/// (millimeter accuracy). SIRGAS 2000 and WGS 84 are treated as the same datum: they differ by less than a meter.
/// </summary>
internal static class Utm
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257222101;       // GRS 80, the ellipsoid of SIRGAS 2000 (WGS 84 differs by 0.1 mm)
    private const double K0 = 0.9996;

    private static readonly double N = F / (2 - F);
    private static readonly double Rectifying = A / (1 + N) * (1 + N * N / 4 + Math.Pow(N, 4) / 64);

    private static readonly double[] Beta =
    [
        N / 2 - 2 * N * N / 3 + 37 * Math.Pow(N, 3) / 96 - Math.Pow(N, 4) / 360,
        N * N / 48 + Math.Pow(N, 3) / 15 - 437 * Math.Pow(N, 4) / 1440,
        17 * Math.Pow(N, 3) / 480 - 37 * Math.Pow(N, 4) / 840,
        4397 * Math.Pow(N, 4) / 161280,
    ];

    private static readonly double[] Delta =
    [
        2 * N - 2 * N * N / 3 - 2 * Math.Pow(N, 3) + 116 * Math.Pow(N, 4) / 45,
        7 * N * N / 3 - 8 * Math.Pow(N, 3) / 5 - 227 * Math.Pow(N, 4) / 45,
        56 * Math.Pow(N, 3) / 15 - 136 * Math.Pow(N, 4) / 35,
        4279 * Math.Pow(N, 4) / 630,
    ];

    /// <param name="zone">UTM zone, 1 to 60.</param>
    /// <param name="south">True for the southern hemisphere (all of Brazil's south of the equator).</param>
    /// <returns>Latitude and longitude in degrees.</returns>
    public static (double Latitude, double Longitude) ToLatLon(int zone, bool south, double easting, double northing)
    {
        var xi = (northing - (south ? 10_000_000.0 : 0.0)) / (K0 * Rectifying);
        var eta = (easting - 500_000.0) / (K0 * Rectifying);
        double xiPrime = xi, etaPrime = eta;
        for (var j = 1; j <= 4; j++)
        {
            xiPrime -= Beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            etaPrime -= Beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
        }
        var chi = Math.Asin(Math.Sin(xiPrime) / Math.Cosh(etaPrime));
        var latitude = chi;
        for (var j = 1; j <= 4; j++)
            latitude += Delta[j - 1] * Math.Sin(2 * j * chi);
        var centralMeridian = (zone * 6 - 183) * Math.PI / 180;
        var longitude = centralMeridian + Math.Atan2(Math.Sinh(etaPrime), Math.Cos(xiPrime));
        return (latitude * 180 / Math.PI, longitude * 180 / Math.PI);
    }
}
