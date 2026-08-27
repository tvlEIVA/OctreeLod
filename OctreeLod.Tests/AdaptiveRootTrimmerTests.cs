using OctreeLod.Core.Model;
using OctreeLod.Core.SplitMergeEngine.Merge;

namespace OctreeLod.Tests;

public class AdaptiveRootTrimmerTests
{
    private static readonly BoundingCube RootBbox = new BoundingCube(0, 0, 0, 64);

    [Fact]
    public void LeafRoot_ReturnsRootUnchanged()
    {
        var root = AllocateLeaf(null, RootBbox, pointCount: 5);

        var result = AdaptiveRootTrimmer.TrimToLogicalRoot(root);

        Assert.Same(root, result);
    }

    [Fact]
    public void RootWithTwoNonEmptyChildren_NoWrapperLevels_ReturnsRootUnchanged()
    {
        var root = AllocateInternalWithLeafChildren(RootBbox, nonEmptyOctants: new[] { 0, 5 });

        var result = AdaptiveRootTrimmer.TrimToLogicalRoot(root);

        Assert.Same(root, result);
    }

    [Fact]
    public void SingleChildWrapperChain_CollapsesToTheBranchingNode()
    {
        // Branching node: 2 non-empty leaf children.
        var branch = AllocateInternalWithLeafChildren(RootBbox.ChildBounds(0).ChildBounds(0).ChildBounds(0), nonEmptyOctants: new[] { 1, 6 });

        // Three wrapper levels above it, each with exactly one non-empty
        // child (the chain leading down to `branch`) and 7 empty leaves.
        var wrap3 = WrapSingleChild(RootBbox.ChildBounds(0).ChildBounds(0), childOctant: 0, onlyChild: branch);
        var wrap2 = WrapSingleChild(RootBbox.ChildBounds(0), childOctant: 0, onlyChild: wrap3);
        var wrap1 = WrapSingleChild(RootBbox, childOctant: 0, onlyChild: wrap2);

        var result = AdaptiveRootTrimmer.TrimToLogicalRoot(wrap1);

        Assert.Same(branch, result);
    }

    [Fact]
    public void DataLossRegression_NeverReturnsANodeAboveOneWithTwoOrMoreNonEmptyChildren()
    {
        // Branching node whose OWN (simulated merged) point count is
        // artificially tiny — a naive count-based trim rule would be tempted
        // to skip past it. The structural rule must not.
        var branch = AllocateInternalWithLeafChildren(RootBbox, nonEmptyOctants: new[] { 2, 7 });
        branch.PointCount = 3; // deliberately below any "stop here" threshold

        var result = AdaptiveRootTrimmer.TrimToLogicalRoot(branch);

        Assert.Same(branch, result);
        int nonEmpty = 0;
        for (int i = 0; i < 8; i++)
        {
            var child = result.Children[i];
            if (child == null) continue;
            if (!(child.IsLeaf && child.PointCount == 0)) nonEmpty++;
        }
        Assert.True(nonEmpty >= 2, "the returned node must retain its real sibling subtrees");
    }

    private static OctreeNode AllocateLeaf(OctreeNode? parent, BoundingCube bbox, long pointCount, int octant = -1)
    {
        var leaf = parent == null ? OctreeNode.CreateRoot(bbox) : OctreeNode.CreateChild(parent, octant, bbox);
        leaf.PointCount = pointCount;
        leaf.Storage = new StorageLocator(0, 0);
        return leaf;
    }

    // Allocates an internal node whose 8 children are all empty leaves,
    // except the given octants which get a non-empty leaf (mirrors what
    // eager child creation on split actually produces).
    private static OctreeNode AllocateInternalWithLeafChildren(BoundingCube bbox, int[] nonEmptyOctants)
    {
        var node = OctreeNode.CreateRoot(bbox);
        node.IsLeaf = false;

        for (int octant = 0; octant < 8; octant++)
        {
            long pointCount = System.Array.IndexOf(nonEmptyOctants, octant) >= 0 ? 10 : 0;
            node.Children[octant] = AllocateLeaf(node, bbox.ChildBounds(octant), pointCount, octant);
        }
        return node;
    }

    // Allocates an internal node with exactly one non-empty child (an
    // already-built subtree passed in) and 7 empty leaf siblings — the
    // "wrapper" shape produced by descending from a world-scale root down to
    // a small, localized dataset.
    private static OctreeNode WrapSingleChild(BoundingCube bbox, int childOctant, OctreeNode onlyChild)
    {
        var node = OctreeNode.CreateRoot(bbox);
        node.IsLeaf = false;

        for (int octant = 0; octant < 8; octant++)
        {
            node.Children[octant] = octant == childOctant
                ? onlyChild
                : AllocateLeaf(node, bbox.ChildBounds(octant), pointCount: 0, octant: octant);
        }
        return node;
    }
}
