using System.Collections.Generic;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.SplitMergeEngine.Merge;

public static class GridSubsampler
{
    // Combines already-merged children point sets into one node's
    // representative set: one point per occupied grid cell. Position =
    // nearest sample to the cell center (deterministic regardless of batch/
    // insertion order, keeps the sample on a real surface point). Color =
    // average across the cell's points (avoids arbitrary color popping at
    // coarse levels).
    //
    // `gridDivisions` must be the same even constant at every level: since a
    // child's bbox is always exactly half its parent's, this makes cell size
    // double automatically per level *and* guarantees child cells nest
    // cleanly inside parent cells (no straddling) — AdaptiveRootTrimmer's
    // correctness assumes this nesting holds.
    public static PointRecord[] Subsample(BoundingCube bbox, IEnumerable<PointRecord> points, int gridDivisions)
    {
        double cellSize = bbox.Size / gridDivisions;
        var cells = new Dictionary<CellKey, Cell>();

        foreach (var p in points)
        {
            var key = CellKey.FromPoint(p, bbox, cellSize);

            double centerX = bbox.MinX + (key.X + 0.5) * cellSize;
            double centerY = bbox.MinY + (key.Y + 0.5) * cellSize;
            double centerZ = bbox.MinZ + (key.Z + 0.5) * cellSize;
            double dx = p.X - centerX, dy = p.Y - centerY, dz = p.Z - centerZ;
            double distSq = dx * dx + dy * dy + dz * dz;

            if (!cells.TryGetValue(key, out var cell))
            {
                cell = new Cell { BestPoint = p, BestDistSq = distSq, Count = 0 };
                cells[key] = cell;
            }

            cell.Count++;
            cell.ColorSumR += p.R;
            cell.ColorSumG += p.G;
            cell.ColorSumB += p.B;
            if (distSq < cell.BestDistSq)
            {
                cell.BestDistSq = distSq;
                cell.BestPoint = p;
            }
        }

        var result = new PointRecord[cells.Count];
        int i = 0;
        foreach (var cell in cells.Values)
        {
            byte r = (byte)(cell.ColorSumR / cell.Count);
            byte g = (byte)(cell.ColorSumG / cell.Count);
            byte b = (byte)(cell.ColorSumB / cell.Count);
            result[i++] = new PointRecord(cell.BestPoint.X, cell.BestPoint.Y, cell.BestPoint.Z, r, g, b);
        }
        return result;
    }

    private sealed class Cell
    {
        public PointRecord BestPoint;
        public double BestDistSq;
        public long ColorSumR, ColorSumG, ColorSumB;
        public int Count;
    }
}
