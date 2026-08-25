using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OctreeLod.Core;

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
public sealed class MergeEngine
{
    private readonly INodeMetadataStore _metadata;
    private readonly IPointBufferStore _leafStore;
    private readonly IMergedPointStore _mergedStore;
    private readonly int _gridDivisions;
    private readonly SemaphoreSlim _concurrencyLimiter;

    public MergeEngine(
        INodeMetadataStore metadata,
        IPointBufferStore leafStore,
        IMergedPointStore mergedStore,
        int gridDivisions,
        int maxDegreeOfParallelism)
    {
        _metadata = metadata;
        _leafStore = leafStore;
        _mergedStore = mergedStore;
        _gridDivisions = gridDivisions;
        _concurrencyLimiter = new SemaphoreSlim(maxDegreeOfParallelism);
    }

    public Task<PointRecord[]> MergeAsync(long nodeId) => MergeSubtreeAsync(nodeId);

    private async Task<PointRecord[]> MergeSubtreeAsync(long nodeId)
    {
        var node = _metadata.Get(nodeId);

        if (node.IsLeaf)
            return await RunGated(() => ReadLeaf(nodeId, node));

        var childIds = OctreeStructureUtil.NonEmptyChildIds(_metadata, node);

        // No gating here: this await must never hold a permit, or a deep
        // single-child chain deadlocks itself against its own descendants.
        var childResults = await Task.WhenAll(childIds.Select(MergeSubtreeAsync));

        return await RunGated(() => MergeInternal(nodeId, node, childResults));
    }

    private PointRecord[] ReadLeaf(long nodeId, NodeRecord node)
    {
        var points = node.PointCount == 0
            ? Array.Empty<PointRecord>()
            : _leafStore.ReadAll(node.Storage, (int)node.PointCount);
        _mergedStore.WriteAll(nodeId, points);
        return points;
    }

    private PointRecord[] MergeInternal(long nodeId, NodeRecord node, PointRecord[][] childResults)
    {
        var combined = childResults.SelectMany(r => r);
        var merged = GridSubsampler.Subsample(node.Bbox, combined, _gridDivisions);

        node.PointCount = merged.Length;
        _metadata.Set(nodeId, node);
        _mergedStore.WriteAll(nodeId, merged);
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
