using System.Collections.Generic;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.SpacingEngine;

// Single-pass streaming LOD build: a point is accepted into the first node
// (walking down from root) where it lands in an unoccupied spacing cell;
// otherwise it's pushed into the correct child (created lazily, one octant
// at a time — not the eager all-8 split OctreeIngestionEngine does) and the
// check repeats one level down. Every node's accepted-point dictionary IS
// its final representative set once ingestion ends — no separate bottom-up
// merge/subsample pass needed (contrast OctreeIngestionEngine + MergeEngine).
//
// Out-of-core node paging (only `MaxInMemoryNodes` nodes' cell maps held in
// RAM at once, LRU) is handled entirely by PagedCellMapCache — this class
// only owns the octree descent/accept/reject algorithm.
public sealed class SpacingIngestionEngine
{
    private readonly SpacingIngestionOptions _options;
    private readonly PagedCellMapCache _cache;
    private long _nodeCount;

    public OctreeNode Root { get; }
    public long NodeCount => _nodeCount;

    public SpacingIngestionEngine(INodePointStore nodeStore, SpacingIngestionOptions options)
    {
        _options = options;
        _cache = new PagedCellMapCache(nodeStore, options.MaxInMemoryNodes);

        Root = OctreeNode.CreateRoot(options.WorldBounds);
        _nodeCount = 1;
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

        var node = Root;
        while (true)
        {
            double cellSize = node.Bbox.Size / _options.GridDivisions;
            var key = CellKey.FromPoint(point, node.Bbox, cellSize);

            var cells = _cache.Touch(node.Id, node.Bbox, cellSize);
            if (!cells.ContainsKey(key))
            {
                cells[key] = point;
                node.PointCount++;
                return;
            }

            int depth = node.Depth;
            if (depth >= _options.MaxSplitDepth)
            {
                // Documented, deliberate violation: a pathological
                // (near-)duplicate point cluster that spatial splitting can
                // never separate. Mirrors OctreeIngestionEngine's oversized
                // leaf handling, except here the point itself is dropped
                // rather than the node accepting it oversized, since a
                // spacing node has no "oversized" concept — its cell map
                // just stays as-is.
                _options.OnWarning?.Invoke(
                    $"Node {node.Id} hit max split depth {_options.MaxSplitDepth} resolving a spacing collision; dropping point.");
                return;
            }

            int octant = node.Bbox.Octant(point);
            node = EnsureChild(node, octant);
        }
    }

    private OctreeNode EnsureChild(OctreeNode node, int octant)
    {
        var child = node.Children[octant];
        if (child != null) return child;

        var childBbox = node.Bbox.ChildBounds(octant);
        child = OctreeNode.CreateChild(node, octant, childBbox);
        _nodeCount++;

        node.IsLeaf = false;
        node.Children[octant] = child;

        return child;
    }

    // Persists whatever's still in memory in the cache once the stream ends
    // — everything already evicted mid-run is on disk already.
    public void Flush() => _cache.Flush();
}
