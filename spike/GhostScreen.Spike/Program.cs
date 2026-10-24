using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using FixtureApp;
using GhostScreen.Capture;
using GhostScreen.Core;
using GhostScreen.Input;
using GhostScreen.Spike;
using GhostScreen.Web;
using GhostScreen.Windows;

var dpiAware = DpiAwareness.EnablePerMonitorV2();
var visibleStart = args.Contains("--visible");
var forceRepaint = !args.Contains("--no-repaint");
var sliver = args.Contains("--sliver");

var outputDirectory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "spike", "out"));
Directory.CreateDirectory(outputDirectory);

if (args.Contains("--cleanup"))
{
    CleanupOffscreenNotepad();
    return;
}

if (args.Contains("--edge"))
{
    await RunEdgeProbe(outputDirectory);
    return;
}

if (args.Contains("--wgc-visible"))
{
    RunVisibleWgcProbe(outputDirectory);
    return;
}

if (args.Contains("--modern"))
{
    RunModernProbe(outputDirectory);
    return;
}

if (args.Contains("--quiet"))
{
    RunQuietInvariants(outputDirectory);
    return;
}

if (args.Contains("--notepad"))
{
    RunNotepadProbe(outputDirectory);
    return;
}

var display = DisplayInfo.Get();
Console.WriteLine($"DPI aware={dpiAware}; visibleStart={visibleStart}; forceRepaint={forceRepaint}; sliver={sliver}");
Console.WriteLine($"Display: virtual={display.VirtualScreen} primary={display.PrimaryMonitor}");

var before = SystemState.Capture();
Console.WriteLine($"State before: {before}");

// Start off-screen by default; --visible starts at the original position once to prove the marker probe.
var initialParkPoint = display.GetParkPosition(new Rect(120, 120, 680, 420));
var startPoint = (visibleStart || sliver)
    ? new Point(120, 120)
    : new Point(initialParkPoint.X, initialParkPoint.Y);
using var fixture = FixtureHost.Start(startPoint.X, startPoint.Y);
Thread.Sleep(500);

var visibleInfo = WindowEnumerator.Describe(fixture.Handle)
    ?? throw new InvalidOperationException("Fixture window not found.");
Console.WriteLine($"Fixture: {visibleInfo}");

using var parked = ParkingManager.Park(fixture.Handle);
if (forceRepaint)
{
    WindowRepaint.Force(fixture.Handle);
}

Thread.Sleep(500);

var parkedInfo = WindowEnumerator.Describe(fixture.Handle)
    ?? throw new InvalidOperationException("Parked window not found.");

if (sliver)
{
    ParkingManager.MoveTo(
        fixture.Handle,
        new Rect(display.VirtualScreen.Right - 1, 120, parkedInfo.Bounds.Width, parkedInfo.Bounds.Height));
    Thread.Sleep(500);
    parkedInfo = WindowEnumerator.Describe(fixture.Handle) ?? parkedInfo;
}

var afterPark = SystemState.Capture();
var parkedProbe = ScreenProbe.Capture(display.VirtualScreen);
var magentaWhileParked = CountMagenta(parkedProbe.Bgra);

Console.WriteLine($"Parked: {parkedInfo}");
Console.WriteLine($"Parked bounds={parked.ParkedBounds} fullyOffscreen={display.IsFullyOffscreen(parked.ParkedBounds)}");
Console.WriteLine($"State after park: {afterPark}");
Console.WriteLine($"Magenta pixels on physical screen while parked: {magentaWhileParked}");

var wgcFrames = new List<CaptureFrame>();
var wgcErrors = new List<string>();

for (var i = 0; i < 20; i++)
{
    var ok = WgcCapture.TryCapture(fixture.Handle, 3_000, out var frame, out var error);
    if (!ok || frame is null)
    {
        wgcErrors.Add(error ?? "unknown");
        Console.WriteLine($"WGC frame {i}: FAILED ({error})");
    }
    else
    {
        wgcFrames.Add(frame);
        Console.WriteLine(
            $"WGC frame {i}: {frame.Width}x{frame.Height} blank={frame.Blank} " +
            $"range={FrameAnalysis.ColorRange(frame.Bgra, frame.Width, frame.Height)} hash={frame.Hash}");
    }

    Thread.Sleep(100);
}

var uniqueHashes = wgcFrames.Select(f => f.Hash).Distinct().Count();
var magentaInWgc = wgcFrames.Count > 0 ? CountMagenta(wgcFrames[0].Bgra) : 0;
Console.WriteLine($"WGC unique hashes: {uniqueHashes}/{wgcFrames.Count}; marker pixels in WGC frame: {magentaInWgc}");

