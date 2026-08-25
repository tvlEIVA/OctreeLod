namespace OctreeLod.Core.Model;

// Opaque-ID metadata record. Deliberately NOT path-encoded by tree position
// (parent/children reference each other by stable id) so that structural
// changes never require renaming/relabeling unrelated nodes.
public struct NodeRecord
{
    public const long NoneId = -1;

    public long Id;
    public long ParentId;
    public BoundingCube Bbox;
    public int OctantSlotInParent; // -1 for the root
    public bool IsLeaf;
    public long PointCount;
    public StorageLocator Storage;
    public long[] Children; // index = octant 0..7; NoneId where absent

    public static NodeRecord CreateLeaf(long parentId, int octantSlotInParent, BoundingCube bbox)
    {
        return new NodeRecord
        {
            Id = NoneId,
            ParentId = parentId,
            OctantSlotInParent = octantSlotInParent,
            Bbox = bbox,
            IsLeaf = true,
            PointCount = 0,
            Storage = StorageLocator.None,
            Children = new[] { NoneId, NoneId, NoneId, NoneId, NoneId, NoneId, NoneId, NoneId },
        };
    }
}
