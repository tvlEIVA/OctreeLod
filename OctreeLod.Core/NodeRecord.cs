using System;
using System.Collections.Generic;

namespace OctreeLod.Core;

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

    private long _child0, _child1, _child2, _child3, _child4, _child5, _child6, _child7;

    public long GetChild(int octant)
    {
        switch (octant)
        {
            case 0: return _child0;
            case 1: return _child1;
            case 2: return _child2;
            case 3: return _child3;
            case 4: return _child4;
            case 5: return _child5;
            case 6: return _child6;
            case 7: return _child7;
            default: throw new ArgumentOutOfRangeException(nameof(octant));
        }
    }

    public void SetChild(int octant, long nodeId)
    {
        switch (octant)
        {
            case 0: _child0 = nodeId; break;
            case 1: _child1 = nodeId; break;
            case 2: _child2 = nodeId; break;
            case 3: _child3 = nodeId; break;
            case 4: _child4 = nodeId; break;
            case 5: _child5 = nodeId; break;
            case 6: _child6 = nodeId; break;
            case 7: _child7 = nodeId; break;
            default: throw new ArgumentOutOfRangeException(nameof(octant));
        }
    }

    public IEnumerable<long> NonNoneChildren()
    {
        for (int i = 0; i < 8; i++)
        {
            long c = GetChild(i);
            if (c != NoneId) yield return c;
        }
    }

    public static NodeRecord CreateLeaf(long parentId, int octantSlotInParent, BoundingCube bbox)
    {
        var node = new NodeRecord
        {
            Id = NoneId,
            ParentId = parentId,
            OctantSlotInParent = octantSlotInParent,
            Bbox = bbox,
            IsLeaf = true,
            PointCount = 0,
            Storage = StorageLocator.None,
        };
        for (int i = 0; i < 8; i++) node.SetChild(i, NoneId);
        return node;
    }
}
