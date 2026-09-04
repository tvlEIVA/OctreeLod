using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.Export;

// A node's content set is either self-contained (a complete, if coarse,
// sample of its whole spatial footprint — safe to swap in place of its
// children, REPLACE) or complementary (only the points its children didn't
// already capture — meaningless without its ancestors' content also shown,
// requires ADD). Which one a given engine produces is a property of how it
// builds node content, not a per-dataset choice — see call sites.
public enum TileRefine
{
    Add,
    Replace,
}

// Exports a built octree (from either engine — legacy split+merge or
// spacing-based) as a 3D Tiles dataset: tileset.json + one .pnts file per
// emitted node under content/. Every non-empty node (leaf or internal) gets
// real content, since both engines produce a genuine representative sample
// at every LOD level — unlike typical BVH tilesets with empty non-leaf
// placeholders.
//
// Every node's own content becomes the root of its own
// `tileset_node_{id}.json`, and its entry in the parent file is a pure
// pointer (`content.uri` -> that nested file, no inline `children`) — the
// standard 3D Tiles "external tileset" mechanism. A client (e.g. deck.gl's
// Tile3DLayer) fetches and parses each node's file lazily, only once
// traversal actually reaches it, instead of eagerly constructing an
// in-memory tile object for every node in the entire tree on every load —
// real, unavoidable client-side cost that scales with total node count and
// isn't bounded by any client-side setting otherwise.
public static class Tiles3DExporter
{
    // `tilesetFileName`: which tileset metadata file to write under
    // outputDirectory — defaults to "tileset.json".
    public static ExportStats Export(
        OctreeNode root,
        INodePointStore mergedStore,
        int gridDivisions,
        string outputDirectory,
        TileRefine refine,
        GeoReference? geoReference = null,
        string tilesetFileName = "tileset.json")
    {
        string contentDir = Path.Combine(outputDirectory, "content");
        Directory.CreateDirectory(contentDir);

        // Deciding tree structure (geometricError, box, which node gets
        // which file, etc.) is a fast, sequential, in-memory walk — it
        // doesn't need to wait on any I/O, so this phase only queues work:
        // `pending` for per-node content writes, and `pendingTilesets` for
        // nested external-tileset documents. Both phases below run AFTER
        // the walk finishes and write in parallel — nested tileset files
        // specifically must not be written before the content they
        // reference exists, or a client could briefly fetch a valid-looking
        // nested tileset whose .pnts content 404s.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pending = new List<PendingWrite>();
        var pendingTilesets = new List<PendingTilesetWrite>();
        string refineJson = refine == TileRefine.Add ? "ADD" : "REPLACE";
        var rootTile = BuildTileContent(root, gridDivisions, outputDirectory, isRoot: true, refineJson, pending, pendingTilesets);
        long walkMs = sw.ElapsedMilliseconds;

        sw.Restart();
        if (pending.Count > 0)
        {
            // Each iteration is I/O-bound (one small file write) rather
            // than CPU-bound, so a thread here spends most of its time
            // blocked on disk, not computing — capping concurrency at core
            // count leaves most of the disk's actual queue depth unused.
            // NodePointFileStore.ReadAll no longer shares a single locked
            // handle (see its own per-thread read handle), so there's
            // nothing left serializing these beyond the disk itself; go
            // well above core count to actually exercise it.
            var writeOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 8 };
            Parallel.ForEach(pending, writeOptions, item =>
            {
                var points = mergedStore.ReadAll(item.Node.Id);
                PntsWriter.WriteFile(item.ContentPath, item.Node.Bbox, points);
            });
        }
        long contentWriteMs = sw.ElapsedMilliseconds;

