namespace OctreeLod.Core;

public readonly struct BoundingCube
{
    public double MinX { get; }
    public double MinY { get; }
    public double MinZ { get; }
    public double Size { get; }

    public BoundingCube(double minX, double minY, double minZ, double size)
    {
        MinX = minX;
        MinY = minY;
        MinZ = minZ;
        Size = size;
    }

    // Half-open containment on every axis: min <= x < min + size. Must stay
    // consistent with Octant/ChildBounds below, or points can be "in bounds"
    // yet fail to land in any child during descent.
    public bool Contains(in PointRecord p) =>
        p.X >= MinX && p.X < MinX + Size &&
        p.Y >= MinY && p.Y < MinY + Size &&
        p.Z >= MinZ && p.Z < MinZ + Size;

    // Octant index in [0,7]: bit0 = X half, bit1 = Y half, bit2 = Z half.
    public int Octant(in PointRecord p)
    {
        double half = Size / 2;
        int octant = 0;
        if (p.X >= MinX + half) octant |= 1;
        if (p.Y >= MinY + half) octant |= 2;
        if (p.Z >= MinZ + half) octant |= 4;
        return octant;
    }

    public BoundingCube ChildBounds(int octant)
    {
        double half = Size / 2;
        double x = MinX + ((octant & 1) != 0 ? half : 0);
        double y = MinY + ((octant & 2) != 0 ? half : 0);
        double z = MinZ + ((octant & 4) != 0 ? half : 0);
        return new BoundingCube(x, y, z, half);
    }
}
