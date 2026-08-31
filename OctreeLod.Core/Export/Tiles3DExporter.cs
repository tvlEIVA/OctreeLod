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
    public static ExportStats Export(
        OctreeNode root,
        INodePointStore mergedStore,
        int gridDivisions,
        string outputDirectory,
        TileRefine refine,
        GeoReference? geoReference = null,
        bool incremental = false,
        string tilesetFileName = "tileset.json")
    {
        string contentDir = Path.Combine(outputDirectory, "content");
        Directory.CreateDirectory(contentDir);

        // Deciding which nodes need a content write is a fast, sequential,
        // in-memory tree walk — it doesn't need to wait on the writes
        // themselves, so BuildTile only queues them into `pending` here.
        // The actual I/O (the real bottleneck once thousands of nodes are
        // dirty) then runs after, in parallel — each node's write is fully
        // independent (its own file, its own node.Dirty flag), so there's
        // nothing here that needs synchronizing beyond what
        // NodePointFileStore.ReadAll already does internally.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pending = new List<PendingWrite>();
        string refineJson = refine == TileRefine.Add ? "ADD" : "REPLACE";
        var rootTile = BuildTile(root, gridDivisions, contentDir, isRoot: true, refineJson, incremental, pending);
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
        long tilesetMetadataWriteMs = sw.ElapsedMilliseconds;

        return new ExportStats(walkMs, contentWriteMs, tilesetMetadataWriteMs, pending.Count);
    }

    // Timing breakdown for one Export call, so a caller (e.g. a periodic
    // preview) can tell which phase actually dominates at scale: the
    // sequential in-memory tree walk (WalkMs, scales with total node
    // count), the parallel per-node .pnts writes (ContentWriteMs, scales
    // with NodesWritten — the dirty/new subset), or the tileset metadata
    // (tileset.json) build+write (TilesetMetadataWriteMs, scales with total
    // node count, same as the walk — inherent to one-file-per-tree, see
    // BuildTile).
    public readonly struct ExportStats
    {
        public readonly long WalkMs;
        public readonly long ContentWriteMs;
        public readonly long TilesetMetadataWriteMs;
        public readonly int NodesWritten;

        public ExportStats(long walkMs, long contentWriteMs, long tilesetMetadataWriteMs, int nodesWritten)
        {
            WalkMs = walkMs;
            ContentWriteMs = contentWriteMs;
            TilesetMetadataWriteMs = tilesetMetadataWriteMs;
            NodesWritten = nodesWritten;
        }
    }

    // Writes to a temp file in the same directory, then swaps it into place
    // with File.Replace (or File.Move, first time round) — so a concurrent
    // reader (a web client fetching the tileset metadata while a periodic
    // preview export is rewriting it) always sees either the complete old
    // file or the complete new one, never a partial write. Used for the
    // tileset metadata only, not per-node content — see BuildTile's plain
    // write for why that doesn't need it.
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
        public readonly int Version;

        public PendingWrite(OctreeNode node, string contentPath, int version)
        {
            Node = node;
            ContentPath = contentPath;
            Version = version;
        }
    }

    // `{id}_v{version}.pnts` — versioned rather than a fixed `{id}.pnts` so
    // rewriting a node's content never touches a filename already-published
    // tileset metadata still references (see OctreeNode.ContentVersion).
    private static string ContentFileName(System.Numerics.BigInteger id, int version) =>
        id.ToString(CultureInfo.InvariantCulture) + "_v" + version.ToString(CultureInfo.InvariantCulture) + ".pnts";

    private static TileResult BuildTile(
        OctreeNode node,
        int gridDivisions,
        string contentDir,
        bool isRoot,
        string refineJson,
        bool incremental,
        List<PendingWrite> pending)
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
        if (isRoot) tile["refine"] = refineJson; // inherited by children per spec

        var childNodes = OctreeStructureUtil.NonEmptyChildren(node);
        if (childNodes.Count > 0)
        {
            var children = new List<object>();
            foreach (var child in childNodes)
                children.Add(BuildTile(child, gridDivisions, contentDir, isRoot: false, refineJson, incremental, pending).Json);
            tile["children"] = children;
        }

        return new TileResult(tile, geometricError);
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
