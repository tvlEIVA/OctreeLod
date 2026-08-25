using System;
using System.IO;

namespace OctreeLod.Core;

public sealed class MergedPointFileStore : IMergedPointStore
{
    private readonly string _directory;

    public MergedPointFileStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(long nodeId) => Path.Combine(_directory, $"merged-{nodeId}.pts");

    public void WriteAll(long nodeId, PointRecord[] points)
    {
        using (var fs = new FileStream(PathFor(nodeId), FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(fs))
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
    }

    public PointRecord[] ReadAll(long nodeId)
    {
        string path = PathFor(nodeId);
        var info = new FileInfo(path);
        if (!info.Exists) return Array.Empty<PointRecord>();

        int count = (int)(info.Length / PointRecord.ByteSize);
        var result = new PointRecord[count];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        using (var reader = new BinaryReader(fs))
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
}
