using System.Collections.Generic;

namespace OctreeLod.Core;

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
        var cells = new Dictionary<(int, int, int), Cell>();

        foreach (var p in points)
        {
            int cx = (int)((p.X - bbox.MinX) / cellSize);
            int cy = (int)((p.Y - bbox.MinY) / cellSize);
            int cz = (int)((p.Z - bbox.MinZ) / cellSize);
            var key = (cx, cy, cz);

            double centerX = bbox.MinX + (cx + 0.5) * cellSize;
            double centerY = bbox.MinY + (cy + 0.5) * cellSize;
            double centerZ = bbox.MinZ + (cz + 0.5) * cellSize;
            double dx = p.X - centerX, dy = p.Y - centerY, dz = p.Z - centerZ;
            double distSq = dx * dx + dy * dy + dz * dz;

            if (!cells.TryGetValue(key, out var cell))
            {
                cells[key] = new Cell
                {
                    BestPoint = p,
                    BestDistSq = distSq,
                    ColorSumR = p.R,
                    ColorSumG = p.G,
                    ColorSumB = p.B,
                    Count = 1,
                };
                continue;
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
            cells[key] = cell;
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

    private struct Cell
    {
        public PointRecord BestPoint;
        public double BestDistSq;
        public long ColorSumR, ColorSumG, ColorSumB;
        public int Count;
    }
}
