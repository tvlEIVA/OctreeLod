using System.Collections.Generic;

namespace OctreeLod.Core;

// Phase-1 orchestrator: streaming ingest into a fixed-bounds octree, never
// holding more than one leaf's worth of points (~SplitThreshold) in memory
// at a time beyond what the point store itself buffers on disk.
public sealed class OctreeIngestionEngine
{
    private readonly INodeMetadataStore _metadata;
    private readonly IPointBufferStore _pointStore;
    private readonly OctreeIngestionOptions _options;
    private readonly SplitCascadeProcessor _cascade;

    public long RootId => _metadata.RootId;

    public OctreeIngestionEngine(INodeMetadataStore metadata, IPointBufferStore pointStore, OctreeIngestionOptions options)
    {
        _metadata = metadata;
        _pointStore = pointStore;
        _options = options;
        _cascade = new SplitCascadeProcessor(metadata, pointStore, options);

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
            _cascade.ProcessOverflow(leafId);
    }

    private long DescendToLeaf(PointRecord point)
    {
        long currentId = _metadata.RootId;
        while (true)
        {
            var node = _metadata.Get(currentId);
            if (node.IsLeaf) return currentId;
            int octant = node.Bbox.Octant(point);
            currentId = node.GetChild(octant);
        }
    }
}
