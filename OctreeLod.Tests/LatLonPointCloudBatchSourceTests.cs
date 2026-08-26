using System;
using System.IO;
using System.Linq;
using OctreeLod.App.Sources;

namespace OctreeLod.Tests;

public class LatLonPointCloudBatchSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    public LatLonPointCloudBatchSourceTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void NoHeader_ReadsAllRowsAndComputesCentroid()
    {
        string path = Path.Combine(_dir, "points.xyz");
        File.WriteAllLines(path, new[]
        {
            "9.0 55.0 0 255 100 255 0 0 0",
            "11.0 55.0 0 255 100 255 0 0 0",
            "10.0 57.0 0 255 100 255 0 0 0",
            "10.0 53.0 0 255 100 255 0 0 0",
        });

        var source = new LatLonPointCloudBatchSource(path, batchSize: 100, hasHeader: false);

        Assert.Equal(10.0, source.Reference.LongitudeDegrees, 9);
        Assert.Equal(55.0, source.Reference.LatitudeDegrees, 9);

        var points = source.ReadBatches().SelectMany(b => b).ToList();
        Assert.Equal(4, points.Count);
    }

    [Fact]
    public void CenterPoint_ConvertsToOriginLocalMeters()
    {
        string path = Path.Combine(_dir, "points2.xyz");
        File.WriteAllLines(path, new[]
        {
            "10.0 56.0 0 1 2 3 0 0 1", // exact centroid (single point, so it IS the mean)
        });

        var source = new LatLonPointCloudBatchSource(path, batchSize: 100, hasHeader: false);
        var points = source.ReadBatches().SelectMany(b => b).ToList();

        Assert.Single(points);
        Assert.Equal(0.0, points[0].X, 6);
        Assert.Equal(0.0, points[0].Y, 6);
        Assert.Equal(0.0, points[0].Z, 6);
    }

    [Fact]
    public void OneDegreeNorth_IsApproximately111320Meters()
    {
        string path = Path.Combine(_dir, "points3.xyz");
        File.WriteAllLines(path, new[]
        {
            "10.0 0.0 0 1 2 3 0 0 1",
            "10.0 1.0 0 1 2 3 0 0 1",
        });

        var source = new LatLonPointCloudBatchSource(path, batchSize: 100, hasHeader: false);
        var points = source.ReadBatches().SelectMany(b => b).OrderBy(p => p.Y).ToList();

        double northingDelta = points[1].Y - points[0].Y;
        Assert.Equal(111_320.0, northingDelta, 3);
    }

    [Fact]
    public void HasHeaderTrue_SkipsFirstLine()
    {
        string path = Path.Combine(_dir, "points4.xyz");
        File.WriteAllLines(path, new[]
        {
            "lon lat height red green blue nx ny nz",
            "10.0 56.0 0 1 2 3 0 0 1",
            "10.0 56.0 0 4 5 6 0 0 1",
        });

        var source = new LatLonPointCloudBatchSource(path, batchSize: 100, hasHeader: true);
        var points = source.ReadBatches().SelectMany(b => b).ToList();

        Assert.Equal(2, points.Count); // header excluded, both data rows kept
        Assert.Equal(1, points[0].R);
    }

    [Fact]
    public void RespectsBatchSize()
    {
        string path = Path.Combine(_dir, "points5.xyz");
        var lines = new System.Collections.Generic.List<string>();
        for (int i = 0; i < 25; i++)
            lines.Add($"{10.0 + i * 0.001} {56.0} 0 1 2 3 0 0 1");
        File.WriteAllLines(path, lines);

        var source = new LatLonPointCloudBatchSource(path, batchSize: 10, hasHeader: false);
        var batches = source.ReadBatches().ToList();

        Assert.Equal(3, batches.Count);
        Assert.Equal(10, batches[0].Count);
        Assert.Equal(10, batches[1].Count);
        Assert.Equal(5, batches[2].Count);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