        sw.Restart();
        if (pendingTilesets.Count > 0)
        {
            var writeOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 8 };
            Parallel.ForEach(pendingTilesets, writeOptions, item =>
            {
                File.WriteAllText(item.Path, MinimalJsonWriter.Write(item.Document));
            });
        }
        long nestedTilesetWriteMs = sw.ElapsedMilliseconds;

        sw.Restart();
        if (geoReference.HasValue)
        {
            var matrix = EcefTransform.ComputeLocalToEcefMatrix(geoReference.Value);
            var matrixJson = new List<object>(matrix.Length);
            foreach (var v in matrix) matrixJson.Add(v);
            rootTile.Json["transform"] = matrixJson;
        }

        var tileset = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = "1.0" },
            ["geometricError"] = rootTile.GeometricError,
            ["root"] = rootTile.Json,
        };

        File.WriteAllText(Path.Combine(outputDirectory, tilesetFileName), MinimalJsonWriter.Write(tileset));
        long tilesetMetadataWriteMs = sw.ElapsedMilliseconds + nestedTilesetWriteMs;

        return new ExportStats(walkMs, contentWriteMs, tilesetMetadataWriteMs, pending.Count, pendingTilesets.Count);
    }

    // Timing breakdown for one Export call: the sequential in-memory tree
    // walk (WalkMs, scales with total node count), the parallel per-node
    // .pnts writes (ContentWriteMs, scales with NodesWritten — the whole
    // tree), or the nested tileset metadata build+write
    // (TilesetMetadataWriteMs, scales with NestedTilesetsWritten — one file
    // per node with children).
    public readonly struct ExportStats
    {
        public readonly long WalkMs;
        public readonly long ContentWriteMs;
        public readonly long TilesetMetadataWriteMs;
        public readonly int NodesWritten;
        public readonly int NestedTilesetsWritten;

        public ExportStats(long walkMs, long contentWriteMs, long tilesetMetadataWriteMs, int nodesWritten, int nestedTilesetsWritten)
        {
            WalkMs = walkMs;
            ContentWriteMs = contentWriteMs;
            TilesetMetadataWriteMs = tilesetMetadataWriteMs;
            NodesWritten = nodesWritten;
            NestedTilesetsWritten = nestedTilesetsWritten;
        }
    }

    private readonly struct TileResult
    {
        public readonly Dictionary<string, object> Json;
        public readonly double GeometricError;

        public TileResult(Dictionary<string, object> json, double geometricError)
        {
            Json = json;
            GeometricError = geometricError;
        }
    }

    private readonly struct PendingWrite
    {
        public readonly OctreeNode Node;
        public readonly string ContentPath;

        public PendingWrite(OctreeNode node, string contentPath)
        {
            Node = node;
            ContentPath = contentPath;
        }
    }

    private readonly struct PendingTilesetWrite
    {
        public readonly string Path;
        public readonly Dictionary<string, object> Document;

        public PendingTilesetWrite(string path, Dictionary<string, object> document)
        {
            Path = path;
            Document = document;
        }
    }

    private static string ContentFileName(System.Numerics.BigInteger id) =>
        id.ToString(CultureInfo.InvariantCulture) + ".pnts";

    private static string NestedTilesetFileName(System.Numerics.BigInteger id) =>
        "tileset_node_" + id.ToString(CultureInfo.InvariantCulture) + ".json";

    // Every CHILD, from its parent's perspective, becomes the root of its
    // own nested tileset document — its real content and children live
    // there, not inlined in the parent's file. The parent only gets a
    // pointer tile: same boundingVolume/geometricError `node` would
    // normally have, but `content.uri` pointing at the nested file instead
    // of a .pnts, and no `children` of its own (one hop away, behind that
    // pointer). Not used for the true output root (Export calls
    // BuildTileContent directly for that) — the root's own content is
    // always inline in the top-level tileset.json.
    private static TileResult BuildBoundaryPointerTile(
        OctreeNode node,
        int gridDivisions,
        string outputDirectory,
        string refineJson,
        List<PendingWrite> pending,
        List<PendingTilesetWrite> pendingTilesets)
    {
        var nestedRoot = BuildTileContent(node, gridDivisions, outputDirectory, isRoot: true, refineJson, pending, pendingTilesets);

        string fileName = NestedTilesetFileName(node.Id);
        var document = new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = "1.0" },
            ["geometricError"] = nestedRoot.GeometricError,
            ["root"] = nestedRoot.Json,
        };
        pendingTilesets.Add(new PendingTilesetWrite(Path.Combine(outputDirectory, fileName), document));

        var pointerTile = new Dictionary<string, object>
        {
            ["boundingVolume"] = new Dictionary<string, object> { ["box"] = TileGeometry.BuildBox(node.Bbox) },
            ["geometricError"] = nestedRoot.GeometricError,
            ["content"] = new Dictionary<string, object> { ["uri"] = fileName },
        };
        return new TileResult(pointerTile, nestedRoot.GeometricError);
    }

    // The actual per-node tile: own content, plus its children — each
    // always a pointer to its own nested document (see
    // BuildBoundaryPointerTile), never inlined. Called for a document's
    // root only: the true output root, or a child node via
    // BuildBoundaryPointerTile's own nested-document root.
    private static TileResult BuildTileContent(
        OctreeNode node,
        int gridDivisions,
        string outputDirectory,
        bool isRoot,
        string refineJson,
        List<PendingWrite> pending,
        List<PendingTilesetWrite> pendingTilesets)
    {
        string contentFileName = ContentFileName(node.Id);
        pending.Add(new PendingWrite(node, Path.Combine(outputDirectory, "content", contentFileName)));

        double geometricError = TileGeometry.GeometricError(node, gridDivisions);

        var tile = new Dictionary<string, object>
        {
            ["boundingVolume"] = new Dictionary<string, object> { ["box"] = TileGeometry.BuildBox(node.Bbox) },
            ["geometricError"] = geometricError,
            ["content"] = new Dictionary<string, object> { ["uri"] = "content/" + contentFileName },
        };
        if (isRoot) tile["refine"] = refineJson; // inherited by children per spec; also re-declared on every nested document's own root

        var childNodes = OctreeStructureUtil.NonEmptyChildren(node);
        if (childNodes.Count > 0)
        {
            var children = new List<object>();
            foreach (var child in childNodes)
                children.Add(BuildBoundaryPointerTile(child, gridDivisions, outputDirectory, refineJson, pending, pendingTilesets).Json);
            tile["children"] = children;
        }

        return new TileResult(tile, geometricError);
    }
}
