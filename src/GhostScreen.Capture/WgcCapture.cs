using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace GhostScreen.Capture;

/// <summary>
/// Per-window capture through Windows.Graphics.Capture. The window must be
/// shown/restored and not cloaked; occlusion does not matter.
/// </summary>
public static class WgcCapture
{
    private static readonly Lazy<GraphicsDevice> SharedDevice =
        new(CreateDevice, LazyThreadSafetyMode.ExecutionAndPublication);

    public static CaptureFrame Capture(IntPtr hwnd, int timeoutMs = 4_000)
    {
        if (!TryCapture(hwnd, timeoutMs, out var frame, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return frame!;
    }

    public static bool TryCapture(IntPtr hwnd, int timeoutMs, out CaptureFrame? frame, out string? error)
    {
        frame = null;
        error = null;

        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;

        try
        {
            var device = SharedDevice.Value;
            var interop = GetItemInterop();
            var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            var hr = interop.CreateForWindow(hwnd, ref itemIid, out var itemPtr);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            var size = item.Size;
            if (size.Width <= 0 || size.Height <= 0)
            {
                throw new InvalidOperationException("Capture item has an empty size.");
            }

            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device.Device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);

            var completion = new TaskCompletionSource<CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
            {
                try
                {
                    using var frame = sender.TryGetNextFrame();
                    if (frame is null)
                    {
                        return;
                    }

                    using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();

                    var frameWidth = bitmap.PixelWidth;
                    var frameHeight = bitmap.PixelHeight;
                    var stride = frameWidth * 4;
                    var bgra = new byte[stride * frameHeight];

                    using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
                    using (var reference = buffer.CreateReference())
                    {
                        var access = reference.As<IMemoryBufferByteAccess>();
                        var getBufferResult = access.GetBuffer(out var pointer, out _);
                        if (getBufferResult < 0)
                        {
                            Marshal.ThrowExceptionForHR(getBufferResult);
                        }

                        var plane = buffer.GetPlaneDescription(0);
                        for (var y = 0; y < frameHeight; y++)
                        {
                            Marshal.Copy(
                                IntPtr.Add(pointer, plane.StartIndex + y * plane.Stride),
                                bgra,
                                y * stride,
                                stride);
                        }
                    }

                    var blank = FrameAnalysis.IsBlank(bgra, frameWidth, frameHeight);
                    var hash = FrameAnalysis.Hash(bgra);
                    completion.TrySetResult(new CaptureFrame(
                        bgra,
                        frameWidth,
                        frameHeight,
                        "wgc",
                        blank,
                        hash,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }

            pool.FrameArrived += OnFrameArrived;
            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            session.StartCapture();

            if (!completion.Task.Wait(timeoutMs))
            {
                throw new TimeoutException($"No WGC frame arrived within {timeoutMs} ms.");
            }

            frame = completion.Task.GetAwaiter().GetResult();
            pool.FrameArrived -= OnFrameArrived;
            return true;
        }
        catch (Exception ex)
        {
            error = ex is AggregateException aggregate
                ? aggregate.GetBaseException().ToString()
                : ex.ToString();
            frame = null;
            return false;
        }
        finally
        {
            session?.Dispose();
            pool?.Dispose();
        }
    }

    private static IGraphicsCaptureItemInterop GetItemInterop()
    {
        var iid = typeof(IGraphicsCaptureItemInterop).GUID;
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var createHr = WindowsCreateString(className, className.Length, out var hstring);
        if (createHr < 0)
        {
            Marshal.ThrowExceptionForHR(createHr);
        }

        try
        {
            var hr = RoGetActivationFactory(hstring, ref iid, out var factory);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            try
            {
                return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static GraphicsDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            1, // D3D_DRIVER_TYPE_HARDWARE
            IntPtr.Zero,
            0x20, // D3D11_CREATE_DEVICE_BGRA_SUPPORT
            IntPtr.Zero,
            0,
            7,
            out var d3d11Device,
            out _,
            out var context);

        if (hr < 0)
        {
            hr = D3D11CreateDevice(
                IntPtr.Zero,
                5, // D3D_DRIVER_TYPE_WARP
                IntPtr.Zero,
                0x20,
                IntPtr.Zero,
                0,
                7,
                out d3d11Device,
                out _,
                out context);
        }

        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        var dxgiIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        hr = Marshal.QueryInterface(d3d11Device, ref dxgiIid, out var dxgiDevice);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        var winrtDevice = MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
        return new GraphicsDevice(winrtDevice, d3d11Device, context, dxgiDevice);
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        [PreserveSig]
        int GetBuffer(out IntPtr buffer, out uint capacity);
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelsCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    private sealed record GraphicsDevice(
        IDirect3DDevice Device,
        IntPtr D3D11Device,
        IntPtr Context,
        IntPtr DxgiDevice);
}
