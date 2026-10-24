using System.Runtime.InteropServices;
using GhostScreen.Core;
using GhostScreen.Windows;

namespace GhostScreen.Capture;

/// <summary>GDI screen sampling used to prove a parked window is not visible.</summary>
public static class ScreenProbe
{
    public readonly record struct ScreenSample(byte[] Bgra, int Width, int Height, string Hash);

    public static ScreenSample Capture(Rect region)
    {
        if (region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Region must have a positive size.");
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC(screen) failed.");
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, region.Width, region.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create GDI resources.");
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            var copied = NativeMethods.BitBlt(
                memoryDc,
                0,
                0,
                region.Width,
                region.Height,
                screenDc,
                region.X,
                region.Y,
                NativeMethods.SRCCOPY);

            if (!copied)
            {
                throw new InvalidOperationException("BitBlt failed.");
            }

            var info = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = region.Width,
                    biHeight = -region.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
            };

            var bgra = new byte[region.Width * region.Height * 4];
            var lines = NativeMethods.GetDIBits(
                memoryDc,
                bitmap,
                0,
                (uint)region.Height,
                bgra,
                ref info,
                NativeMethods.DIB_RGB_COLORS);

            if (lines != region.Height)
            {
                throw new InvalidOperationException($"GetDIBits returned {lines}/{region.Height} lines.");
            }

            return new ScreenSample(bgra, region.Width, region.Height, FrameAnalysis.Hash(bgra));
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
