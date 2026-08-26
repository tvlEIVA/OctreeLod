using System.Collections.Generic;
using System.Linq;
using OctreeLod.Core.Model;
using OctreeLod.Core.SplitMergeEngine.Merge;

namespace OctreeLod.Tests;

public class GridSubsamplerTests
{
    private static readonly BoundingCube Bbox = new BoundingCube(0, 0, 0, 64);

    [Fact]
    public void OutputCountNeverExceedsInputCount()
    {
        var points = MakeGrid(10, spacing: 0.5); // many points crammed into a small region
        var result = GridSubsampler.Subsample(Bbox, points, gridDivisions: 8);

        Assert.True(result.Length <= points.Count);
    }

    [Fact]
    public void EveryOutputPointIsWithinItsCellOfARealInputPoint()
    {
        var points = MakeGrid(20, spacing: 1.0);
        int gridDivisions = 8;
        double cellSize = Bbox.Size / gridDivisions;

        var result = GridSubsampler.Subsample(Bbox, points, gridDivisions);

        foreach (var outPoint in result)
        {
            bool hasNearbyRealPoint = points.Any(p =>
                System.Math.Abs(p.X - outPoint.X) < 1e-9 &&
                System.Math.Abs(p.Y - outPoint.Y) < 1e-9 &&
                System.Math.Abs(p.Z - outPoint.Z) < 1e-9);
            Assert.True(hasNearbyRealPoint, "output point must be a real sampled input point, not synthesized");
        }
    }

    [Fact]
    public void OnePointPerOccupiedCell_TwoPointsInSameCellCollapseToOne()
    {
        var points = new List<PointRecord>
        {
            new PointRecord(1, 1, 1, 10, 20, 30),
            new PointRecord(1.1, 1.1, 1.1, 50, 60, 70),
        };

        var result = GridSubsampler.Subsample(Bbox, points, gridDivisions: 8); // cell size = 8

        Assert.Single(result);
    }

    [Fact]
    public void ColorIsAveragedAcrossCellPoints()
    {
        var points = new List<PointRecord>
        {
            new PointRecord(1, 1, 1, 0, 0, 0),
            new PointRecord(1.1, 1.1, 1.1, 100, 100, 100),
        };

        var result = GridSubsampler.Subsample(Bbox, points, gridDivisions: 8);

        Assert.Single(result);
        Assert.Equal(50, result[0].R);
        Assert.Equal(50, result[0].G);
        Assert.Equal(50, result[0].B);
    }

    [Fact]
    public void ChildCellsNestInsideParentCells_ForEvenGridDivisions()
    {
        // A child's bbox is exactly half its parent's on every axis (by
        // construction, see BoundingCube.ChildBounds). With an even
        // gridDivisions shared at every level, cell size doubles per level
        // and a child's grid origin lines up exactly with a parent cell
        // boundary — no straddling.
        const int gridDivisions = 8;
        var parent = new BoundingCube(0, 0, 0, 64);
        double parentCellSize = parent.Size / gridDivisions;

        for (int octant = 0; octant < 8; octant++)
        {
            var child = parent.ChildBounds(octant);
            double childCellSize = child.Size / gridDivisions;
            Assert.Equal(parentCellSize / 2, childCellSize, 9);

            // Child's min corner must land exactly on a parent-cell boundary.
            Assert.Equal(0, (child.MinX - parent.MinX) % parentCellSize, 9);
            Assert.Equal(0, (child.MinY - parent.MinY) % parentCellSize, 9);
            Assert.Equal(0, (child.MinZ - parent.MinZ) % parentCellSize, 9);
        }
    }

    private static List<PointRecord> MakeGrid(int countPerAxis, double spacing)
    {
        var points = new List<PointRecord>();
        for (int x = 0; x < countPerAxis; x++)
            for (int y = 0; y < countPerAxis; y++)
                for (int z = 0; z < countPerAxis; z++)
                    points.Add(new PointRecord(x * spacing + 1, y * spacing + 1, z * spacing + 1, 1, 2, 3));
        return points;
    }
}
