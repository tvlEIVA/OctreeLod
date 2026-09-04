using System.Collections.Generic;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.Export;

// Pure tile-geometry math shared between Tiles3DExporter (file-based export)
// and any other tileset builder (e.g. an HTTP server generating tiles live,
// on the fly, straight from the in-memory octree) — kept in one place so the
// two can't silently drift apart on the boundingVolume/geometricError
// formulas.
public static class TileGeometry
{
    // Spacing between representative samples at this level — the spacing
    // engine applies the same grid-quantized acceptance rule at every node
    // regardless of leaf status, so a leaf's points are exactly as spaced
    // as any other node's; there's no "raw, unsampled" leaf case that would
    // justify a zero-error special case (a real PotreeConverter/Cesium
    // dataset keeps halving all the way to its leaves too, never zero).
    public static double GeometricError(OctreeNode node, int gridDivisions) =>
        node.Bbox.Size / gridDivisions;

    // Axis-aligned cube -> 3D Tiles `box`: center + 3 half-axis vectors.
    // Our cubes make this the trivial diagonal case.
    public static List<object> BuildBox(BoundingCube bbox)
    {
        double half = bbox.Size / 2;
        double cx = bbox.MinX + half, cy = bbox.MinY + half, cz = bbox.MinZ + half;
        return new List<object>
        {
            (object)cx, (object)cy, (object)cz,
            (object)half, (object)0.0, (object)0.0,
            (object)0.0, (object)half, (object)0.0,
            (object)0.0, (object)0.0, (object)half,
        };
    }
}
