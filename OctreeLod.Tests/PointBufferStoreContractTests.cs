using System;
using System.Collections.Generic;
using System.IO;
using OctreeLod.Core;

namespace OctreeLod.Tests;

// Same contract suite run against both IPointBufferStore implementations —
// SlabPointStore (the v1 default) and PerFileNodePointStore (the debugging /
// baseline implementation) must behave identically from the caller's point
// of view.
public class PointBufferStoreContractTests : IDisposable
{
    private readonly List<string> _tempDirs = new List<string>();

    public static IEnumerable<object[]> StoreKinds()
    {
        yield return new object[] { "slab" };
        yield return new object[] { "perfile" };
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public void AppendThenReadAll_RoundTrips(string kind)
    {
        using var store = Create(kind, out _);
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

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public void FreeThenReallocate_SlotReuseDoesNotCorruptOtherActiveLeaf(string kind)
    {
        using var store = Create(kind, out _);

        var locatorA = store.Allocate(1);
        store.Append(locatorA, 0, new PointRecord(1, 1, 1, 1, 1, 1));

        var locatorB = store.Allocate(2);
        store.Append(locatorB, 0, new PointRecord(2, 2, 2, 2, 2, 2));

        store.Free(locatorA);

        var locatorC = store.Allocate(3); // may or may not reuse A's slot
        store.Append(locatorC, 0, new PointRecord(3, 3, 3, 3, 3, 3));

        var resultB = store.ReadAll(locatorB, 1);
        Assert.Equal(2, resultB[0].X, 9);

        var resultC = store.ReadAll(locatorC, 1);
        Assert.Equal(3, resultC[0].X, 9);
    }

    private CombinedDisposableStore Create(string kind, out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);

        IPointBufferStore store = kind switch
        {
            "slab" => new SlabPointStore(Path.Combine(dir, "leaves.bin"), slotCapacityPoints: 10),
            "perfile" => new PerFileNodePointStore(dir),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return new CombinedDisposableStore(store);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // Wraps a store so `using var store = Create(...)` works uniformly even
    // though only some implementations are IDisposable.
    private sealed class CombinedDisposableStore : IPointBufferStore, IDisposable
    {
        private readonly IPointBufferStore _inner;

        public CombinedDisposableStore(IPointBufferStore inner) => _inner = inner;

        public StorageLocator Allocate(long nodeId) => _inner.Allocate(nodeId);
        public void Append(StorageLocator locator, int indexInSlot, in PointRecord point) => _inner.Append(locator, indexInSlot, point);
        public PointRecord[] ReadAll(StorageLocator locator, int count) => _inner.ReadAll(locator, count);
        public void Free(StorageLocator locator) => _inner.Free(locator);

        public void Dispose()
        {
            if (_inner is IDisposable disposable) disposable.Dispose();
        }
    }
}
