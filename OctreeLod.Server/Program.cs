using System.Numerics;
using OctreeLod.App.Sources;
using OctreeLod.Core.Export;
using OctreeLod.Core.Model;
using OctreeLod.Core.SpacingEngine;

namespace OctreeLod.Server;

// Ingests the same way OctreeLod.App does, but instead of periodically
// writing tileset.json/.pnts snapshots to disk (OctreeLod.App's live
// preview — see its own doc comments), serves the octree LIVE over HTTP:
// every request is generated fresh, straight from whatever the tree
// currently looks like. No file writes, no ContentVersion/TilesetVersion,
// no accumulating preview files to page through — a client (e.g. the
// deck.gl viewer in Viewer/) just always sees current state on every fetch.
//
// OctreeLod.App is left untouched and still runs standalone; this is a
// separate, parallel way to watch a run, not a replacement.
public static class Program
{
    private const string InputPath = @"D:\Data\full_laser_9_2_8_(WithHeader).xyz";
    private const int BatchSize = 2500;

    private const bool UseLatLonSource = false;
    private const bool LatLonHasHeader = true;

    private const bool UseSyntheticSource = true;
    private const double SyntheticAreaSize = 90000.0;
    private const double SyntheticPointSpacing = 2.0;
    private const int SyntheticLinesPerBatch = 2;

    // How often (in ingested points) the ingestion loop persists its
    // in-memory cell cache to disk (SpacingIngestionEngine.Persist — writes
    // without evicting, unlike Flush). HTTP handlers read content only via
    // NodePointFileStore (see the /content handler below) — never the live
    // cache directly, since that's touched by ingestion on literally every
    // point and isn't safe to also read concurrently from request threads
    // without adding locking to a very hot path. So a node's latest points
    // aren't visible to a request until its data has been persisted at
    // least once. Root and other near-root nodes are touched by nearly
    // every point and so stay cache-resident regardless of eviction
    // pressure (same reasoning as SpacingIngestionEngine's own
    // MaxInMemoryNodes docs) — without periodic persistence, THEIR content
    // would never reach disk at all until ingestion fully ends.
    private const int PersistEveryPoints = 200_000;

    private const string ListenUrl = "http://localhost:5251";

