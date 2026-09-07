using System;
using System.Collections.Generic;
using OctreeLod.Core.Model;

namespace OctreeLod.Server;

// Renders a given timestamp as literal dot-matrix digits, baked directly
// into real PointRecords — generated fresh on every call, never stored
// anywhere, so each response to /recent-points.bin carries its own
// timestamp as actual point-cloud data (not a client-side overlay drawn on
// top of it). The caller passes RecentPointsBuffer.LastIngestedAt, not
// DateTime.Now, so this reflects when the points were actually ingested,
// not whenever a client happens to poll. Placed wherever the caller says
// (Program.cs anchors it to the current recent-points centroid) so it's
// easy to spot without hunting across a huge survey area.
public static class ClockPointsGenerator
{
    // Meters per "pixel" — fixed, not scaled to survey size, so digits stay
    // a sane physical size regardless of how big the area is.
    private const double PixelSize = 40.0;
    private static readonly (byte r, byte g, byte b) Color = (255, 0, 255); // bright magenta, distinct from terrain colors

    private static readonly Dictionary<char, string[]> DigitFont = new()
    {
        ['0'] = new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
        ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
        ['3'] = new[] { ".###.", "#...#", "....#", "..##.", "....#", "#...#", ".###." },
        ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
        ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
        ['6'] = new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." },
        ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
        ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
        ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." },
        [':'] = new[] { ".....", "..#..", ".....", ".....", ".....", "..#..", "....." },
    };

    public static List<PointRecord> Build(double originX, double originY, double originZ, DateTime timestamp)
    {
        string text = timestamp.ToString("HH:mm:ss");
        const int glyphCols = 5, glyphRows = 7;

        var points = new List<PointRecord>();
        for (int d = 0; d < text.Length; d++)
        {
            if (!DigitFont.TryGetValue(text[d], out var glyph)) continue;
            for (int row = 0; row < glyphRows; row++)
            {
                for (int col = 0; col < glyphCols; col++)
                {
                    if (glyph[row][col] != '#') continue;
                    double x = originX + d * (glyphCols + 1) * PixelSize + col * PixelSize;
                    double y = originY + (glyphRows - 1 - row) * PixelSize; // flip: row 0 (top of glyph) = largest y, for a top-down view
                    points.Add(new PointRecord(x, y, originZ, Color.r, Color.g, Color.b));
                }
            }
        }
        return points;
    }
}
