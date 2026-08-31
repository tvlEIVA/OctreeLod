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

    // Locality fast path. A node's cell being occupied for a point implies
    // every ancestor's corresponding cell is occupied too: a child cell
    // bucket always nests inside exactly one specific parent cell bucket
    // (fixed by the cell-index arithmetic, regardless of exact position
    // within it), so whoever occupies that child bucket was necessarily
    // rejected by that same parent bucket first. The converse holds too:
    // free at a node means free at every descendant (nothing could have
    // reached a deeper bucket without this one being occupied first). So
    // for a single point, the occupied/free sequence from Root down is
    // exactly "occupied...occupied, free...free" with one transition, and
    // that transition is the correct acceptance level — reachable by
    // climbing from the last-touched node instead of re-walking from Root.
    private OctreeNode? _lastNode;

    // Count of currently-dirty nodes (OctreeNode.Dirty == true) — tracked
    // incrementally rather than by walking the tree, so a caller (a preview
    // exporter deciding when enough new work has piled up to be worth a
    // pass) can check it in O(1). Goes up on every false->true Dirty
    // transition (a brand-new node, or an existing clean node accepting
    // another point); an external caller who clears some nodes' Dirty flags
    // back to false (see Tiles3DExporter/OctreeNode.Dirty) reports that back
    // via NotifyNodesClean so the count stays accurate.
    private long _dirtyNodeCount;

    public OctreeNode Root { get; }
    public long NodeCount => _nodeCount;
    public long DirtyNodeCount => _dirtyNodeCount;

    public SpacingIngestionEngine(INodePointStore nodeStore, SpacingIngestionOptions options)
    {
        _options = options;
        _cache = new PagedCellMapCache(nodeStore, options.MaxInMemoryNodes);

        Root = OctreeNode.CreateRoot(options.WorldBounds);
        _nodeCount = 1;
        _dirtyNodeCount = 1; // Root starts Dirty (see OctreeNode)
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

        var node = ClosestStartingNode(point);
        while (true)
        {
            double cellSize = node.Bbox.Size / _options.GridDivisions;
            var key = CellKey.FromPoint(point, node.Bbox, cellSize);

            var cells = _cache.Touch(node.Id, node.Bbox, cellSize);
            if (!cells.ContainsKey(key))
            {
                cells[key] = point;
                node.PointCount++;
                if (!node.Dirty) { node.Dirty = true; _dirtyNodeCount++; }
                _lastNode = node;
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
                _lastNode = node;
                return;
            }

            int octant = node.Bbox.Octant(point);
            node = EnsureChild(node, octant);
        }
    }

    // Finds the correct starting node for the descent loop: climb from the
    // last-touched node toward Root only as long as cells keep coming back
    // free, stopping at the first occupied ancestor (or Root). See the
    // class-level comment on `_lastNode` for why this lands on the same
    // node a fresh Root-down walk would eventually reach.
    private OctreeNode ClosestStartingNode(in PointRecord point)
    {
        var node = _lastNode ?? Root;
        while (!node.Bbox.Contains(point)) node = node.Parent!; // Root always contains any in-bounds point, so this always terminates

        if (IsOccupied(node, point)) return node; // ancestors guaranteed occupied too (see proof above) — descend from here

        while (node.Parent != null && !IsOccupied(node.Parent, point))
            node = node.Parent;

        return node;
    }

    private bool IsOccupied(OctreeNode node, in PointRecord point)
    {
        double cellSize = node.Bbox.Size / _options.GridDivisions;
        var key = CellKey.FromPoint(point, node.Bbox, cellSize);
        return _cache.Touch(node.Id, node.Bbox, cellSize).ContainsKey(key);
    }

    private OctreeNode EnsureChild(OctreeNode node, int octant)
    {
        var child = node.Children[octant];
        if (child != null) return child;

        var childBbox = node.Bbox.ChildBounds(octant);
        child = OctreeNode.CreateChild(node, octant, childBbox);
        _nodeCount++;
        _dirtyNodeCount++; // new node starts Dirty (see OctreeNode)

        node.IsLeaf = false;
        node.Children[octant] = child;

        return child;
    }

    // Persists whatever's still in memory in the cache once the stream ends
    // — everything already evicted mid-run is on disk already.
    public void Flush() => _cache.Flush();

    // Called by a preview exporter after it clears some nodes' Dirty flags
    // back to false (see PreviewExporter.dirtyPairs), so DirtyNodeCount
    // stays accurate for the next threshold check. Clamped at zero: a node
    // ingestion re-dirtied concurrently, between the exporter reading its
    // snapshot and clearing it, was already counted again by IngestPoint's
    // own increment — this call must not double-subtract it.
    public void NotifyNodesClean(long count)
    {
        _dirtyNodeCount = System.Math.Max(0, _dirtyNodeCount - count);
    }
}
