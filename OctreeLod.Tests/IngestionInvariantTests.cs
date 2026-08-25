using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OctreeLod.Core;

namespace OctreeLod.Tests;

public class IngestionInvariantTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PointCountIsConservedAfterEveryBatch()
    {
        const int threshold = 50;
        var options = new OctreeIngestionOptions { SplitThreshold = threshold };
        var metadata = new InMemoryNodeMetadataStore();
        using var store = new SlabPointStore(Path.Combine(_dir, "leaves.bin"), threshold);
        var engine = new OctreeIngestionEngine(metadata, store, options);

        var random = new Random(3);
        long expectedTotal = 0;

        for (int batch = 0; batch < 30; batch++)
        {
            var points = MakeRandomBatch(random, count: 37);
            engine.IngestBatch(points);
            expectedTotal += points.Count;

            long actualTotal = SumLeafPointCounts(metadata, engine.RootId);
            Assert.Equal(expectedTotal, actualTotal);
        }
    }

    [Fact]
    public void EveryStoredLeafPointLiesWithinItsLeafBbox()
    {
        const int threshold = 30;
        var options = new OctreeIngestionOptions { SplitThreshold = threshold };
        var metadata = new InMemoryNodeMetadataStore();
        using var store = new SlabPointStore(Path.Combine(_dir, "leaves.bin"), threshold);
        var engine = new OctreeIngestionEngine(metadata, store, options);

        var random = new Random(9);
        engine.IngestBatch(MakeRandomBatch(random, count: 4000));

        WalkLeaves(metadata, engine.RootId, leafId =>
        {
            var leaf = metadata.Get(leafId);
            if (leaf.PointCount == 0) return;
            var points = store.ReadAll(leaf.Storage, (int)leaf.PointCount);
            foreach (var p in points)
                Assert.True(leaf.Bbox.Contains(p), $"point ({p.X},{p.Y},{p.Z}) not contained in leaf {leafId}'s bbox");
        });
    }

    [Fact]
    public void EveryChildBboxIsContainedInItsParentBbox()
    {
        const int threshold = 20;
        var options = new OctreeIngestionOptions { SplitThreshold = threshold };
        var metadata = new InMemoryNodeMetadataStore();
        using var store = new SlabPointStore(Path.Combine(_dir, "leaves.bin"), threshold);
        var engine = new OctreeIngestionEngine(metadata, store, options);

        engine.IngestBatch(MakeRandomBatch(new Random(11), count: 6000));

        WalkAll(metadata, engine.RootId, (nodeId, node) =>
        {
            if (node.IsLeaf) return;
            for (int octant = 0; octant < 8; octant++)
            {
                long childId = node.Children[octant];
                var child = metadata.Get(childId);
                Assert.True(child.Bbox.MinX >= node.Bbox.MinX && child.Bbox.MinX + child.Bbox.Size <= node.Bbox.MinX + node.Bbox.Size);
                Assert.True(child.Bbox.MinY >= node.Bbox.MinY && child.Bbox.MinY + child.Bbox.Size <= node.Bbox.MinY + node.Bbox.Size);
                Assert.True(child.Bbox.MinZ >= node.Bbox.MinZ && child.Bbox.MinZ + child.Bbox.Size <= node.Bbox.MinZ + node.Bbox.Size);
            }
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RandomizedBatchSizeAndOrder_PointCountAndContainmentStillHold(int seed)
    {
        const int threshold = 25;
        var options = new OctreeIngestionOptions { SplitThreshold = threshold };
        var metadata = new InMemoryNodeMetadataStore();
        using var store = new SlabPointStore(Path.Combine(_dir + "-" + seed, "leaves.bin"), threshold);
        var engine = new OctreeIngestionEngine(metadata, store, options);

        var random = new Random(seed);
        var allPoints = MakeRandomBatch(random, count: 3000);

        // Shuffle and chop into randomly-sized batches — order/chunking
        // shouldn't matter to the final invariants.
        Shuffle(allPoints, random);
        int index = 0;
        long expectedTotal = 0;
        while (index < allPoints.Count)
        {
            int size = random.Next(1, 80);
            size = Math.Min(size, allPoints.Count - index);
            var batch = allPoints.GetRange(index, size);
            index += size;

            engine.IngestBatch(batch);
            expectedTotal += batch.Count;

            Assert.Equal(expectedTotal, SumLeafPointCounts(metadata, engine.RootId));
        }

        WalkLeaves(metadata, engine.RootId, leafId =>
        {
            var leaf = metadata.Get(leafId);
            Assert.True(leaf.PointCount <= threshold, $"leaf {leafId} exceeded threshold");
        });
    }

    private static List<PointRecord> MakeRandomBatch(Random random, int count)
    {
        var list = new List<PointRecord>(count);
        for (int i = 0; i < count; i++)
        {
            double x = 1_000_000 + random.NextDouble() * 100;
            double y = 500_000 + random.NextDouble() * 100;
            double z = 200_000 + random.NextDouble() * 100;
            list.Add(new PointRecord(x, y, z, (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        }
        return list;
    }

    private static void Shuffle<T>(List<T> list, Random random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static long SumLeafPointCounts(InMemoryNodeMetadataStore metadata, long rootId)
    {
        long total = 0;
        WalkLeaves(metadata, rootId, leafId => total += metadata.Get(leafId).PointCount);
        return total;
    }

    private static void WalkLeaves(InMemoryNodeMetadataStore metadata, long nodeId, Action<long> onLeaf)
    {
        WalkAll(metadata, nodeId, (id, node) =>
        {
            if (node.IsLeaf) onLeaf(id);
        });
    }

    private static void WalkAll(InMemoryNodeMetadataStore metadata, long nodeId, Action<long, NodeRecord> onNode)
    {
        var node = metadata.Get(nodeId);
        onNode(nodeId, node);
        if (node.IsLeaf) return;
        for (int octant = 0; octant < 8; octant++)
        {
            long childId = node.Children[octant];
            if (childId != NodeRecord.NoneId) WalkAll(metadata, childId, onNode);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        for (int seed = 1; seed <= 5; seed++)
        {
            try { Directory.Delete(_dir + "-" + seed, recursive: true); } catch { }
        }
    }
}
