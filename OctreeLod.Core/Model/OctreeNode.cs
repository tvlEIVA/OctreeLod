using System.Numerics;

namespace OctreeLod.Core.Model;

// Reference-type tree node: Parent/Children are real object references, so
// the whole tree is reachable from just the root — no separate flat store
// or id-lookup needed for in-memory traversal in either direction.
//
// `Id` still exists purely as a stable external handle: INodePointStore
// keys a node's on-disk point data by it, and 3D Tiles export uses it as
// the content filename (`{id}.pnts`). Neither of those can address a node
// by C# object reference, so the id has to exist regardless of how the
// in-memory tree is shaped — it's not used for in-memory traversal at all.
//
// Derived from position, not a counter: root = 0, child = parent.Id * 8 +
// octant + 1 (the standard complete-8-ary-tree indexing scheme — same
// arithmetic a binary heap uses generalized from 2 children to 8).
// Deterministic and collision-free by construction. BigInteger, not long:
// the id needs ~3 bits per depth level (8 children per level), and
// MaxSplitDepth defaults to 60 for both engines — 180 bits needed at that
// depth, well past a 64-bit long's range (which silently wraps rather than
// throwing on overflow, so this would be a real collision risk, not just a
// theoretical one, for the exact pathological duplicate-cluster case
// MaxSplitDepth exists to handle).
public sealed class OctreeNode
{
    public BigInteger Id;
    public OctreeNode? Parent;
    public BoundingCube Bbox;
    public bool IsLeaf;
    public long PointCount;
    public StorageLocator Storage;
    public readonly OctreeNode?[] Children = new OctreeNode?[8]; // index = octant 0..7; null where absent

    public static OctreeNode CreateRoot(BoundingCube bbox)
    {
        return new OctreeNode
        {
            Id = BigInteger.Zero,
            Parent = null,
            Bbox = bbox,
            IsLeaf = true,
            PointCount = 0,
            Storage = StorageLocator.None,
        };
    }

    public static OctreeNode CreateChild(OctreeNode parent, int octant, BoundingCube bbox)
    {
        return new OctreeNode
        {
            Id = parent.Id * 8 + octant + 1,
            Parent = parent,
            Bbox = bbox,
            IsLeaf = true,
            PointCount = 0,
            Storage = StorageLocator.None,
        };
    }
}