if (wgcFrames.Count > 0)
{
    var first = wgcFrames[0];
    PngWriter.SaveBgraAsPng(first.Bgra, first.Width, first.Height, Path.Combine(outputDirectory, "fixture-wgc.png"));
}

if (wgcFrames.Count > 10)
{
    var later = wgcFrames[10];
    PngWriter.SaveBgraAsPng(later.Bgra, later.Width, later.Height, Path.Combine(outputDirectory, "fixture-wgc-10.png"));
}

CaptureFrame? printWindowFrame = null;
var printWindowOk = PrintWindowCapture.TryCapture(fixture.Handle, out printWindowFrame, out var printWindowError);
if (printWindowFrame is not null)
{
    PngWriter.SaveBgraAsPng(
        printWindowFrame.Bgra,
        printWindowFrame.Width,
        printWindowFrame.Height,
        Path.Combine(outputDirectory, "fixture-printwindow.png"));
    Console.WriteLine(
        $"PrintWindow: ok={printWindowOk} {printWindowFrame.Width}x{printWindowFrame.Height} " +
        $"blank={printWindowFrame.Blank} hash={printWindowFrame.Hash} error={printWindowError}");
}
else
{
    Console.WriteLine($"PrintWindow: FAILED ({printWindowError})");
}

var printWindowHashes = new List<string>();
var printWindowTimes = new List<double>();
for (var i = 0; i < 8; i++)
{
    var stopwatch = Stopwatch.StartNew();
    if (PrintWindowCapture.TryCapture(fixture.Handle, out var sequenceFrame, out _) && sequenceFrame is not null)
    {
        stopwatch.Stop();
        printWindowTimes.Add(stopwatch.Elapsed.TotalMilliseconds);
        printWindowHashes.Add(sequenceFrame.Hash);
        if (i == 0)
        {
            PngWriter.SaveBgraAsPng(
                sequenceFrame.Bgra,
                sequenceFrame.Width,
                sequenceFrame.Height,
                Path.Combine(outputDirectory, "fixture-printwindow-0.png"));
        }
        else if (i == 7)
        {
            PngWriter.SaveBgraAsPng(
                sequenceFrame.Bgra,
                sequenceFrame.Width,
                sequenceFrame.Height,
                Path.Combine(outputDirectory, "fixture-printwindow-7.png"));
        }
    }

    Thread.Sleep(150);
}

var sortedPrintWindowTimes = printWindowTimes.OrderBy(value => value).ToArray();
var printWindowP50 = sortedPrintWindowTimes.Length == 0
    ? 0
    : sortedPrintWindowTimes[(int)(sortedPrintWindowTimes.Length * 0.5)];
var printWindowP95 = sortedPrintWindowTimes.Length == 0
    ? 0
    : sortedPrintWindowTimes[Math.Min(sortedPrintWindowTimes.Length - 1, (int)(sortedPrintWindowTimes.Length * 0.95))];

Console.WriteLine(
    $"PrintWindow unique hashes over 8 pulls: {printWindowHashes.Distinct().Count()}/{printWindowHashes.Count}; " +
    $"p50={printWindowP50:F1}ms p95={printWindowP95:F1}ms");

var stateBeforeUia = SystemState.Capture();
Console.WriteLine("Fixture children:");
foreach (var child in WindowEnumerator.Children(fixture.Handle))
{
    Console.WriteLine($"  {child.HexHandle} class={child.ClassName} title=\"{child.Title}\"");
}

var uiaElements = UiaDriver.Snapshot(fixture.Handle, 80);
Console.WriteLine($"UIA elements: {uiaElements.Count}");
foreach (var element in uiaElements.Take(40))
{
    Console.WriteLine($"  {element}");
}

var uiaButtonInvoked = false;
var uiaButtonError = string.Empty;
try
{
    uiaButtonInvoked = UiaDriver.Invoke(fixture.Handle, "Click Me");
}
catch (Exception ex)
{
    uiaButtonError = ex.Message;
}

Thread.Sleep(200);
var statusAfterUia = fixture.Status;

var uiaValueSet = false;
var uiaValueError = string.Empty;
try
{
    uiaValueSet = UiaDriver.SetValue(fixture.Handle, "txtInput", "uia 世界");
}
catch (Exception ex)
{
    uiaValueError = ex.Message;
    try
    {
        var inputElement = UiaDriver.FindByName(fixture.Handle, "Input");
        if (inputElement is not null)
        {
            uiaValueSet = UiaDriver.SetValue(fixture.Handle, inputElement.AutomationId, "uia 世界");
        }
    }
    catch (Exception fallbackEx)
    {
        uiaValueError += $" | fallback: {fallbackEx.Message}";
    }
}

Thread.Sleep(200);
var inputAfterUia = fixture.InputText;

