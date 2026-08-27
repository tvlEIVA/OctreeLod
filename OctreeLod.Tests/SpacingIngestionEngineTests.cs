using System;
using System.Collections.Generic;
using System.IO;
using OctreeLod.Core.Model;
using OctreeLod.Core.SpacingEngine;

namespace OctreeLod.Tests;

public class SpacingIngestionEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));
    private readonly List<NodePointFileStore> _stores = new List<NodePointFileStore>();

    [Fact]
    public void TwoPointsInTheSameCell_OnlyFirstAcceptedAtThatNode_SecondDescendsToChild()
    {
        var (engine, _, _) = MakeEngine(worldSize: 100, gridDivisions: 4); // root cellSize = 25

        var first = new PointRecord(0.1, 0.1, 0.1, 0, 0, 0);
        var second = new PointRecord(0.4, 0.4, 0.4, 0, 0, 0); // same root cell as `first` (25-unit cells)
        engine.IngestPoint(first);
        engine.IngestPoint(second);

        var root = engine.Root;
        Assert.False(root.IsLeaf);
        Assert.Equal(1, root.PointCount);

        int octant = root.Bbox.Octant(second);
        var child = root.Children[octant];
        Assert.NotNull(child);
        Assert.True(child!.IsLeaf);
        Assert.Equal(1, child.PointCount);
    }

    [Fact]
    public void WellSeparatedPoints_AllAcceptedAtRoot_NoChildrenCreated()
    {
        var (engine, warnings, _) = MakeEngine(worldSize: 2000, gridDivisions: 8); // root cellSize = 250, bounds +-1000

        // Spaced 300 units apart on a line through the positive octant — well
        // clear of the 250-unit root cell size, and within bounds, so every
        // point should land in its own free cell on the first try.
        for (int i = 0; i < 4; i++)
            engine.IngestPoint(new PointRecord(i * 300.0, i * 300.0, i * 300.0, 0, 0, 0));

        Assert.Empty(warnings);
        var root = engine.Root;
        Assert.True(root.IsLeaf);
        Assert.Equal(4, root.PointCount);
    }

    [Fact]
    public void EveryAcceptedPointLiesWithinItsOwningNodesBbox()
    {
        var (engine, _, nodeStore) = MakeEngine(worldSize: 1000, gridDivisions: 8);
        var random = new Random(5);

        for (int i = 0; i < 3000; i++)
        {
            engine.IngestPoint(new PointRecord(
                random.NextDouble() * 400, random.NextDouble() * 400, random.NextDouble() * 400, 0, 0, 0));
        }

        engine.Flush();

        WalkAll(engine.Root, node =>
        {
            foreach (var p in nodeStore.ReadAll(node.Id))
                Assert.True(node.Bbox.Contains(p), $"point ({p.X},{p.Y},{p.Z}) not contained in node {node.Id}'s bbox");
        });
    }

    [Fact]
    public void ExtremelySmallResidencyCap_StillFindsCorrectAnswerAfterEvictAndReload()
    {
        // MaxInMemoryNodes = 1 forces a page-out/reload on almost every
        // point past the first, exercising the disk round-trip: a node's
        // occupied-cell state must survive being evicted and reloaded, or a
        // later point that collides with an already-accepted point would be
        // wrongly accepted again instead of being pushed to a child.
        var (engine, _, _) = MakeEngine(worldSize: 100, gridDivisions: 4, maxInMemoryNodes: 1);

        engine.IngestPoint(new PointRecord(0.1, 0.1, 0.1, 0, 0, 0)); // accepted at root
        engine.IngestPoint(new PointRecord(0.4, 0.4, 0.4, 0, 0, 0)); // same root cell -> descends; evicts root to disk
        engine.IngestPoint(new PointRecord(0.15, 0.15, 0.15, 0, 0, 0)); // same root cell again -> root must reload from disk and still reject it

        Assert.Equal(1, engine.Root.PointCount); // root's cell was never re-accepted after reload
        Assert.Equal(3, SumPointCounts(engine.Root)); // nothing lost or double-counted across the tree
    }

    [Fact]
    public void SumOfPointCountsAcrossAllNodes_EqualsInputCount_WhenNothingIsDropped()
    {
        var (engine, warnings, _) = MakeEngine(worldSize: 100_000, gridDivisions: 16);
        var random = new Random(11);

        long total = 0;
        for (int batch = 0; batch < 20; batch++)
        {
            int size = random.Next(10, 100);
            for (int i = 0; i < size; i++)
            {
                engine.IngestPoint(new PointRecord(
                    random.NextDouble() * 40_000, random.NextDouble() * 40_000, random.NextDouble() * 40_000, 0, 0, 0));
                total++;
            }

            Assert.Equal(total, SumPointCounts(engine.Root));
        }
        Assert.Empty(warnings);
    }

    [Fact]
    public void ExactDuplicatePoints_HitMaxDepthAndAreDroppedInsteadOfHanging()
    {
        const int maxSplitDepth = 12;
        var (engine, warnings, _) = MakeEngine(worldSize: 1.0, gridDivisions: 4, maxSplitDepth: maxSplitDepth);

        for (int i = 0; i < 50; i++)
            engine.IngestPoint(new PointRecord(0.4, 0.4, 0.4, 1, 2, 3)); // identical every time — never separable

        Assert.Contains(warnings, w => w.Contains("max split depth"));
        // Exactly one copy is accepted per node along the collision chain
        // down to maxSplitDepth; every point after that is dropped.
        Assert.True(SumPointCounts(engine.Root) <= maxSplitDepth + 1);
    }

    [Fact]
    public void PointOutsideWorldBounds_IsDroppedWithWarning()
    {
        var (engine, warnings, _) = MakeEngine(worldSize: 100, gridDivisions: 4);

        engine.IngestPoint(new PointRecord(10_000, 10_000, 10_000, 0, 0, 0));

        Assert.Contains(warnings, w => w.Contains("outside fixed world bounds"));
        Assert.Equal(0, engine.Root.PointCount);
    }

    private (SpacingIngestionEngine engine, List<string> warnings, NodePointFileStore nodeStore) MakeEngine(
        double worldSize, int gridDivisions, int maxSplitDepth = 60, int maxInMemoryNodes = 4096)
    {
        var warnings = new List<string>();
        var options = new SpacingIngestionOptions
        {
            WorldBounds = new BoundingCube(-worldSize / 2, -worldSize / 2, -worldSize / 2, worldSize),
            GridDivisions = gridDivisions,
            MaxSplitDepth = maxSplitDepth,
            MaxInMemoryNodes = maxInMemoryNodes,
            OnWarning = warnings.Add,
        };
        var nodeStore = new NodePointFileStore(Path.Combine(_dir, "nodes-" + Guid.NewGuid().ToString("N")));
        _stores.Add(nodeStore);
        var engine = new SpacingIngestionEngine(nodeStore, options);
        return (engine, warnings, nodeStore);
    }

    private static long SumPointCounts(OctreeNode node)
    {
        long total = 0;
        WalkAll(node, n => total += n.PointCount);
        return total;
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
        foreach (var store in _stores) store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
