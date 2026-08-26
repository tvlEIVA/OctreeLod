namespace OctreeLod.Core.Model;

// Anchors a tileset's local Cartesian frame (the same X/Y/Z the octree was
// built in) to a real position on the WGS84 ellipsoid, so 3D Tiles viewers
// place it on the globe instead of defaulting to ECEF-interpreted raw local
// coordinates (which, for typical local-frame magnitudes, land the whole
// dataset a few hundred km from Earth's center — nowhere near the surface).
// Local X/Y/Z are treated as East/North/Up offsets in meters from this point
// — if a dataset's Z axis is actually "depth" (positive downward, as in the
// "easting northing depth" input format), it needs negating before being
// treated as ENU "up"; not needed here since HeightMeters covers a nonzero
// ellipsoidal offset for the reference point itself, not a per-point sign.
public readonly struct GeoReference
{
    public double LatitudeDegrees { get; }
    public double LongitudeDegrees { get; }
    public double HeightMeters { get; }

    public GeoReference(double latitudeDegrees, double longitudeDegrees, double heightMeters)
    {
        LatitudeDegrees = latitudeDegrees;
        LongitudeDegrees = longitudeDegrees;
        HeightMeters = heightMeters;
    }
}
