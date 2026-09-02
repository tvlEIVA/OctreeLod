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

        Assert.Equal(0, rootTile.GetProperty("geometricError").GetDouble());
        Assert.False(rootTile.TryGetProperty("children", out _));
        Assert.True(File.Exists(Path.Combine(outputDir, "content", $"{logicalRoot.Id}_v1.pnts")));
    }

    [Fact]
    public void PartitionedExport_BoundaryNodeBecomesPointerToLinkedExternalTileset()
    {
        // Identical repeated point forces a straight single-child chain
        // (each level: rejected -> pushed one deeper), so with
        // partitionDepthInterval 2 the first boundary is deterministically
        // exactly 2 levels down from root.
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
        const int partitionDepthInterval = 2;
        var stats = Tiles3DExporter.Export(engine.Root, nodeStore, options.GridDivisions, outputDir, TileRefine.Add, partitionDepthInterval: partitionDepthInterval);

        Assert.True(stats.NestedTilesetsWritten > 0);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDir, "tileset.json")));
        var tile = doc.RootElement.GetProperty("root");
        for (int depth = 0; depth < partitionDepthInterval; depth++)
        {
            Assert.True(tile.TryGetProperty("children", out var children), $"expected a child at depth {depth}");
            using var childEnumerator = children.EnumerateArray().GetEnumerator();
            Assert.True(childEnumerator.MoveNext());
            tile = childEnumerator.Current;
        }

        // The boundary tile itself: pure pointer, no inline children, no
        // own .pnts — content.uri is a sibling tileset file instead.
        Assert.False(tile.TryGetProperty("children", out _), "a boundary tile must not inline its children");
        string uri = tile.GetProperty("content").GetProperty("uri").GetString()!;
        Assert.EndsWith(".json", uri);

        string nestedPath = Path.Combine(outputDir, uri);
        Assert.True(File.Exists(nestedPath), $"missing nested tileset file: {nestedPath}");

        using var nestedDoc = JsonDocument.Parse(File.ReadAllText(nestedPath));
        var nestedRoot = nestedDoc.RootElement.GetProperty("root");
        Assert.Equal("ADD", nestedRoot.GetProperty("refine").GetString());

        // The nested root represents the SAME boundary node, now holding
        // its own real content (not redirected again).
        string nestedContentUri = nestedRoot.GetProperty("content").GetProperty("uri").GetString()!;
        Assert.EndsWith(".pnts", nestedContentUri);
        Assert.True(File.Exists(Path.Combine(outputDir, nestedContentUri)));
    }

    [Fact]
    public void PartitionedExport_ReExport_UnchangedTreeReusesTheSameNestedTilesetVersion()
    {
        var (engine, nodeStore, options) = BuildPartitionedTestTree(Path.Combine(_dir, "nodes-partitioned2"));
        const int partitionDepthInterval = 2;
        string outputDir = Path.Combine(_dir, "tiles-partitioned2");
        Tiles3DExporter.Export(engine.Root, nodeStore, options.GridDivisions, outputDir, TileRefine.Add, incremental: true, partitionDepthInterval: partitionDepthInterval);

        string firstUri = FirstBoundaryTilesetUri(outputDir, partitionDepthInterval);
        string firstNestedFile = Path.Combine(outputDir, firstUri);
        byte[] firstBytes = File.ReadAllBytes(firstNestedFile);
        Assert.EndsWith("_v1.json", firstUri);

        // Nothing changed in the tree between calls — the boundary's nested
        // file must be reused as-is, not rewritten under a new version.
        // Without this, every preview pass rewrites EVERY boundary's file
        // regardless of whether it changed, so total nested-write cost
        // grows with the whole tree's boundary count on every single pass.
        Tiles3DExporter.Export(engine.Root, nodeStore, options.GridDivisions, outputDir, TileRefine.Add, incremental: true, partitionDepthInterval: partitionDepthInterval);

        string secondUri = FirstBoundaryTilesetUri(outputDir, partitionDepthInterval);
        Assert.Equal(firstUri, secondUri);
        Assert.Equal(firstBytes, File.ReadAllBytes(firstNestedFile));

        nodeStore.Dispose();
    }

    [Fact]
    public void PartitionedExport_ReExport_ChangedSubtreeVersionsNestedTilesetWithoutTouchingThePreviousFile()
    {
        var (engine, nodeStore, options) = BuildPartitionedTestTree(Path.Combine(_dir, "nodes-partitioned3"));
        const int partitionDepthInterval = 2;
        string outputDir = Path.Combine(_dir, "tiles-partitioned3");
        Tiles3DExporter.Export(engine.Root, nodeStore, options.GridDivisions, outputDir, TileRefine.Add, incremental: true, partitionDepthInterval: partitionDepthInterval);

        string firstUri = FirstBoundaryTilesetUri(outputDir, partitionDepthInterval);
        string firstNestedFile = Path.Combine(outputDir, firstUri);
        byte[] firstBytes = File.ReadAllBytes(firstNestedFile);
        Assert.EndsWith("_v1.json", firstUri);

        // One more of the same repeated point continues the existing
        // single-child chain one level deeper (past the boundary, into its
        // subtree) — dirtying a DESCENDANT of the boundary node, not the
        // boundary itself, so this exercises "changed" bubbling up through
        // BuildTileContent's children loop, not just a node reporting its
        // own change. (Dirtying an ANCESTOR of the boundary instead — e.g.
        // Root — would NOT trigger a rewrite: bubbling only flows upward
        // within a subtree, so Root's own dirty state is irrelevant to
        // whether the boundary two levels below it needs rewriting.)
        engine.IngestPoint(new PointRecord(1, 1, 1, 9, 9, 9));
        engine.Flush();
        Tiles3DExporter.Export(engine.Root, nodeStore, options.GridDivisions, outputDir, TileRefine.Add, incremental: true, partitionDepthInterval: partitionDepthInterval);

        string secondUri = FirstBoundaryTilesetUri(outputDir, partitionDepthInterval);
        Assert.EndsWith("_v2.json", secondUri);
        Assert.True(File.Exists(firstNestedFile));
        Assert.Equal(firstBytes, File.ReadAllBytes(firstNestedFile));
        Assert.True(File.Exists(Path.Combine(outputDir, secondUri)));

        nodeStore.Dispose();
    }

    private static (SpacingIngestionEngine Engine, NodePointFileStore NodeStore, SpacingIngestionOptions Options) BuildPartitionedTestTree(string nodeStorePath)
    {
        var options = new SpacingIngestionOptions
        {
            WorldBounds = new BoundingCube(-50, -50, -50, 100),
            GridDivisions = 4,
            MaxSplitDepth = 10,
        };
        var nodeStore = new NodePointFileStore(nodeStorePath);
        var engine = new SpacingIngestionEngine(nodeStore, options);

        // Identical repeated point forces a straight single-child chain
        // (each level: rejected -> pushed one deeper), so with
        // partitionDepthInterval 2 the first boundary is deterministically
        // exactly 2 levels down from root.
        var repeated = new PointRecord(1, 1, 1, 9, 9, 9);
        for (int i = 0; i < 8; i++) engine.IngestPoint(repeated);
        engine.Flush();

        return (engine, nodeStore, options);
    }

    // Walks tileset.json down exactly partitionDepthInterval levels (the
    // same deterministic single-child-chain shape both partitioning tests
    // rely on) and returns that first boundary tile's content.uri.
    private static string FirstBoundaryTilesetUri(string outputDir, int partitionDepthInterval)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDir, "tileset.json")));
        var tile = doc.RootElement.GetProperty("root");
        for (int depth = 0; depth < partitionDepthInterval; depth++)
        {
            using var childEnumerator = tile.GetProperty("children").EnumerateArray().GetEnumerator();
            childEnumerator.MoveNext();
            tile = childEnumerator.Current;
        }
        return tile.GetProperty("content").GetProperty("uri").GetString()!;
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
