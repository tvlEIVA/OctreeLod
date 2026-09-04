using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.SpacingEngine;

// LRU-paged cache of per-node cell->point maps, backed by an
// INodePointStore. Bounds RAM to `capacity` in-memory nodes regardless of
// tree size — a node not currently in memory is transparently reloaded from
// the store on the next Touch, re-keyed against the caller-supplied
// bbox/cellSize (deterministic from position alone, so no separate index
// needs to be persisted). Callers mutate the returned dictionary directly
// (e.g. accept a point into a cell); Flush() persists whatever's still in
// memory once the caller is done.
//
// Out-of-core rationale: a node's data can never be considered "final" and
// dropped mid-run — a point for it (even one near the root) can arrive at
// any time until the caller stops touching this cache. So eviction only
// ever moves a node's data between RAM and disk, never discards it.
public sealed class PagedCellMapCache
{
    private readonly INodePointStore _store;
    private readonly int _capacity;

    // The recency list holds these directly (not just nodeId) so evicting
    // the tail reads its Cells straight off the list node — no second
    // lookup back into _lruIndex to find the payload.
    private sealed class CellMap
    {
        public readonly BigInteger NodeId;
        public readonly Dictionary<CellKey, PointRecord> Cells = new Dictionary<CellKey, PointRecord>();

        public CellMap(BigInteger nodeId) => NodeId = nodeId;
    }

    // _lruList is the recency order itself (front = most recent, back = next
    // to evict). _lruIndex maps nodeId -> its node in _lruList, for O(1) "is this
    // in memory, and where" on Touch.
    private readonly LinkedList<CellMap> _lruList = new LinkedList<CellMap>();
    private readonly Dictionary<BigInteger, LinkedListNode<CellMap>> _lruIndex = new Dictionary<BigInteger, LinkedListNode<CellMap>>();

    public PagedCellMapCache(INodePointStore store, int capacity)
    {
        _store = store;
        _capacity = capacity;
    }

    // Returns the node's live cell dictionary, loading it from disk (a
    // brand-new node just yields an empty read) if it isn't in memory, and
    // marks it most-recently-used. May evict some other node to disk to stay
    // within capacity.
    public Dictionary<CellKey, PointRecord> Touch(BigInteger nodeId, in BoundingCube bbox, double cellSize)
    {
        if (_lruIndex.TryGetValue(nodeId, out var listNode))
        {
            if (listNode != _lruList.First)
            {
                _lruList.Remove(listNode);
                _lruList.AddFirst(listNode);
            }
            return listNode.Value.Cells;
        }

        var entry = new CellMap(nodeId);
        foreach (var p in _store.ReadAll(nodeId))
            entry.Cells[CellKey.FromPoint(p, bbox, cellSize)] = p;

        _lruIndex[nodeId] = _lruList.AddFirst(entry);

        EvictIfOverCapacity();
        return entry.Cells;
    }

    private void EvictIfOverCapacity()
    {
        while (_lruIndex.Count > _capacity && _lruList.Last != null)
        {
            var evicted = _lruList.Last.Value;
            _lruList.RemoveLast();
            _lruIndex.Remove(evicted.NodeId);
            _store.WriteAll(evicted.NodeId, evicted.Cells.Values.ToArray());
        }
    }

    // Writes out whatever's still in memory WITHOUT evicting it — resident
    // nodes (root and other near-root nodes especially, which never reach
    // the LRU tail on their own — see class doc) stay in RAM, so the very
    // next Touch is still O(1) instead of a full reload+rebuild from disk.
    // Use this for periodic mid-run persistence, where the whole point is
    // to make current content visible on disk without paying to reload it
    // moments later; use Flush() only once nothing will touch the cache
    // again.
    public void Persist()
    {
        _store.WriteAllBatch(_lruList.Select(entry => (entry.NodeId, entry.Cells.Values.ToArray())));
    }

    // Persists, then evicts everything — after this call the cache is
    // empty and every touched node's complete cell set is readable from the
    // backing store. Only appropriate as true end-of-run cleanup: calling
    // this mid-run forces the next Touch on any resident node (root
    // included) to reload and rebuild its cell dictionary from disk — see
    // Persist() for the mid-run alternative.
    public void Flush()
    {
        Persist();
        _lruIndex.Clear();
        _lruList.Clear();
    }
}