var messageClick = MessageInput.ClickButton(fixture.Handle, "Click Me");
Thread.Sleep(150);
var statusAfterMessage = fixture.Status;
var messageSetText = MessageInput.SetEditText(fixture.Handle, "Edit", "msg 123");
Thread.Sleep(150);
var inputAfterMessage = fixture.InputText;

var foregroundRestoreAfterActions = NativeMethods.SetForegroundWindow(stateBeforeUia.ForegroundWindow);
Thread.Sleep(200);

var stateAfterUia = SystemState.Capture();
var afterInputFrame = PrintWindowCapture.Capture(fixture.Handle);
PngWriter.SaveBgraAsPng(
    afterInputFrame.Bgra,
    afterInputFrame.Width,
    afterInputFrame.Height,
    Path.Combine(outputDirectory, "fixture-after-input.png"));

Console.WriteLine($"UIA invoke: ok={uiaButtonInvoked} error={uiaButtonError} status=\"{statusAfterUia}\"");
Console.WriteLine($"UIA set value: ok={uiaValueSet} error={uiaValueError} input=\"{inputAfterUia}\"");
Console.WriteLine($"Message input: click={messageClick} status=\"{statusAfterMessage}\" setText={messageSetText} input=\"{inputAfterMessage}\"");
Console.WriteLine($"State after UIA: {stateAfterUia} (restoreRequested={foregroundRestoreAfterActions})");

fixture.Close();
parked.Dispose();
Thread.Sleep(300);

var afterClose = SystemState.Capture();
var humanInputDetected = before.LastInputTick != afterPark.LastInputTick ||
                         before.LastInputTick != afterClose.LastInputTick;
Console.WriteLine($"State after close: {afterClose}");
Console.WriteLine($"Human input during run: {humanInputDetected}");

var summary = new
{
    dpiAware,
    visibleStart,
    forceRepaint,
    sliver,
    display = new { virtualScreen = display.VirtualScreen.ToString(), primary = display.PrimaryMonitor.ToString() },
    stateBefore = before.ToString(),
    stateAfterPark = afterPark.ToString(),
    stateAfterClose = afterClose.ToString(),
    foregroundUnchanged = before.ForegroundWindow == afterPark.ForegroundWindow &&
                          before.ForegroundWindow == afterClose.ForegroundWindow,
    humanInputDetected,
    lastInputTicks = new
    {
        before = before.LastInputTick,
        afterPark = afterPark.LastInputTick,
        afterClose = afterClose.LastInputTick,
    },
    cursorUnchanged = before.CursorX == afterPark.CursorX && before.CursorY == afterPark.CursorY &&
                      before.CursorX == afterClose.CursorX && before.CursorY == afterClose.CursorY,
    clipboardUnchanged = before.ClipboardSequence == afterPark.ClipboardSequence &&
                         before.ClipboardSequence == afterClose.ClipboardSequence,
    parked = new
    {
        bounds = parked.ParkedBounds.ToString(),
        fullyOffscreen = display.IsFullyOffscreen(parked.ParkedBounds),
        visible = parkedInfo.Visible,
        minimized = parkedInfo.Minimized,
        cloaked = parkedInfo.Cloaked,
    },
    marker = new
    {
        onPhysicalScreenWhileParked = magentaWhileParked,
        inWgcFrame = magentaInWgc,
    },
    wgc = new
    {
        frames = wgcFrames.Count,
        uniqueHashes,
        blankFrames = wgcFrames.Count(f => f.Blank),
        errors = wgcErrors,
        first = wgcFrames.Count > 0 ? new { wgcFrames[0].Width, wgcFrames[0].Height, wgcFrames[0].Hash } : null,
    },
    printWindow = new
    {
        ok = printWindowOk,
        error = printWindowError,
        blank = printWindowFrame?.Blank,
        hash = printWindowFrame?.Hash,
        uniqueHashesOver8Pulls = printWindowHashes.Distinct().Count(),
        p50Ms = printWindowP50,
        p95Ms = printWindowP95,
    },
    uia = new
    {
        count = uiaElements.Count,
        buttonInvoked = uiaButtonInvoked,
        buttonError = uiaButtonError,
        statusAfterUia,
        valueSet = uiaValueSet,
        valueError = uiaValueError,
        inputAfterUia,
        foregroundUnchanged = stateBeforeUia.ForegroundWindow == stateAfterUia.ForegroundWindow,
        restoreRequested = foregroundRestoreAfterActions,
    },
    messageInput = new
    {
        click = messageClick,
        statusAfterMessage,
        setText = messageSetText,
        inputAfterMessage,
    },
};

var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(outputDirectory, "summary.json"), json);
Console.WriteLine();
Console.WriteLine(json);

