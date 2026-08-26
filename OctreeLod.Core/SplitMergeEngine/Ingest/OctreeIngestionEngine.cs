using System.Collections.Generic;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.SplitMergeEngine.Ingest;

// Phase-1 orchestrator: streaming ingest into a fixed-bounds octree, never
// holding more than one leaf's worth of points (~SplitThreshold) in memory
// at a time beyond what the point store itself buffers on disk.
//
// Split cascading is iterative (explicit work-stack), not recursive: an
// overflowing leaf splits into 8 children, and any child that immediately
// overflows again (clustered data) is pushed for further splitting before
// the next incoming point is processed.
public sealed class OctreeIngestionEngine
{
    private readonly INodeMetadataStore _metadata;
    private readonly IPointBufferStore _pointStore;
    private readonly OctreeIngestionOptions _options;

    public long RootId => _metadata.RootId;

    public OctreeIngestionEngine(INodeMetadataStore metadata, IPointBufferStore pointStore, OctreeIngestionOptions options)
    {
        _metadata = metadata;
        _pointStore = pointStore;
        _options = options;

        if (_metadata.Count == 0)
        {
            var root = NodeRecord.CreateLeaf(NodeRecord.NoneId, -1, options.WorldBounds);
            long rootId = _metadata.Allocate(root);
            var stored = _metadata.Get(rootId);
            stored.Storage = _pointStore.Allocate(rootId);
            _metadata.Set(rootId, stored);
            _metadata.RootId = rootId;
        }
    }

    public void IngestBatch(IEnumerable<PointRecord> points)
    {
        foreach (var point in points) IngestPoint(point);
    }

    public void IngestPoint(PointRecord point)
    {
        if (!_options.WorldBounds.Contains(point))
        {
            _options.OnWarning?.Invoke("Point outside fixed world bounds — dropped.");
            return;
        }

        long leafId = DescendToLeaf(point);
        var leaf = _metadata.Get(leafId);

        bool stuckAtMaxDepth = leaf.PointCount >= _options.SplitThreshold
            && NodeDepthUtil.DepthOf(_metadata, leafId) >= _options.MaxSplitDepth;
        if (stuckAtMaxDepth)
        {
            _options.OnWarning?.Invoke($"Leaf {leafId} frozen at max split depth — dropping point.");
            return;
        }

        _pointStore.Append(leaf.Storage, (int)leaf.PointCount, point);
        leaf.PointCount++;
        _metadata.Set(leafId, leaf);

        if (leaf.PointCount >= _options.SplitThreshold)
            ProcessOverflow(leafId);
    }

    private long DescendToLeaf(PointRecord point)
    {
        long currentId = _metadata.RootId;
        while (true)
        {
            var node = _metadata.Get(currentId);
            if (node.IsLeaf) return currentId;
            int octant = node.Bbox.Octant(point);
            currentId = node.Children[octant];
        }
    }

    private void ProcessOverflow(long startNodeId)
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
            node.Children[octant] = childId;
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
