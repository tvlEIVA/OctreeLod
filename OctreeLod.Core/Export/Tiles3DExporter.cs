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
// spacing-based) as a 3D Tiles dataset: tileset.json + one legacy .pnts file
// per emitted node under content/. Every non-empty node (leaf or internal)
// gets real content, since both engines produce a genuine representative
// sample at every LOD level — unlike typical BVH tilesets with empty
// non-leaf placeholders.
public static class Tiles3DExporter
{
    // `incremental`: reuse a node's existing content/{id}_v{version}.pnts
    // file instead of re-reading it from the store and rewriting it when
    // the node hasn't changed since the last export (OctreeNode.Dirty
    // false) — the tileset metadata is still rebuilt in full every call
    // (cheap: pure in-memory tree walk, no store reads), only per-node
    // content I/O is skipped. Meant for periodic preview exports
    // mid-ingestion; pass false (default) for a one-shot final export where
    // every node is written unconditionally.
    //
    // `tilesetFileName`: which tileset metadata file to (over)write under
    // outputDirectory — defaults to "tileset.json". A periodic preview
    // export can pass a distinct name per call (e.g. "tileset_0007.json")
    // to keep every snapshot on disk instead of overwriting the same file;
    // content/ is versioned per node (OctreeNode.ContentVersion) so
    // already-published tileset metadata's content references stay valid
    // forever too — rewriting a changed node writes a NEW `_v{n}.pnts` file
    // rather than overwriting the one an earlier tileset still points at.
    // This matters for a client that keeps multiple previews' tiles loaded
    // simultaneously (rather than replacing on each new preview): without
    // it, an in-place overwrite could swap out from under an already-loaded
    // tile, or hand back stale bytes via HTTP caching on a reused URL.
    //
    // `partitionDepthInterval`: 0 (default) exports one monolithic
    // tileset.json describing the whole tree, as before. A positive value
    // splits it into linked external tilesets instead: every node at a
    // depth that's a multiple of this value becomes a partition boundary —
    // its own content becomes the ROOT of a new, separate
    // `tileset_node_{id}_v{n}.json` file (versioned exactly like content,
    // for the same reason), and the node's entry in its PARENT's file
    // becomes a pure pointer (`content.uri` -> that nested file, no
    // `children` of its own). This is the standard 3D Tiles "external
    // tileset" mechanism — a client (e.g. deck.gl's Tile3DLayer) fetches
    // and parses a nested file lazily, only once traversal actually reaches
    // that branch, instead of eagerly constructing an in-memory tile object
    // for every node in the entire tree on every load. That eager
    // construction is real, unavoidable client-side cost that scales with
    // total node count regardless of any tile-selection/cache setting on
    // the client — this is the only thing that actually bounds it, by
    // bounding how much any single file describes.
    public static ExportStats Export(
        OctreeNode root,
        INodePointStore mergedStore,
        int gridDivisions,
        string outputDirectory,
        TileRefine refine,
        GeoReference? geoReference = null,
        bool incremental = false,
        string tilesetFileName = "tileset.json",
        int partitionDepthInterval = 0)
    {
        string contentDir = Path.Combine(outputDirectory, "content");
        Directory.CreateDirectory(contentDir);

        // Deciding tree structure (what's a boundary, what needs a content
        // write, geometricError, etc.) is a fast, sequential, in-memory
        // walk — it doesn't need to wait on any I/O, so this phase only
        // queues work: `pending` for per-node content writes, and
        // `pendingTilesets` for nested external-tileset documents. Both
        // phases below run AFTER the walk finishes and write in parallel —
        // nested tileset files specifically must not be written before the
        // content they reference exists, or a client could briefly fetch a
        // valid-looking nested tileset whose .pnts content 404s.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pending = new List<PendingWrite>();
        var pendingTilesets = new List<PendingTilesetWrite>();
        string refineJson = refine == TileRefine.Add ? "ADD" : "REPLACE";
        var rootTile = BuildTileContent(root, gridDivisions, contentDir, outputDirectory, isRoot: true, refineJson, incremental, partitionDepthInterval, pending, pendingTilesets);
        long walkMs = sw.ElapsedMilliseconds;

        sw.Restart();
        if (pending.Count > 0)
        {
            // Each iteration is I/O-bound (one small file read + one small
            // file write) rather than CPU-bound, so a thread here spends
            // most of its time blocked on disk, not computing — capping
            // concurrency at core count leaves most of the disk's actual
            // queue depth unused. NodePointFileStore.ReadAll no longer
            // shares a single locked handle (see its own per-thread read
            // handle), so there's nothing left serializing these beyond the
            // disk itself; go well above core count to actually exercise it.
            var writeOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 8 };
            Parallel.ForEach(pending, writeOptions, item =>
            {
                var points = mergedStore.ReadAll(item.Node.Id);
                PntsWriter.WriteFile(item.ContentPath, item.Node.Bbox, points);
                item.Node.Dirty = false;
                item.Node.ContentVersion = item.Version;
            });
        }
        long contentWriteMs = sw.ElapsedMilliseconds;

