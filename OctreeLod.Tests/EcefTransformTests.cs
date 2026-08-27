using System;
using System.IO;
using System.Text.Json;
using OctreeLod.Core.Export;
using OctreeLod.Core.Model;
using OctreeLod.Core.SplitMergeEngine.Ingest;

namespace OctreeLod.Tests;

// EcefTransform itself is an internal implementation detail of
// Tiles3DExporter (same pattern as MinimalJsonWriter) — exercised here
// through the public Export API, reading the resulting tileset.json's
// root.transform back out, same approach Tiles3DExporterTests uses.
public class EcefTransformTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EquatorPrimeMeridian_OriginIsOnPositiveXAxisAtEarthRadius()
    {
        var matrix = ExportAndReadTransform(new GeoReference(0, 0, 0));

        Assert.Equal(6378137.0, matrix[12], 3);
        Assert.Equal(0.0, matrix[13], 3);
        Assert.Equal(0.0, matrix[14], 3);
    }

    [Fact]
    public void OriginPointsRoughlyOutwardAlongUpVector()
    {
        // At any reference point, "up" (local +Z) should point close to the
        // same direction as the ECEF origin vector itself (exactly true on a
        // sphere; WGS84's slight flattening makes it only approximate).
        var matrix = ExportAndReadTransform(new GeoReference(latitudeDegrees: 56.0, longitudeDegrees: 10.0, heightMeters: 0.0));

        double ox = matrix[12], oy = matrix[13], oz = matrix[14];
        double originLength = Math.Sqrt(ox * ox + oy * oy + oz * oz);
        double odx = ox / originLength, ody = oy / originLength, odz = oz / originLength;

        double ux = matrix[8], uy = matrix[9], uz = matrix[10]; // Up column
        double dot = odx * ux + ody * uy + odz * uz;

        Assert.True(dot > 0.999, $"expected up vector nearly parallel to origin direction, dot={dot}");
        Assert.True(originLength > 6_356_000 && originLength < 6_379_000, $"origin distance from Earth center out of WGS84 range: {originLength}");
    }

    [Fact]
    public void EastNorthUpBasis_IsOrthonormal()
    {
        var matrix = ExportAndReadTransform(new GeoReference(56.0, 10.0, 0.0));

        AssertUnitLength(matrix[0], matrix[1], matrix[2]);
        AssertUnitLength(matrix[4], matrix[5], matrix[6]);
        AssertUnitLength(matrix[8], matrix[9], matrix[10]);
        Assert.Equal(0.0, Dot(matrix, 0, 4), 9);
        Assert.Equal(0.0, Dot(matrix, 0, 8), 9);
        Assert.Equal(0.0, Dot(matrix, 4, 8), 9);
    }

    [Fact]
    public void HeightMeters_MovesOriginFartherFromEarthCenterByExactlyThatMuch()
    {
        var m0 = ExportAndReadTransform(new GeoReference(56.0, 10.0, 0.0));
        var m1000 = ExportAndReadTransform(new GeoReference(56.0, 10.0, 1000.0));

        double d0 = Math.Sqrt(m0[12] * m0[12] + m0[13] * m0[13] + m0[14] * m0[14]);
        double d1000 = Math.Sqrt(m1000[12] * m1000[12] + m1000[13] * m1000[13] + m1000[14] * m1000[14]);

        Assert.Equal(1000.0, d1000 - d0, 1);
    }

    [Fact]
    public void NoGeoReference_TilesetHasNoTransform()
    {
        var (root, mergedStore) = BuildTinySingleNodeTree();
        using var disposeMergedStore = mergedStore;
        string outputDir = Path.Combine(_dir, "no-geo");

        Tiles3DExporter.Export(root, mergedStore, gridDivisions: 8, outputDir, TileRefine.Replace);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDir, "tileset.json")));
        Assert.False(doc.RootElement.GetProperty("root").TryGetProperty("transform", out _));
    }

    private double[] ExportAndReadTransform(GeoReference reference)
    {
        var (root, mergedStore) = BuildTinySingleNodeTree();
        using var disposeMergedStore = mergedStore;
        string outputDir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));

        Tiles3DExporter.Export(root, mergedStore, gridDivisions: 8, outputDir, TileRefine.Replace, reference);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outputDir, "tileset.json")));
        var transformElement = doc.RootElement.GetProperty("root").GetProperty("transform");
        var matrix = new double[16];
        int i = 0;
        foreach (var v in transformElement.EnumerateArray()) matrix[i++] = v.GetDouble();
        return matrix;
    }

    private (OctreeNode root, NodePointFileStore mergedStore) BuildTinySingleNodeTree()
    {
        const int threshold = 100;
        var options = new OctreeIngestionOptions { SplitThreshold = threshold };
        using var leafStore = new SlabPointStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".bin"), threshold);
        var engine = new OctreeIngestionEngine(leafStore, options);
        engine.IngestPoint(new PointRecord(1, 1, 1, 100, 50, 200));

        var mergedStore = new NodePointFileStore(Path.Combine(_dir, Guid.NewGuid().ToString("N")));
        mergedStore.WriteAll(engine.Root.Id, leafStore.ReadAll(engine.Root.Storage, 1));

        return (engine.Root, mergedStore);
    }

    private static void AssertUnitLength(double x, double y, double z) =>
        Assert.Equal(1.0, Math.Sqrt(x * x + y * y + z * z), 9);

    private static double Dot(double[] m, int colA, int colB) =>
        m[colA] * m[colB] + m[colA + 1] * m[colB + 1] + m[colA + 2] * m[colB + 2];

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
