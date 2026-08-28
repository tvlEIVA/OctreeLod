using System;
using System.Collections.Generic;
using OctreeLod.Core.Model;

namespace OctreeLod.App.Sources;

// Synthetic point cloud: a "waving" undulating surface (sum of a few sine
// components at different frequencies/directions, so it looks like natural
// rolling terrain rather than one repeating ripple), with color mapped to
// elevation via a low-to-high terrain gradient (blue -> green -> yellow ->
// red). No file, no georeferencing — plain local Cartesian meters, same as
// TextPointCloudBatchSource's "already-Cartesian" contract.
//
// Streams points in raster (row-major) order, top row first, one row =
// one line of the "scan". `linesPerBatch` groups that many consecutive
// rows into a single yielded batch, rather than one row (or one arbitrary
// point count) at a time — simulates several scan lines arriving together.
public sealed class WavingSurfacePointCloudBatchSource : IPointBatchSource
{
    private readonly double _pointSpacing;
    private readonly int _linesPerBatch;

    public int PointsPerLine { get; }
    public int LineCount { get; }
    public long TotalPointCount => (long)PointsPerLine * LineCount;

    // areaSize: extent of the (square) area covered, in meters.
    // pointSpacing: distance between adjacent samples along a line and
    // between lines — the "density" knob (smaller = denser, more points).
    // linesPerBatch: how many consecutive rows are grouped into one
    // yielded batch.
    public WavingSurfacePointCloudBatchSource(double areaSize, double pointSpacing, int linesPerBatch = 1)
    {
        if (areaSize <= 0) throw new ArgumentOutOfRangeException(nameof(areaSize));
        if (pointSpacing <= 0) throw new ArgumentOutOfRangeException(nameof(pointSpacing));
        if (linesPerBatch < 1) throw new ArgumentOutOfRangeException(nameof(linesPerBatch));

        _pointSpacing = pointSpacing;
        _linesPerBatch = linesPerBatch;

        PointsPerLine = (int)(areaSize / pointSpacing) + 1;
        LineCount = PointsPerLine; // square area
    }

    public IEnumerable<IReadOnlyList<PointRecord>> ReadBatches()
    {
        var batch = new List<PointRecord>(PointsPerLine * _linesPerBatch);

        for (int row = 0; row < LineCount; row++)
        {
            double y = row * _pointSpacing;
            for (int col = 0; col < PointsPerLine; col++)
            {
                double x = col * _pointSpacing;
                double elevation = Elevation(x, y);
                var (r, g, b) = ElevationColor(elevation);
                batch.Add(new PointRecord(x, y, elevation, r, g, b));
            }

            if ((row + 1) % _linesPerBatch == 0)
            {
                yield return batch;
                batch = new List<PointRecord>(PointsPerLine * _linesPerBatch);
            }
        }

        if (batch.Count > 0) yield return batch;
    }

    // Sum of a few sine components at different frequencies/directions —
    // avoids the perfectly-regular look a single sine wave would give,
    // while staying cheap to evaluate per point. Range is +-13.5.
    private static double Elevation(double x, double y)
    {
        return 8.0 * Math.Sin(x * 0.004 + y * 0.002)
             + 4.0 * Math.Sin(x * 0.011 - y * 0.017)
             + 1.5 * Math.Sin(x * 0.031 + y * 0.037);
    }

    // Classic low-to-high terrain gradient: blue (low) -> green -> yellow
    // -> red (high), matched to Elevation's +-13.5 range.
    private static (byte r, byte g, byte b) ElevationColor(double elevation)
    {
        double t = (elevation + 13.5) / 27.0;
        t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);

        Span<(double r, double g, double b)> stops = stackalloc (double, double, double)[]
        {
            (0.10, 0.20, 0.80), // low: blue
            (0.10, 0.70, 0.30), // green
            (0.90, 0.80, 0.10), // yellow
            (0.80, 0.10, 0.10), // high: red
        };

        double scaled = t * (stops.Length - 1);
        int i = (int)scaled;
        if (i >= stops.Length - 1) i = stops.Length - 2;
        double frac = scaled - i;

        double r = Lerp(stops[i].r, stops[i + 1].r, frac);
        double g = Lerp(stops[i].g, stops[i + 1].g, frac);
        double b = Lerp(stops[i].b, stops[i + 1].b, frac);

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
