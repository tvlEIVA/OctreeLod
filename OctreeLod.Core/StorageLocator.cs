namespace OctreeLod.Core;

// Opaque handle into whichever IPointBufferStore issued it. Field meaning is
// store-specific (e.g. segment+slot for the slab store, or a node id tucked
// into SlotIndex for the per-file store) — callers never interpret it.
public readonly struct StorageLocator
{
    public static readonly StorageLocator None = new StorageLocator(-1, -1);

    public int SegmentId { get; }
    public long SlotIndex { get; }

    public StorageLocator(int segmentId, long slotIndex)
    {
        SegmentId = segmentId;
        SlotIndex = slotIndex;
    }

    public bool IsNone => SegmentId < 0;
}
