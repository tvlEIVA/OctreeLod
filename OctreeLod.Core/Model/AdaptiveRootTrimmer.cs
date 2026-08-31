namespace OctreeLod.Core.Model;

public static class AdaptiveRootTrimmer
{
    // Structural-only trim: elide nodes with exactly one non-empty child
    // (provably safe — nothing else is there to lose). A count-based rule
    // ("stop once accepted count drops below ~1000") is NOT used here: a
    // node's point count is bounded by the sum of up to 8 children, so a
    // genuinely-branching node (>=2 real children) can still show a small
    // count, and stopping there would silently discard a whole sibling
    // subtree. This purely structural walk is safe by construction and
    // reproduces the "use true top if nothing collapses" fallback for free.
    //
    // Runs on ingestion-time structure alone (IsLeaf + PointCount) — an
    // internal node is only ever created by an overflow (i.e. always has
    // >=1 non-empty child), so "empty" reduces to "leaf with zero points".
    public static OctreeNode TrimToLogicalRoot(OctreeNode trueTop)
    {
        var current = trueTop;
        while (true)
        {
            if (current.IsLeaf) return current;

            var nonEmpty = OctreeStructureUtil.NonEmptyChildren(current);
            if (nonEmpty.Count != 1) return current;
            current = nonEmpty[0];
        }
    }
}
