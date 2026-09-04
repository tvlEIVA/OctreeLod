using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.Sources;

// Reads a whitespace-delimited text point cloud with a header row:
//   easting northing depth red green blue nx ny nz
// easting/northing/depth map to X/Y/Z, red/green/blue to R/G/B. Normals
// (nx/ny/nz) are ignored — PointRecord has no normal field. Streams the file
// line by line and yields fixed-size batches, never holding the whole file
// in memory.
public sealed class TextPointCloudBatchSource : IPointBatchSource
{
    private readonly string _path;
    private readonly int _batchSize;

    public TextPointCloudBatchSource(string path, int batchSize = 5000)
    {
        _path = path;
        _batchSize = batchSize;
    }

    public IEnumerable<IReadOnlyList<PointRecord>> ReadBatches()
    {
        using var reader = new StreamReader(_path);
        reader.ReadLine(); // header row

        var batch = new List<PointRecord>(_batchSize);
        string? line;
        int lineNumber = 1;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6)
                throw new FormatException($"{_path}:{lineNumber}: expected at least 6 columns (easting northing depth red green blue), got {parts.Length}.");

            double easting = double.Parse(parts[0], CultureInfo.InvariantCulture);
            double northing = double.Parse(parts[1], CultureInfo.InvariantCulture);
            double depth = double.Parse(parts[2], CultureInfo.InvariantCulture);
            byte red = byte.Parse(parts[3], CultureInfo.InvariantCulture);
            byte green = byte.Parse(parts[4], CultureInfo.InvariantCulture);
            byte blue = byte.Parse(parts[5], CultureInfo.InvariantCulture);

            batch.Add(new PointRecord(easting, northing, depth, red, green, blue));
            if (batch.Count >= _batchSize)
            {
                yield return batch;
                batch = new List<PointRecord>(_batchSize);
            }
        }

        if (batch.Count > 0) yield return batch;
    }
}
