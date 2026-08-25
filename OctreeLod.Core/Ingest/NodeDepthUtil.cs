using OctreeLod.Core.Model;

namespace OctreeLod.Core.Ingest;

internal static class NodeDepthUtil
{
    // Depth is never stored on a node (would go stale under any future
    // structural change) — walk the parent chain on demand. Only called on
    // overflow events, which are already bounded in number.
    public static int DepthOf(INodeMetadataStore metadata, long nodeId)
    {
        int depth = 0;
        var node = metadata.Get(nodeId);
        while (node.ParentId != NodeRecord.NoneId)
        {
            depth++;
            node = metadata.Get(node.ParentId);
        }
        return depth;
    }
}
