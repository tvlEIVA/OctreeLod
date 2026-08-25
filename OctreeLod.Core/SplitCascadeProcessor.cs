using System.Collections.Generic;

namespace OctreeLod.Core;

// Iterative (explicit work-stack), not recursive: an overflowing leaf splits
// into 8 children, and any child that immediately overflows again (clustered
// data) is pushed for further splitting before the next incoming point is
// processed.
public sealed class SplitCascadeProcessor
{
    private readonly INodeMetadataStore _metadata;
    private readonly IPointBufferStore _pointStore;
    private readonly OctreeIngestionOptions _options;

    public SplitCascadeProcessor(INodeMetadataStore metadata, IPointBufferStore pointStore, OctreeIngestionOptions options)
    {
        _metadata = metadata;
        _pointStore = pointStore;
        _options = options;
    }

    public void ProcessOverflow(long startNodeId)
    {
        var stack = new Stack<long>();
        stack.Push(startNodeId);

        while (stack.Count > 0)
        {
            long nodeId = stack.Pop();
            var node = _metadata.Get(nodeId);
            if (!node.IsLeaf || node.PointCount < _options.SplitThreshold) continue;

            int depth = NodeDepthUtil.DepthOf(_metadata, nodeId);
            if (depth >= _options.MaxSplitDepth)
            {
                // Documented, deliberate violation: a pathological
                // (near-)duplicate point cluster that spatial splitting can
                // never separate. The leaf stays oversized; IngestPoint stops
                // routing further points here once this state is reached.
                _options.OnWarning?.Invoke(
                    $"Leaf {nodeId} exceeded split threshold at max depth {_options.MaxSplitDepth}; accepting oversized leaf.");
                continue;
            }

            SplitLeaf(nodeId, node, stack);
        }
    }

    private void SplitLeaf(long nodeId, NodeRecord node, Stack<long> stack)
    {
        var buffered = _pointStore.ReadAll(node.Storage, (int)node.PointCount);
        _pointStore.Free(node.Storage);

        node.IsLeaf = false;
        node.Storage = StorageLocator.None;

        var childIds = new long[8];
        for (int octant = 0; octant < 8; octant++)
        {
            var childBbox = node.Bbox.ChildBounds(octant);
            var child = NodeRecord.CreateLeaf(nodeId, octant, childBbox);
            long childId = _metadata.Allocate(child);
            var stored = _metadata.Get(childId);
            stored.Storage = _pointStore.Allocate(childId);
            _metadata.Set(childId, stored);

            childIds[octant] = childId;
            node.SetChild(octant, childId);
        }
        _metadata.Set(nodeId, node);

        var childCounts = new int[8];
        foreach (var p in buffered)
        {
            int octant = node.Bbox.Octant(p);
            long childId = childIds[octant];
            var child = _metadata.Get(childId);
            _pointStore.Append(child.Storage, childCounts[octant], p);
            childCounts[octant]++;
            child.PointCount = childCounts[octant];
            _metadata.Set(childId, child);
        }

        for (int octant = 0; octant < 8; octant++)
        {
            if (childCounts[octant] >= _options.SplitThreshold)
                stack.Push(childIds[octant]);
        }
    }
}
