using OctreeLod.Core.Model;

namespace OctreeLod.Core.SplitMergeEngine.Ingest;

internal static class NodeDepthUtil
{
    // Depth is never stored on a node (would go stale under any future
    // structural change) — walk the parent chain on demand. Only called on
    // overflow events, which are already bounded in number.
    public static int DepthOf(OctreeNode node)
    {
        int depth = 0;
        while (node.Parent != null)
        {
            depth++;
            node = node.Parent;
        }
        return depth;
    }
}
