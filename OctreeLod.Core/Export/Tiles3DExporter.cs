using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OctreeLod.Core.Merge;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.Export;

// Exports the built + merged octree (phases 1-2) as a 3D Tiles dataset:
// tileset.json + one legacy .pnts file per emitted node under content/.
// Every non-empty node (leaf or internal) gets real content, since grid
// subsampling means every LOD level has genuine representative samples —
// unlike typical BVH tilesets with empty non-leaf placeholders.
public static class Tiles3DExporter
{
    public static void Export(
        INodeMetadataStore metadata,
        IMergedPointStore mergedStore,
        long rootId,
        int gridDivisions,
        string outputDirectory,
        GeoReference? geoReference = null)
    {
        string contentDir = Path.Combine(outputDirectory, "content");
        Directory.CreateDirectory(contentDir);

        var rootTile = BuildTile(metadata, mergedStore, rootId, gridDivisions, contentDir, isRoot: true);

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

        File.WriteAllText(Path.Combine(outputDirectory, "tileset.json"), MinimalJsonWriter.Write(tileset));
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

    private static TileResult BuildTile(
        INodeMetadataStore metadata,
        IMergedPointStore mergedStore,
        long nodeId,
        int gridDivisions,
        string contentDir,
        bool isRoot)
    {
        var node = metadata.Get(nodeId);
        var points = mergedStore.ReadAll(nodeId);

        string contentFileName = nodeId.ToString(CultureInfo.InvariantCulture) + ".pnts";
        PntsWriter.WriteFile(Path.Combine(contentDir, contentFileName), node.Bbox, points);

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
        if (isRoot) tile["refine"] = "ADD"; // inherited by children per spec

        var childIds = OctreeStructureUtil.NonEmptyChildIds(metadata, node);
        if (childIds.Count > 0)
        {
            var children = new List<object>();
            foreach (var childId in childIds)
                children.Add(BuildTile(metadata, mergedStore, childId, gridDivisions, contentDir, isRoot: false).Json);
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