        sw.Restart();
        if (pendingTilesets.Count > 0)
        {
            // Plain write, not WriteAtomic: same reasoning as content's
            // plain write (see BuildTileContent) — a nested
            // tileset_node_{id}_v{n}.json is a brand-new versioned filename
            // every time, never previously published, so nothing can be
            // reading that exact path yet. The only way a client learns
            // this URI exists is by fetching the PARENT file that
            // references it, and that publish happens strictly after this
            // whole phase completes (see the ordering note above) — so
            // there's no reader to race, unlike the top-level manifest
            // (tileset.json / tileset_preview_NNNN.json), which a client
            // polls directly by a known, reused URL and does need the
            // atomic swap.
            var writeOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 8 };
            Parallel.ForEach(pendingTilesets, writeOptions, item =>
            {
                File.WriteAllText(item.Path, MinimalJsonWriter.Write(item.Document));
                item.Node.TilesetVersion = item.Version;
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

        WriteAtomic(Path.Combine(outputDirectory, tilesetFileName), path => File.WriteAllText(path, MinimalJsonWriter.Write(tileset)));
        long tilesetMetadataWriteMs = sw.ElapsedMilliseconds + nestedTilesetWriteMs;

        return new ExportStats(walkMs, contentWriteMs, tilesetMetadataWriteMs, pending.Count, pendingTilesets.Count);
    }

    // Timing breakdown for one Export call, so a caller (e.g. a periodic
    // preview) can tell which phase actually dominates at scale: the
    // sequential in-memory tree walk (WalkMs, scales with total node
    // count), the parallel per-node .pnts writes (ContentWriteMs, scales
    // with NodesWritten — the dirty/new subset), or the tileset metadata
    // (tileset.json plus any nested external tilesets) build+write
    // (TilesetMetadataWriteMs). With partitionDepthInterval == 0 this scales
    // with total node count same as the walk (inherent to one monolithic
    // file); with partitioning on, each individual file's cost is bounded
    // by the partition size instead — NestedTilesetsWritten shows how many
    // separate files that work is spread across.
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

    // Writes to a temp file in the same directory, then swaps it into place
    // with File.Replace (or File.Move, first time round) — so a concurrent
    // reader (a web client fetching the tileset metadata while a periodic
    // preview export is rewriting it) always sees either the complete old
    // file or the complete new one, never a partial write. Used for tileset
    // metadata (top-level and nested) only, not per-node content — see
    // BuildTileContent's plain write for why that doesn't need it.
    private static void WriteAtomic(string path, Action<string> write)
    {
        string tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        write(tempPath);

        if (File.Exists(path))
            File.Replace(tempPath, path, null);
        else
            File.Move(tempPath, path);
    }

    private readonly struct TileResult
    {
        public readonly Dictionary<string, object> Json;
        public readonly double GeometricError;

        // Did this node's own content need writing, OR did anything in its
        // subtree? Bubbles up through BuildTileContent's children loop so a
        // partition boundary (BuildBoundaryPointerTile) can tell whether its
        // whole nested document is actually different from what's already
        // on disk — skip-if-unchanged for nested tileset files, same idea
        // as OctreeNode.Dirty for content, just aggregated over a subtree
        // instead of a single node (content itself has no equivalent need:
        // it's always exactly one node, no subtree to aggregate over).
        public readonly bool Changed;

        public TileResult(Dictionary<string, object> json, double geometricError, bool changed)
        {
            Json = json;
            GeometricError = geometricError;
            Changed = changed;
        }
    }

    private readonly struct PendingWrite
    {
        public readonly OctreeNode Node;
        public readonly string ContentPath;
        public readonly int Version;

        public PendingWrite(OctreeNode node, string contentPath, int version)
        {
            Node = node;
            ContentPath = contentPath;
            Version = version;
        }
    }

    private readonly struct PendingTilesetWrite
    {
        public readonly OctreeNode Node;
        public readonly string Path;
        public readonly int Version;
        public readonly Dictionary<string, object> Document;

        public PendingTilesetWrite(OctreeNode node, string path, int version, Dictionary<string, object> document)
        {
            Node = node;
            Path = path;
            Version = version;
            Document = document;
        }
    }

    // `{id}_v{version}.pnts` — versioned rather than a fixed `{id}.pnts` so
    // rewriting a node's content never touches a filename already-published
    // tileset metadata still references (see OctreeNode.ContentVersion).
    private static string ContentFileName(System.Numerics.BigInteger id, int version) =>
        id.ToString(CultureInfo.InvariantCulture) + "_v" + version.ToString(CultureInfo.InvariantCulture) + ".pnts";

    private static string NestedTilesetFileName(System.Numerics.BigInteger id, int version) =>
        "tileset_node_" + id.ToString(CultureInfo.InvariantCulture) + "_v" + version.ToString(CultureInfo.InvariantCulture) + ".json";

    // Entry point for each CHILD from its parent's perspective — this is
    // where partitioning decides whether `node` is inlined normally or
    // becomes an external-tileset boundary. Not used for the true output
    // root (Export calls BuildTileContent directly for that) or for a
    // boundary node's own nested-document root (BuildBoundaryPointerTile
    // does the same) — both of those must always build `node` inline, or a
    // boundary node would immediately redirect to itself.
    private static TileResult BuildTile(
        OctreeNode node,
        int gridDivisions,
        string contentDir,
        string outputDirectory,
        string refineJson,
        bool incremental,
        int partitionDepthInterval,
        List<PendingWrite> pending,
        List<PendingTilesetWrite> pendingTilesets)
    {
        bool isBoundary = partitionDepthInterval > 0 && node.Depth > 0 && node.Depth % partitionDepthInterval == 0;
        if (isBoundary)
            return BuildBoundaryPointerTile(node, gridDivisions, contentDir, outputDirectory, refineJson, incremental, partitionDepthInterval, pending, pendingTilesets);

        return BuildTileContent(node, gridDivisions, contentDir, outputDirectory, isRoot: false, refineJson, incremental, partitionDepthInterval, pending, pendingTilesets);
    }

    // `node` becomes the root of its own nested tileset document — this
    // node's real content and children live there, not in the parent's
    // file. The parent only gets a pointer tile: same boundingVolume and
    // geometricError as `node` would normally have, but `content.uri`
    // pointing at the nested file instead of a .pnts, and no `children` of
    // its own (they're one hop away, behind that pointer).
    private static TileResult BuildBoundaryPointerTile(
        OctreeNode node,
        int gridDivisions,
        string contentDir,
        string outputDirectory,
        string refineJson,
        bool incremental,
        int partitionDepthInterval,
        List<PendingWrite> pending,
        List<PendingTilesetWrite> pendingTilesets)
    {
        var nestedRoot = BuildTileContent(node, gridDivisions, contentDir, outputDirectory, isRoot: true, refineJson, incremental, partitionDepthInterval, pending, pendingTilesets);

        // Skip-if-unchanged, same idea as content's node.Dirty check: if
        // nothing anywhere in this subtree needed writing this pass AND a
        // file already exists for the current version, reuse it — without
        // this, every boundary's nested file gets rewritten on EVERY pass
        // regardless of whether it changed, so the total nested-write cost
        // grows with the WHOLE tree's boundary count forever instead of
        // just the new/changed ones (this was the actual cause of tileset
        // export time climbing every preview, not the write mechanism).
        bool canSkip = incremental && !nestedRoot.Changed && node.TilesetVersion > 0;
        int targetVersion = canSkip ? node.TilesetVersion : node.TilesetVersion + 1;
        string fileName = NestedTilesetFileName(node.Id, targetVersion);

        if (!canSkip)
        {
            var document = new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object> { ["version"] = "1.0" },
                ["geometricError"] = nestedRoot.GeometricError,
                ["root"] = nestedRoot.Json,
            };
            pendingTilesets.Add(new PendingTilesetWrite(node, Path.Combine(outputDirectory, fileName), targetVersion, document));
        }

        var pointerTile = new Dictionary<string, object>
        {
            ["boundingVolume"] = new Dictionary<string, object> { ["box"] = BuildBox(node.Bbox) },
            ["geometricError"] = nestedRoot.GeometricError,
            ["content"] = new Dictionary<string, object> { ["uri"] = fileName },
        };
        return new TileResult(pointerTile, nestedRoot.GeometricError, changed: !canSkip);
    }