static int CountMagenta(byte[] bgra)
{
    var count = 0;
    for (var i = 0; i + 2 < bgra.Length; i += 4)
    {
        if (bgra[i + 2] > 240 && bgra[i] > 240 && bgra[i + 1] < 15)
        {
            count++;
        }
    }

    return count;
}

static async Task RunEdgeProbe(string outputDirectory)
{
    var pagePath = Path.GetFullPath(Path.Combine(outputDirectory, "..", "testpage", "index.html"));
    Console.WriteLine($"Edge probe: page={pagePath}");

    var process = TestLaunch.StartHeadlessEdge();
    if (process is null)
    {
        Console.WriteLine("Failed to start headless Edge.");
        return;
    }

    try
    {
        await using var cdp = await EdgeCdpClient.ConnectAsync(9222);
        await cdp.NavigateAsync(new Uri(pagePath).AbsoluteUri);

        var title = await cdp.EvaluateAsync<string>("document.title");
        var initialStatus = await cdp.EvaluateAsync<string>("document.getElementById('status').textContent");
        var initialFrame = await cdp.EvaluateAsync<string>("document.getElementById('box').textContent");
        var elements = await cdp.GetInteractiveElementsAsync();

        Console.WriteLine($"Web title=\"{title}\" status=\"{initialStatus}\" frame=\"{initialFrame}\" elements={elements.Count}");
        foreach (var element in elements)
        {
            Console.WriteLine($"  {element.Selector} <{element.Tag}> text=\"{element.Text}\" at ({element.X},{element.Y}) {element.Width}x{element.Height}");
        }

        await cdp.CaptureScreenshotAsync(Path.Combine(outputDirectory, "web-before.png"));

        var (x, y) = await cdp.GetElementCenterAsync("#btn");
        await cdp.ClickAsync(x, y);
        await Task.Delay(400);

        var statusAfterClick = await cdp.EvaluateAsync<string>("document.getElementById('status').textContent");
        var frameAfterClick = await cdp.EvaluateAsync<string>("document.getElementById('box').textContent");
        await cdp.CaptureScreenshotAsync(Path.Combine(outputDirectory, "web-after.png"));

        var console = string.Join(" | ", cdp.Console.Select(entry => $"{entry.Level}:{entry.Text}"));
        Console.WriteLine($"Web after click: status=\"{statusAfterClick}\" frame=\"{frameAfterClick}\" console=\"{console}\"");
        Console.WriteLine($"Web click point: ({x:F1},{y:F1})");

        await cdp.SetViewportAsync(390, 844, 3, true);
        await cdp.CaptureScreenshotAsync(Path.Combine(outputDirectory, "web-mobile.png"));
        var mobile = await cdp.EvaluateAsync<string>(
            "'viewport=' + innerWidth + 'x' + innerHeight + ' dpr=' + devicePixelRatio");
        Console.WriteLine($"Web mobile: {mobile}");
    }
    finally
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch
        {
            // Best effort.
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var profile = Path.Combine(outputDirectory, "edge-profile");
                if (Directory.Exists(profile))
                {
                    Directory.Delete(profile, recursive: true);
                }

                break;
            }
            catch
            {
                await Task.Delay(300);
            }
        }
    }
}

static void RunQuietInvariants(string outputDirectory)
{
    var display = DisplayInfo.Get();
    var parkPoint = display.GetParkPosition(new Rect(120, 120, 680, 420));
    var quiet = WaitForIdle(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3));
    Console.WriteLine($"Quiet window acquired: {quiet}");

    var before = SystemState.Capture();
    var result = new Dictionary<string, object?>();

    using var fixture = FixtureHost.Start(parkPoint.X, parkPoint.Y);
    Thread.Sleep(300);
    using var parked = ParkingManager.Park(fixture.Handle);
    WindowRepaint.Force(fixture.Handle);
    Thread.Sleep(300);

    var uiaOk = false;
    try
    {
        uiaOk = UiaDriver.Invoke(fixture.Handle, "Click Me") &&
                UiaDriver.SetValue(fixture.Handle, "txtInput", "quiet 世界");
    }
    catch (Exception ex)
    {
        result["uiaError"] = ex.Message;
    }

    Thread.Sleep(200);
    WindowRepaint.Force(fixture.Handle);

    var hashes = new List<string>();
    for (var i = 0; i < 3; i++)
    {
        if (PrintWindowCapture.TryCaptureWithTimeout(fixture.Handle, 5_000, out var frame, out _) &&
            frame is not null)
        {
            hashes.Add(frame.Hash);
        }

        Thread.Sleep(120);
    }

    fixture.Close();
    parked.Dispose();
    Thread.Sleep(300);

    var after = SystemState.Capture();
    var humanInput = before.LastInputTick != after.LastInputTick;
    var foregroundOk = before.ForegroundWindow == after.ForegroundWindow;
    var cursorOk = before.CursorX == after.CursorX && before.CursorY == after.CursorY;
    var clipboardOk = before.ClipboardSequence == after.ClipboardSequence;
    var pass = quiet && !humanInput && foregroundOk && cursorOk && clipboardOk;

    result["quiet"] = quiet;
    result["humanInput"] = humanInput;
    result["foregroundOk"] = foregroundOk;
    result["cursorOk"] = cursorOk;
    result["clipboardOk"] = clipboardOk;
    result["pass"] = pass;
    result["uiaOk"] = uiaOk;
    result["printWindowHashes"] = hashes;
    result["before"] = before.ToString();
    result["after"] = after.ToString();

    var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(outputDirectory, "quiet.json"), json);
    Console.WriteLine(json);

    Environment.ExitCode = pass ? 0 : 2;
}

