using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OctreeLod.Core.Model;

namespace OctreeLod.App.Sources;

// Reads a whitespace-delimited geodetic (WGS84) point cloud:
//   lon lat height red green blue [nx ny nz]
// Converts to local East/North/Up meters around the dataset's own centroid
// (a cheap prepass over the file — two running sums, never holds points in
// memory, still out-of-core) so the rest of the pipeline can work in plain
// Cartesian meters as usual. The computed centroid is exposed as
// `Reference`, ready to hand straight to Tiles3DExporter.Export so the
// tileset gets placed back at the correct real-world location.
public sealed class LatLonPointCloudBatchSource : IPointBatchSource
{
    private const double MetersPerDegreeLat = 111_320.0;

    private readonly string _path;
    private readonly int _batchSize;
    private readonly bool _hasHeader;
    private readonly double _metersPerDegreeLon;

    public GeoReference Reference { get; }

    public LatLonPointCloudBatchSource(string path, int batchSize = 5000, bool hasHeader = false)
    {
        _path = path;
        _batchSize = batchSize;
        _hasHeader = hasHeader;

        var (centerLon, centerLat) = ComputeCentroid();
        Reference = new GeoReference(latitudeDegrees: centerLat, longitudeDegrees: centerLon, heightMeters: 0.0);
        _metersPerDegreeLon = MetersPerDegreeLat * Math.Cos(centerLat * Math.PI / 180.0);
    }

    public IEnumerable<IReadOnlyList<PointRecord>> ReadBatches()
    {
        using var reader = new StreamReader(_path);
        if (_hasHeader) reader.ReadLine();

        double centerLon = Reference.LongitudeDegrees;
        double centerLat = Reference.LatitudeDegrees;

        var batch = new List<PointRecord>(_batchSize);
        string? line;
        int lineNumber = _hasHeader ? 1 : 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 6)
                throw new FormatException($"{_path}:{lineNumber}: expected at least 6 columns (lon lat height red green blue), got {parts.Length}.");

            double lon = double.Parse(parts[0], CultureInfo.InvariantCulture);
            double lat = double.Parse(parts[1], CultureInfo.InvariantCulture);
            double height = double.Parse(parts[2], CultureInfo.InvariantCulture);
            byte red = byte.Parse(parts[3], CultureInfo.InvariantCulture);
            byte green = byte.Parse(parts[4], CultureInfo.InvariantCulture);
            byte blue = byte.Parse(parts[5], CultureInfo.InvariantCulture);

            double easting = (lon - centerLon) * _metersPerDegreeLon;
            double northing = (lat - centerLat) * MetersPerDegreeLat;

            batch.Add(new PointRecord(easting, northing, height, red, green, blue));
            if (batch.Count >= _batchSize)
            {
                yield return batch;
                batch = new List<PointRecord>(_batchSize);
            }
        }

        if (batch.Count > 0) yield return batch;
    }

    // Streams the file once just to average lon/lat — O(1) memory, needed
    // up front so every point can be converted relative to the same,
    // already-known centroid on the single real read in ReadBatches.
    private (double lon, double lat) ComputeCentroid()
    {
        using var reader = new StreamReader(_path);
        if (_hasHeader) reader.ReadLine();

        double sumLon = 0, sumLat = 0;
        long count = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            sumLon += double.Parse(parts[0], CultureInfo.InvariantCulture);
            sumLat += double.Parse(parts[1], CultureInfo.InvariantCulture);
            count++;
        }

        if (count == 0) throw new InvalidOperationException($"{_path}: no data rows found.");
        return (sumLon / count, sumLat / count);
    }
}