    // The actual per-node tile: own content + (unless a boundary redirects
    // it) its children inline. Every entry point that must build `node`
    // itself rather than let it redirect to a boundary — the true output
    // root, and a boundary node's own nested-document root — calls this
    // directly; ordinary child recursion goes through BuildTile instead,
    // which is where the boundary check happens.
    private static TileResult BuildTileContent(
        OctreeNode node,
        int gridDivisions,
        string contentDir,
        string outputDirectory,
        bool isRoot,
        string refineJson,
        bool incremental,
        int partitionDepthInterval,
        List<PendingWrite> pending,
        List<PendingTilesetWrite> pendingTilesets)
    {
        // No File.Exists check needed here (unlike the pre-versioning
        // design): a version >0 file is guaranteed to exist — versioned
        // files are never deleted or rewritten in place — and version ==0
        // (never written) always implies node.Dirty is still true, since
        // Dirty only ever clears alongside a successful write that bumps
        // the version past 0. So `node.Dirty` alone already covers it.
        int existingVersion = node.ContentVersion;
        bool needsWrite = !incremental || node.Dirty;
        int targetVersion = needsWrite ? existingVersion + 1 : existingVersion;
        string contentFileName = ContentFileName(node.Id, targetVersion);
        string contentPath = Path.Combine(contentDir, contentFileName);

        if (needsWrite)
            pending.Add(new PendingWrite(node, contentPath, targetVersion));

        // Internal (subsampled) node: error = spacing between representative
        // samples at this level, same constant MergeEngine subsamples with.
        // Leaf (raw, unsampled data): no finer detail available.
        double geometricError = node.IsLeaf ? 0.0 : node.Bbox.Size / gridDivisions;

        var tile = new Dictionary<string, object>
        {
            ["boundingVolume"] = new Dictionary<string, object> { ["box"] = BuildBox(node.Bbox) },
            ["geometricError"] = geometricError,
            ["content"] = new Dictionary<string, object> { ["uri"] = "content/" + contentFileName },
        };
        if (isRoot) tile["refine"] = refineJson; // inherited by children per spec; also re-declared on every nested document's own root

        bool anyChildChanged = false;
        var childNodes = OctreeStructureUtil.NonEmptyChildren(node);
        if (childNodes.Count > 0)
        {
            var children = new List<object>();
            foreach (var child in childNodes)
            {
                var childResult = BuildTile(child, gridDivisions, contentDir, outputDirectory, refineJson, incremental, partitionDepthInterval, pending, pendingTilesets);
                children.Add(childResult.Json);
                anyChildChanged |= childResult.Changed;
            }
            tile["children"] = children;
        }

        return new TileResult(tile, geometricError, changed: needsWrite || anyChildChanged);
    }

    // Axis-aligned cube -> 3D Tiles `box`: center + 3 half-axis vectors.
    // Our cubes make this the trivial diagonal case.
    private static List<object> BuildBox(BoundingCube bbox)
    {
        double half = bbox.Size / 2;
        double cx = bbox.MinX + half, cy = bbox.MinY + half, cz = bbox.MinZ + half;
        return new List<object>
        {
            (object)cx, (object)cy, (object)cz,
            (object)half, (object)0.0, (object)0.0,
            (object)0.0, (object)half, (object)0.0,
            (object)0.0, (object)0.0, (object)half,
        };
    }
}
