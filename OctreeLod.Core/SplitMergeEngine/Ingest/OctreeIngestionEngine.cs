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
    private readonly IPointBufferStore _pointStore;
    private readonly OctreeIngestionOptions _options;
    private long _nodeCount;

    public OctreeNode Root { get; }
    public long NodeCount => _nodeCount;

    public OctreeIngestionEngine(IPointBufferStore pointStore, OctreeIngestionOptions options)
    {
        _pointStore = pointStore;
        _options = options;

        Root = OctreeNode.CreateRoot(options.WorldBounds);
        _nodeCount = 1;
        Root.Storage = _pointStore.Allocate(Root.Id);
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

        var leaf = DescendToLeaf(point);

        bool stuckAtMaxDepth = leaf.PointCount >= _options.SplitThreshold
            && NodeDepthUtil.DepthOf(leaf) >= _options.MaxSplitDepth;
        if (stuckAtMaxDepth)
        {
            _options.OnWarning?.Invoke($"Leaf {leaf.Id} frozen at max split depth — dropping point.");
            return;
        }

        _pointStore.Append(leaf.Storage, (int)leaf.PointCount, point);
        leaf.PointCount++;

        if (leaf.PointCount >= _options.SplitThreshold)
            ProcessOverflow(leaf);
    }

    private OctreeNode DescendToLeaf(PointRecord point)
    {
        var node = Root;
        while (!node.IsLeaf)
        {
            int octant = node.Bbox.Octant(point);
            node = node.Children[octant]!;
        }
        return node;
    }

    private void ProcessOverflow(OctreeNode startNode)
    {
        var stack = new Stack<OctreeNode>();
        stack.Push(startNode);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!node.IsLeaf || node.PointCount < _options.SplitThreshold) continue;

            int depth = NodeDepthUtil.DepthOf(node);
            if (depth >= _options.MaxSplitDepth)
            {
                // Documented, deliberate violation: a pathological
                // (near-)duplicate point cluster that spatial splitting can
                // never separate. The leaf stays oversized; IngestPoint stops
                // routing further points here once this state is reached.
                _options.OnWarning?.Invoke(
                    $"Leaf {node.Id} exceeded split threshold at max depth {_options.MaxSplitDepth}; accepting oversized leaf.");
                continue;
            }

            SplitLeaf(node, stack);
        }
    }

    private void SplitLeaf(OctreeNode node, Stack<OctreeNode> stack)
    {
        var buffered = _pointStore.ReadAll(node.Storage, (int)node.PointCount);
        _pointStore.Free(node.Storage);

        node.IsLeaf = false;
        node.Storage = StorageLocator.None;

        var children = new OctreeNode[8];
        for (int octant = 0; octant < 8; octant++)
        {
            var childBbox = node.Bbox.ChildBounds(octant);
            var child = OctreeNode.CreateChild(node, octant, childBbox);
            _nodeCount++;
            child.Storage = _pointStore.Allocate(child.Id);

            children[octant] = child;
            node.Children[octant] = child;
        }

        var childCounts = new int[8];
        foreach (var p in buffered)
        {
            int octant = node.Bbox.Octant(p);
            var child = children[octant];
            _pointStore.Append(child.Storage, childCounts[octant], p);
            childCounts[octant]++;
            child.PointCount = childCounts[octant];
        }

        for (int octant = 0; octant < 8; octant++)
        {
            if (childCounts[octant] >= _options.SplitThreshold)
                stack.Push(children[octant]);
        }
    }
}
