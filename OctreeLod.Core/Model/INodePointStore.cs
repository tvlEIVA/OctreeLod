using System.Collections.Generic;
using System.Numerics;

namespace OctreeLod.Core.Model;

// Out-of-core point storage for a node's full representative set: one bulk
// write per node (a node's set may be rewritten more than once — e.g. a
// spacing-engine node paged back in and out again — but each write replaces
// the previous one wholesale), size varies per node (not bounded to a fixed
// slot like leaf ingest buffers) — kept as its own interface rather than
// forced to share IPointBufferStore's incremental-append shape. Used by the
// legacy engine's merge phase (each node's set is written once, after
// subsampling) and by the spacing engine's LRU node paging (a node's set may
// be written and re-read several times as it's evicted/reloaded).
public interface INodePointStore
{
    void WriteAll(BigInteger nodeId, PointRecord[] points);

    // Writes many nodes in one call instead of one WriteAll call each — see
    // NodePointFileStore.WriteAllBatch for why that's worth having: writing
    // dozens/thousands of nodes one at a time each pays a per-call
    // durability cost (an OS-level flush) that a real batch can pay once.
    void WriteAllBatch(IEnumerable<(BigInteger NodeId, PointRecord[] Points)> items);

    PointRecord[] ReadAll(BigInteger nodeId);
}
