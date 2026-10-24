namespace GhostScreen.Capture;

/// <summary>One raw BGRA frame plus the metadata the agent needs to judge freshness.</summary>
public sealed record CaptureFrame(
    byte[] Bgra,
    int Width,
    int Height,
    string Backend,
    bool Blank,
    string Hash,
    long CapturedAtUnixMs)
{
    public int Stride => Width * 4;

    public long ByteCount => (long)Bgra.Length;
}
