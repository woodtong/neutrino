namespace GhostScreen.Core;

/// <summary>Screen/window rectangle in physical pixels.</summary>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool IntersectsWith(Rect other) =>
        !(other.X >= Right || other.Right <= X || other.Y >= Bottom || other.Bottom <= Y);

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public Rect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public override string ToString() => $"({X},{Y} {Width}x{Height})";
}
