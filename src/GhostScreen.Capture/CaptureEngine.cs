using GhostScreen.Core;
using GhostScreen.Windows;

namespace GhostScreen.Capture;

public sealed record CaptureOptions(
    int TimeoutMs = 6_000,
    int WaitStableMs = 250,
    int MaxWidth = 0,
    bool IncludeFrame = false);

public sealed record CaptureResult(CaptureFrame Frame, CaptureMetadata Metadata);

/// <summary>
/// Off-screen native capture: PrintWindow first (works for Edge/UWP/classic),
/// WGC only as a visible-window fallback. Stability is measured by repeated
/// pulls separated by ~50 ms.
/// </summary>
public static class CaptureEngine
{
    public static CaptureResult Capture(IntPtr hwnd, CaptureOptions? options = null)
    {
        // Must run before WindowEnumerator.Describe: some window queries clear
        // the "not responding" flag, and a hung window still has a DWM surface
        // that PrintWindow would return as a stale frame.
        if (NativeMethods.IsHungAppWindow(hwnd))
        {
            throw new CaptureException("TARGET_HUNG", "Window is not responding (IsHungAppWindow).");
        }

        var opts = options ?? new CaptureOptions();
        var info = WindowEnumerator.Describe(hwnd)
            ?? throw new CaptureException("NOT_RENDERABLE", "Window handle is invalid or has no bounds.");

        if (info.Minimized)
        {
            throw new CaptureException("NOT_RENDERABLE", "Window is minimized.");
        }

        if (info.ProtectedFromCapture)
        {
            throw new CaptureException("PROTECTED_CONTENT", "Window is excluded from capture (WDA_EXCLUDEFROMCAPTURE).");
        }

        var display = DisplayInfo.Get();
        var visible = info.Visible && !display.IsFullyOffscreen(info.Bounds);

        var first = CaptureOnce(hwnd, info, visible, opts, out var firstError, out var firstTimedOut)
            ?? throw new CaptureException(
                firstTimedOut ? "CAPTURE_TIMEOUT" : "CAPTURE_FAILED",
                firstError ?? "No capture backend succeeded.");

        var stable = false;
        var frame = CropIfNeeded(first, info, opts);

        if (opts.WaitStableMs > 0)
        {
            var deadline = Environment.TickCount64 + Math.Max(opts.WaitStableMs * 4, 600);
            var lastHash = frame.Hash;
            var stableSince = Environment.TickCount64;

            while (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(50);
                var nextRaw = CaptureOnce(hwnd, info, visible, opts, out _, out _);
                if (nextRaw is null)
                {
                    continue;
                }

                var next = CropIfNeeded(nextRaw, info, opts);
                if (next.Hash == lastHash)
                {
                    if (Environment.TickCount64 - stableSince >= opts.WaitStableMs)
                    {
                        frame = next;
                        stable = true;
                        break;
                    }
                }
                else
                {
                    lastHash = next.Hash;
                    stableSince = Environment.TickCount64;
                    frame = next;
                }
            }
        }

        return new CaptureResult(frame, BuildMetadata(info, frame, stable, backend: frame.Backend));
    }

    private static CaptureFrame CropIfNeeded(CaptureFrame frame, WindowInfo info, CaptureOptions options)
    {
        if (options.IncludeFrame || frame.Backend != "printwindow")
        {
            return frame;
        }

        var originX = info.ClientBounds.X - info.Bounds.X;
        var originY = info.ClientBounds.Y - info.Bounds.Y;
        var width = Math.Min(info.ClientBounds.Width, frame.Width - originX);
        var height = Math.Min(info.ClientBounds.Height, frame.Height - originY);

        if (originX < 0 || originY < 0 || width <= 0 || height <= 0 ||
            (originX == 0 && originY == 0 && width == frame.Width && height == frame.Height))
        {
            return frame;
        }

        var stride = width * 4;
        var bgra = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var sourceOffset = (originY + y) * frame.Stride + originX * 4;
            Array.Copy(frame.Bgra, sourceOffset, bgra, y * stride, stride);
        }

        return frame with
        {
            Bgra = bgra,
            Width = width,
            Height = height,
            Blank = FrameAnalysis.IsBlank(bgra, width, height),
            Hash = FrameAnalysis.Hash(bgra),
            Backend = "printwindow",
        };
    }

    private static CaptureFrame? CaptureOnce(
        IntPtr hwnd,
        WindowInfo info,
        bool visible,
        CaptureOptions options,
        out string? error,
        out bool timedOut)
    {
        error = null;
        timedOut = false;

        var dpiUnaware = info.DpiAwareness == WindowDpiAwareness.Unaware;

        if (PrintWindowCapture.TryCaptureWithTimeout(
                hwnd,
                options.TimeoutMs,
                out var printWindowFrame,
                out var printWindowError,
                dpiUnaware,
                info.Bounds.Width,
                info.Bounds.Height) &&
            printWindowFrame is not null &&
            !printWindowFrame.Blank)
        {
            return printWindowFrame;
        }

        error = printWindowError ?? "PrintWindow returned a blank frame.";
        var printWindowTimedOut = printWindowError?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true;

        if (!visible)
        {
            timedOut = printWindowTimedOut;
            return null;
        }

        if (WgcCapture.TryCapture(hwnd, options.TimeoutMs, out var wgcFrame, out var wgcError) &&
            wgcFrame is not null &&
            !wgcFrame.Blank)
        {
            return wgcFrame;
        }

        error += $" WGC: {wgcError}";
        timedOut = printWindowTimedOut;
        _ = info;
        return null;
    }

    private static CaptureMetadata BuildMetadata(
        WindowInfo info,
        CaptureFrame frame,
        bool stable,
        string backend)
    {
        var isClientCropped = backend == "printwindow" &&
                              frame.Width == info.ClientBounds.Width &&
                              frame.Height == info.ClientBounds.Height;
        var clientOriginX = backend == "printwindow" && !isClientCropped
            ? info.ClientBounds.X - info.Bounds.X
            : 0;
        var clientOriginY = backend == "printwindow" && !isClientCropped
            ? info.ClientBounds.Y - info.Bounds.Y
            : 0;

        return new CaptureMetadata(
            backend,
            frame.Width,
            frame.Height,
            1.0,
            info.Bounds,
            info.ClientBounds,
            clientOriginX,
            clientOriginY,
            info.DpiScalePercent,
            stable,
            frame.Blank,
            frame.Hash,
            frame.CapturedAtUnixMs);
    }
}
