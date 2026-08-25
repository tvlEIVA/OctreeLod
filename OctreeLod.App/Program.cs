using System;
using System.Collections.Generic;
using System.IO;
using OctreeLod.App.Sources;
using OctreeLod.Core.Export;
using OctreeLod.Core.Ingest;
using OctreeLod.Core.Merge;
using OctreeLod.Core.Model;

namespace OctreeLod.App;

public static class Program
{
    private const string InputPath = @"D:\Data\full_laser_9_2_8_(WithHeader).xyz";
    private const int BatchSize = 1500;

    public static async System.Threading.Tasks.Task Main()
    {
        string workDir = Path.Combine(Path.GetTempPath(), "OctreeLodDemo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"Working directory: {workDir}");

        var options = new OctreeIngestionOptions
        {
            SplitThreshold = 1000,
            OnWarning = msg => Console.WriteLine($"[warn] {msg}"),
        };

        var metadata = new InMemoryNodeMetadataStore();
        using var leafStore = new SlabPointStore(Path.Combine(workDir, "leaves.bin"), options.SplitThreshold);
        var engine = new OctreeIngestionEngine(metadata, leafStore, options);

        Console.WriteLine($"Reading points from: {InputPath}");
        var batches = new TextPointCloudBatchSource(InputPath, BatchSize).ReadBatches();

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
                Console.Write($"\r  {totalPoints:N0} points | {metadata.Count:N0} nodes | {rate:N0} pts/sec | {stopwatch.Elapsed:hh\\:mm\\:ss}   ");
                lastReportMs = stopwatch.ElapsedMilliseconds;
            }
        }
        Console.WriteLine();
        Console.WriteLine($"Ingested {totalPoints:N0} points into {metadata.Count:N0} nodes in {stopwatch.Elapsed:hh\\:mm\\:ss}.");

        Console.WriteLine("Phase 2: bottom-up merge...");
        var mergedStore = new MergedPointFileStore(Path.Combine(workDir, "merged"));
        var mergeEngine = new MergeEngine(metadata, leafStore, mergedStore, gridDivisions: 64, maxDegreeOfParallelism: Environment.ProcessorCount);
        await mergeEngine.MergeAsync(engine.RootId);

        long logicalRootId = AdaptiveRootTrimmer.TrimToLogicalRoot(metadata, engine.RootId);
        var logicalRoot = metadata.Get(logicalRootId);
        var rootPoints = mergedStore.ReadAll(logicalRootId);

        Console.WriteLine($"True geometric root id: {engine.RootId}");
        Console.WriteLine($"Logical (emitted) root id: {logicalRootId}, representative point count: {rootPoints.Length}");
        Console.WriteLine($"Logical root bbox: min=({logicalRoot.Bbox.MinX:F1},{logicalRoot.Bbox.MinY:F1},{logicalRoot.Bbox.MinZ:F1}) size={logicalRoot.Bbox.Size:F1}");

        Console.WriteLine("Exporting 3D Tiles dataset...");
        string tilesDir = Path.Combine(workDir, "3dtiles");
        Tiles3DExporter.Export(metadata, mergedStore, logicalRootId, gridDivisions: 64, tilesDir);
        Console.WriteLine($"3D Tiles dataset written to: {tilesDir}");
    }
}
