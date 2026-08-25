using System;
using System.IO;
using System.Text;
using OctreeLod.Core;

namespace OctreeLod.Tests;

public class PntsWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OctreeLodTests-" + Guid.NewGuid().ToString("N"));

    public PntsWriterTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void RoundTrips_PositionsWithinFloatTolerance_AndColorsExactly()
    {
        var bbox = new BoundingCube(1_000_000, 500_000, 200_000, 100);
        var points = new[]
        {
            new PointRecord(1_000_010, 500_020, 200_005, 10, 20, 30),
            new PointRecord(1_000_090, 500_080, 200_095, 200, 210, 220),
        };
        string path = Path.Combine(_dir, "test.pnts");

        PntsWriter.WriteFile(path, bbox, points);
        var parsed = ParsePnts(path);

        Assert.Equal(points.Length, parsed.PointCount);
        for (int i = 0; i < points.Length; i++)
        {
            Assert.Equal(points[i].X, parsed.Positions[i].X, 3); // float32 precision at this magnitude
            Assert.Equal(points[i].Y, parsed.Positions[i].Y, 3);
            Assert.Equal(points[i].Z, parsed.Positions[i].Z, 3);
            Assert.Equal(points[i].R, parsed.Colors[i].R);
            Assert.Equal(points[i].G, parsed.Colors[i].G);
            Assert.Equal(points[i].B, parsed.Colors[i].B);
        }
    }

    [Fact]
    public void RtcCenterIsTileBboxCenter()
    {
        var bbox = new BoundingCube(2_000_000, -3_000_000, 500_000, 64);
        var points = new[] { new PointRecord(2_000_010, -2_999_990, 500_010, 1, 2, 3) };
        string path = Path.Combine(_dir, "rtc.pnts");

        PntsWriter.WriteFile(path, bbox, points);
        var parsed = ParsePnts(path);

        Assert.Equal(2_000_000 + 32, parsed.RtcCenter[0], 6);
        Assert.Equal(-3_000_000 + 32, parsed.RtcCenter[1], 6);
        Assert.Equal(500_000 + 32, parsed.RtcCenter[2], 6);
    }

    [Fact]
    public void ByteLengthAndFeatureTableJsonAreEightByteAligned()
    {
        var bbox = new BoundingCube(0, 0, 0, 8);
        // Odd count deliberately produces a JSON string length unlikely to
        // already be aligned, exercising the padding logic.
        var points = new PointRecord[7];
        for (int i = 0; i < points.Length; i++)
            points[i] = new PointRecord(i, i, i, (byte)i, (byte)i, (byte)i);
        string path = Path.Combine(_dir, "align.pnts");

        PntsWriter.WriteFile(path, bbox, points);

        byte[] raw = File.ReadAllBytes(path);
        uint byteLength = BitConverter.ToUInt32(raw, 8);
        uint jsonLength = BitConverter.ToUInt32(raw, 12);

        Assert.Equal(0u, byteLength % 8);
        Assert.Equal(0, (int)(28 + jsonLength) % 8); // binary section starts 8-byte aligned
        Assert.Equal((uint)raw.Length, byteLength);
    }

    [Fact]
    public void EmptyPointSet_WritesValidFile()
    {
        var bbox = new BoundingCube(0, 0, 0, 8);
        string path = Path.Combine(_dir, "empty.pnts");

        PntsWriter.WriteFile(path, bbox, Array.Empty<PointRecord>());
        var parsed = ParsePnts(path);

        Assert.Equal(0, parsed.PointCount);
    }

    private struct Parsed
    {
        public int PointCount;
        public double[] RtcCenter;
        public (double X, double Y, double Z)[] Positions;
        public (byte R, byte G, byte B)[] Colors;
    }

    // Independent manual parse of the binary layout — deliberately not
    // reusing PntsWriter's own logic, so this actually catches writer bugs
    // rather than confirming the writer agrees with itself.
    private static Parsed ParsePnts(string path)
    {
        byte[] raw = File.ReadAllBytes(path);

        string magic = Encoding.ASCII.GetString(raw, 0, 4);
        Assert.Equal("pnts", magic);
        uint version = BitConverter.ToUInt32(raw, 4);
        Assert.Equal(1u, version);

        uint byteLength = BitConverter.ToUInt32(raw, 8);
        uint jsonLength = BitConverter.ToUInt32(raw, 12);
        uint binaryLength = BitConverter.ToUInt32(raw, 16);
        uint batchJsonLength = BitConverter.ToUInt32(raw, 20);
        uint batchBinaryLength = BitConverter.ToUInt32(raw, 24);
        Assert.Equal(0u, batchJsonLength);
        Assert.Equal(0u, batchBinaryLength);
        Assert.Equal((uint)raw.Length, byteLength);

        string json = Encoding.ASCII.GetString(raw, 28, (int)jsonLength).TrimEnd(' ');
        int pointsLength = ExtractInt(json, "\"POINTS_LENGTH\":");
        double[] rtcCenter = ExtractDoubleArray(json, "\"RTC_CENTER\":[");
        int positionOffset = ExtractInt(json, "\"POSITION\":{\"byteOffset\":");
        int rgbOffset = ExtractInt(json, "\"RGB\":{\"byteOffset\":");

        int binaryStart = 28 + (int)jsonLength;
        var positions = new (double, double, double)[pointsLength];
        for (int i = 0; i < pointsLength; i++)
        {
            int off = binaryStart + positionOffset + i * 12;
            float x = BitConverter.ToSingle(raw, off);
            float y = BitConverter.ToSingle(raw, off + 4);
            float z = BitConverter.ToSingle(raw, off + 8);
            positions[i] = (rtcCenter[0] + x, rtcCenter[1] + y, rtcCenter[2] + z);
        }

        var colors = new (byte, byte, byte)[pointsLength];
        for (int i = 0; i < pointsLength; i++)
        {
            int off = binaryStart + rgbOffset + i * 3;
            colors[i] = (raw[off], raw[off + 1], raw[off + 2]);
        }

        _ = binaryLength;
        return new Parsed
        {
            PointCount = pointsLength,
            RtcCenter = rtcCenter,
            Positions = Array.ConvertAll(positions, p => (p.Item1, p.Item2, p.Item3)),
            Colors = Array.ConvertAll(colors, c => (c.Item1, c.Item2, c.Item3)),
        };
    }

    private static int ExtractInt(string json, string key)
    {
        int idx = json.IndexOf(key, StringComparison.Ordinal) + key.Length;
        int end = idx;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
        return int.Parse(json.Substring(idx, end - idx));
    }

    private static double[] ExtractDoubleArray(string json, string key)
    {
        int idx = json.IndexOf(key, StringComparison.Ordinal) + key.Length;
        int end = json.IndexOf(']', idx);
        string[] parts = json.Substring(idx, end - idx).Split(',');
        var result = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            result[i] = double.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        return result;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
