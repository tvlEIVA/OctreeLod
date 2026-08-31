using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OctreeLod.Core.Export;
using OctreeLod.Core.Model;
using OctreeLod.Core.SpacingEngine;

namespace OctreeLod.Tests;

public class SpacingEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FullPipeline_TwoClustersAndDuplicateHotspot_ProducesValidTileset()
    {
        var options = new SpacingIngestionOptions
        {
            GridDivisions = 32,
            MaxSplitDepth = 40,
        };
        using var nodeStore = new NodePointFileStore(Path.Combine(_dir, "nodes"));
        var engine = new SpacingIngestionEngine(nodeStore, options);

        var allPoints = BuildSyntheticDataset();
        var random = new Random(123);
        int idx = 0;
        while (idx < allPoints.Count)
        {
            int size = random.Next(50, 300);
            size = Math.Min(size, allPoints.Count - idx);
            engine.IngestBatch(allPoints.GetRange(idx, size));
            idx += size;
        }

        engine.Flush();

        var logicalRoot = AdaptiveRootTrimmer.TrimToLogicalRoot(engine.Root);

        // Same shape assertion as the legacy pipeline's EndToEndTests: the
        // two clusters are far apart relative to their own extent, so the
        // branching point must have been found.
        Assert.NotSame(engine.Root, logicalRoot);
        int nonEmptyChildren = 0;
        for (int octant = 0; octant < 8; octant++)
        {
            var child = logicalRoot.Children[octant];
            if (child == null) continue;
            if (!(child.IsLeaf && child.PointCount == 0)) nonEmptyChildren++;
        }
        Assert.True(nonEmptyChildren >= 2, "logical root should be the real branching point between the two clusters");

        var rootPoints = nodeStore.ReadAll(logicalRoot.Id);
        Assert.True(rootPoints.Length > 0);
        Assert.True(rootPoints.Length <= allPoints.Count);

        string tilesDir = Path.Combine(_dir, "3dtiles");
        Tiles3DExporter.Export(logicalRoot, nodeStore, options.GridDivisions, tilesDir, TileRefine.Add);

        string tilesetPath = Path.Combine(tilesDir, "tileset.json");
        Assert.True(File.Exists(tilesetPath));
        var contentFiles = Directory.GetFiles(Path.Combine(tilesDir, "content"), "*.pnts");
        Assert.NotEmpty(contentFiles);

        // Spacing engine's node content is complement-only (a node's set
        // holds only what its children didn't already capture), so it's
        // meaningless without its ancestors also shown — must be ADD, never
        // REPLACE. MaxDepth raised: the duplicate hotspot in this dataset
        // cascades several tile levels deep, past JsonDocument's default of
        // 64.
        using var doc = JsonDocument.Parse(File.ReadAllText(tilesetPath), new JsonDocumentOptions { MaxDepth = 256 });
        Assert.Equal("ADD", doc.RootElement.GetProperty("root").GetProperty("refine").GetString());
    }

    // Same dataset shape as EndToEndTests.BuildSyntheticDataset: uniform
    // region + a tight duplicate-heavy cluster + a second, distant cluster
    // (forces real branching) on the same side of the world-scale root's
    // origin (exercises AdaptiveRootTrimmer's wrapper-level collapsing).
    private static List<PointRecord> BuildSyntheticDataset()
    {
        var random = new Random(55);
        var points = new List<PointRecord>();

        for (int i = 0; i < 2000; i++)
        {
            points.Add(new PointRecord(
                2_000_000 + random.NextDouble() * 50,
                800_000 + random.NextDouble() * 50,
                300_000 + random.NextDouble() * 10,
                (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        }

        var dupPoint = new PointRecord(2_000_025, 800_025, 300_005, 9, 9, 9);
        for (int i = 0; i < 150; i++)
            points.Add(dupPoint);

        for (int i = 0; i < 2000; i++)
        {
            points.Add(new PointRecord(
                1_000_000 + random.NextDouble() * 50,
                700_000 + random.NextDouble() * 50,
                150_000 + random.NextDouble() * 10,
                (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
        }

        return points;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
