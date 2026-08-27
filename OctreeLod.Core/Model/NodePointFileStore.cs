using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace OctreeLod.Core.Model;

// Single growable file holding every node's point-set version ever written,
// indexed in memory by nodeId -> (offset, count) — avoids one-file-per-node
// (millions of nodes at scale means millions of small files, real NTFS/
// Defender overhead; see SlabPointStore's own design notes for the same
// concern on phase 1's leaf buffers). WriteAll always appends fresh bytes at
// the current end of file; a rewrite of an already-written node (e.g. the
// spacing engine evicting/reloading/re-evicting the same node) just orphans
// its previous bytes rather than reclaiming them — no compaction. Acceptable
// for scratch space that's deleted once the run's done; not meant for a
// long-lived store under heavy rewrite churn.
public sealed class NodePointFileStore : INodePointStore, IDisposable
{
    private readonly FileStream _file;
    private readonly Dictionary<BigInteger, (long Offset, int Count)> _index = new Dictionary<BigInteger, (long Offset, int Count)>();

    public NodePointFileStore(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "nodes.bin");
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess);
    }

    public void WriteAll(BigInteger nodeId, PointRecord[] points)
    {
        long offset = _file.Length;
        _file.Seek(offset, SeekOrigin.Begin);
        using (var writer = new BinaryWriter(_file, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var p in points)
            {
                writer.Write(p.X);
                writer.Write(p.Y);
                writer.Write(p.Z);
                writer.Write(p.R);
                writer.Write(p.G);
                writer.Write(p.B);
            }
        }
        _index[nodeId] = (offset, points.Length);
    }

    public PointRecord[] ReadAll(BigInteger nodeId)
    {
        if (!_index.TryGetValue(nodeId, out var entry)) return Array.Empty<PointRecord>();

        var result = new PointRecord[entry.Count];
        _file.Seek(entry.Offset, SeekOrigin.Begin);
        using (var reader = new BinaryReader(_file, Encoding.UTF8, leaveOpen: true))
        {
            for (int i = 0; i < entry.Count; i++)
            {
                result[i] = new PointRecord(
                    reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(),
                    reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            }
        }
        return result;
    }

    public void Dispose() => _file.Dispose();
}
