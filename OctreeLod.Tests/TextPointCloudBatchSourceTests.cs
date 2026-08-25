using System;
using System.IO;
using System.Linq;
using OctreeLod.App.Sources;

namespace OctreeLod.Tests;

public class TextPointCloudBatchSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    public TextPointCloudBatchSourceTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void SkipsHeaderAndMapsColumnsCorrectly()
    {
        string path = Path.Combine(_dir, "points.txt");
        File.WriteAllLines(path, new[]
        {
            "easting northing depth red green blue nx ny nz",
            "-1.36346900463104 2.32567095756531 0.04331199824810 25 23 26 -0.979106 0.094357 0.180136",
            "-1.37035202980042 2.32817196846008 0.04350699856877 28 26 31 -0.979207 0.094131 0.179705",
        });

        var source = new TextPointCloudBatchSource(path, batchSize: 100);
        var points = source.ReadBatches().SelectMany(b => b).ToList();

        Assert.Equal(2, points.Count);
        Assert.Equal(-1.36346900463104, points[0].X, 12);
        Assert.Equal(2.32567095756531, points[0].Y, 12);
        Assert.Equal(0.04331199824810, points[0].Z, 12);
        Assert.Equal(25, points[0].R);
        Assert.Equal(23, points[0].G);
        Assert.Equal(26, points[0].B);
    }

    [Fact]
    public void RespectsBatchSize()
    {
        string path = Path.Combine(_dir, "points2.txt");
        var lines = new System.Collections.Generic.List<string> { "easting northing depth red green blue nx ny nz" };
        for (int i = 0; i < 25; i++)
            lines.Add($"{i} {i} {i} 1 2 3 0 0 1");
        File.WriteAllLines(path, lines);

        var source = new TextPointCloudBatchSource(path, batchSize: 10);
        var batches = source.ReadBatches().ToList();

        Assert.Equal(3, batches.Count); // 10, 10, 5
        Assert.Equal(10, batches[0].Count);
        Assert.Equal(10, batches[1].Count);
        Assert.Equal(5, batches[2].Count);
    }

    [Fact]
    public void SkipsBlankLines()
    {
        string path = Path.Combine(_dir, "points3.txt");
        File.WriteAllLines(path, new[]
        {
            "easting northing depth red green blue nx ny nz",
            "1 2 3 4 5 6 0 0 1",
            "",
            "   ",
            "7 8 9 10 11 12 0 0 1",
        });

        var source = new TextPointCloudBatchSource(path, batchSize: 100);
        var points = source.ReadBatches().SelectMany(b => b).ToList();

        Assert.Equal(2, points.Count);
    }

    [Fact]
    public void TooFewColumns_Throws()
    {
        string path = Path.Combine(_dir, "points4.txt");
        File.WriteAllLines(path, new[]
        {
            "easting northing depth red green blue nx ny nz",
            "1 2 3 4",
        });

        var source = new TextPointCloudBatchSource(path, batchSize: 100);
        Assert.Throws<FormatException>(() => source.ReadBatches().SelectMany(b => b).ToList());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
