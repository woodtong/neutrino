using System.Security.Cryptography;

namespace GhostScreen.Capture;

public static class FrameAnalysis
{
    /// <summary>Stable content hash for freshness/stability checks.</summary>
    public static string Hash(byte[] bgra)
    {
        var hash = SHA256.HashData(bgra);
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// True when the frame is effectively a single color. Catches the common
    /// "capture returned black because the window is minimized/hidden" failure.
    /// </summary>
    public static bool IsBlank(byte[] bgra, int width, int height)
    {
        if (bgra.Length < 4 || width <= 0 || height <= 0)
        {
            return true;
        }

        var pixelCount = width * height;
        var stride = width * 4;
        var step = Math.Max(1, pixelCount / 2048);

        var minB = 255;
        var minG = 255;
        var minR = 255;
        var maxB = 0;
        var maxG = 0;
        var maxR = 0;

        for (var i = 0; i < pixelCount; i += step)
        {
            var offset = i / width * stride + i % width * 4;
            if (offset + 3 >= bgra.Length)
            {
                break;
            }

            var b = bgra[offset];
            var g = bgra[offset + 1];
            var r = bgra[offset + 2];

            if (b < minB) minB = b;
            if (g < minG) minG = g;
            if (r < minR) minR = r;
            if (b > maxB) maxB = b;
            if (g > maxG) maxG = g;
            if (r > maxR) maxR = r;
        }

        const int tolerance = 2;
        return maxB - minB <= tolerance && maxG - minG <= tolerance && maxR - minR <= tolerance;
    }

    public static int ColorRange(byte[] bgra, int width, int height)
    {
        if (bgra.Length == 0 || width <= 0 || height <= 0)
        {
            return 0;
        }

        var pixelCount = width * height;
        var stride = width * 4;
        var step = Math.Max(1, pixelCount / 2048);
        var min = 255;
        var max = 0;

        for (var i = 0; i < pixelCount; i += step)
        {
            var offset = i / width * stride + i % width * 4;
            if (offset + 2 >= bgra.Length)
            {
                break;
            }

            var luma = (bgra[offset] + bgra[offset + 1] + bgra[offset + 2]) / 3;
            if (luma < min) min = luma;
            if (luma > max) max = luma;
        }

        return max - min;
    }
}
