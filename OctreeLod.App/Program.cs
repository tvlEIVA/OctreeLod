using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using OctreeLod.App.Sources;
using OctreeLod.Core.Export;
using OctreeLod.Core.Model;
using OctreeLod.Core.SpacingEngine;
using OctreeLod.Core.SplitMergeEngine.Ingest;
using OctreeLod.Core.SplitMergeEngine.Merge;

namespace OctreeLod.App;

public static class Program
{
    private const string InputPath = @"D:\Data\full_laser_9_2_8_(WithHeader).xyz";
    private const int BatchSize = 1500;

    // Toggle input source: true = geodetic lon/lat input, converted to local
    // ENU meters (LatLonPointCloudBatchSource, header row optional — set
    // LatLonHasHeader below). false = already-Cartesian easting/northing/depth
    // input, header row required (TextPointCloudBatchSource). Both take the
    // same InputPath/BatchSize above.
    private const bool UseLatLonSource = false;
    private const bool LatLonHasHeader = true;

    // Toggle between the legacy split+merge pipeline (OctreeIngestionEngine
    // + MergeEngine) and the spacing-based single-pass engine
    // (SpacingIngestionEngine) — see README "How it works" for the
    // difference.
    private const bool UseSpacingEngine = false;

    public static async Task Main()
    {
        string workDir = Path.Combine(Path.GetTempPath(), "OctreeLodDemo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"Working directory: {workDir}");

        Console.WriteLine($"Reading points from: {InputPath}");

        IPointBatchSource source;
        GeoReference? reference = null;
        if (UseLatLonSource)
        {
            var latLonSource = new LatLonPointCloudBatchSource(InputPath, BatchSize, LatLonHasHeader);
            Console.WriteLine($"Centroid (reference point): lat={latLonSource.Reference.LatitudeDegrees:F6} lon={latLonSource.Reference.LongitudeDegrees:F6}");
            reference = latLonSource.Reference;
            source = latLonSource;
        }
        else
        {
            source = new TextPointCloudBatchSource(InputPath, BatchSize);
        }
        var batches = source.ReadBatches();

        if (UseSpacingEngine)
            await RunSpacingEngineAsync(workDir, reference, batches);
        else
            await RunLegacyPipelineAsync(workDir, reference, batches);
    }

    private static async Task RunLegacyPipelineAsync(string workDir, GeoReference? reference, IEnumerable<IReadOnlyList<PointRecord>> batches)
    {
        var options = new OctreeIngestionOptions
        {
            SplitThreshold = 1000,
            OnWarning = msg => Console.WriteLine($"[warn] {msg}"),
        };

        using var leafStore = new SlabPointStore(Path.Combine(workDir, "leaves.bin"), options.SplitThreshold);
        var engine = new OctreeIngestionEngine(leafStore, options);

        Console.WriteLine("Phase 1: streaming ingest...");
        long totalPoints = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long lastReportMs = 0;
        foreach (var batch in batches)
        {
            engine.IngestBatch(batch);
            totalPoints += batch.Count;

            if (stopwatch.ElapsedMilliseconds - lastReportMs >= 500)
            {
                double rate = totalPoints / stopwatch.Elapsed.TotalSeconds;
                Console.Write($"\r  {totalPoints:N0} points | {engine.NodeCount:N0} nodes | {rate:N0} pts/sec | {stopwatch.Elapsed:hh\\:mm\\:ss}   ");
                lastReportMs = stopwatch.ElapsedMilliseconds;
            }
        }
        Console.WriteLine();
        Console.WriteLine($"Ingested {totalPoints:N0} points into {engine.NodeCount:N0} nodes in {stopwatch.Elapsed:hh\\:mm\\:ss}.");

        Console.WriteLine("Phase 2: bottom-up merge...");
        using var mergedStore = new NodePointFileStore(Path.Combine(workDir, "merged"));
        var mergeEngine = new MergeEngine(leafStore, mergedStore, gridDivisions: 64, maxDegreeOfParallelism: Environment.ProcessorCount);
        await mergeEngine.MergeAsync(engine.Root);

        var logicalRoot = AdaptiveRootTrimmer.TrimToLogicalRoot(engine.Root);
        var rootPoints = mergedStore.ReadAll(logicalRoot.Id);

        Console.WriteLine($"True geometric root id: {engine.Root.Id}");
        Console.WriteLine($"Logical (emitted) root id: {logicalRoot.Id}, representative point count: {rootPoints.Length}");
        Console.WriteLine($"Logical root bbox: min=({logicalRoot.Bbox.MinX:F1},{logicalRoot.Bbox.MinY:F1},{logicalRoot.Bbox.MinZ:F1}) size={logicalRoot.Bbox.Size:F1}");

        Console.WriteLine("Exporting 3D Tiles dataset...");
        string tilesDir = Path.Combine(workDir, "3dtiles");
        Tiles3DExporter.Export(logicalRoot, mergedStore, gridDivisions: 64, tilesDir, TileRefine.Replace, reference);
        Console.WriteLine($"3D Tiles dataset written to: {tilesDir}");
    }

    private static Task RunSpacingEngineAsync(string workDir, GeoReference? reference, IEnumerable<IReadOnlyList<PointRecord>> batches)
    {
        var options = new SpacingIngestionOptions
        {
            OnWarning = msg => Console.WriteLine($"[warn] {msg}"),
        };

        using var nodeStore = new NodePointFileStore(Path.Combine(workDir, "nodes"));
        var engine = new SpacingIngestionEngine(nodeStore, options);

        Console.WriteLine($"Spacing engine: single-pass streaming ingest (LOD decided at insertion, no merge phase, max {options.MaxInMemoryNodes:N0} nodes in memory)...");
        long totalPoints = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long lastReportMs = 0;
        foreach (var batch in batches)
        {
            engine.IngestBatch(batch);
            totalPoints += batch.Count;

            if (stopwatch.ElapsedMilliseconds - lastReportMs >= 500)
            {
                double rate = totalPoints / stopwatch.Elapsed.TotalSeconds;
                Console.Write($"\r  {totalPoints:N0} points | {engine.NodeCount:N0} nodes | {rate:N0} pts/sec | {stopwatch.Elapsed:hh\\:mm\\:ss}   ");
                lastReportMs = stopwatch.ElapsedMilliseconds;
            }
        }
        Console.WriteLine();
        Console.WriteLine($"Ingested {totalPoints:N0} points into {engine.NodeCount:N0} nodes in {stopwatch.Elapsed:hh\\:mm\\:ss}.");

        engine.Flush();

        var logicalRoot = AdaptiveRootTrimmer.TrimToLogicalRoot(engine.Root);
        var rootPoints = nodeStore.ReadAll(logicalRoot.Id);

        Console.WriteLine($"True geometric root id: {engine.Root.Id}");
        Console.WriteLine($"Logical (emitted) root id: {logicalRoot.Id}, representative point count: {rootPoints.Length}");
        Console.WriteLine($"Logical root bbox: min=({logicalRoot.Bbox.MinX:F1},{logicalRoot.Bbox.MinY:F1},{logicalRoot.Bbox.MinZ:F1}) size={logicalRoot.Bbox.Size:F1}");

        Console.WriteLine("Exporting 3D Tiles dataset...");
        string tilesDir = Path.Combine(workDir, "3dtiles");
        Tiles3DExporter.Export(logicalRoot, nodeStore, gridDivisions: options.GridDivisions, tilesDir, TileRefine.Add, reference);
        Console.WriteLine($"3D Tiles dataset written to: {tilesDir}");

        return Task.CompletedTask;
    }
}
