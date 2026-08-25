using System.Collections.Generic;

namespace OctreeLod.Core;

// Flat struct list indexed by id. At ~125-140 bytes/record this stays
// resident even at billions-of-points scale (see design notes) — no
// out-of-core metadata needed for v1.
//
// Thread-safety note: phase 1 (ingestion) mutates this single-threaded.
// Phase 2 (merge) writes concurrently, but only ever to *distinct* indices
// (each node's own record, written by exactly one merge task) with no
// Add/Remove happening after ingestion ends, so concurrent Set calls on
// different ids are safe.
public sealed class InMemoryNodeMetadataStore : INodeMetadataStore
{
    private readonly List<NodeRecord> _nodes = new List<NodeRecord>();

    public long RootId { get; set; } = NodeRecord.NoneId;

    public int Count => _nodes.Count;

    public long Allocate(NodeRecord record)
    {
        long id = _nodes.Count;
        record.Id = id;
        _nodes.Add(record);
        return id;
    }

    public NodeRecord Get(long id) => _nodes[(int)id];

    public void Set(long id, NodeRecord record) => _nodes[(int)id] = record;
}
