using System;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.Export;

// Builds the 3D Tiles root `transform`: a 16-value column-major 4x4 matrix
// mapping the tileset's local East/North/Up meters (at a GeoReference point)
// to ECEF (Earth-Centered, Earth-Fixed) meters — the standard way to place a
// non-georeferenced local point cloud at a real spot on the WGS84 ellipsoid.
internal static class EcefTransform
{
    private const double SemiMajorAxis = 6378137.0; // WGS84 a
    private const double Flattening = 1.0 / 298.257223563; // WGS84 f
    private const double EccentricitySquared = Flattening * (2.0 - Flattening);

    public static double[] ComputeLocalToEcefMatrix(GeoReference reference)
    {
        double lat = reference.LatitudeDegrees * Math.PI / 180.0;
        double lon = reference.LongitudeDegrees * Math.PI / 180.0;
        double h = reference.HeightMeters;

        double sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
        double sinLon = Math.Sin(lon), cosLon = Math.Cos(lon);

        double primeVerticalRadius = SemiMajorAxis / Math.Sqrt(1 - EccentricitySquared * sinLat * sinLat);
        double originX = (primeVerticalRadius + h) * cosLat * cosLon;
        double originY = (primeVerticalRadius + h) * cosLat * sinLon;
        double originZ = (primeVerticalRadius * (1 - EccentricitySquared) + h) * sinLat;

        // East/North/Up basis vectors at (lat, lon), expressed in ECEF.
        double eastX = -sinLon, eastY = cosLon, eastZ = 0.0;
        double northX = -sinLat * cosLon, northY = -sinLat * sinLon, northZ = cosLat;
        double upX = cosLat * cosLon, upY = cosLat * sinLon, upZ = sinLat;

        // Column-major: columns are [East, North, Up, Origin].
        return new[]
        {
            eastX, eastY, eastZ, 0.0,
            northX, northY, northZ, 0.0,
            upX, upY, upZ, 0.0,
            originX, originY, originZ, 1.0,
        };
    }
}