static bool WaitForIdle(TimeSpan timeout, TimeSpan quietFor)
{
    var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
    var lastTick = SystemState.Capture().LastInputTick;
    var quietSince = Environment.TickCount64;

    while (Environment.TickCount64 < deadline)
    {
        Thread.Sleep(100);
        var current = SystemState.Capture().LastInputTick;
        if (current != lastTick)
        {
            lastTick = current;
            quietSince = Environment.TickCount64;
            continue;
        }

        if (Environment.TickCount64 - quietSince >= (long)quietFor.TotalMilliseconds)
        {
            return true;
        }
    }

    return false;
}

static void RunModernProbe(string outputDirectory)
{
    var specs = new[]
    {
        new ModernAppSpec(
            "EdgeGui",
            TestLaunch.StartEdgeGui,
            ["msedge"],
            ["GhostScreen Spike", "新建标签页", "New Tab", "about:blank", "Microsoft Edge", "Bing"],
            Path.Combine(outputDirectory, "edge-gui-profile")),
        new ModernAppSpec(
            "VsCode",
            TestLaunch.StartVsCode,
            ["Code"],
            ["Visual Studio Code", "Welcome", "欢迎"],
            Path.Combine(outputDirectory, "vscode-profile")),
        new ModernAppSpec(
            "Calculator",
            TestLaunch.StartCalculator,
            ["Calculator", "ApplicationFrameHost"],
            ["计算器", "Calculator"],
            null),
    };

    var results = new List<ModernAppResult>();

    foreach (var spec in specs)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {spec.Name} ===");
        var result = ProbeModernApp(spec, outputDirectory);
        results.Add(result);
        Console.WriteLine(
            $"{spec.Name}: launched={result.Launched} window={result.Window} " +
            $"parked={result.Parked} printWindow={result.PrintWindowOk} blank={result.PrintWindowBlank} " +
            $"uia={result.UiaElements} foregroundUnchanged={result.ForegroundUnchanged} " +
            $"closed={result.Closed} kill={result.ForcedKill}");
    }

    var json = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(outputDirectory, "modern.json"), json);
    Console.WriteLine();
    Console.WriteLine(json);
}

