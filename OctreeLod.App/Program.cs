using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using OctreeLod.App.Sources;
using OctreeLod.Core.Export;
using OctreeLod.Core.Model;
using OctreeLod.Core.SpacingEngine;

namespace OctreeLod.App;

public static class Program
{
    private const string InputPath = @"D:\Data\full_laser_9_2_8_(WithHeader).xyz";
    private const int BatchSize = 2500;

    // Toggle input source: true = geodetic lon/lat input, converted to local
    // ENU meters (LatLonPointCloudBatchSource, header row optional — set
    // LatLonHasHeader below). false = already-Cartesian easting/northing/depth
    // input, header row required (TextPointCloudBatchSource). Both take the
    // same InputPath/BatchSize above. Ignored when UseSyntheticSource is true.
    private const bool UseLatLonSource = false;
    private const bool LatLonHasHeader = true;

    // Synthetic waving-surface dataset instead of reading InputPath at all —
    // see WavingSurfacePointCloudBatchSource. AreaSize/PointSpacing below are
    // sized for ~20M points (4501x4501).
    private const bool UseSyntheticSource = true;
    private const double SyntheticAreaSize = 9000.0;
    private const double SyntheticPointSpacing = 2.0;
    private const int SyntheticLinesPerBatch = 4;

    public static async Task Main()
    {
        //string workDir = Path.Combine(Path.GetTempPath(), "OctreeLodDemo-" + Guid.NewGuid().ToString("N"));
        string workDir = Path.Combine("D:\\tmp", "OctreeLodDemo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"Working directory: {workDir}");

        IPointBatchSource source;
        GeoReference? reference = null;
        if (UseSyntheticSource)
        {
            var syntheticSource = new WavingSurfacePointCloudBatchSource(SyntheticAreaSize, SyntheticPointSpacing, SyntheticLinesPerBatch);
            Console.WriteLine($"Synthetic waving surface: {syntheticSource.PointsPerLine:N0} x {syntheticSource.LineCount:N0} points ({syntheticSource.TotalPointCount:N0} total), {SyntheticLinesPerBatch} lines/batch.");
            source = syntheticSource;

            // No real-world anchor for synthetic data, but 3D Tiles viewers
            // (deck.gl's Tile3DLayer in particular) hardcode point-cloud
            // tiles to a geospatial path: it converts the tile's local
            // Cartesian center as if it were an ECEF point on the WGS84
            // ellipsoid, regardless of any coordinateSystem prop passed to
            // the layer. With no root `transform` (i.e. no GeoReference),
            // that center is only tens of thousands of meters from the
            // origin — wildly inside the ~6,378,137m WGS84 ellipsoid, not
            // on its surface — producing a near-garbage lat/lon/height and
            // a broken per-tile transform (looks like severe z-fighting/
            // jitter, but is actually wrong geometry, not a precision
            // issue). An arbitrary anchor (equator/prime meridian here)
            // fixes this: with a real root transform, everything ends up
            // genuinely ECEF-scale, so that same conversion operates on an
            // actual near-surface point and produces a correct transform.
            reference = new GeoReference(latitudeDegrees: 0, longitudeDegrees: 0, heightMeters: 0);
        }
        else if (UseLatLonSource)
        {
            Console.WriteLine($"Reading points from: {InputPath}");
            var latLonSource = new LatLonPointCloudBatchSource(InputPath, BatchSize, LatLonHasHeader);
            Console.WriteLine($"Centroid (reference point): lat={latLonSource.Reference.LatitudeDegrees:F6} lon={latLonSource.Reference.LongitudeDegrees:F6}");
            reference = latLonSource.Reference;
            source = latLonSource;
        }
        else
        {
            Console.WriteLine($"Reading points from: {InputPath}");
            source = new TextPointCloudBatchSource(InputPath, BatchSize);
        }
        var batches = source.ReadBatches();
        await RunSpacingEngineAsync(workDir, reference, batches);
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
