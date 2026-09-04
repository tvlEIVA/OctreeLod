using System.IO;
using System.Text;
using OctreeLod.Core.Model;

namespace OctreeLod.Core.Export;

// Legacy 3D Tiles Point Cloud (.pnts) writer. Binary layout verified against
// CesiumGS/3d-tiles/specification/TileFormats/PointCloud/README.adoc:
// 28-byte header, then feature table JSON (space-padded to an 8-byte
// boundary), then feature table binary (POSITION, then RGB; zero-padded so
// the total file byteLength is 8-byte aligned per spec).
//
// POSITION is stored as float32 *relative to RTC_CENTER* (the tile's own
// bbox center, given as a feature-table global semantic). Without this,
// float32 alone loses real precision once data sits millions of units from
// the origin (which this design's fixed world-scale root allows) — RTC_CENTER
// keeps every stored offset bounded by the tile's own (much smaller) bbox
// half-size instead of the absolute coordinate magnitude.
public static class PntsWriter
{
    private const int HeaderByteSize = 28;

    public static void WriteFile(string path, BoundingCube tileBbox, PointRecord[] points)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        WriteTo(fs, tileBbox, points);
    }

    // Same binary layout as WriteFile, but into any Stream — e.g. an HTTP
    // response body, for a server generating .pnts content live from the
    // in-memory octree instead of writing a file at all.
    public static void WriteTo(Stream stream, BoundingCube tileBbox, PointRecord[] points)
    {
        double centerX = tileBbox.MinX + tileBbox.Size / 2;
        double centerY = tileBbox.MinY + tileBbox.Size / 2;
        double centerZ = tileBbox.MinZ + tileBbox.Size / 2;

        int positionBytes = points.Length * 3 * sizeof(float);
        int rgbBytes = points.Length * 3 * sizeof(byte);

        string featureTableJson =
            "{\"POINTS_LENGTH\":" + points.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"RTC_CENTER\":[" +
            FormatDouble(centerX) + "," + FormatDouble(centerY) + "," + FormatDouble(centerZ) +
            "],\"POSITION\":{\"byteOffset\":0}" +
            ",\"RGB\":{\"byteOffset\":" + positionBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";

        byte[] jsonBytesUnpadded = Encoding.ASCII.GetBytes(featureTableJson);
        int jsonPad = PadTo8(HeaderByteSize + jsonBytesUnpadded.Length) - (HeaderByteSize + jsonBytesUnpadded.Length);
        int featureTableJsonByteLength = jsonBytesUnpadded.Length + jsonPad;

        int binaryUnpadded = positionBytes + rgbBytes;
        int totalUnpadded = HeaderByteSize + featureTableJsonByteLength + binaryUnpadded;
        int binaryPad = PadTo8(totalUnpadded) - totalUnpadded;
        int featureTableBinaryByteLength = binaryUnpadded + binaryPad;

        int byteLength = HeaderByteSize + featureTableJsonByteLength + featureTableBinaryByteLength;

        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("pnts"));
            writer.Write((uint)1);
            writer.Write((uint)byteLength);
            writer.Write((uint)featureTableJsonByteLength);
            writer.Write((uint)featureTableBinaryByteLength);
            writer.Write((uint)0); // batchTableJSONByteLength
            writer.Write((uint)0); // batchTableBinaryByteLength

            writer.Write(jsonBytesUnpadded);
            for (int i = 0; i < jsonPad; i++) writer.Write((byte)' ');

            foreach (var p in points)
            {
                writer.Write((float)(p.X - centerX));
                writer.Write((float)(p.Y - centerY));
                writer.Write((float)(p.Z - centerZ));
            }
            foreach (var p in points)
            {
                writer.Write(p.R);
                writer.Write(p.G);
                writer.Write(p.B);
            }
            for (int i = 0; i < binaryPad; i++) writer.Write((byte)0);
        }
    }

    private static int PadTo8(int length)
    {
        int remainder = length % 8;
        return remainder == 0 ? length : length + (8 - remainder);
    }

    private static string FormatDouble(double value) =>
        value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
