using System.Collections.Generic;
using System.Numerics;
using OctreeLod.Core.Export;
using OctreeLod.Core.Model;

namespace OctreeLod.Server;

// Builds a 3D Tiles tileset JSON document straight from the LIVE,
// still-mutating octree — no file writes, no versioning, no dirty-tracking.
// Every call just walks CURRENT structure and returns fresh JSON, meant to
// be serialized directly into an HTTP response — nothing is ever
// "published", so each request just reflects whatever the tree looks like
// at that instant.
//
// Reading the live tree while ingestion concurrently mutates it
// (Children/PointCount/IsLeaf) is a benign, eventually-consistent race —
// same reasoning the preview export's clone-snapshot mechanism was built
// on, just without the snapshot step: a request might render a subtree a
// moment before or after a sibling gains a new child, never a crash or a
// torn individual field (reference and primitive-field writes don't tear
// in .NET).
//
// Every node with children redirects each of them to its own nested-tileset
// ENDPOINT (`/tileset/node/{id}.json`) instead of inlining it — the standard
// 3D Tiles external-tileset mechanism, same as Tiles3DExporter's file-based
// export, just pointing at an endpoint instead of a file. Content redirects
// to a content ENDPOINT (`/content/{id}.pnts`) instead of a filename — both
// origin-absolute paths, so they resolve correctly no matter what path the
// containing document was itself served from.
public static class LiveTilesetBuilder
{
    public static Dictionary<string, object> BuildDocument(OctreeNode documentRoot, int gridDivisions, TileRefine refine)
    {
        string refineJson = refine == TileRefine.Add ? "ADD" : "REPLACE";
        var rootTile = BuildTileContent(documentRoot, gridDivisions, isRoot: true, refineJson);
        return new Dictionary<string, object>
        {
            ["asset"] = new Dictionary<string, object> { ["version"] = "1.0" },
            ["geometricError"] = rootTile.GeometricError,
            ["root"] = rootTile.Json,
        };
    }

    // Ids are derived from tree position alone (root = 0, child = parent.Id
    // * 8 + octant + 1 — see OctreeNode), so a target node can be found by
    // decoding that formula back into an octant path and walking straight
    // to it — O(depth), not a search over the whole tree. Always walks from
    // the TRUE geometric root (engine.Root), never a trimmed logical root:
    // trimming only picks a different starting point for what gets
    // exported, it doesn't change any node's id. Returns null if that
    // node's path doesn't exist (yet, or ever) in the current tree.
    public static OctreeNode? FindNodeById(OctreeNode trueRoot, BigInteger id)
    {
        if (id == BigInteger.Zero) return trueRoot;

        var octants = new Stack<int>();
        var remaining = id;
        while (remaining > BigInteger.Zero)
        {
            var zeroBased = remaining - 1;
            octants.Push((int)(zeroBased % 8));
            remaining = zeroBased / 8;
        }

        var node = trueRoot;
        while (octants.Count > 0)
        {
            var child = node.Children[octants.Pop()];
            if (child == null) return null;
            node = child;
        }
        return node;
    }

    // Same pointer-tile shape as Tiles3DExporter's boundary handling, just
    // pointing at an endpoint instead of a file — nothing about `node`'s
    // own content or children is built here, that happens lazily, only if
    // and when a client actually requests /tileset/node/{node.Id}.
    private static TileResult BuildBoundaryPointerTile(OctreeNode node, int gridDivisions)
    {
        double geometricError = TileGeometry.GeometricError(node, gridDivisions);
        var tile = new Dictionary<string, object>
        {
            ["boundingVolume"] = new Dictionary<string, object> { ["box"] = TileGeometry.BuildBox(node.Bbox) },
            ["geometricError"] = geometricError,
            ["content"] = new Dictionary<string, object> { ["uri"] = "/tileset/node/" + node.Id + ".json" },
        };
        return new TileResult(tile, geometricError);
    }

    private static TileResult BuildTileContent(OctreeNode node, int gridDivisions, bool isRoot, string refineJson)
    {
        double geometricError = TileGeometry.GeometricError(node, gridDivisions);
        var tile = new Dictionary<string, object>
        {
            ["boundingVolume"] = new Dictionary<string, object> { ["box"] = TileGeometry.BuildBox(node.Bbox) },
            ["geometricError"] = geometricError,
            ["content"] = new Dictionary<string, object> { ["uri"] = "/content/" + node.Id + ".pnts" },
        };
        if (isRoot) tile["refine"] = refineJson; // inherited by children per spec; also re-declared on every nested document's own root

        var childNodes = OctreeStructureUtil.NonEmptyChildren(node);
        if (childNodes.Count > 0)
        {
            var children = new List<object>();
            foreach (var child in childNodes)
                children.Add(BuildBoundaryPointerTile(child, gridDivisions).Json);
            tile["children"] = children;
        }

        return new TileResult(tile, geometricError);
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
}
