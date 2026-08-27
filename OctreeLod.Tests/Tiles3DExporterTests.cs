using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using OctreeLod.Core.Export;
using OctreeLod.Core.Model;
using OctreeLod.Core.SplitMergeEngine.Ingest;
using OctreeLod.Core.SplitMergeEngine.Merge;

namespace OctreeLod.Tests;

public class Tiles3DExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExportedTileset_ParsesAsValidJsonWithOnlyNonEmptyChildrenAndExistingContentFiles()
    {
        const int threshold = 100;
        const int gridDivisions = 16;
        var options = new OctreeIngestionOptions { SplitThreshold = threshold, MaxSplitDepth = 40 };
        using var leafStore = new SlabPointStore(Path.Combine(_dir, "leaves.bin"), threshold);
        var engine = new OctreeIngestionEngine(leafStore, options);

        var random = new Random(21);
        engine.IngestBatch(MakeTwoClusterDataset(random));

        using var mergedStore = new NodePointFileStore(Path.Combine(_dir, "merged"));
        var mergeEngine = new MergeEngine(leafStore, mergedStore, gridDivisions, maxDegreeOfParallelism: 4);
        await mergeEngine.MergeAsync(engine.Root);

        var logicalRoot = AdaptiveRootTrimmer.TrimToLogicalRoot(engine.Root);

        string outputDir = Path.Combine(_dir, "tiles-out");
        Tiles3DExporter.Export(logicalRoot, mergedStore, gridDivisions, outputDir, TileRefine.Replace);

        string tilesetPath = Path.Combine(outputDir, "tileset.json");
        Assert.True(File.Exists(tilesetPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(tilesetPath));
        var root = doc.RootElement;

        Assert.Equal("1.0", root.GetProperty("asset").GetProperty("version").GetString());
        var rootTile = root.GetProperty("root");
        Assert.Equal("REPLACE", rootTile.GetProperty("refine").GetString());

        int visitedTiles = 0;
        double parentGeometricError = double.PositiveInfinity;
        WalkAndVerify(rootTile, outputDir, ref visitedTiles, parentGeometricError);
        Assert.True(visitedTiles > 1, "expected more than just the root tile given two clusters");
    }

    [Fact]
    public void SingleLeafRoot_ExportsOneTileWithNoChildren()
    {
        const int threshold = 1000;
        var options = new OctreeIngestionOptions { SplitThreshold = threshold };
        using var leafStore = new SlabPointStore(Path.Combine(_dir, "leaves2.bin"), threshold);
        var engine = new OctreeIngestionEngine(leafStore, options);

        var random = new Random(5);
        var points = new List<PointRecord>();
        for (int i = 0; i < 50; i++)
            points.Add(new PointRecord(random.NextDouble(), random.NextDouble(), random.NextDouble(), 1, 2, 3));
        engine.IngestBatch(points);

        using var mergedStore = new NodePointFileStore(Path.Combine(_dir, "merged2"));

        var logicalRoot = AdaptiveRootTrimmer.TrimToLogicalRoot(engine.Root);
        Assert.True(logicalRoot.IsLeaf); // never overflowed, single leaf for the whole tree

        mergedStore.WriteAll(logicalRoot.Id, leafStore.ReadAll(logicalRoot.Storage, (int)logicalRoot.PointCount));

        string outputDir = Path.Combine(_dir, "tiles-out2");
        Tiles3DExporter.Export(logicalRoot, mergedStore, gridDivisions: 16, outputDir, TileRefine.Replace);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDir, "tileset.json")));
        var rootTile = doc.RootElement.GetProperty("root");

        Assert.Equal(0, rootTile.GetProperty("geometricError").GetDouble());
        Assert.False(rootTile.TryGetProperty("children", out _));
        Assert.True(File.Exists(Path.Combine(outputDir, "content", $"{logicalRoot.Id}.pnts")));
    }

    private static void WalkAndVerify(JsonElement tile, string outputDir, ref int visitedCount, double parentGeometricError)
    {
        visitedCount++;

        double geometricError = tile.GetProperty("geometricError").GetDouble();
        Assert.True(geometricError <= parentGeometricError,
            $"geometricError should be non-increasing with depth (parent {parentGeometricError}, this {geometricError})");

        string uri = tile.GetProperty("content").GetProperty("uri").GetString()!;
        string contentPath = Path.Combine(outputDir, uri);
        Assert.True(File.Exists(contentPath), $"missing content file: {contentPath}");

        var box = tile.GetProperty("boundingVolume").GetProperty("box");
        Assert.Equal(12, box.GetArrayLength());

        if (tile.TryGetProperty("children", out var children))
        {
            int count = 0;
            // Re-walk with a local counter since ref params can't cross a
            // foreach-captured lambda; plain loop instead.
            foreach (var child in children.EnumerateArray())
            {
                count++;
                WalkAndVerify(child, outputDir, ref visitedCount, geometricError);
            }
            Assert.True(count >= 1);
        }
    }

    private static List<PointRecord> MakeTwoClusterDataset(Random random)
    {
        var points = new List<PointRecord>();
        var centers = new (double X, double Y, double Z)[]
        {
            (1_000_000, 700_000, 150_000),
            (1_000_500, 700_000, 150_000),
        };
        foreach (var c in centers)
        {
            for (int i = 0; i < 1500; i++)
            {
                points.Add(new PointRecord(
                    c.X + random.NextDouble() * 20,
                    c.Y + random.NextDouble() * 20,
                    c.Z + random.NextDouble() * 5,
                    (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }
        return points;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
