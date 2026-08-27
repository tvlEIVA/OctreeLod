using System.Collections.Generic;

namespace OctreeLod.Core.Model;

// Shared structural helper used by AdaptiveRootTrimmer, MergeEngine, and
// Tiles3DExporter, so the "is this child empty" rule lives in exactly one
// place. A child is empty iff it's a leaf that never received any points —
// an internal node is never empty by construction (it was only ever created
// by an overflow, so it always has at least one non-empty descendant).
public static class OctreeStructureUtil
{
    public static bool IsEmptyChild(OctreeNode child) => child.IsLeaf && child.PointCount == 0;

    public static List<OctreeNode> NonEmptyChildren(OctreeNode node)
    {
        var result = new List<OctreeNode>();
        for (int octant = 0; octant < 8; octant++)
        {
            var child = node.Children[octant];
            if (child == null) continue;
            if (IsEmptyChild(child)) continue;

            result.Add(child);
        }
        return result;
    }
}
