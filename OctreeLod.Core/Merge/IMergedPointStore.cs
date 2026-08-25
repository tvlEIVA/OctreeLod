using OctreeLod.Core.Model;

namespace OctreeLod.Core.Merge;

// Phase-2 representative point sets: one bulk write per node, written
// exactly once, size varies per node (not bounded to a fixed slot like leaf
// ingest buffers) — kept as its own interface rather than forced to share
// IPointBufferStore's incremental-append shape.
public interface IMergedPointStore
{
    void WriteAll(long nodeId, PointRecord[] points);
    PointRecord[] ReadAll(long nodeId);
}
