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
    private const double SyntheticAreaSize = 90000.0;
    private const double SyntheticPointSpacing = 2.0;
    private const int SyntheticLinesPerBatch = 4;

    // Run RunSpacingEngineWithPreviewAsync instead of
    // plain RunSpacingEngineAsync — periodically re-exports tileset.json +
    // content mid-ingestion (point tilesDir's tileset.json at a viewer for a
    // live preview). Since the spacing engine has no separate merge phase,
    // engine.Root is a valid, exportable tree at any point — export just
    // reuses whatever's been accepted so far. Tiles3DExporter is called with
    // incremental:true so unchanged nodes' content files are reused instead
    // of rewritten (see OctreeNode.Dirty).
    //
    // Triggered by accumulated dirty-node count (engine.DirtyNodeCount),
    // not wall-clock time: a fixed time interval made preview cost spike as
    // ingestion sped up on a growing tree — a slow pass got skipped instead
    // of overlapping, so its backlog rolled into the next one, which then
    // took even longer, compounding. Triggering once PreviewDirtyNodeThreshold
    // new/changed nodes have piled up keeps each pass covering roughly the
    // same amount of new work regardless of how fast ingestion is running.
    //
    // Also directly controls how many swaps a live-viewing session sees
    // over the whole run: the exported tree only ever grows, so each swap's
    // tileset is bigger (and costs more browser-side memory to load) than
    // the last — raised from 10,000 to cut the total number of swaps for a
    // large dataset, giving the viewer's GC more breathing room between
    // them instead of climbing continuously (see Viewer/src/main.js).
    private const bool UseLivePreview = true;
    private const int PreviewDirtyNodeThreshold = 20_000;

    // Splits each export into linked external tilesets every this many
    // levels of depth, instead of one monolithic tileset.json describing
    // the whole tree — see Tiles3DExporter.Export's own doc comment. 0
    // disables it (single file, as before). Exists because a 3D Tiles
    // client (e.g. deck.gl's Tile3DLayer) eagerly constructs an in-memory
    // tile object for every node in whatever tileset.json it loads, a real
    // client-side cost that scales with total node count and isn't bounded
    // by any client-side setting — only bounding how much any one file
    // describes fixes it. Needed once the tree gets large; a small dataset
    // gains nothing from the extra files.
    private const int PartitionDepthInterval = 6;

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

        if (UseLivePreview)
            await RunSpacingEngineWithPreviewAsync(workDir, reference, batches);
        else
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
        Tiles3DExporter.Export(logicalRoot, nodeStore, gridDivisions: options.GridDivisions, tilesDir, TileRefine.Add, reference, partitionDepthInterval: PartitionDepthInterval);
        Console.WriteLine($"3D Tiles dataset written to: {tilesDir}");

        return Task.CompletedTask;
    }

    // Same as RunSpacingEngineAsync, plus a periodic live preview: every
    // PreviewIntervalMs, re-exports tileset.json + content from whatever's
    // been accepted so far (point tilesDir's tileset.json at a viewer while
    // this runs). See PreviewExporter for how that avoids blocking ingestion.
    private static async Task RunSpacingEngineWithPreviewAsync(string workDir, GeoReference? reference, IEnumerable<IReadOnlyList<PointRecord>> batches)
    {
        var options = new SpacingIngestionOptions
        {
            OnWarning = msg => Console.WriteLine($"[warn] {msg}"),
        };

        using var nodeStore = new NodePointFileStore(Path.Combine(workDir, "nodes"));
        var engine = new SpacingIngestionEngine(nodeStore, options);
        string tilesDir = Path.Combine(workDir, "3dtiles");

        Console.WriteLine($"Spacing engine: single-pass streaming ingest with live preview every {PreviewDirtyNodeThreshold:N0} dirty nodes (LOD decided at insertion, no merge phase, max {options.MaxInMemoryNodes:N0} nodes in memory)...");
        long totalPoints = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long lastReportMs = 0;
        var preview = new PreviewExporter(engine, nodeStore, options, tilesDir, reference);
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

            preview.TryTrigger();
        }
        Console.WriteLine();
        Console.WriteLine($"Ingested {totalPoints:N0} points into {engine.NodeCount:N0} nodes in {stopwatch.Elapsed:hh\\:mm\\:ss}.");

        await preview.WaitForInFlightAsync(); // let a still-running preview finish (and surface any exception) before the final export
        engine.Flush();

        var logicalRoot = AdaptiveRootTrimmer.TrimToLogicalRoot(engine.Root);
        var rootPoints = nodeStore.ReadAll(logicalRoot.Id);

        Console.WriteLine($"True geometric root id: {engine.Root.Id}");
        Console.WriteLine($"Logical (emitted) root id: {logicalRoot.Id}, representative point count: {rootPoints.Length}");
        Console.WriteLine($"Logical root bbox: min=({logicalRoot.Bbox.MinX:F1},{logicalRoot.Bbox.MinY:F1},{logicalRoot.Bbox.MinZ:F1}) size={logicalRoot.Bbox.Size:F1}");

        Console.WriteLine("Exporting 3D Tiles dataset...");
        Tiles3DExporter.Export(logicalRoot, nodeStore, gridDivisions: options.GridDivisions, tilesDir, TileRefine.Add, reference, incremental: true, partitionDepthInterval: PartitionDepthInterval);
        Console.WriteLine($"3D Tiles dataset written to: {tilesDir}");
    }

    // Periodically re-exports preview tileset metadata + content mid-ingestion
    // without blocking the ingest loop calling TryTrigger. Each preview gets
    // its own tileset_preview_NNNN.json (rather than overwriting one shared
    // file) so every snapshot stays on disk to page through afterward, while
    // content/ is still shared and reused across them (a node's .pnts file
    // never changes once written — see `incremental` on Tiles3DExporter).
    // Point a viewer at the latest tileset_preview_*.json under tilesDir for
    // a live preview. Only applies to the spacing engine: it has no separate
    // merge phase, so engine.Root is a valid, exportable tree at any point —
    // export just reuses whatever's been accepted so far.
    //
    // The expensive part (tree walk + .pnts/tileset.json I/O) runs on a
    // background Task so ingestion keeps going; only the fast,
    // bounded-to-resident-nodes engine.Flush() call happens inline, right
    // before handing off. Ingestion keeps mutating the live OctreeNode tree
    // (Children/PointCount/Dirty) the whole time that background task runs,
    // so the export walk operates on a detached CLONE taken synchronously at
    // trigger time (CloneSnapshot, still on the ingest thread, right after
    // Flush) instead of the live tree — the preview then reflects the octree
    // exactly as of the trigger moment, never a mix of pre- and
    // mid-ingestion state. That clone is a cheap in-memory field copy (no
    // I/O), same category of cost as Flush(); the disk-bound part stays
    // fully backgrounded. At most one preview task is ever in flight:
    // TryTrigger is a no-op if the prior one hasn't finished yet, rather
    // than piling up overlapping exports.
    private sealed class PreviewExporter
    {
        private readonly SpacingIngestionEngine _engine;
        private readonly NodePointFileStore _nodeStore;
        private readonly SpacingIngestionOptions _options;
        private readonly string _tilesDir;
        private readonly GeoReference? _reference;
        private Task? _inFlight;
        private int _previewCount;

        public PreviewExporter(SpacingIngestionEngine engine, NodePointFileStore nodeStore, SpacingIngestionOptions options, string tilesDir, GeoReference? reference)
        {
            _engine = engine;
            _nodeStore = nodeStore;
            _options = options;
            _tilesDir = tilesDir;
            _reference = reference;
        }

        public void TryTrigger()
        {
            if (PreviewDirtyNodeThreshold <= 0) return;
            if (_engine.DirtyNodeCount < PreviewDirtyNodeThreshold) return;
            if (_inFlight != null && !_inFlight.IsCompleted) return;

            _engine.Flush(); // cache -> nodeStore, so the exporter's ReadAll sees everything accepted so far
            var trimmedRoot = AdaptiveRootTrimmer.TrimToLogicalRoot(_engine.Root);

            // Dirty-at-snapshot pairs (clone, live): only these could
            // possibly get written by this export pass, so only these are
            // eligible to have their live counterpart's Dirty cleared
            // afterward — a node that was already clean at snapshot time is
            // never touched here, even if ingestion marks it dirty again
            // moments later.
            var dirtyPairs = new List<(OctreeNode Clone, OctreeNode Live)>();
            // Partition-boundary pairs (clone, live): every one of these
            // gets its nested tileset file unconditionally rewritten by
            // every Export call that reaches it (Tiles3DExporter doesn't
            // skip-if-unchanged for nested files the way it does for
            // content), so — unlike dirtyPairs above — propagating
            // TilesetVersion back afterward isn't gated on anything; it
            // always happened.
            var boundaryPairs = new List<(OctreeNode Clone, OctreeNode Live)>();
            var previewRoot = CloneSnapshot(trimmedRoot, dirtyPairs, boundaryPairs);

            int previewNumber = ++_previewCount;
            string tilesetFileName = $"tileset_preview_{previewNumber:D4}.json";
            Console.WriteLine($"\n[preview {previewNumber}] building ({dirtyPairs.Count:N0} dirty of {_engine.NodeCount:N0} nodes)...");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _inFlight = Task.Run(() =>
            {
                var stats = Tiles3DExporter.Export(previewRoot, _nodeStore, gridDivisions: _options.GridDivisions, _tilesDir, TileRefine.Add, _reference, incremental: true, tilesetFileName, PartitionDepthInterval);
                Console.WriteLine($"\n[preview {previewNumber}] walk={stats.WalkMs}ms content={stats.ContentWriteMs}ms ({stats.NodesWritten:N0} nodes) tileset={stats.TilesetMetadataWriteMs}ms ({stats.NestedTilesetsWritten:N0} nested)");

                // Every dirtyPairs entry got a real Export() write this pass
                // (that's exactly why it was collected — see CloneSnapshot),
                // so live.ContentVersion must always advance to match: the
                // file at that version now exists and is claimed, so the
                // next pass has to pick the version after it, not reuse it
                // (PntsWriter.WriteFile truncates/overwrites whatever's
                // already at its path — reusing a version number would
                // silently corrupt a file that already-published tileset
                // metadata, e.g. an earlier tileset_preview_NNNN.json, still
                // points to). Whether the node is fully caught-up (safe to
                // clear Dirty) is a SEPARATE question, gated separately below —
                // conflating the two was the original bug here.
                long clearedCount = 0;
                foreach (var (clone, live) in dirtyPairs)
                {
                    live.ContentVersion = clone.ContentVersion;

                    // Only clear Dirty if PointCount still matches the
                    // snapshot: equal means this pass's write really is
                    // current. If it grew, a new point landed during this
                    // export's run — content on disk is now stale again, so
                    // leave Dirty set (and don't count it as cleared) rather
                    // than silently losing that point from every future
                    // preview until something else re-dirties the node.
                    if (clone.Dirty || live.PointCount != clone.PointCount) continue;
                    live.Dirty = false;
                    clearedCount++;
                }
                _engine.NotifyNodesClean(clearedCount);

                // No dirty/point-count gating here — a boundary's nested
                // file is rewritten unconditionally every pass, so its
                // version always advances too; skipping this would repeat
                // the exact ContentVersion bug above, one field over.
                foreach (var (clone, live) in boundaryPairs)
                    live.TilesetVersion = clone.TilesetVersion;

                Console.WriteLine($"\n[preview {previewNumber}] done in {sw.Elapsed.TotalSeconds:F1}s -> {Path.Combine(_tilesDir, tilesetFileName)}");
            });
        }

        // Detached deep copy of `node`'s subtree — same OctreeNode type
        // Tiles3DExporter already knows how to walk, but with no shared
        // references back into the live tree, so ingestion mutating the live
        // tree afterward can't affect what this export sees. Collects
        // (clone, live) pairs for nodes dirty at snapshot time into
        // `dirtyPairs` (so the caller can propagate Dirty=false back to the
        // live tree for whichever of them actually get written) and for
        // partition-boundary nodes into `boundaryPairs` (so the caller can
        // propagate their bumped TilesetVersion back) — same depth rule
        // Tiles3DExporter itself uses to decide a boundary, computed
        // independently here since Tiles3DExporter operates on the clone
        // and has no way to hand live-tree references back on its own.
        private static OctreeNode CloneSnapshot(OctreeNode node, List<(OctreeNode Clone, OctreeNode Live)> dirtyPairs, List<(OctreeNode Clone, OctreeNode Live)> boundaryPairs)
        {
            var clone = new OctreeNode
            {
                Id = node.Id,
                Parent = null,
                Depth = node.Depth,
                Bbox = node.Bbox,
                IsLeaf = node.IsLeaf,
                PointCount = node.PointCount,
                Storage = node.Storage,
                Dirty = node.Dirty,
                ContentVersion = node.ContentVersion,
                TilesetVersion = node.TilesetVersion,
            };
            if (node.Dirty) dirtyPairs.Add((clone, node));
            if (PartitionDepthInterval > 0 && node.Depth > 0 && node.Depth % PartitionDepthInterval == 0)
                boundaryPairs.Add((clone, node));

            for (int octant = 0; octant < 8; octant++)
            {
                var child = node.Children[octant];
                if (child != null) clone.Children[octant] = CloneSnapshot(child, dirtyPairs, boundaryPairs);
            }
            return clone;
        }

        public Task WaitForInFlightAsync() => _inFlight ?? Task.CompletedTask;
    }
}
