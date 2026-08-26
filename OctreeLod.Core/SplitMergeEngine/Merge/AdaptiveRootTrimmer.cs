using OctreeLod.Core.Model;

namespace OctreeLod.Core.Merge;

public static class AdaptiveRootTrimmer
{
    // Structural-only trim: elide nodes with exactly one non-empty child
    // (provably safe — nothing else is there to lose). A count-based rule
    // ("stop once merged count drops below ~1000") is NOT used here: a
    // merge's point count is bounded by the *sum* of up to 8 children, so a
    // genuinely-branching node (>=2 real children) can still show a small
    // count, and stopping there would silently discard a whole sibling
    // subtree. This purely structural walk is safe by construction and
    // reproduces the "use true top if nothing collapses" fallback for free.
    //
    // Runs on ingestion-time structure alone (IsLeaf + PointCount) — it does
    // not need phase-2 merge to have run first, since an internal node is
    // only ever created by an overflow (i.e. always has >=1 non-empty
    // child), so "empty" reduces to "leaf with zero points".
    public static long TrimToLogicalRoot(INodeMetadataStore metadata, long trueTopId)
    {
        long current = trueTopId;
        while (true)
        {
            var node = metadata.Get(current);
            if (node.IsLeaf) return current;

            var nonEmpty = OctreeStructureUtil.NonEmptyChildIds(metadata, node);
            if (nonEmpty.Count != 1) return current;
            current = nonEmpty[0];
        }
    }
}
