using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.SplitMergeEngine.Ingest;

// Default leaf-buffer store. One growable file divided into fixed-size
// slots (one slot = one leaf's buffer, sized for exactly `slotCapacityPoints`
// points), grown in fixed-size chunks (`slotsPerGrowth` at a time) to
// amortize the cost of extending it. A free-list reclaims a split leaf's
// slot immediately, bounding wasted space to at most one slot per
// currently-active leaf rather than accumulating orphaned space over the
// whole run — unlike a naive create-a-file-per-leaf approach, which also
// hits real Windows overhead (Defender/NTFS) from millions of
// create-then-delete cycles at scale.
//
// This file is scratch space for phase 1 only — safe to delete once phase 2
// (merge) and the 3D Tiles export have run; nothing downstream reads it
// again.
public sealed class SlabPointStore : IPointBufferStore, IDisposable
{
    private readonly FileStream _file;
    private readonly int _slotCapacityPoints;
    private readonly int _slotsPerGrowth;
    private readonly long _slotByteSize;

    private readonly Stack<StorageLocator> _freeList = new Stack<StorageLocator>();
    private long _allocatedSlots;
    private long _nextSlot;

    public SlabPointStore(string path, int slotCapacityPoints, int slotsPerGrowth = 4096)
    {
        _slotCapacityPoints = slotCapacityPoints;
        _slotsPerGrowth = slotsPerGrowth;
        _slotByteSize = (long)slotCapacityPoints * PointRecord.ByteSize;

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess);
        GrowBy(slotsPerGrowth);
    }

    private void GrowBy(int slots)
    {
        _allocatedSlots += slots;
        _file.SetLength(_allocatedSlots * _slotByteSize);
    }

    public StorageLocator Allocate(long nodeId)
    {
        if (_freeList.Count > 0) return _freeList.Pop();

        if (_nextSlot >= _allocatedSlots) GrowBy(_slotsPerGrowth);

        var locator = new StorageLocator(0, _nextSlot);
        _nextSlot++;
        return locator;
    }

    public void Append(StorageLocator locator, int indexInSlot, in PointRecord point)
    {
        if (indexInSlot < 0 || indexInSlot >= _slotCapacityPoints)
            throw new InvalidOperationException(
                $"Slot capacity ({_slotCapacityPoints}) exceeded at index {indexInSlot} — leaf should have split before this point.");

        long offset = locator.SlotIndex * _slotByteSize + (long)indexInSlot * PointRecord.ByteSize;
        _file.Seek(offset, SeekOrigin.Begin);
        using (var writer = new BinaryWriter(_file, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(point.X);
            writer.Write(point.Y);
            writer.Write(point.Z);
            writer.Write(point.R);
            writer.Write(point.G);
            writer.Write(point.B);
        }
    }

    public PointRecord[] ReadAll(StorageLocator locator, int count)
    {
        long offset = locator.SlotIndex * _slotByteSize;
        _file.Seek(offset, SeekOrigin.Begin);
        var result = new PointRecord[count];
        using (var reader = new BinaryReader(_file, Encoding.UTF8, leaveOpen: true))
        {
            for (int i = 0; i < count; i++)
            {
                double x = reader.ReadDouble();
                double y = reader.ReadDouble();
                double z = reader.ReadDouble();
                byte r = reader.ReadByte();
                byte g = reader.ReadByte();
                byte b = reader.ReadByte();
                result[i] = new PointRecord(x, y, z, r, g, b);
            }
        }
        return result;
    }

    public void Free(StorageLocator locator) => _freeList.Push(locator);

    public void Dispose() => _file.Dispose();
}
