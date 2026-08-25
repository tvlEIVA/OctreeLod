using System;
using System.IO;
using OctreeLod.Core.Ingest;
using OctreeLod.Core.Model;

namespace OctreeLod.Tests;

public class SlabPointStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AppendThenReadAll_RoundTrips()
    {
        using var store = new SlabPointStore(Path.Combine(_dir, "leaves.bin"), slotCapacityPoints: 10);
        var locator = store.Allocate(nodeId: 1);
        var points = new[]
        {
            new PointRecord(1, 2, 3, 10, 20, 30),
            new PointRecord(4, 5, 6, 40, 50, 60),
        };
        for (int i = 0; i < points.Length; i++) store.Append(locator, i, points[i]);

        var result = store.ReadAll(locator, points.Length);

        Assert.Equal(points.Length, result.Length);
        for (int i = 0; i < points.Length; i++)
        {
            Assert.Equal(points[i].X, result[i].X, 9);
            Assert.Equal(points[i].Y, result[i].Y, 9);
            Assert.Equal(points[i].Z, result[i].Z, 9);
            Assert.Equal(points[i].R, result[i].R);
            Assert.Equal(points[i].G, result[i].G);
            Assert.Equal(points[i].B, result[i].B);
        }
    }

    [Fact]
    public void FreeThenReallocate_SlotReuseDoesNotCorruptOtherActiveLeaf()
    {
        using var store = new SlabPointStore(Path.Combine(_dir, "leaves.bin"), slotCapacityPoints: 10);

        var locatorA = store.Allocate(1);
        store.Append(locatorA, 0, new PointRecord(1, 1, 1, 1, 1, 1));

        var locatorB = store.Allocate(2);
        store.Append(locatorB, 0, new PointRecord(2, 2, 2, 2, 2, 2));

        store.Free(locatorA);

        var locatorC = store.Allocate(3); // reuses A's freed slot
        store.Append(locatorC, 0, new PointRecord(3, 3, 3, 3, 3, 3));

        var resultB = store.ReadAll(locatorB, 1);
        Assert.Equal(2, resultB[0].X, 9);

        var resultC = store.ReadAll(locatorC, 1);
        Assert.Equal(3, resultC[0].X, 9);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