static ModernAppResult ProbeModernApp(ModernAppSpec spec, string outputDirectory)
{
    var beforeHandles = WindowEnumerator.List(includeInvisible: true, includeOwnProcess: true)
        .Select(window => window.Handle)
        .ToHashSet();
    var before = SystemState.Capture();
    var result = new ModernAppResult { App = spec.Name };
    var beforeForeground = WindowEnumerator.Describe(before.ForegroundWindow);
    Console.WriteLine($"Before foreground: {beforeForeground}");

    var process = spec.Launch();
    if (process is null)
    {
        result.LaunchError = "CreateProcess returned null";
        return result;
    }

    result.Launched = true;
    WindowInfo? window = null;

    try
    {
        window = WaitForNewWindow(spec, beforeHandles, 20_000);
        if (window is null)
        {
            result.LaunchError = "No matching top-level window";
            return result;
        }

        result.Window = window.ToString();
        result.Bounds = window.Bounds.ToString();
        result.ProcessName = window.ProcessName;

        using var parked = ParkingManager.Park(window.Handle);
        WindowRepaint.Force(window.Handle);
        Thread.Sleep(300);
        WindowRepaint.Force(window.Handle);
        Thread.Sleep(300);

        result.Parked = true;
        result.ParkedBounds = parked.ParkedBounds.ToString();
        result.UiaElements = TryGetUiaCount(window.Handle, 3_000);

        var printWindowOk = PrintWindowCapture.TryCaptureWithTimeout(
            window.Handle,
            6_000,
            out var printWindowFrame,
            out var printWindowError);

        result.PrintWindowOk = printWindowOk;
        result.PrintWindowError = printWindowError;

        if (printWindowFrame is not null)
        {
            result.PrintWindowBlank = printWindowFrame.Blank;
            result.PrintWindowSize = $"{printWindowFrame.Width}x{printWindowFrame.Height}";
            result.PrintWindowHash = printWindowFrame.Hash;
            result.PrintWindowRange = FrameAnalysis.ColorRange(
                printWindowFrame.Bgra,
                printWindowFrame.Width,
                printWindowFrame.Height);
            PngWriter.SaveBgraAsPng(
                printWindowFrame.Bgra,
                printWindowFrame.Width,
                printWindowFrame.Height,
                Path.Combine(outputDirectory, $"modern-{spec.Name}-printwindow.png"));
        }

        var wgcOk = WgcCapture.TryCapture(window.Handle, 4_000, out var wgcFrame, out var wgcError);
        if (wgcOk && wgcFrame is not null)
        {
            result.WgcOk = true;
            result.WgcBlank = wgcFrame.Blank;
            result.WgcHash = wgcFrame.Hash;
            result.WgcRange = FrameAnalysis.ColorRange(wgcFrame.Bgra, wgcFrame.Width, wgcFrame.Height);
            PngWriter.SaveBgraAsPng(
                wgcFrame.Bgra,
                wgcFrame.Width,
                wgcFrame.Height,
                Path.Combine(outputDirectory, $"modern-{spec.Name}-wgc.png"));
        }
        else
        {
            result.WgcError = wgcError;
        }

        var foregroundRestoreRequested = false;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (NativeMethods.GetForegroundWindow() == before.ForegroundWindow)
            {
                foregroundRestoreRequested = true;
                break;
            }

            if (FocusGuard.TryRestoreForeground(before.ForegroundWindow))
            {
                foregroundRestoreRequested = true;
            }

            Thread.Sleep(120);
        }

        result.ForegroundRestoreRequested = foregroundRestoreRequested;
        Thread.Sleep(300);

        var after = SystemState.Capture();
        result.ForegroundUnchanged = before.ForegroundWindow == after.ForegroundWindow;
        result.ClipboardUnchanged = before.ClipboardSequence == after.ClipboardSequence;
        result.HumanInputDetected = before.LastInputTick != after.LastInputTick;

        if (spec.Name == "VsCode")
        {
            try
            {
                var cdp = EdgeCdpClient.ConnectAsync(9223).GetAwaiter().GetResult();
                try
                {
                    result.CdpTitle = cdp.EvaluateAsync<string>("document.title").GetAwaiter().GetResult();
                    cdp.CaptureScreenshotAsync(Path.Combine(outputDirectory, "modern-VsCode-cdp.png"))
                        .GetAwaiter().GetResult();
                    result.CdpOk = true;
                }
                finally
                {
                    cdp.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                result.CdpError = ex.Message;
            }
        }

        NativeMethods.PostMessage(window.Handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(1_500);

        if (NativeMethods.IsWindow(window.Handle))
        {
            if (spec.Name != "Calculator")
            {
                try
                {
                    var owner = Process.GetProcessById((int)window.ProcessId);
                    owner.Kill(entireProcessTree: true);
                    owner.WaitForExit(5_000);
                    result.ForcedKill = true;
                }
                catch (Exception ex)
                {
                    result.CloseError = ex.Message;
                }
            }
            else
            {
                result.CloseError = "Calculator window did not close; not killing ApplicationFrameHost";
            }
        }

        result.Closed = !NativeMethods.IsWindow(window.Handle);
    }
    catch (Exception ex)
    {
        result.Error = ex.ToString();
    }
    finally
    {
        try
        {
            if (process is { HasExited: false } && spec.Name != "Calculator")
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3_000);
                result.ForcedKill = true;
            }
        }
        catch
        {
            // Best effort.
        }

        if (spec.ProfilePath is not null)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(spec.ProfilePath))
                    {
                        Directory.Delete(spec.ProfilePath, recursive: true);
                    }

                    break;
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
        }
    }

    return result;
}

