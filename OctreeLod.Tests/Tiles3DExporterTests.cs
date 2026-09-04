using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OctreeLod.Core.Export;
using OctreeLod.Core.Model;
using OctreeLod.Core.SpacingEngine;

namespace OctreeLod.Tests;

public class Tiles3DExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExportedTileset_ParsesAsValidJsonWithOnlyNonEmptyChildrenAndExistingContentFiles()
    {
        var options = new SpacingIngestionOptions { GridDivisions = 16, MaxSplitDepth = 40 };
        using var nodeStore = new NodePointFileStore(Path.Combine(_dir, "nodes"));
        var engine = new SpacingIngestionEngine(nodeStore, options);

        var random = new Random(21);
        engine.IngestBatch(MakeTwoClusterDataset(random));
        engine.Flush();

        var logicalRoot = AdaptiveRootTrimmer.TrimToLogicalRoot(engine.Root);

        string outputDir = Path.Combine(_dir, "tiles-out");
        Tiles3DExporter.Export(logicalRoot, nodeStore, options.GridDivisions, outputDir, TileRefine.Add);

        string tilesetPath = Path.Combine(outputDir, "tileset.json");
        Assert.True(File.Exists(tilesetPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(tilesetPath));
        var root = doc.RootElement;

        Assert.Equal("1.0", root.GetProperty("asset").GetProperty("version").GetString());
        var rootTile = root.GetProperty("root");
        Assert.Equal("ADD", rootTile.GetProperty("refine").GetString());

        int visitedTiles = 0;
        double parentGeometricError = double.PositiveInfinity;
        WalkAndVerify(rootTile, outputDir, ref visitedTiles, parentGeometricError);
        Assert.True(visitedTiles > 1, "expected more than just the root tile given two clusters");
    }

    [Fact]
    public void SingleLeafRoot_ExportsOneTileWithNoChildren()
    {
        // Well-separated points (300 units apart, comfortably clear of the
        // 250-unit root cell size) so every point is accepted directly at
        // Root and nothing overflows into a child — mirrors
        // SpacingIngestionEngineTests' WellSeparatedPoints scenario.
        var options = new SpacingIngestionOptions
        {
            WorldBounds = new BoundingCube(-1000, -1000, -1000, 2000),
            GridDivisions = 8,
        };
        using var nodeStore = new NodePointFileStore(Path.Combine(_dir, "nodes2"));
        var engine = new SpacingIngestionEngine(nodeStore, options);

        var random = new Random(5);
        for (int i = 0; i < 4; i++)
        {
            engine.IngestPoint(new PointRecord(
                i * 300.0, i * 300.0, i * 300.0,
                (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        }
        engine.Flush();

        var logicalRoot = AdaptiveRootTrimmer.TrimToLogicalRoot(engine.Root);
        Assert.True(logicalRoot.IsLeaf); // all well-separated, nothing pushed to a child

        string outputDir = Path.Combine(_dir, "tiles-out2");
        Tiles3DExporter.Export(logicalRoot, nodeStore, options.GridDivisions, outputDir, TileRefine.Add);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDir, "tileset.json")));
        var rootTile = doc.RootElement.GetProperty("root");

        // Leaf or not, geometricError is always Bbox.Size / GridDivisions —
        // a leaf's points are just as grid-spaced as any other node's.
        Assert.Equal(2000.0 / 8, rootTile.GetProperty("geometricError").GetDouble());
        Assert.False(rootTile.TryGetProperty("children", out _));
        Assert.True(File.Exists(Path.Combine(outputDir, "content", $"{logicalRoot.Id}.pnts")));
    }

    [Fact]
    public void Export_ChildBecomesPointerToLinkedExternalTileset()
    {
        var options = new SpacingIngestionOptions
        {
            WorldBounds = new BoundingCube(-50, -50, -50, 100),
            GridDivisions = 4,
            MaxSplitDepth = 10,
        };
        using var nodeStore = new NodePointFileStore(Path.Combine(_dir, "nodes-partitioned"));
        var engine = new SpacingIngestionEngine(nodeStore, options);

        var repeated = new PointRecord(1, 1, 1, 9, 9, 9);
        for (int i = 0; i < 8; i++) engine.IngestPoint(repeated);
        engine.Flush();

        string outputDir = Path.Combine(_dir, "tiles-partitioned");
        var stats = Tiles3DExporter.Export(engine.Root, nodeStore, options.GridDivisions, outputDir, TileRefine.Add);

        Assert.True(stats.NestedTilesetsWritten > 0);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDir, "tileset.json")));
        var rootTile = doc.RootElement.GetProperty("root");

        // Root's own content is inline; its child is a pure pointer — no
        // inline children, no own .pnts, content.uri is a sibling file.
        using var childEnumerator = rootTile.GetProperty("children").EnumerateArray().GetEnumerator();
        Assert.True(childEnumerator.MoveNext());
        var tile = childEnumerator.Current;
        Assert.False(tile.TryGetProperty("children", out _), "a pointer tile must not inline its children");
        string uri = tile.GetProperty("content").GetProperty("uri").GetString()!;
        Assert.EndsWith(".json", uri);

        string nestedPath = Path.Combine(outputDir, uri);
        Assert.True(File.Exists(nestedPath), $"missing nested tileset file: {nestedPath}");

        using var nestedDoc = JsonDocument.Parse(File.ReadAllText(nestedPath));
        var nestedRoot = nestedDoc.RootElement.GetProperty("root");
        Assert.Equal("ADD", nestedRoot.GetProperty("refine").GetString());

        // The nested root represents the SAME child node, now holding its
        // own real content (not redirected again).
        string nestedContentUri = nestedRoot.GetProperty("content").GetProperty("uri").GetString()!;
        Assert.EndsWith(".pnts", nestedContentUri);
        Assert.True(File.Exists(Path.Combine(outputDir, nestedContentUri)));
    }

    // Every tile here is a document root (own real .pnts content); every
    // child of it is a pointer to ITS own nested document (see
    // Tiles3DExporter.BuildBoundaryPointerTile) — never inlined. So walking
    // the whole tree means following each child pointer into its nested
    // file and recursing into that file's own root.
    private static void WalkAndVerify(JsonElement tile, string outputDir, ref int visitedCount, double parentGeometricError)
    {
        visitedCount++;

        double geometricError = tile.GetProperty("geometricError").GetDouble();
        Assert.True(geometricError <= parentGeometricError,
            $"geometricError should be non-increasing with depth (parent {parentGeometricError}, this {geometricError})");

        string uri = tile.GetProperty("content").GetProperty("uri").GetString()!;
        Assert.EndsWith(".pnts", uri);
        string contentPath = Path.Combine(outputDir, uri);
        Assert.True(File.Exists(contentPath), $"missing content file: {contentPath}");

        var box = tile.GetProperty("boundingVolume").GetProperty("box");
        Assert.Equal(12, box.GetArrayLength());

        if (tile.TryGetProperty("children", out var children))
        {
            int count = 0;
            foreach (var child in children.EnumerateArray())
            {
                count++;
                string childUri = child.GetProperty("content").GetProperty("uri").GetString()!;
                Assert.EndsWith(".json", childUri);
                string nestedPath = Path.Combine(outputDir, childUri);
                Assert.True(File.Exists(nestedPath), $"missing nested tileset file: {nestedPath}");

                using var nestedDoc = JsonDocument.Parse(File.ReadAllText(nestedPath));
                WalkAndVerify(nestedDoc.RootElement.GetProperty("root"), outputDir, ref visitedCount, geometricError);
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
