using System.Collections.Generic;

namespace OctreeLod.Core.Model;

// Shared structural helper used by AdaptiveRootTrimmer, MergeEngine, and
// Tiles3DExporter, so the "is this child empty" rule lives in exactly one
// place. A child is empty iff it's a leaf that never received any points —
// an internal node is never empty by construction (it was only ever created
// by an overflow, so it always has at least one non-empty descendant).
public static class OctreeStructureUtil
{
    public static bool IsEmptyChild(NodeRecord child) => child.IsLeaf && child.PointCount == 0;

    public static List<long> NonEmptyChildIds(INodeMetadataStore metadata, NodeRecord node)
    {
        var result = new List<long>();
        for (int octant = 0; octant < 8; octant++)
        {
            long childId = node.Children[octant];
            if (childId == NodeRecord.NoneId) continue;

            var child = metadata.Get(childId);
            if (IsEmptyChild(child)) continue;

            result.Add(childId);
        }
        return result;
    }
}