static WindowInfo? WaitForNewWindow(ModernAppSpec spec, HashSet<IntPtr> existing, int timeoutMs)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        var candidate = WindowEnumerator.List(includeInvisible: true, includeOwnProcess: true)
            .Where(window => !existing.Contains(window.Handle))
            .Where(window => window.Bounds.Width > 100 && window.Bounds.Height > 100)
            .Where(window => spec.ProcessNames.Any(name =>
                window.ProcessName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Where(window => spec.Titles.Any(title =>
                window.Title.Contains(title, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(window => (long)window.Bounds.Width * window.Bounds.Height)
            .FirstOrDefault();

        if (candidate is not null)
        {
            return candidate;
        }

        Thread.Sleep(200);
    }

    return null;
}

static int TryGetUiaCount(IntPtr hwnd, int timeoutMs)
{
    try
    {
        var task = Task.Run(() => UiaDriver.Snapshot(hwnd, 50).Count);
        return task.Wait(timeoutMs) ? task.Result : -1;
    }
    catch
    {
        return -1;
    }
}

static void RunVisibleWgcProbe(string outputDirectory)
{
    var before = SystemState.Capture();
    using var fixture = FixtureHost.Start(120, 120);
    Thread.Sleep(600);

    var info = WindowEnumerator.Describe(fixture.Handle);
    Console.WriteLine($"Visible fixture: {info}");
    Console.WriteLine($"State before visible WGC: {before}");

    var hashes = new List<string>();
    for (var i = 0; i < 10; i++)
    {
        var ok = WgcCapture.TryCapture(fixture.Handle, 3_000, out var frame, out var error);
        if (ok && frame is not null)
        {
            hashes.Add(frame.Hash);
            Console.WriteLine(
                $"Visible WGC {i}: {frame.Width}x{frame.Height} blank={frame.Blank} " +
                $"range={FrameAnalysis.ColorRange(frame.Bgra, frame.Width, frame.Height)} hash={frame.Hash}");
            if (i == 0)
            {
                PngWriter.SaveBgraAsPng(frame.Bgra, frame.Width, frame.Height, Path.Combine(outputDirectory, "fixture-wgc-visible.png"));
            }
            else if (i == 9)
            {
                PngWriter.SaveBgraAsPng(frame.Bgra, frame.Width, frame.Height, Path.Combine(outputDirectory, "fixture-wgc-visible-9.png"));
            }
        }
        else
        {
            Console.WriteLine($"Visible WGC {i}: FAILED ({error})");
        }

        Thread.Sleep(100);
    }

    var after = SystemState.Capture();
    Console.WriteLine($"Visible WGC unique hashes: {hashes.Distinct().Count()}/{hashes.Count}");
    Console.WriteLine($"State after visible WGC: {after}");
    Console.WriteLine(
        $"foregroundUnchanged={before.ForegroundWindow == after.ForegroundWindow} " +
        $"humanInput={before.LastInputTick != after.LastInputTick}");

    fixture.Close();
}

static void CleanupOffscreenNotepad()
{
    var display = DisplayInfo.Get();
    var windows = WindowEnumerator.List(includeInvisible: true)
        .Where(w => w.ProcessName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    Console.WriteLine($"Notepad windows: {windows.Length}");
    foreach (var window in windows)
    {
        var offscreen = display.IsFullyOffscreen(window.Bounds);
        var testWindow = window.Title.Contains("hello 世界 123", StringComparison.Ordinal) ||
                         (!window.Visible &&
                          window.Title.Equals("无标题 - Notepad", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(
            $"  {window.HexHandle} title=\"{window.Title}\" bounds={window.Bounds} " +
            $"visible={window.Visible} offscreen={offscreen} testWindow={testWindow}");
        if (offscreen || testWindow)
        {
            NativeMethods.PostMessage(window.Handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    Thread.Sleep(500);
}

static void RunNotepadProbe(string outputDirectory)
{
    var stateBefore = SystemState.Capture();
    var existing = WindowEnumerator.List(includeInvisible: true)
        .Where(w => w.ProcessName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
        .Select(w => w.Handle)
        .ToHashSet();

    Console.WriteLine($"Notepad probe. State before: {stateBefore}");
    Console.WriteLine($"Existing notepad windows: {existing.Count}");

    var process = TestLaunch.StartNotepad();
    if (process is null)
    {
        Console.WriteLine("Failed to start notepad.exe.");
        return;
    }

    try
    {
        WindowInfo? window = null;
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            window = WindowEnumerator.List(includeInvisible: true)
                .Where(w => w.ProcessName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
                .Where(w => !existing.Contains(w.Handle))
                .OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height)
                .FirstOrDefault();

            if (window is not null)
            {
                break;
            }

            Thread.Sleep(100);
        }

        if (window is null)
        {
            Console.WriteLine("No new Notepad window found.");
            return;
        }

        Console.WriteLine($"Notepad window: {window}");
        using var parked = ParkingManager.Park(window.Handle);
        WindowRepaint.Force(window.Handle);
        Thread.Sleep(300);
        WindowRepaint.Force(window.Handle);
        Thread.Sleep(200);

        var parkedInfo = WindowEnumerator.Describe(window.Handle);
        Console.WriteLine($"Notepad parked: {parkedInfo}");

        var foregroundRestoreRequested = NativeMethods.SetForegroundWindow(stateBefore.ForegroundWindow);
        Thread.Sleep(200);

        var afterPark = SystemState.Capture();
        var printWindow = PrintWindowCapture.Capture(window.Handle);
        PngWriter.SaveBgraAsPng(
            printWindow.Bgra,
            printWindow.Width,
            printWindow.Height,
            Path.Combine(outputDirectory, "notepad-printwindow.png"));
        Console.WriteLine(
            $"Notepad PrintWindow: {printWindow.Width}x{printWindow.Height} blank={printWindow.Blank} " +
            $"range={FrameAnalysis.ColorRange(printWindow.Bgra, printWindow.Width, printWindow.Height)} hash={printWindow.Hash}");

        var wgcOk = WgcCapture.TryCapture(window.Handle, 3_000, out var wgcFrame, out var wgcError);
        if (wgcOk && wgcFrame is not null)
        {
            PngWriter.SaveBgraAsPng(
                wgcFrame.Bgra,
                wgcFrame.Width,
                wgcFrame.Height,
                Path.Combine(outputDirectory, "notepad-wgc.png"));
            Console.WriteLine(
                $"Notepad WGC: {wgcFrame.Width}x{wgcFrame.Height} blank={wgcFrame.Blank} " +
                $"range={FrameAnalysis.ColorRange(wgcFrame.Bgra, wgcFrame.Width, wgcFrame.Height)} hash={wgcFrame.Hash}");
        }
        else
        {
            Console.WriteLine($"Notepad WGC failed: {wgcError}");
        }

        Console.WriteLine("Children:");
        foreach (var child in WindowEnumerator.Children(window.Handle))
        {
            Console.WriteLine($"  {child.HexHandle} class={child.ClassName} title=\"{child.Title}\" bounds={child.Bounds}");
        }

        var edit = WindowEnumerator.Children(window.Handle)
            .FirstOrDefault(c => c.ClassName is "Edit" or "RichEditD2DPT" or "RichEdit20W" or "RichEdit50W");
        if (edit is not null)
        {
            NativeMethods.SendMessage(edit.Handle, NativeMethods.WM_SETTEXT, IntPtr.Zero, "hello 世界 123");
            Thread.Sleep(300);
            WindowRepaint.Force(window.Handle);
            var afterText = PrintWindowCapture.Capture(window.Handle);
            PngWriter.SaveBgraAsPng(
                afterText.Bgra,
                afterText.Width,
                afterText.Height,
                Path.Combine(outputDirectory, "notepad-typed.png"));
            Console.WriteLine($"Notepad after WM_SETTEXT: hash={afterText.Hash} (changed={afterText.Hash != printWindow.Hash})");
        }
        else
        {
            Console.WriteLine("No edit child found; WM_SETTEXT skipped.");
        }

        var after = SystemState.Capture();
        Console.WriteLine($"Notepad state after: {after}");
        Console.WriteLine(
            $"foregroundRestoreRequested={foregroundRestoreRequested} " +
            $"foregroundUnchanged={stateBefore.ForegroundWindow == after.ForegroundWindow} " +
            $"clipboardUnchanged={stateBefore.ClipboardSequence == after.ClipboardSequence}");

        NativeMethods.PostMessage(window.Handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(500);
    }
    finally
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}

internal sealed record ModernAppSpec(
    string Name,
    Func<Process?> Launch,
    string[] ProcessNames,
    string[] Titles,
    string? ProfilePath);

internal sealed class ModernAppResult
{
    public required string App { get; init; }

    public bool Launched { get; set; }

    public string? LaunchError { get; set; }

    public string? Window { get; set; }

    public string? Bounds { get; set; }

    public string? ProcessName { get; set; }

    public bool Parked { get; set; }

    public string? ParkedBounds { get; set; }

    public int UiaElements { get; set; }

    public bool PrintWindowOk { get; set; }

    public bool PrintWindowBlank { get; set; }

    public string? PrintWindowError { get; set; }

    public string? PrintWindowSize { get; set; }

    public string? PrintWindowHash { get; set; }

    public int PrintWindowRange { get; set; }

    public bool WgcOk { get; set; }

    public bool WgcBlank { get; set; }

    public string? WgcHash { get; set; }

    public int WgcRange { get; set; }

    public string? WgcError { get; set; }

    public bool ForegroundUnchanged { get; set; }

    public bool ForegroundRestoreRequested { get; set; }

    public bool ClipboardUnchanged { get; set; }

    public bool HumanInputDetected { get; set; }

    public bool CdpOk { get; set; }

    public string? CdpTitle { get; set; }

    public string? CdpError { get; set; }

    public bool Closed { get; set; }

    public bool ForcedKill { get; set; }

    public string? CloseError { get; set; }

    public string? Error { get; set; }
}
