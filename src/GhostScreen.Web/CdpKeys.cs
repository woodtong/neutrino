using System.Buffers.Binary;

namespace GhostScreen.Web;

/// <summary>Key metadata for CDP <c>Input.dispatchKeyEvent</c>.</summary>
public sealed record CdpKey(string Key, string Code, int VirtualKey, string? Text = null, string? ShiftText = null)
{
    private const int AltMask = 1;
    private const int CtrlMask = 2;
    private const int MetaMask = 4;
    private const int ShiftMask = 8;

    public string? TextFor(int modifiers)
    {
        if ((modifiers & (CtrlMask | AltMask | MetaMask)) != 0)
        {
            return null;
        }

        return (modifiers & ShiftMask) != 0 ? ShiftText ?? Text?.ToUpperInvariant() : Text;
    }
}

public static class CdpKeys
{
    private static readonly Dictionary<string, CdpKey> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTER"] = new("Enter", "Enter", 13, "\r"),
        ["RETURN"] = new("Enter", "Enter", 13, "\r"),
        ["TAB"] = new("Tab", "Tab", 9),
        ["ESC"] = new("Escape", "Escape", 27),
        ["ESCAPE"] = new("Escape", "Escape", 27),
        ["BACKSPACE"] = new("Backspace", "Backspace", 8),
        ["DELETE"] = new("Delete", "Delete", 46),
        ["DEL"] = new("Delete", "Delete", 46),
        ["INSERT"] = new("Insert", "Insert", 45),
        ["HOME"] = new("Home", "Home", 36),
        ["END"] = new("End", "End", 35),
        ["PAGEUP"] = new("PageUp", "PageUp", 33),
        ["PAGEDOWN"] = new("PageDown", "PageDown", 34),
        ["SPACE"] = new(" ", "Space", 32, " "),
        ["ARROWUP"] = new("ArrowUp", "ArrowUp", 38),
        ["UP"] = new("ArrowUp", "ArrowUp", 38),
        ["ARROWDOWN"] = new("ArrowDown", "ArrowDown", 40),
        ["DOWN"] = new("ArrowDown", "ArrowDown", 40),
        ["ARROWLEFT"] = new("ArrowLeft", "ArrowLeft", 37),
        ["LEFT"] = new("ArrowLeft", "ArrowLeft", 37),
        ["ARROWRIGHT"] = new("ArrowRight", "ArrowRight", 39),
        ["RIGHT"] = new("ArrowRight", "ArrowRight", 39),
        ["CTRL"] = new("Control", "ControlLeft", 17),
        ["CONTROL"] = new("Control", "ControlLeft", 17),
        ["SHIFT"] = new("Shift", "ShiftLeft", 16),
        ["ALT"] = new("Alt", "AltLeft", 18),
        ["OPTION"] = new("Alt", "AltLeft", 18),
        ["WIN"] = new("Meta", "MetaLeft", 91),
        ["META"] = new("Meta", "MetaLeft", 91),
        ["CMD"] = new("Meta", "MetaLeft", 91),
        ["COMMAND"] = new("Meta", "MetaLeft", 91),
    };

    private const string ShiftedDigits = ")!@#$%^&*(";

    public static CdpKey? Resolve(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var trimmed = key.Trim();

        if (Named.TryGetValue(trimmed, out var named))
        {
            return named;
        }

        if (trimmed.Length == 1)
        {
            var character = trimmed[0];

            if (character is >= 'a' and <= 'z' || character is >= 'A' and <= 'Z')
            {
                var lower = char.ToLowerInvariant(character);
                var upper = char.ToUpperInvariant(lower);
                return new CdpKey(
                    lower.ToString(),
                    "Key" + upper,
                    upper,
                    character.ToString(),
                    upper.ToString());
            }

            if (character is >= '0' and <= '9')
            {
                var index = character - '0';
                return new CdpKey(
                    character.ToString(),
                    "Digit" + character,
                    character,
                    character.ToString(),
                    ShiftedDigits[index].ToString());
            }
        }

        if (trimmed.Length is 2 or 3 &&
            (trimmed[0] is 'F' or 'f') &&
            int.TryParse(trimmed.AsSpan(1), out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            return new CdpKey($"F{functionKey}", $"F{functionKey}", 111 + functionKey, null);
        }

        return null;
    }

    public static bool IsModifier(string key) => ModifierMasks.ContainsKey(key.Trim());

    public static int ModifierMask(IReadOnlyList<string> modifiers)
    {
        var mask = 0;
        foreach (var modifier in modifiers)
        {
            if (ModifierMasks.TryGetValue(modifier.Trim(), out var bit))
            {
                mask |= bit;
            }
        }

        return mask;
    }

    private static readonly Dictionary<string, int> ModifierMasks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ALT"] = 1,
        ["OPTION"] = 1,
        ["CTRL"] = 2,
        ["CONTROL"] = 2,
        ["WIN"] = 4,
        ["META"] = 4,
        ["CMD"] = 4,
        ["COMMAND"] = 4,
        ["SHIFT"] = 8,
    };
}

public static class PngInfo
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool HasSignature(ReadOnlySpan<byte> png) =>
        png.Length >= 8 && png[..8].SequenceEqual(Signature);

    public static (int Width, int Height)? TryReadSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !HasSignature(png))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
        return width > 0 && height > 0 ? (width, height) : null;
    }
}