    public static async Task Main()
    {
        var options = new SpacingIngestionOptions
        {
            OnWarning = msg => Console.WriteLine($"[warn] {msg}"),
        };

        //string workDir = Path.Combine(Path.GetTempPath(), "OctreeLodServer-" + Guid.NewGuid().ToString("N"));
        string workDir = Path.Combine("D:\\tmp", "OctreeLodServer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        Console.WriteLine($"Node store: {workDir}");

        using var nodeStore = new NodePointFileStore(Path.Combine(workDir, "nodes"));
        var engine = new SpacingIngestionEngine(nodeStore, options);

        IPointBatchSource source;
        GeoReference? reference = null;
        if (UseSyntheticSource)
        {
            var syntheticSource = new WavingSurfacePointCloudBatchSource(SyntheticAreaSize, SyntheticPointSpacing, SyntheticLinesPerBatch);
            Console.WriteLine($"Synthetic waving surface: {syntheticSource.PointsPerLine:N0} x {syntheticSource.LineCount:N0} points ({syntheticSource.TotalPointCount:N0} total).");
            source = syntheticSource;

            // No real-world anchor for synthetic data, but deck.gl's
            // Tile3DLayer hardcodes point-cloud tiles to a geospatial path
            // regardless of any client-side setting — see
            // OctreeLod.App/Program.cs's identical comment for the full
            // explanation. Same fix: an arbitrary anchor.
            reference = new GeoReference(latitudeDegrees: 0, longitudeDegrees: 0, heightMeters: 0);
        }
        else if (UseLatLonSource)
        {
            Console.WriteLine($"Reading points from: {InputPath}");
            var latLonSource = new LatLonPointCloudBatchSource(InputPath, BatchSize, LatLonHasHeader);
            reference = latLonSource.Reference;
            source = latLonSource;
        }
        else
        {
            Console.WriteLine($"Reading points from: {InputPath}");
            source = new TextPointCloudBatchSource(InputPath, BatchSize);
        }

        var app = BuildApp(engine, nodeStore, options, reference);
        var serverTask = app.RunAsync(ListenUrl);
        Console.WriteLine($"Live tile server listening on {ListenUrl}");
        Console.WriteLine($"Point deck.gl's Tile3DLayer at {ListenUrl}/tileset.json (see Viewer/)");

        // Ingestion runs on THIS thread only — Persist()/Flush() and the
        // octree mutations they trigger never happen anywhere else, so
        // there's no new concurrency to reason about beyond what
        // NodePointFileStore and LiveTilesetBuilder's own doc comments
        // already cover for concurrent HTTP reads.
        long totalPoints = 0;
        long lastPersistAtPoints = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long lastReportMs = 0;
        foreach (var batch in source.ReadBatches())
        {
            engine.IngestBatch(batch);
            totalPoints += batch.Count;

            if (totalPoints - lastPersistAtPoints >= PersistEveryPoints)
            {
                engine.Persist(); // write current content to disk WITHOUT evicting it — see PagedCellMapCache.Persist
                lastPersistAtPoints = totalPoints;
            }

            if (stopwatch.ElapsedMilliseconds - lastReportMs >= 500)
            {
                double rate = totalPoints / stopwatch.Elapsed.TotalSeconds;
                Console.Write($"\r  {totalPoints:N0} points | {engine.NodeCount:N0} nodes | {rate:N0} pts/sec | {stopwatch.Elapsed:hh\\:mm\\:ss}   ");
                lastReportMs = stopwatch.ElapsedMilliseconds;
            }
        }
        Console.WriteLine();
        engine.Flush();
        Console.WriteLine($"Ingestion complete: {totalPoints:N0} points, {engine.NodeCount:N0} nodes. Server still running — Ctrl+C to stop.");

        await serverTask;
    }

    private static WebApplication BuildApp(SpacingIngestionEngine engine, NodePointFileStore nodeStore, SpacingIngestionOptions options, GeoReference? reference)
    {
        var builder = WebApplication.CreateBuilder();
        // Quiet, not silent: routine per-request logging would fight with
        // ingestion's own \r-progress line, but an unhandled exception must
        // still be visible — a fully cleared logging pipeline is exactly
        // what hid the AllowSynchronousIO exception below during testing.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        // Wide open — this serves a local viewer (Viewer/) that may run
        // from a different origin/port, not a public service.
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                // Local dev tool, not a public service — printing the full
                // exception is more useful here than a bare 500.
                Console.WriteLine($"\n[server] {context.Request.Path} -> {ex}");
                throw;
            }
        });

        app.MapGet("/", () => "OctreeLod live tile server — see /tileset.json");

        // GET *and* HEAD on every data endpoint: MapGet alone only
        // registers GET — ASP.NET Core doesn't fall back to it for HEAD the
        // way some frameworks do, it 405s — and the viewer (Viewer/) polls
        // with `fetch(url, {method: 'HEAD'})` to check existence before
        // switching. HEAD short-circuits before doing any tree-walk/disk
        // read work, same as a real HEAD implementation should.
        string[] getAndHead = { "GET", "HEAD" };

        app.MapMethods("/tileset.json", getAndHead, (HttpRequest request, HttpResponse response) =>
        {
            response.Headers["Cache-Control"] = "no-store";
            if (HttpMethods.IsHead(request.Method)) return Results.Ok();

            var logicalRoot = AdaptiveRootTrimmer.TrimToLogicalRoot(engine.Root);
            var document = LiveTilesetBuilder.BuildDocument(logicalRoot, options.GridDivisions, TileRefine.Add);
            if (reference.HasValue) ApplyGeoReference(document, reference.Value);
            return Results.Text(MinimalJsonWriter.Write(document), "application/json");
        });

        app.MapMethods("/tileset/node/{id}.json", getAndHead, (string id, HttpRequest request, HttpResponse response) =>
        {
            if (!BigInteger.TryParse(id, out var nodeId)) return Results.BadRequest();
            var node = LiveTilesetBuilder.FindNodeById(engine.Root, nodeId);
            if (node == null) return Results.NotFound();

            response.Headers["Cache-Control"] = "no-store";
            if (HttpMethods.IsHead(request.Method)) return Results.Ok();

            var document = LiveTilesetBuilder.BuildDocument(node, options.GridDivisions, TileRefine.Add);
            return Results.Text(MinimalJsonWriter.Write(document), "application/json");
        });

        app.MapMethods("/content/{id}.pnts", getAndHead, async (string id, HttpRequest request, HttpResponse response) =>
        {
            if (!BigInteger.TryParse(id, out var nodeId)) { response.StatusCode = 400; return; }
            var node = LiveTilesetBuilder.FindNodeById(engine.Root, nodeId);
            if (node == null) { response.StatusCode = 404; return; }

            // A node's own PointCount only grows when a point is actually
            // stored there (see SpacingIngestionEngine.IngestPoint), so it's
            // an exact version for this node's content bytes — reuse it as
            // the ETag instead of a separate counter. "no-cache" (not
            // "no-store") lets the browser still cache the body, but forces
            // it to revalidate via If-None-Match on every fetch; unchanged
            // nodes then cost a 304 with no body instead of a full re-read +
            // re-serialize + resend of every tile every poll.
            var etag = $"\"{nodeId}-{node.PointCount}\"";
            response.Headers["Cache-Control"] = "no-cache";
            response.Headers["ETag"] = etag;
            if (request.Headers["If-None-Match"] == etag)
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            response.ContentType = "application/octet-stream";
            if (HttpMethods.IsHead(request.Method)) return;

            var points = nodeStore.ReadAll(nodeId);

            // Kestrel disallows synchronous writes to the response body by
            // default (AllowSynchronousIO), but PntsWriter.WriteTo writes
            // synchronously (it's shared with Tiles3DExporter's plain
            // FileStream case, where that's fine) — so build the small
            // .pnts payload into an in-memory buffer first (synchronous
            // writes to a MemoryStream are always fine, no I/O involved),
            // then copy that buffer to the real response asynchronously.
            using var buffer = new MemoryStream();
            PntsWriter.WriteTo(buffer, node.Bbox, points);
            buffer.Position = 0;
            await buffer.CopyToAsync(response.Body);
        });

        return app;
    }

    private static void ApplyGeoReference(Dictionary<string, object> document, GeoReference geoReference)
    {
        var matrix = EcefTransform.ComputeLocalToEcefMatrix(geoReference);
        var matrixJson = new List<object>(matrix.Length);
        foreach (var v in matrix) matrixJson.Add(v);
        ((Dictionary<string, object>)document["root"])["transform"] = matrixJson;
    }
}
