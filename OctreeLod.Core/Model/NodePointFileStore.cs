using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Threading;

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
//
// One write FileStream plus one read FileStream PER THREAD that calls
// ReadAll, instead of a single shared read handle behind a lock. Because
// the file is append-only and a node's bytes are never mutated once
// written, any number of independent read handles can seek/read
// concurrently with no coordination needed at all — they're just looking
// at complete, immutable bytes, each through its own OS file handle and
// position. A single shared read handle+lock was serializing every
// ReadAll to one at a time, which capped a parallel preview export's
// throughput on the read side even though its writes (each to a distinct
// .pnts file) ran fully concurrently — this removes that ceiling. Only
// _writeFile still needs a lock: MergeEngine calls WriteAll from multiple
// threads concurrently (maxDegreeOfParallelism), and unlike reads, writes
// share one growing append position that genuinely must be serialized.
public sealed class NodePointFileStore : INodePointStore, IDisposable
{
    private readonly string _path;
    private readonly FileStream _writeFile;
    private readonly object _writeLock = new object();
    private readonly ThreadLocal<FileStream> _readFile;
    private readonly ConcurrentDictionary<BigInteger, (long Offset, int Count)> _index = new ConcurrentDictionary<BigInteger, (long Offset, int Count)>();

    public NodePointFileStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "nodes.bin");
        _writeFile = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        _readFile = new ThreadLocal<FileStream>(
            () => new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess),
            trackAllValues: true);
    }

    public void WriteAll(BigInteger nodeId, PointRecord[] points) =>
        WriteAllBatch(new[] { (nodeId, points) });

    // Writes many nodes under a single lock hold and a single OS-level
    // flush at the end, instead of one lock+flush per node (see WriteAll's
    // single-item call above) — a caller writing many nodes back-to-back
    // (e.g. PagedCellMapCache.Persist snapshotting everything currently
    // resident) would otherwise pay a real per-call flush cost that many
    // times over for no extra durability: nothing needs any ONE of these
    // nodes visible before the whole batch is done.
    public void WriteAllBatch(IEnumerable<(BigInteger NodeId, PointRecord[] Points)> items)
    {
        lock (_writeLock)
        {
            _writeFile.Seek(_writeFile.Length, SeekOrigin.Begin);
            using (var writer = new BinaryWriter(_writeFile, Encoding.UTF8, leaveOpen: true))
            {
                foreach (var (nodeId, points) in items)
                {
                    long offset = _writeFile.Position;
                    foreach (var p in points)
                    {
                        writer.Write(p.X);
                        writer.Write(p.Y);
                        writer.Write(p.Z);
                        writer.Write(p.R);
                        writer.Write(p.G);
                        writer.Write(p.B);
                    }
                    _index[nodeId] = (offset, points.Length);
                }
            }
            _writeFile.Flush(); // push past the FileStream's own buffer so _readFile's independent handle can see these bytes
        }
    }

    public PointRecord[] ReadAll(BigInteger nodeId)
    {
        if (!_index.TryGetValue(nodeId, out var entry)) return Array.Empty<PointRecord>();

        var file = _readFile.Value!;
        var result = new PointRecord[entry.Count];
        file.Seek(entry.Offset, SeekOrigin.Begin);
        using (var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true))
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

    public void Dispose()
    {
        _writeFile.Dispose();
        foreach (var file in _readFile.Values) file.Dispose();
        _readFile.Dispose();
    }
}
