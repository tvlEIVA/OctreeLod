using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OctreeLod.Core;

// Debugging / contract-test baseline: one growable file per leaf node
// (named by node id, tucked into StorageLocator.SlotIndex). Simpler than
// SlabPointStore but creates one file per leaf that ever existed, including
// ones that later split away — not the v1 default at scale, see design
// notes, but useful for inspecting a single node's raw points.
public sealed class PerFileNodePointStore : IPointBufferStore, IDisposable
{
    private readonly string _directory;
    private readonly Dictionary<long, FileStream> _open = new Dictionary<long, FileStream>();

    public PerFileNodePointStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(long nodeId) => Path.Combine(_directory, $"node-{nodeId}.pts");

    public StorageLocator Allocate(long nodeId)
    {
        var fs = new FileStream(PathFor(nodeId), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        _open[nodeId] = fs;
        return new StorageLocator(0, nodeId);
    }

    public void Append(StorageLocator locator, int indexInSlot, in PointRecord point)
    {
        var fs = _open[locator.SlotIndex];
        fs.Seek(0, SeekOrigin.End);
        using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
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
        var fs = _open[locator.SlotIndex];
        fs.Seek(0, SeekOrigin.Begin);
        var result = new PointRecord[count];
        using (var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true))
        {
            for (int i = 0; i < count; i++)
            {
                result[i] = new PointRecord(
                    reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(),
                    reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            }
        }
        return result;
    }

    public void Free(StorageLocator locator)
    {
        if (_open.TryGetValue(locator.SlotIndex, out var fs))
        {
            fs.Dispose();
            _open.Remove(locator.SlotIndex);
            File.Delete(PathFor(locator.SlotIndex));
        }
    }

    public void Dispose()
    {
        foreach (var fs in _open.Values) fs.Dispose();
    }
}
