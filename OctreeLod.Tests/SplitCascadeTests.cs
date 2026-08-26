using System;
using System.Collections.Generic;
using System.IO;
using OctreeLod.Core.Model;
using OctreeLod.Core.SplitMergeEngine.Ingest;

namespace OctreeLod.Tests;

public class SplitCascadeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OverflowingLeaf_SplitsIntoEightChildren()
    {
        var (metadata, engine, _) = MakeEngine(threshold: 10);

        for (int i = 0; i < 10; i++)
            engine.IngestPoint(new PointRecord(i * 0.001, 0, 0, 0, 0, 0));

        var root = metadata.Get(engine.RootId);
        Assert.False(root.IsLeaf);
        for (int octant = 0; octant < 8; octant++)
            Assert.NotEqual(NodeRecord.NoneId, root.Children[octant]);
    }

    [Fact]
    public void ClusteredData_CascadesThroughMultipleSplitsBeforeNextPointIsProcessed()
    {
        // All points identical except for a tiny offset that only separates
        // them after several halvings of a modest-size root — forces the
        // cascade to recurse more than once within a single overflow event.
        var (metadata, engine, _) = MakeEngine(threshold: 5, worldSize: 1.0);

        for (int i = 0; i < 5; i++)
        {
            double jitter = i * 1e-6; // separable, but only several levels down — well within default maxSplitDepth
            engine.IngestPoint(new PointRecord(0.4 + jitter, 0.4, 0.4, 0, 0, 0));
        }

        // Enough depth budget exists to fully separate these 5 points, so
        // the cascade should resolve completely — no leaf left holding all
        // of them unsplit.
        Assert.Equal(0, CountLeavesAtOrAboveThreshold(metadata, engine.RootId, threshold: 5));
    }

    [Fact]
    public void ExactDuplicatePoints_CascadeTerminatesAtMaxDepthInsteadOfHanging()
    {
        const int maxSplitDepth = 12;
        var (metadata, engine, warnings) = MakeEngine(threshold: 5, worldSize: 1.0, maxSplitDepth: maxSplitDepth);

        for (int i = 0; i < 50; i++)
            engine.IngestPoint(new PointRecord(0.4, 0.4, 0.4, 1, 2, 3)); // identical every time — never separable

        Assert.Contains(warnings, w => w.Contains("max depth") || w.Contains("frozen"));
        Assert.True(AnyLeafAtOrAboveDepth(metadata, engine.RootId, maxSplitDepth));
    }

    [Fact]
    public void NoLeafExceedsThreshold_ExceptAtDocumentedMaxDepth()
    {
        var (metadata, engine, _) = MakeEngine(threshold: 20, maxSplitDepth: 15);
        var random = new Random(7);

        for (int i = 0; i < 5000; i++)
        {
            engine.IngestPoint(new PointRecord(
                random.NextDouble() * 100, random.NextDouble() * 100, random.NextDouble() * 100, 0, 0, 0));
        }

        WalkLeaves(metadata, engine.RootId, leafId =>
        {
            var leaf = metadata.Get(leafId);
            if (leaf.PointCount >= 20)
            {
                Assert.True(NodeDepthOf(metadata, leafId) >= 15,
                    $"leaf {leafId} reached threshold without splitting, below maxSplitDepth");
            }
            Assert.True(leaf.PointCount <= 20, $"leaf {leafId} exceeded threshold ({leaf.PointCount})");
        });
    }

    private (InMemoryNodeMetadataStore metadata, OctreeIngestionEngine engine, List<string> warnings) MakeEngine(
        int threshold, double worldSize = 20_000_000, int maxSplitDepth = 60)
    {
        var warnings = new List<string>();
        var options = new OctreeIngestionOptions
        {
            SplitThreshold = threshold,
            MaxSplitDepth = maxSplitDepth,
            WorldBounds = new BoundingCube(-worldSize / 2, -worldSize / 2, -worldSize / 2, worldSize),
            OnWarning = warnings.Add,
        };
        var metadata = new InMemoryNodeMetadataStore();
        var store = new SlabPointStore(Path.Combine(_dir, "leaves.bin"), threshold);
        var engine = new OctreeIngestionEngine(metadata, store, options);
        return (metadata, engine, warnings);
    }

    private static int CountLeavesAtOrAboveThreshold(InMemoryNodeMetadataStore metadata, long nodeId, int threshold)
    {
        int count = 0;
        WalkLeaves(metadata, nodeId, leafId =>
        {
            if (metadata.Get(leafId).PointCount >= threshold) count++;
        });
        return count;
    }

    private static bool AnyLeafAtOrAboveDepth(InMemoryNodeMetadataStore metadata, long nodeId, int depthThreshold)
    {
        bool found = false;
        WalkLeaves(metadata, nodeId, leafId =>
        {
            if (NodeDepthOf(metadata, leafId) >= depthThreshold) found = true;
        });
        return found;
    }

    private static int NodeDepthOf(InMemoryNodeMetadataStore metadata, long nodeId)
    {
        int depth = 0;
        var node = metadata.Get(nodeId);
        while (node.ParentId != NodeRecord.NoneId)
        {
            depth++;
            node = metadata.Get(node.ParentId);
        }
        return depth;
    }

    private static void WalkLeaves(InMemoryNodeMetadataStore metadata, long nodeId, Action<long> onLeaf)
    {
        var node = metadata.Get(nodeId);
        if (node.IsLeaf)
        {
            onLeaf(nodeId);
            return;
        }
        for (int octant = 0; octant < 8; octant++)
        {
            long childId = node.Children[octant];
            if (childId != NodeRecord.NoneId) WalkLeaves(metadata, childId, onLeaf);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
