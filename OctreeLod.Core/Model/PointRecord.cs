namespace OctreeLod.Core.Model;

public readonly struct PointRecord
{
    public const int ByteSize = sizeof(double) * 3 + sizeof(byte) * 3;

    public double X { get; }
    public double Y { get; }
    public double Z { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public PointRecord(double x, double y, double z, byte r, byte g, byte b)
    {
        X = x;
        Y = y;
        Z = z;
        R = r;
        G = g;
        B = b;
    }
}
