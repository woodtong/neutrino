namespace GhostScreen.Core;

/// <summary>Metadata returned with every captured frame.</summary>
public sealed record CaptureMetadata(
    string Backend,
    int Width,
    int Height,
    double ImageScale,
    Rect WindowRect,
    Rect ClientRect,
    int ClientOriginX,
    int ClientOriginY,
    int DpiScalePercent,
    bool Stable,
    bool Blank,
    string Hash,
    long CapturedAtUnixMs)
{
    public override string ToString() =>
        $"backend={Backend} {Width}x{Height} stable={Stable} blank={Blank} " +
        $"hash={Hash} window={WindowRect} client={ClientRect} origin=({ClientOriginX},{ClientOriginY})";
}

public sealed class CaptureException : Exception
{
    public CaptureException(string code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}
