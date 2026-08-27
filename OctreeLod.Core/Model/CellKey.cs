using System;

namespace OctreeLod.Core.Model;

// Identifies one voxel cell within a node's own bbox at a given spacing
// (cellSize = bbox.Size / gridDivisions). Two points map to the same
// CellKey iff they land in the same cell at that node's LOD level — used as
// the dictionary key by both engines' per-node cell maps (GridSubsampler's
// bottom-up dedup, SpacingIngestionEngine's insertion-time accept/reject).
public readonly struct CellKey : IEquatable<CellKey>
{
    public readonly int X;
    public readonly int Y;
    public readonly int Z;

    public CellKey(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public static CellKey FromPoint(in PointRecord p, in BoundingCube bbox, double cellSize)
    {
        int x = (int)((p.X - bbox.MinX) / cellSize);
        int y = (int)((p.Y - bbox.MinY) / cellSize);
        int z = (int)((p.Z - bbox.MinZ) / cellSize);
        return new CellKey(x, y, z);
    }

    public bool Equals(CellKey other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is CellKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + X;
            hash = hash * 31 + Y;
            hash = hash * 31 + Z;
            return hash;
        }
    }
}
