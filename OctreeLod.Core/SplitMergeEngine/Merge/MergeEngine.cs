using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OctreeLod.Core.Model;
using OctreeLod.Core.SplitMergeEngine.Ingest;

namespace OctreeLod.Core.SplitMergeEngine.Merge;

// Phase-2 orchestrator: bottom-up, post-order, parallel across sibling
// subtrees (bounded concurrency). Each node's merge reads only its direct
// children's *already-merged* representative sets (never raw leaves from
// deep subtrees), which is what keeps the per-merge working set bounded to
// ~8x threshold and preserves the out-of-core guarantee end to end.
//
// The concurrency limiter gates only each node's own local work (a leaf
// read, or a grid-subsample + write) — never the recursive await on
// children. Gating the whole recursive call (permit held while awaiting
// children) would self-deadlock here: the fixed world-scale root (see
// OctreeIngestionOptions) deliberately produces long single-child "wrapper"
// chains while descending toward wherever real data clusters, and with a
// bounded number of permits, ancestors would hold every permit while
// blocked on descendants that can never acquire one to proceed.
//
// Thread-safety: phase 1 (ingestion) has already finished by the time this
// runs. Each merge task only ever mutates the one OctreeNode object it was
// handed (its own PointCount) — distinct objects, so concurrent tasks never
// touch shared mutable state.
public sealed class MergeEngine
{
    private readonly IPointBufferStore _leafStore;
    private readonly INodePointStore _mergedStore;
    private readonly int _gridDivisions;
    private readonly SemaphoreSlim _concurrencyLimiter;

    public MergeEngine(
        IPointBufferStore leafStore,
        INodePointStore mergedStore,
        int gridDivisions,
        int maxDegreeOfParallelism)
    {
        _leafStore = leafStore;
        _mergedStore = mergedStore;
        _gridDivisions = gridDivisions;
        _concurrencyLimiter = new SemaphoreSlim(maxDegreeOfParallelism);
    }

    public Task<PointRecord[]> MergeAsync(OctreeNode node) => MergeSubtreeAsync(node);

    private async Task<PointRecord[]> MergeSubtreeAsync(OctreeNode node)
    {
        if (node.IsLeaf)
            return await RunGated(() => ReadLeaf(node));

        var children = OctreeStructureUtil.NonEmptyChildren(node);

        // No gating here: this await must never hold a permit, or a deep
        // single-child chain deadlocks itself against its own descendants.
        var childResults = await Task.WhenAll(children.Select(MergeSubtreeAsync));

        return await RunGated(() => MergeInternal(node, childResults));
    }

    private PointRecord[] ReadLeaf(OctreeNode node)
    {
        var points = node.PointCount == 0
            ? Array.Empty<PointRecord>()
            : _leafStore.ReadAll(node.Storage, (int)node.PointCount);
        _mergedStore.WriteAll(node.Id, points);
        return points;
    }

    private PointRecord[] MergeInternal(OctreeNode node, PointRecord[][] childResults)
    {
        var combined = childResults.SelectMany(r => r);
        var merged = GridSubsampler.Subsample(node.Bbox, combined, _gridDivisions);

        node.PointCount = merged.Length;
        _mergedStore.WriteAll(node.Id, merged);
        return merged;
    }

    private async Task<PointRecord[]> RunGated(Func<PointRecord[]> work)
    {
        await _concurrencyLimiter.WaitAsync();
        try
        {
            return work();
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }
}
