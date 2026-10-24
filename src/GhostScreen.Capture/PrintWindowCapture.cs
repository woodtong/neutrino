using System.Runtime.InteropServices;
using GhostScreen.Windows;

namespace GhostScreen.Capture;

/// <summary>
/// Pull-based fallback capture. PW_RENDERFULLCONTENT asks DWM for the composed
/// surface, so the window does not need to be on screen; app support varies.
/// </summary>
public static class PrintWindowCapture
{
    public static CaptureFrame Capture(IntPtr hwnd)
    {
        if (!TryCapture(hwnd, out var frame, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return frame!;
    }

    /// <summary>
    /// Guards the daemon against a hung target. On timeout the worker remains
    /// blocked inside PrintWindow, so callers should mark this target degraded
    /// and avoid scheduling more captures for a while.
    /// </summary>
    public static bool TryCaptureWithTimeout(
        IntPtr hwnd,
        int timeoutMs,
        out CaptureFrame? frame,
        out string? error,
        bool unawareContext = false,
        int physicalWidth = 0,
        int physicalHeight = 0)
    {
        frame = null;
        error = null;

        if (timeoutMs <= 0)
        {
            error = "timeoutMs must be positive.";
            return false;
        }

        // A dedicated background thread keeps a hung PrintWindow from starving the
        // thread pool. On timeout the thread is abandoned; callers must degrade the
        // target and avoid scheduling more captures against it for a while.
        var completion = new TaskCompletionSource<(bool Ok, CaptureFrame? Frame, string? Error)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = new Thread(() =>
        {
            IntPtr previousContext = IntPtr.Zero;
            var changedContext = false;

            try
            {
                if (unawareContext)
                {
                    previousContext = NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(-1));
                    changedContext = previousContext != IntPtr.Zero;
                }

                var ok = TryCapture(hwnd, out var captured, out var captureError, physicalWidth, physicalHeight);
                completion.TrySetResult((ok, captured, captureError));
            }
            catch (Exception ex)
            {
                completion.TrySetResult((false, null, ex.Message));
            }
            finally
            {
                if (changedContext)
                {
                    NativeMethods.SetThreadDpiAwarenessContext(previousContext);
                }
            }
        })
        {
            IsBackground = true,
            Name = "GhostScreen.PrintWindow",
        };

        worker.Start();

        if (!completion.Task.Wait(timeoutMs))
        {
            error = $"PrintWindow timed out after {timeoutMs} ms; target may be hung.";
            return false;
        }

        var result = completion.Task.Result;
        frame = result.Frame;
        error = result.Error;
        return result.Ok;
    }

    private static byte[]? StretchBgra(
        IntPtr sourceDc,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight)
    {
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        var destDc = IntPtr.Zero;
        var destBitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            destDc = NativeMethods.CreateCompatibleDC(screenDc);
            destBitmap = NativeMethods.CreateCompatibleBitmap(screenDc, targetWidth, targetHeight);
            if (destDc == IntPtr.Zero || destBitmap == IntPtr.Zero)
            {
                return null;
            }

            previous = NativeMethods.SelectObject(destDc, destBitmap);
            NativeMethods.SetStretchBltMode(destDc, 4); // HALFTONE

            const uint srcCopy = 0x00CC0020;
            if (!NativeMethods.StretchBlt(
                    destDc,
                    0,
                    0,
                    targetWidth,
                    targetHeight,
                    sourceDc,
                    0,
                    0,
                    sourceWidth,
                    sourceHeight,
                    srcCopy))
            {
                return null;
            }

            var info = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = targetWidth,
                    biHeight = -targetHeight,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
            };

            var bgra = new byte[targetWidth * targetHeight * 4];
            var lines = NativeMethods.GetDIBits(
                destDc,
                destBitmap,
                0,
                (uint)targetHeight,
                bgra,
                ref info,
                NativeMethods.DIB_RGB_COLORS);

            return lines == targetHeight ? bgra : null;
        }
        finally
        {
            if (previous != IntPtr.Zero && destDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(destDc, previous);
            }

            if (destBitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(destBitmap);
            }

            if (destDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(destDc);
            }

            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static bool TryCapture(
        IntPtr hwnd,
        out CaptureFrame? frame,
        out string? error,
        int physicalWidth = 0,
        int physicalHeight = 0)
    {
        frame = null;
        error = null;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            error = "GetWindowRect failed.";
            return false;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            error = "Window has an empty rectangle.";
            return false;
        }

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            error = "GetDC(screen) failed.";
            return false;
        }

        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                error = "Failed to create GDI resources.";
                return false;
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            var printed = NativeMethods.PrintWindow(hwnd, memoryDc, NativeMethods.PW_RENDERFULLCONTENT);

            var info = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB,
                },
            };

            var bgra = new byte[width * height * 4];
            var lines = NativeMethods.GetDIBits(
                memoryDc,
                bitmap,
                0,
                (uint)height,
                bgra,
                ref info,
                NativeMethods.DIB_RGB_COLORS);

            if (lines != height)
            {
                error = $"GetDIBits returned {lines}/{height} lines.";
                return false;
            }

            // DPI-unaware targets render at logical size inside a physical bitmap.
            // Capture under an unaware thread context, then scale up so the image
            // matches the physical window/client rectangle used by coordinates.
            if (physicalWidth > 0 &&
                physicalHeight > 0 &&
                (physicalWidth != width || physicalHeight != height))
            {
                var scaled = StretchBgra(memoryDc, width, height, physicalWidth, physicalHeight);
                if (scaled is not null)
                {
                    bgra = scaled;
                    width = physicalWidth;
                    height = physicalHeight;
                }
                else
                {
                    error = "DPI-unaware capture could not be scaled to physical size.";
                }
            }

            var blank = FrameAnalysis.IsBlank(bgra, width, height);
            frame = new CaptureFrame(
                bgra,
                width,
                height,
                "printwindow",
                blank,
                FrameAnalysis.Hash(bgra),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            if (!printed)
            {
                error = "PrintWindow returned false; frame may be incomplete.";
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
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
