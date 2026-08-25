using OctreeLod.Core;

namespace OctreeLod.Tests;

public class AdaptiveRootTrimmerTests
{
    private static readonly BoundingCube RootBbox = new BoundingCube(0, 0, 0, 64);

    [Fact]
    public void LeafRoot_ReturnsRootUnchanged()
    {
        var metadata = new InMemoryNodeMetadataStore();
        long rootId = AllocateLeaf(metadata, NodeRecord.NoneId, -1, RootBbox, pointCount: 5);

        long result = AdaptiveRootTrimmer.TrimToLogicalRoot(metadata, rootId);

        Assert.Equal(rootId, result);
    }

    [Fact]
    public void RootWithTwoNonEmptyChildren_NoWrapperLevels_ReturnsRootUnchanged()
    {
        var metadata = new InMemoryNodeMetadataStore();
        long rootId = AllocateInternalWithLeafChildren(metadata, RootBbox, nonEmptyOctants: new[] { 0, 5 });

        long result = AdaptiveRootTrimmer.TrimToLogicalRoot(metadata, rootId);

        Assert.Equal(rootId, result);
    }

    [Fact]
    public void SingleChildWrapperChain_CollapsesToTheBranchingNode()
    {
        var metadata = new InMemoryNodeMetadataStore();

        // Branching node: 2 non-empty leaf children.
        long branchId = AllocateInternalWithLeafChildren(metadata, RootBbox.ChildBounds(0).ChildBounds(0).ChildBounds(0), nonEmptyOctants: new[] { 1, 6 });

        // Three wrapper levels above it, each with exactly one non-empty
        // child (the chain leading down to `branchId`) and 7 empty leaves.
        long wrap3 = WrapSingleChild(metadata, RootBbox.ChildBounds(0).ChildBounds(0), childOctant: 0, onlyChild: branchId);
        long wrap2 = WrapSingleChild(metadata, RootBbox.ChildBounds(0), childOctant: 0, onlyChild: wrap3);
        long wrap1 = WrapSingleChild(metadata, RootBbox, childOctant: 0, onlyChild: wrap2);

        long result = AdaptiveRootTrimmer.TrimToLogicalRoot(metadata, wrap1);

        Assert.Equal(branchId, result);
    }

    [Fact]
    public void DataLossRegression_NeverReturnsANodeAboveOneWithTwoOrMoreNonEmptyChildren()
    {
        var metadata = new InMemoryNodeMetadataStore();

        // Branching node whose OWN (simulated merged) point count is
        // artificially tiny — a naive count-based trim rule would be tempted
        // to skip past it. The structural rule must not.
        long branchId = AllocateInternalWithLeafChildren(metadata, RootBbox, nonEmptyOctants: new[] { 2, 7 });
        var branch = metadata.Get(branchId);
        branch.PointCount = 3; // deliberately below any "stop here" threshold
        metadata.Set(branchId, branch);

        long result = AdaptiveRootTrimmer.TrimToLogicalRoot(metadata, branchId);

        Assert.Equal(branchId, result);
        var resultNode = metadata.Get(result);
        int nonEmpty = 0;
        for (int i = 0; i < 8; i++)
        {
            long childId = resultNode.GetChild(i);
            if (childId == NodeRecord.NoneId) continue;
            var child = metadata.Get(childId);
            if (!(child.IsLeaf && child.PointCount == 0)) nonEmpty++;
        }
        Assert.True(nonEmpty >= 2, "the returned node must retain its real sibling subtrees");
    }

    private static long AllocateLeaf(InMemoryNodeMetadataStore metadata, long parentId, int octantSlot, BoundingCube bbox, long pointCount)
    {
        var leaf = NodeRecord.CreateLeaf(parentId, octantSlot, bbox);
        long id = metadata.Allocate(leaf);
        var stored = metadata.Get(id);
        stored.PointCount = pointCount;
        stored.Storage = new StorageLocator(0, id);
        metadata.Set(id, stored);
        return id;
    }

    // Allocates an internal node whose 8 children are all empty leaves,
    // except the given octants which get a non-empty leaf (mirrors what
    // eager child creation on split actually produces).
    private static long AllocateInternalWithLeafChildren(InMemoryNodeMetadataStore metadata, BoundingCube bbox, int[] nonEmptyOctants)
    {
        var node = NodeRecord.CreateLeaf(NodeRecord.NoneId, -1, bbox);
        node.IsLeaf = false;
        long nodeId = metadata.Allocate(node);

        for (int octant = 0; octant < 8; octant++)
        {
            long pointCount = System.Array.IndexOf(nonEmptyOctants, octant) >= 0 ? 10 : 0;
            long childId = AllocateLeaf(metadata, nodeId, octant, bbox.ChildBounds(octant), pointCount);
            var updated = metadata.Get(nodeId);
            updated.SetChild(octant, childId);
            metadata.Set(nodeId, updated);
        }
        return nodeId;
    }

    // Allocates an internal node with exactly one non-empty child (an
    // already-built subtree passed in) and 7 empty leaf siblings — the
    // "wrapper" shape produced by descending from a world-scale root down to
    // a small, localized dataset.
    private static long WrapSingleChild(InMemoryNodeMetadataStore metadata, BoundingCube bbox, int childOctant, long onlyChild)
    {
        var node = NodeRecord.CreateLeaf(NodeRecord.NoneId, -1, bbox);
        node.IsLeaf = false;
        long nodeId = metadata.Allocate(node);

        for (int octant = 0; octant < 8; octant++)
        {
            long childId;
            if (octant == childOctant)
            {
                childId = onlyChild;
            }
            else
            {
                childId = AllocateLeaf(metadata, nodeId, octant, bbox.ChildBounds(octant), pointCount: 0);
            }
            var updated = metadata.Get(nodeId);
            updated.SetChild(octant, childId);
            metadata.Set(nodeId, updated);
        }
        return nodeId;
    }
}
