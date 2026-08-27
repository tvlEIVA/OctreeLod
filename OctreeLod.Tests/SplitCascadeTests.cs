using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using OctreeLod.Core.Model;
using OctreeLod.Core.SplitMergeEngine.Ingest;

namespace OctreeLod.Tests;

public class SplitCascadeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void OverflowingLeaf_SplitsIntoEightChildren()
    {
        var (engine, _) = MakeEngine(threshold: 10);

        for (int i = 0; i < 10; i++)
            engine.IngestPoint(new PointRecord(i * 0.001, 0, 0, 0, 0, 0));

        var root = engine.Root;
        Assert.False(root.IsLeaf);
        for (int octant = 0; octant < 8; octant++)
            Assert.NotNull(root.Children[octant]);
    }

    [Fact]
    public void ClusteredData_CascadesThroughMultipleSplitsBeforeNextPointIsProcessed()
    {
        // All points identical except for a tiny offset that only separates
        // them after several halvings of a modest-size root — forces the
        // cascade to recurse more than once within a single overflow event.
        var (engine, _) = MakeEngine(threshold: 5, worldSize: 1.0);

        for (int i = 0; i < 5; i++)
        {
            double jitter = i * 1e-6; // separable, but only several levels down — well within default maxSplitDepth
            engine.IngestPoint(new PointRecord(0.4 + jitter, 0.4, 0.4, 0, 0, 0));
        }

        // Enough depth budget exists to fully separate these 5 points, so
        // the cascade should resolve completely — no leaf left holding all
        // of them unsplit.
        Assert.Equal(0, CountLeavesAtOrAboveThreshold(engine.Root, threshold: 5));
    }

    [Fact]
    public void ExactDuplicatePoints_CascadeTerminatesAtMaxDepthInsteadOfHanging()
    {
        const int maxSplitDepth = 12;
        var (engine, warnings) = MakeEngine(threshold: 5, worldSize: 1.0, maxSplitDepth: maxSplitDepth);

        for (int i = 0; i < 50; i++)
            engine.IngestPoint(new PointRecord(0.4, 0.4, 0.4, 1, 2, 3)); // identical every time — never separable

        Assert.Contains(warnings, w => w.Contains("max depth") || w.Contains("frozen"));
        Assert.True(AnyLeafAtOrAboveDepth(engine.Root, maxSplitDepth));
    }

    [Fact]
    public void NoLeafExceedsThreshold_ExceptAtDocumentedMaxDepth()
    {
        var (engine, _) = MakeEngine(threshold: 20, maxSplitDepth: 15);
        var random = new Random(7);

        for (int i = 0; i < 5000; i++)
        {
            engine.IngestPoint(new PointRecord(
                random.NextDouble() * 100, random.NextDouble() * 100, random.NextDouble() * 100, 0, 0, 0));
        }

        WalkLeaves(engine.Root, leaf =>
        {
            if (leaf.PointCount >= 20)
            {
                Assert.True(NodeDepthOf(leaf) >= 15,
                    $"leaf {leaf.Id} reached threshold without splitting, below maxSplitDepth");
            }
            Assert.True(leaf.PointCount <= 20, $"leaf {leaf.Id} exceeded threshold ({leaf.PointCount})");
        });
    }

    [Fact]
    public void DeepCascadeBeyondOldLongOverflowThreshold_EveryNodeIdIsUnique()
    {
        // Depth 25 needs ~3*25=75 bits under the id scheme (root=0, child =
        // parent.Id*8+octant+1) — already past a 64-bit long's range, where
        // C#'s default unchecked arithmetic would silently wrap and collide.
        // BigInteger has no such ceiling; this proves ids stay distinct well
        // past that point. Identical points every time forces a single-child
        // cascade straight down to maxSplitDepth in one overflow event.
        const int maxSplitDepth = 25;
        var (engine, _) = MakeEngine(threshold: 2, worldSize: 1.0, maxSplitDepth: maxSplitDepth);

        for (int i = 0; i < 10; i++)
            engine.IngestPoint(new PointRecord(0.4, 0.4, 0.4, 0, 0, 0));

        var seenIds = new HashSet<BigInteger>();
        int nodeCount = 0;
        WalkAll(engine.Root, node =>
        {
            nodeCount++;
            Assert.True(seenIds.Add(node.Id), $"duplicate node id {node.Id} — collision");
        });

        Assert.True(nodeCount > maxSplitDepth, "expected the cascade to actually reach deep into the tree");
    }

    private (OctreeIngestionEngine engine, List<string> warnings) MakeEngine(
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
        var store = new SlabPointStore(Path.Combine(_dir, "leaves.bin"), threshold);
        var engine = new OctreeIngestionEngine(store, options);
        return (engine, warnings);
    }

    private static int CountLeavesAtOrAboveThreshold(OctreeNode root, int threshold)
    {
        int count = 0;
        WalkLeaves(root, leaf =>
        {
            if (leaf.PointCount >= threshold) count++;
        });
        return count;
    }

    private static bool AnyLeafAtOrAboveDepth(OctreeNode root, int depthThreshold)
    {
        bool found = false;
        WalkLeaves(root, leaf =>
        {
            if (NodeDepthOf(leaf) >= depthThreshold) found = true;
        });
        return found;
    }

    private static int NodeDepthOf(OctreeNode node)
    {
        int depth = 0;
        while (node.Parent != null)
        {
            depth++;
            node = node.Parent;
        }
        return depth;
    }

    private static void WalkLeaves(OctreeNode node, Action<OctreeNode> onLeaf)
    {
        if (node.IsLeaf)
        {
            onLeaf(node);
            return;
        }
        for (int octant = 0; octant < 8; octant++)
        {
            var child = node.Children[octant];
            if (child != null) WalkLeaves(child, onLeaf);
        }
    }

    private static void WalkAll(OctreeNode node, Action<OctreeNode> onNode)
    {
        onNode(node);
        if (node.IsLeaf) return;
        for (int octant = 0; octant < 8; octant++)
        {
            var child = node.Children[octant];
            if (child != null) WalkAll(child, onNode);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
