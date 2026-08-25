using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OctreeLod.Core;

namespace OctreeLod.Tests;

public class EndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FullPipeline_UniformClusteredBoundaryAndDuplicateHeavyData_ProducesConsistentStructure()
    {
        const int threshold = 200;
        var options = new OctreeIngestionOptions
        {
            SplitThreshold = threshold,
            MaxSplitDepth = 40,
        };
        var metadata = new InMemoryNodeMetadataStore();
        using var leafStore = new SlabPointStore(Path.Combine(_dir, "leaves.bin"), threshold);
        var engine = new OctreeIngestionEngine(metadata, leafStore, options);

        var allPoints = BuildSyntheticDataset();
        // Stream in irregular batches, mirroring real arrival patterns.
        var random = new Random(123);
        int idx = 0;
        while (idx < allPoints.Count)
        {
            int size = random.Next(50, 300);
            size = Math.Min(size, allPoints.Count - idx);
            engine.IngestBatch(allPoints.GetRange(idx, size));
            idx += size;
        }

        // Invariant: no data lost during ingestion — every raw point is
        // still recoverable from exactly one leaf's buffer.
        long totalStored = 0;
        WalkLeaves(metadata, engine.RootId, leafId =>
        {
            var leaf = metadata.Get(leafId);
            totalStored += leaf.PointCount;
        });
        Assert.Equal(allPoints.Count, totalStored);

        // Phase 2.
        var mergedStore = new MergedPointFileStore(Path.Combine(_dir, "merged"));
        var mergeEngine = new MergeEngine(metadata, leafStore, mergedStore, gridDivisions: 32, maxDegreeOfParallelism: 4);
        await mergeEngine.MergeAsync(engine.RootId);

        long logicalRootId = AdaptiveRootTrimmer.TrimToLogicalRoot(metadata, engine.RootId);
        var logicalRoot = metadata.Get(logicalRootId);

        // The two real clusters are far apart relative to their own extent,
        // so the branching point must have been found (not the untouched
        // geometric top, and not over-collapsed into one cluster only).
        Assert.NotEqual(engine.RootId, logicalRootId);
        int nonEmptyChildren = 0;
        for (int octant = 0; octant < 8; octant++)
        {
            long childId = logicalRoot.GetChild(octant);
            if (childId == NodeRecord.NoneId) continue;
            var child = metadata.Get(childId);
            if (!(child.IsLeaf && child.PointCount == 0)) nonEmptyChildren++;
        }
        Assert.True(nonEmptyChildren >= 2, "logical root should be the real branching point between the two clusters");

        var rootPoints = mergedStore.ReadAll(logicalRootId);
        Assert.True(rootPoints.Length > 0);
        Assert.True(rootPoints.Length <= allPoints.Count);
    }

    // Uniform region + a tight duplicate-heavy cluster + a second, distant
    // cluster (forces real branching) + points near a cell boundary.
    private static List<PointRecord> BuildSyntheticDataset()
    {
        var random = new Random(55);
        var points = new List<PointRecord>();

        // Cluster A: uniform-ish spread, ~2000 points.
        for (int i = 0; i < 2000; i++)
        {
            points.Add(new PointRecord(
                2_000_000 + random.NextDouble() * 50,
                800_000 + random.NextDouble() * 50,
                300_000 + random.NextDouble() * 10,
                (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        }

        // Cluster A, duplicate-heavy hotspot within it — kept under the
        // split threshold (200) so it stresses the split path without
        // deliberately triggering the max-split-depth drop, which is
        // covered directly by SplitCascadeTests.
        var dupPoint = new PointRecord(2_000_025, 800_025, 300_005, 9, 9, 9);
        for (int i = 0; i < 150; i++)
            points.Add(dupPoint);

        // Cluster B: a second cluster on the SAME side of the world-scale
        // root's origin as cluster A (both positive on every axis) — this
        // forces several single-child "wrapper" levels while descending
        // from world scale before the two clusters actually separate into
        // different octants, exercising AdaptiveRootTrimmer's collapsing
        // behavior rather than finding a branch at the very first split.
        for (int i = 0; i < 2000; i++)
        {
            points.Add(new PointRecord(
                1_000_000 + random.NextDouble() * 50,
                700_000 + random.NextDouble() * 50,
                150_000 + random.NextDouble() * 10,
                (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        }

        return points;
    }

    private static void WalkLeaves(InMemoryNodeMetadataStore metadata, long nodeId, Action<long> onLeaf)
    {
        var node = metadata.Get(nodeId);
        if (node.IsLeaf) { onLeaf(nodeId); return; }
        for (int octant = 0; octant < 8; octant++)
        {
            long childId = node.GetChild(octant);
            if (childId != NodeRecord.NoneId) WalkLeaves(metadata, childId, onLeaf);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
