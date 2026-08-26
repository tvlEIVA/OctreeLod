using System.Collections.Generic;
using System.Linq;
using OctreeLod.Core.Model;
using OctreeLod.Core.SplitMergeEngine.Ingest;

namespace OctreeLod.Core.SpacingEngine;

// Single-pass streaming LOD build: a point is accepted into the first node
// (walking down from root) where it lands in an unoccupied spacing cell;
// otherwise it's pushed into the correct child (created lazily, one octant
// at a time — not the eager all-8 split OctreeIngestionEngine does) and the
// check repeats one level down. Every node's accepted-point dictionary IS
// its final representative set once ingestion ends — no separate bottom-up
// merge/subsample pass needed (contrast OctreeIngestionEngine + MergeEngine).
//
// Out-of-core: a point for any node (even one near the root) can arrive at
// any time until the stream ends, so no node's accepted-point set can be
// considered "final" and dropped mid-run. Instead, node point sets are
// paged: only the `MaxResidentNodes` most-recently-touched nodes' cell
// dictionaries are held in RAM at once (LRU); every other node's data lives
// in `nodeStore` on disk and is reloaded (re-keyed by cell, from the raw
// point list — no separate index needed) the next time a point routes
// through it. Ancestors near the root are touched by every single point and
// so stay resident naturally; deep/leaf nodes cool off and page out once the
// stream moves past their region.
public sealed class SpacingIngestionEngine
{
    private readonly INodeMetadataStore _metadata;
    private readonly INodePointStore _nodeStore;
    private readonly SpacingIngestionOptions _options;

    private readonly Dictionary<long, Dictionary<(int, int, int), PointRecord>> _resident =
        new Dictionary<long, Dictionary<(int, int, int), PointRecord>>();
    private readonly LinkedList<long> _lru = new LinkedList<long>(); // most-recently-used at the front
    private readonly Dictionary<long, LinkedListNode<long>> _lruNodes = new Dictionary<long, LinkedListNode<long>>();

    public long RootId => _metadata.RootId;

    public SpacingIngestionEngine(INodeMetadataStore metadata, INodePointStore nodeStore, SpacingIngestionOptions options)
    {
        _metadata = metadata;
        _nodeStore = nodeStore;
        _options = options;

        if (_metadata.Count == 0)
        {
            var root = NodeRecord.CreateLeaf(NodeRecord.NoneId, -1, options.WorldBounds);
            long rootId = _metadata.Allocate(root);
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

        long nodeId = _metadata.RootId;
        while (true)
        {
            var node = _metadata.Get(nodeId);
            double cellSize = node.Bbox.Size / _options.GridDivisions;
            var key = CellKey(point, node.Bbox, cellSize);

            var cells = Touch(nodeId, node.Bbox, cellSize);
            if (!cells.ContainsKey(key))
            {
                cells[key] = point;
                node.PointCount++;
                _metadata.Set(nodeId, node);
                return;
            }

            int depth = NodeDepthUtil.DepthOf(_metadata, nodeId);
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
                    $"Node {nodeId} hit max split depth {_options.MaxSplitDepth} resolving a spacing collision; dropping point.");
                return;
            }

            int octant = node.Bbox.Octant(point);
            nodeId = EnsureChild(nodeId, node, octant);
        }
    }

    private long EnsureChild(long nodeId, NodeRecord node, int octant)
    {
        long childId = node.Children[octant];
        if (childId != NodeRecord.NoneId) return childId;

        var childBbox = node.Bbox.ChildBounds(octant);
        var child = NodeRecord.CreateLeaf(nodeId, octant, childBbox);
        childId = _metadata.Allocate(child);

        node.IsLeaf = false;
        node.Children[octant] = childId;
        _metadata.Set(nodeId, node);

        return childId;
    }

    // Returns the node's live cell dictionary, loading it from disk (a
    // brand-new node just yields an empty read) if it isn't resident, and
    // marks it most-recently-used. May evict some other node to disk to stay
    // within MaxResidentNodes.
    private Dictionary<(int, int, int), PointRecord> Touch(long nodeId, in BoundingCube bbox, double cellSize)
    {
        if (_resident.TryGetValue(nodeId, out var cells))
        {
            var lruNode = _lruNodes[nodeId];
            if (lruNode != _lru.First)
            {
                _lru.Remove(lruNode);
                _lru.AddFirst(lruNode);
            }
            return cells;
        }

        cells = new Dictionary<(int, int, int), PointRecord>();
        foreach (var p in _nodeStore.ReadAll(nodeId))
            cells[CellKey(p, bbox, cellSize)] = p;

        _resident[nodeId] = cells;
        _lruNodes[nodeId] = _lru.AddFirst(nodeId);

        EvictIfOverCapacity();
        return cells;
    }

    private void EvictIfOverCapacity()
    {
        while (_resident.Count > _options.MaxResidentNodes && _lru.Last != null)
        {
            long evictId = _lru.Last.Value;
            _lru.RemoveLast();
            _lruNodes.Remove(evictId);

            var cells = _resident[evictId];
            _resident.Remove(evictId);
            _nodeStore.WriteAll(evictId, cells.Values.ToArray());
        }
    }

    private static (int, int, int) CellKey(in PointRecord p, in BoundingCube bbox, double cellSize)
    {
        int cx = (int)((p.X - bbox.MinX) / cellSize);
        int cy = (int)((p.Y - bbox.MinY) / cellSize);
        int cz = (int)((p.Z - bbox.MinZ) / cellSize);
        return (cx, cy, cz);
    }

    // Writes out whatever's still resident once the stream ends — everything
    // already evicted mid-run is on disk already. After this call, every
    // node's complete accepted-point set is readable from `nodeStore`.
    public void Flush()
    {
        foreach (var pair in _resident)
            _nodeStore.WriteAll(pair.Key, pair.Value.Values.ToArray());
        _resident.Clear();
        _lru.Clear();
        _lruNodes.Clear();
    }
}
