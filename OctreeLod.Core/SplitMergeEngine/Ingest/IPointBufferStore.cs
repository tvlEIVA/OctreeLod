using System.Numerics;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.SplitMergeEngine.Ingest;

// Leaf ingest buffers: incremental single-point appends into a slot reserved
// up front, freed when the leaf splits. Separate from INodePointStore
// (phase-2 representative sets) because the access pattern differs — one
// point at a time here vs. one bulk write there.
public interface IPointBufferStore
{
    StorageLocator Allocate(BigInteger nodeId);
    void Append(StorageLocator locator, int indexInSlot, in PointRecord point);
    PointRecord[] ReadAll(StorageLocator locator, int count);
    void Free(StorageLocator locator);
}
