using System;
using System.Collections.Generic;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.Sources;

// Synthetic point cloud: a "waving" undulating surface (sum of a few sine
// components at different frequencies/directions, so it looks like natural
// rolling terrain rather than one repeating ripple), with color mapped to
// elevation via a low-to-high terrain gradient (blue -> green -> yellow ->
// red). No file, no georeferencing — plain local Cartesian meters, same as
// TextPointCloudBatchSource's "already-Cartesian" contract.
//
// Models a real airborne-LiDAR survey: the platform flies straight "run
// lines" along X, each covering a cross-track (Y) band `swathWidth` wide —
// narrower than the full area, since one pass can't see the whole width.
// At each along-track (X) step the scanner emits one full cross-track (Y)
// sweep, PERPENDICULAR to the direction of travel — not a point trickling
// along X. Successive run lines are stacked along Y and alternate X
// direction (lawn-mower/boustrophedon), since a real flight path reverses
// rather than flying back to the start every time. `sweepsPerBatch` groups
// that many consecutive along-track sweeps into one yielded batch, rather
// than one sweep (or one arbitrary point count) at a time.
public sealed class WavingSurfacePointCloudBatchSource : IPointBatchSource
{
    private readonly double _pointSpacing;
    private readonly double _swathWidth;
    private readonly int _sweepsPerBatch;

    public int PointsPerSweep { get; }
    public int SweepsPerRunLine { get; }
    public int RunLineCount { get; }
    public long TotalPointCount => (long)PointsPerSweep * SweepsPerRunLine * RunLineCount;

    // areaSize: extent of the (square) area covered, in meters.
    // pointSpacing: distance between adjacent samples within a sweep and
    // between along-track steps — the "density" knob (smaller = denser).
    // swathWidth: cross-track width one run line's sweep covers, in
    // meters — smaller means more, narrower run lines needed to cover the
    // full area (a real scanner's limited field of view).
    // sweepsPerBatch: how many consecutive along-track sweeps are grouped
    // into one yielded batch.
    public WavingSurfacePointCloudBatchSource(double areaSize, double pointSpacing, double swathWidth, int sweepsPerBatch = 1)
    {
        if (areaSize <= 0) throw new ArgumentOutOfRangeException(nameof(areaSize));
        if (pointSpacing <= 0) throw new ArgumentOutOfRangeException(nameof(pointSpacing));
        if (swathWidth <= 0) throw new ArgumentOutOfRangeException(nameof(swathWidth));
        if (sweepsPerBatch < 1) throw new ArgumentOutOfRangeException(nameof(sweepsPerBatch));

        _pointSpacing = pointSpacing;
        _swathWidth = swathWidth;
        _sweepsPerBatch = sweepsPerBatch;

        PointsPerSweep = (int)(swathWidth / pointSpacing) + 1;
        SweepsPerRunLine = (int)(areaSize / pointSpacing) + 1; // along-track: one run line covers the full area length
        RunLineCount = (int)Math.Ceiling(areaSize / swathWidth); // cross-track: enough run lines to cover the full width
    }

    public IEnumerable<IReadOnlyList<PointRecord>> ReadBatches()
    {
        var batch = new List<PointRecord>(PointsPerSweep * _sweepsPerBatch);
        int sweepsInBatch = 0;

        for (int runLine = 0; runLine < RunLineCount; runLine++)
        {
            double yOffset = runLine * _swathWidth;
            bool reverse = (runLine % 2) == 1; // lawn-mower: alternate along-track direction each run line

            for (int i = 0; i < SweepsPerRunLine; i++)
            {
                int alongTrackIndex = reverse ? SweepsPerRunLine - 1 - i : i;
                double x = alongTrackIndex * _pointSpacing;

                // One instantaneous cross-track sweep, perpendicular to the
                // run line's along-track (X) direction of travel.
                for (int j = 0; j < PointsPerSweep; j++)
                {
                    double y = yOffset + j * _pointSpacing;
                    double elevation = Elevation(x, y);
                    var (r, g, b) = ElevationColor(elevation);
                    batch.Add(new PointRecord(x, y, elevation, r, g, b));
                }

                sweepsInBatch++;
                if (sweepsInBatch == _sweepsPerBatch)
                {
                    yield return batch;
                    batch = new List<PointRecord>(PointsPerSweep * _sweepsPerBatch);
                    sweepsInBatch = 0;
                }
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
    private static readonly (double r, double g, double b)[] ColorStops =
    {
        (0.10, 0.20, 0.80), // low: blue
        (0.10, 0.70, 0.30), // green
        (0.90, 0.80, 0.10), // yellow
        (0.80, 0.10, 0.10), // high: red
    };

    private static (byte r, byte g, byte b) ElevationColor(double elevation)
    {
        double t = (elevation + 13.5) / 27.0;
        t = t < 0.0 ? 0.0 : (t > 1.0 ? 1.0 : t);

        double scaled = t * (ColorStops.Length - 1);
        int i = (int)scaled;
        if (i >= ColorStops.Length - 1) i = ColorStops.Length - 2;
        double frac = scaled - i;

        double r = Lerp(ColorStops[i].r, ColorStops[i + 1].r, frac);
        double g = Lerp(ColorStops[i].g, ColorStops[i + 1].g, frac);
        double b = Lerp(ColorStops[i].b, ColorStops[i + 1].b, frac);

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
