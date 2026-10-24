using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FixtureApp;
using GhostScreen.Capture;
using GhostScreen.Core;
using GhostScreen.Daemon;
using GhostScreen.Input;
using GhostScreen.Tests;
using GhostScreen.Web;
using GhostScreen.Windows;

DpiAwareness.EnablePerMonitorV2();

var tests = new (string Name, Action Body)[]
{
    ("Rect.IntersectsWith", () =>
    {
        Assert(new Rect(0, 0, 10, 10).IntersectsWith(new Rect(5, 5, 10, 10)), "overlap should intersect");
        Assert(!new Rect(0, 0, 10, 10).IntersectsWith(new Rect(10, 0, 5, 5)), "touching edges should not intersect");
        Assert(!new Rect(0, 0, 10, 10).IntersectsWith(new Rect(20, 20, 5, 5)), "far apart should not intersect");
    }),
    ("Rect.Contains", () =>
    {
        Assert(new Rect(0, 0, 10, 10).Contains(0, 0), "top-left is inside");
        Assert(new Rect(0, 0, 10, 10).Contains(9, 9), "bottom-right-1 is inside");
        Assert(!new Rect(0, 0, 10, 10).Contains(10, 10), "right/bottom edge is outside");
    }),
    ("DisplayInfo.IsFullyOffscreen", () =>
    {
        var display = new DisplayInfo(new Rect(0, 0, 2560, 1600), new Rect(0, 0, 2560, 1600));
        Assert(display.IsFullyOffscreen(new Rect(2624, 120, 702, 476)), "parked rect is off-screen");
        Assert(!display.IsFullyOffscreen(new Rect(2559, 120, 702, 476)), "one visible pixel is not off-screen");
        Assert(!display.IsFullyOffscreen(new Rect(0, 0, 100, 100)), "on-screen rect is not off-screen");
    }),
    ("DisplayInfo.GetParkPosition", () =>
    {
        var display = new DisplayInfo(new Rect(0, 0, 1920, 1080), new Rect(0, 0, 1920, 1080));
        var point = display.GetParkPosition(new Rect(100, 200, 800, 600), gap: 64);
        Assert(point.X == 1984, $"expected x=1984, got {point.X}");
        Assert(point.Y == 200, $"expected y=200, got {point.Y}");
    }),
    ("FrameAnalysis.Blank", () =>
    {
        var solid = new byte[16 * 4];
        for (var i = 0; i < solid.Length; i += 4)
        {
            solid[i] = 20;
            solid[i + 1] = 20;
            solid[i + 2] = 20;
        }

        Assert(FrameAnalysis.IsBlank(solid, 4, 4), "solid frame should be blank");

        var varied = new byte[16 * 4];
        for (var i = 0; i < 16; i++)
        {
            varied[i * 4] = (byte)(i * 15);
            varied[i * 4 + 1] = (byte)(255 - i * 10);
            varied[i * 4 + 2] = (byte)(i * 7);
        }

        Assert(!FrameAnalysis.IsBlank(varied, 4, 4), "varied frame should not be blank");
    }),
    ("FrameAnalysis.Hash is deterministic", () =>
    {
        var frame = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        Assert(FrameAnalysis.Hash(frame) == FrameAnalysis.Hash((byte[])frame.Clone()), "same bytes must hash the same");
        frame[0] = 9;
        Assert(FrameAnalysis.Hash(frame) != FrameAnalysis.Hash(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "different bytes must hash differently");
    }),
    ("WindowEnumerator.Describe rejects null", () =>
    {
        Assert(WindowEnumerator.Describe(IntPtr.Zero) is null, "null handle should have no description");
    }),
    ("InputRouter rejects Chromium message input", () =>
    {
        var chromium = new WindowInfo(
            IntPtr.Zero,
            1,
            "msedge",
            "test",
            "Chrome_WidgetWin_1",
            new Rect(0, 0, 100, 100),
            new Rect(0, 0, 100, 100),
            true,
            false,
            false,
            false,
            100,
            false);

        Assert(!InputRouter.SupportsMessageInput(chromium), "Chromium must not use message input");
        Assert(InputRouter.GetInputStrategies(chromium).SequenceEqual(["uia"]), "Chromium capabilities");
        try
        {
            InputRouter.EnsureMessageInput(chromium);
            throw new InvalidOperationException("Expected INPUT_UNSUPPORTED.");
        }
        catch (TargetException ex)
        {
            Assert(ex.Code == "INPUT_UNSUPPORTED", $"unexpected code {ex.Code}");
        }
    }),
    ("Web URL validation", () =>
    {
        Assert(
            EdgeBrowser.NormalizeUrl("https://example.com") == "https://example.com/",
            "https URL normalizes");
        Assert(
            EdgeBrowser.NormalizeUrl("file:///C:/tmp/page.html") == "file:///C:/tmp/page.html",
            "file URL passes through");

        foreach (var invalid in new[]
                 {
                     "--headless",
                     "javascript:alert(1)",
                     "data:text/html,hi",
                     "not a url",
                     " ",
                 })
        {
            try
            {
                EdgeBrowser.NormalizeUrl(invalid);
                throw new InvalidOperationException($"Expected rejection for '{invalid}'.");
            }
            catch (WebLaunchException ex)
            {
                Assert(ex.Code == "INVALID_ARGUMENTS", $"unexpected code {ex.Code} for '{invalid}'");
            }
        }
    }),
    ("Edge executable allow-list", () =>
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        Assert(
            EdgeBrowser.IsKnownEdgePath(Path.Combine(programFilesX86, "Microsoft\\Edge\\Application\\msedge.exe")),
            "standard Edge path is allowed");
        Assert(
            !EdgeBrowser.IsKnownEdgePath(Path.Combine(Path.GetTempPath(), "msedge.exe")),
            "msedge.exe outside the install roots is rejected");
        Assert(
            !EdgeBrowser.IsKnownEdgePath(Path.Combine(Environment.SystemDirectory, "cmd.exe")),
            "cmd.exe is rejected");
        Assert(!EdgeBrowser.IsKnownEdgePath("msedge.exe"), "relative paths are rejected");
    }),
    ("CdpKeys mapping", () =>
    {
        var f5 = CdpKeys.Resolve("F5");
        Assert(f5 is { Key: "F5", VirtualKey: 116 }, "F5 mapping");

        var s = CdpKeys.Resolve("s");
        Assert(s is { Key: "s", Code: "KeyS", VirtualKey: 83 }, "letter mapping");
        Assert(s!.TextFor(0) == "s", "plain letter text");
        Assert(s.TextFor(8) == "S", "shifted letter text");
        Assert(s.TextFor(2) is null, "ctrl suppresses text");
        Assert(CdpKeys.Resolve("S")!.TextFor(0) == "S", "uppercase letter text");
        Assert(CdpKeys.ModifierMask(["CTRL", "SHIFT"]) == 10, "modifier mask");
        Assert(CdpKeys.Resolve("NOPE") is null, "unknown key rejected");
    }),
    ("PngInfo reads IHDR", () =>
    {
        var png = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        png[18] = 0x01;
        png[19] = 0x2C;
        png[20] = 0x00;
        png[21] = 0x00;
        png[22] = 0x02;
        png[23] = 0x58;
        Assert(PngInfo.HasSignature(png), "PNG signature");
        var size = PngInfo.TryReadSize(png);
        Assert(size is { Width: 300, Height: 600 }, $"unexpected size {size}");
    }),
    ("DisplayInfo handles negative-origin monitor layouts", () =>
    {
        // Virtual desktop spans a left monitor at -1920 and the primary at 0.
        var display = new DisplayInfo(new Rect(-1920, -200, 3840, 1280), new Rect(0, 0, 1920, 1080));

        Assert(display.IsFullyOffscreen(new Rect(1984, 120, 800, 600)), "right of the virtual screen");
        Assert(display.IsFullyOffscreen(new Rect(-2600, 0, 100, 100)), "left of the virtual screen");
        Assert(!display.IsFullyOffscreen(new Rect(-1920, 0, 100, 100)), "left monitor is on-screen");
        Assert(!display.IsFullyOffscreen(new Rect(-100, 0, 100, 100)), "window straddling the seam is on-screen");

        var point = display.GetParkPosition(new Rect(100, 100, 800, 600));
        Assert(point.X == 1984, $"expected park x=1984, got {point.X}");
        Assert(point.Y == 100, $"expected park y=100, got {point.Y}");
    }),
    ("WGC captures live frames from a visible window", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive)
        {
            Console.WriteLine("SKIP WGC: no interactive desktop.");
            return;
        }

        if (Environment.GetEnvironmentVariable("GHOSTSCREEN_VISIBLE_TESTS") != "1")
        {
            Console.WriteLine("SKIP WGC: visible-window test; set GHOSTSCREEN_VISIBLE_TESTS=1 to run it.");
            return;
        }

        using var fixture = FixtureHost.Start(300, 220);
        Thread.Sleep(300);

        var hashes = new HashSet<string>(StringComparer.Ordinal);
        for (var frameIndex = 0; frameIndex < 8; frameIndex++)
        {
            var ok = WgcCapture.TryCapture(fixture.Handle, 3_000, out var frame, out var error);
            Assert(ok && frame is not null, $"WGC failed on frame {frameIndex}: {error}");
            Assert(!frame!.Blank, $"WGC frame {frameIndex} is blank");
            Assert(frame.Width > 100 && frame.Height > 100, $"unexpected WGC size {frame.Width}x{frame.Height}");
            hashes.Add(frame.Hash);
            Thread.Sleep(120);
        }

        Assert(hashes.Count >= 4, $"WGC frames should change with the animation, got {hashes.Count} unique");
    }),
    ("WindowEnumerator reports DPI awareness", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive)
        {
            Console.WriteLine("SKIP DPI awareness: no interactive desktop.");
            return;
        }

        using var fixture = StartFixtureOffscreen();
        Thread.Sleep(250);
        var info = WindowEnumerator.Describe(fixture.Handle);
        Assert(info is not null, "fixture window should be describable");
        Assert(
            info!.DpiAwareness == WindowDpiAwareness.PerMonitorAware,
            $"expected PerMonitorAware, got {info.DpiAwareness}");
    }),
    ("WindowRecovery restores a parked window", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive)
        {
            Console.WriteLine("SKIP WindowRecovery: no interactive desktop.");
            return;
        }

        using var fixture = StartFixtureOffscreen();
        Thread.Sleep(250);
        var original = WindowEnumerator.Describe(fixture.Handle)!.Bounds;

        using var parked = ParkingManager.Park(fixture.Handle);
        var parkedBounds = WindowEnumerator.Describe(fixture.Handle)!.Bounds;
        Assert(DisplayInfo.Get().IsFullyOffscreen(parkedBounds), "window is parked off-screen");

        var snapshot = parked.ToSnapshot(WindowEnumerator.Describe(fixture.Handle)!);
        Assert(WindowRecovery.TryRestore(snapshot, out var reason), $"restore failed: {reason}");

        var restored = WindowEnumerator.Describe(fixture.Handle)!.Bounds;
        AssertRestored(original, restored, "WindowRecovery");
    }),
    ("Journal recovers a crashed target", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive)
        {
            Console.WriteLine("SKIP journal recovery: no interactive desktop.");
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ghostscreen-journal-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDirectory);
        var journalPath = Path.Combine(tempDirectory, "targets.json");
        var profileRoot = Path.Combine(tempDirectory, "profiles");

        using var fixture = StartFixtureOffscreen();
        Thread.Sleep(250);
        var original = WindowEnumerator.Describe(fixture.Handle)!.Bounds;

        var manager1 = new TargetManager(new TargetJournal(journalPath, profileRoot));
        var manager2 = new TargetManager(new TargetJournal(journalPath, profileRoot));
        try
        {
            manager1.Attach(fixture.Handle, confirm: true);
            Assert(File.Exists(journalPath), "journal file written");

            manager2.Dispose();
            manager2 = new TargetManager(new TargetJournal(journalPath, profileRoot));
            Assert(manager2.LastRecovery.RestoredApps == 1, "one app restored");

            var restored = WindowEnumerator.Describe(fixture.Handle)!.Bounds;
            AssertRestored(original, restored, "Journal recovery");
            Assert(!File.Exists(journalPath), "journal cleared after recovery");
        }
        finally
        {
            manager1.CloseAll();
            manager2.Dispose();
            fixture.Close();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }),
    ("Journal kills an orphaned headless browser", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive ||
            EdgeBrowser.FindEdge() is null)
        {
            Console.WriteLine("SKIP browser recovery: no interactive desktop or Edge.");
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ghostscreen-web-recovery-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDirectory);
        var journalPath = Path.Combine(tempDirectory, "targets.json");
        var profileRoot = Path.Combine(tempDirectory, "profiles");
        var pagePath = Path.Combine(tempDirectory, "page.html");
        File.WriteAllText(
            pagePath,
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Orphan</title></head><body>orphan</body></html>",
            new UTF8Encoding(false));

        var manager1 = new TargetManager(new TargetJournal(journalPath, profileRoot));
        TargetManager? manager2 = null;
        try
        {
            var session = manager1
                .OpenWebAsync(new WebLaunchOptions(new Uri(pagePath).AbsoluteUri, 640, 480))
                .GetAwaiter()
                .GetResult();
            var browserPid = session.ProcessId;
            var profile = session.ProfileDirectory;
            Assert(IsProcessAlive(browserPid), "browser started");

            manager2 = new TargetManager(new TargetJournal(journalPath, profileRoot));
            Assert(manager2.LastRecovery.KilledBrowsers == 1, "browser killed during recovery");
            Assert(manager2.LastRecovery.DeletedProfiles == 1, "profile deleted during recovery");
            Assert(!IsProcessAlive(browserPid), "browser process exited");
            Assert(!Directory.Exists(profile), "profile directory gone");
        }
        finally
        {
            manager1.CloseAll();
            manager2?.Dispose();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }),
    ("Hung target times out then degrades", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive)
        {
            Console.WriteLine("SKIP hung target: no interactive desktop.");
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ghostscreen-hung-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDirectory);

        using var fixture = StartFixtureOffscreen();
        Thread.Sleep(250);
        using var manager = new TargetManager(new TargetJournal(
            Path.Combine(tempDirectory, "targets.json"),
            Path.Combine(tempDirectory, "profiles")));
        var session = manager.Attach(fixture.Handle, confirm: true);

        try
        {
            // PrintWindow can still read the DWM surface of a hung WinForms app,
            // so the timeout path is injected instead of simulated with Sleep.
            session.SetCaptureForTests((_, _) =>
                throw new CaptureException("CAPTURE_TIMEOUT", "simulated timeout"));

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    session.Capture(new CaptureOptions(400, 0));
                    throw new InvalidOperationException($"Expected timeout on attempt {attempt}.");
                }
                catch (CaptureException ex)
                {
                    Assert(
                        ex.Code == "CAPTURE_TIMEOUT",
                        $"attempt {attempt} code {ex.Code}: {ex.Message}");
                }
            }

            var fast = Stopwatch.StartNew();
            try
            {
                session.Capture(new CaptureOptions(400, 0));
                throw new InvalidOperationException("Expected TARGET_HUNG.");
            }
            catch (TargetException ex)
            {
                Assert(ex.Code == "TARGET_HUNG", $"third capture code {ex.Code}");
            }

            Assert(fast.ElapsedMilliseconds < 200, $"degraded capture should fail fast: {fast.ElapsedMilliseconds}ms");

            using var dto = JsonDocument.Parse(JsonSerializer.Serialize(session.ToDto()));
            Assert(
                dto.RootElement.GetProperty("state").GetString() == "degraded",
                "target state degraded");
            Assert(
                dto.RootElement.GetProperty("degraded_reason").GetString() == "TARGET_HUNG",
                "degraded reason TARGET_HUNG");
        }
        finally
        {
            manager.Close(session.Id);
            fixture.Close();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }),
    ("Hung app is refused instead of returning a stale frame", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive)
        {
            Console.WriteLine("SKIP hung app detection: no interactive desktop.");
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ghostscreen-hung-real-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDirectory);

        using var fixture = StartFixtureOffscreen();
        Thread.Sleep(250);
        using var manager = new TargetManager(new TargetJournal(
            Path.Combine(tempDirectory, "targets.json"),
            Path.Combine(tempDirectory, "profiles")));
        var session = manager.Attach(fixture.Handle, confirm: true);

        try
        {
            fixture.Hang(10_000);
            // Windows marks a window as hung after roughly five seconds without PeekMessage.
            Thread.Sleep(6_000);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                session.Capture(new CaptureOptions(1_000, 0));
                throw new InvalidOperationException("Expected TARGET_HUNG.");
            }
            catch (CaptureException ex)
            {
                Assert(ex.Code == "TARGET_HUNG", $"unexpected code {ex.Code}: {ex.Message}");
            }

            Assert(stopwatch.ElapsedMilliseconds < 1_000, $"hung check should be immediate: {stopwatch.ElapsedMilliseconds}ms");

            using var dto = JsonDocument.Parse(JsonSerializer.Serialize(session.ToDto()));
            Assert(
                dto.RootElement.GetProperty("state").GetString() == "degraded",
                "hung target is degraded");
            Assert(
                dto.RootElement.GetProperty("degraded_reason").GetString() == "TARGET_HUNG",
                "degraded reason TARGET_HUNG");

            using var secondManager = new TargetManager(new TargetJournal(
                Path.Combine(tempDirectory, "second.json"),
                Path.Combine(tempDirectory, "profiles")));
            try
            {
                secondManager.Attach(fixture.Handle, confirm: true);
                throw new InvalidOperationException("Expected attach to refuse a hung window.");
            }
            catch (TargetException ex)
            {
                Assert(ex.Code == "TARGET_HUNG", $"attach refused with {ex.Code}");
            }

            // Let the UI thread finish so Close can restore the window.
            Thread.Sleep(4_500);
        }
        finally
        {
            manager.Close(session.Id);
            fixture.Close();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }),
    ("Status server lists targets and panics", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive)
        {
            Console.WriteLine("SKIP status server: no interactive desktop.");
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ghostscreen-status-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDirectory);
        var endpointFile = Path.Combine(tempDirectory, "status.json");

        using var fixture = StartFixtureOffscreen();
        Thread.Sleep(250);
        using var manager = new TargetManager(new TargetJournal(
            Path.Combine(tempDirectory, "targets.json"),
            Path.Combine(tempDirectory, "profiles")));
        using var status = new StatusServer(manager, 0, null, endpointFile);
        status.Start();

        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var baseUrl = $"http://127.0.0.1:{status.Port}";

        try
        {
            var unauthorized = http.GetAsync(baseUrl + "/api/status").GetAwaiter().GetResult();
            Assert((int)unauthorized.StatusCode == 401, "status requires a token");

            var session = manager.Attach(fixture.Handle, confirm: true);

            var endpoint = StatusClient.TryReadEndpoint(endpointFile);
            Assert(endpoint is not null && endpoint.Token == status.Token, "endpoint file is readable");
            using var statusClient = new StatusClient(endpoint!);
            var snapshot = statusClient.GetStatusAsync().GetAwaiter().GetResult();
            Assert(snapshot is not null && snapshot.TargetCount == 1, "status client sees one target");

            var json = http.GetStringAsync($"{baseUrl}/api/status?token={status.Token}")
                .GetAwaiter()
                .GetResult();
            Assert(json.Contains(session.Id, StringComparison.Ordinal), "status lists the target id");
            Assert(json.Contains("\"kind\":\"app\"", StringComparison.Ordinal), "status lists the kind");

            var html = http.GetStringAsync(status.Url).GetAwaiter().GetResult();
            Assert(html.Contains("GhostScreen", StringComparison.Ordinal), "page renders");
            Assert(html.Contains("急停", StringComparison.Ordinal), "page has panic");

            var shotResponse = http
                .GetAsync($"{baseUrl}/api/screenshot?target_id={session.Id}&token={status.Token}")
                .GetAwaiter()
                .GetResult();
            Assert((int)shotResponse.StatusCode == 200, "screenshot returns 200");
            var shotBytes = shotResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            Assert(PngInfo.HasSignature(shotBytes), "screenshot is a PNG");
            Assert(shotBytes.Length > 1_000, "screenshot has content");

            var clientClose = statusClient.CloseTargetAsync(session.Id).GetAwaiter().GetResult();
            Assert(clientClose, "status client closes one target");
            Assert(manager.List().Count == 0, "close endpoint removed the target");

            var gone = http
                .GetAsync($"{baseUrl}/api/screenshot?target_id={session.Id}&token={status.Token}")
                .GetAwaiter()
                .GetResult();
            Assert((int)gone.StatusCode == 404, "screenshot for a closed target returns 404");

            manager.Attach(fixture.Handle, confirm: true);

            var panic = http.PostAsync($"{baseUrl}/api/panic?token={status.Token}", null)
                .GetAwaiter()
                .GetResult();
            Assert((int)panic.StatusCode == 200, "panic returns 200");
            Assert(manager.List().Count == 0, "panic closed all targets");

            var clientPanic = statusClient.PanicAsync().GetAwaiter().GetResult();
            Assert(clientPanic is not null && clientPanic.Closed == 0, "status client parses panic result");

            status.Dispose();
            Assert(!File.Exists(endpointFile), "endpoint file removed on dispose");
        }
        finally
        {
            manager.CloseAll();
            fixture.Close();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }),
    ("Status page renders in a headless browser", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive ||
            EdgeBrowser.FindEdge() is null)
        {
            Console.WriteLine("SKIP status page render: no interactive desktop or Edge.");
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ghostscreen-status-page-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDirectory);

        using var fixture = StartFixtureOffscreen();
        Thread.Sleep(250);
        using var manager = new TargetManager(new TargetJournal(
            Path.Combine(tempDirectory, "targets.json"),
            Path.Combine(tempDirectory, "profiles")));
        using var status = new StatusServer(manager, 0, null, Path.Combine(tempDirectory, "status.json"));
        status.Start();

        EdgeBrowser? browser = null;
        EdgeCdpClient? client = null;

        try
        {
            manager.Attach(fixture.Handle, confirm: true);
            browser = EdgeBrowser
                .LaunchAsync(new WebLaunchOptions(status.Url, 900, 700))
                .GetAwaiter()
                .GetResult();
            client = EdgeCdpClient.ConnectAsync(browser.Port).GetAwaiter().GetResult();
            client.SetViewportAsync(900, 700, 1, false).GetAwaiter().GetResult();
            client.NavigateAsync(status.Url).GetAwaiter().GetResult();
            Thread.Sleep(1_500);

            var png = client.CaptureScreenshotBytesAsync().GetAwaiter().GetResult();
            var size = PngInfo.TryReadSize(png);
            Assert(size is { Width: 900, Height: 700 }, $"status page size {size}");

            var artifactDirectory = Path.Combine(Environment.CurrentDirectory, "spike", "out");
            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllBytes(Path.Combine(artifactDirectory, "status-page.png"), png);
        }
        finally
        {
            if (client is not null)
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            if (browser is not null)
            {
                browser.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            manager.CloseAll();
            fixture.Close();
            status.Dispose();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }),
    ("Attach to an existing CDP endpoint detaches without closing it", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive ||
            EdgeBrowser.FindEdge() is null)
        {
            Console.WriteLine("SKIP attach_cdp: no interactive desktop or Edge.");
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ghostscreen-cdp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDirectory);
        var pagePath = Path.Combine(tempDirectory, "page.html");
        File.WriteAllText(
            pagePath,
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>CDP Attach</title></head><body>attached</body></html>",
            new UTF8Encoding(false));
        var url = new Uri(pagePath).AbsoluteUri;

        var browser = EdgeBrowser
            .LaunchAsync(new WebLaunchOptions("about:blank", 700, 500))
            .GetAwaiter()
            .GetResult();
        TargetManager? manager = null;

        try
        {
            manager = new TargetManager(new TargetJournal(
                Path.Combine(tempDirectory, "targets.json"),
                Path.Combine(tempDirectory, "profiles")));

            try
            {
                manager.AttachCdpAsync(1).GetAwaiter().GetResult();
                throw new InvalidOperationException("Expected CDP_CONNECT_FAILED.");
            }
            catch (TargetException ex)
            {
                Assert(ex.Code == "CDP_CONNECT_FAILED", $"unexpected code {ex.Code}");
            }

            var session = manager.AttachCdpAsync(browser.Port).GetAwaiter().GetResult();
            Assert(session.Kind == "web", "external target kind");
            Assert(!session.OwnsBrowser, "external target must not own the browser");

            session.NavigateAsync(url).GetAwaiter().GetResult();
            var capture = session.CaptureAsync().GetAwaiter().GetResult();
            Assert(capture.Width > 0 && capture.Height > 0, "external capture");
            Assert(capture.Png.Length > 100, "external capture has content");

            manager.Close(session.Id);
            Thread.Sleep(300);
            Assert(!browser.Process.HasExited, "closing an external target must not kill the browser");
        }
        finally
        {
            manager?.Dispose();
            browser.DisposeAsync().AsTask().GetAwaiter().GetResult();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }),
    ("Closing a target whose window disappeared is safe", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive)
        {
            Console.WriteLine("SKIP dead native target: no interactive desktop.");
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ghostscreen-dead-native-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDirectory);
        var journalPath = Path.Combine(tempDirectory, "targets.json");

        using var fixture = StartFixtureOffscreen();
        Thread.Sleep(250);
        var manager = new TargetManager(new TargetJournal(journalPath, Path.Combine(tempDirectory, "profiles")));

        try
        {
            var session = manager.Attach(fixture.Handle, confirm: true);
            fixture.Close();
            Thread.Sleep(300);

            try
            {
                session.Capture(new CaptureOptions(1_000, 0));
                throw new InvalidOperationException("Expected capture to fail after the window closed.");
            }
            catch (Exception ex) when (ex is CaptureException or TargetException)
            {
                // A clear NOT_RENDERABLE/TARGET_CLOSED-style failure is the contract.
            }

            Assert(manager.Close(session.Id), "close after window loss returns true");
            Assert(!File.Exists(journalPath), "journal entry removed for a dead window");
        }
        finally
        {
            manager.Dispose();
            fixture.Close();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }),
    ("Killed headless browser is reported as TARGET_CLOSED", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive ||
            EdgeBrowser.FindEdge() is null)
        {
            Console.WriteLine("SKIP killed browser: no interactive desktop or Edge.");
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ghostscreen-killed-browser-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDirectory);
        var pagePath = Path.Combine(tempDirectory, "page.html");
        File.WriteAllText(
            pagePath,
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Killed</title></head><body>killed</body></html>",
            new UTF8Encoding(false));

        var manager = new TargetManager(new TargetJournal(
            Path.Combine(tempDirectory, "targets.json"),
            Path.Combine(tempDirectory, "profiles")));

        try
        {
            var session = manager
                .OpenWebAsync(new WebLaunchOptions(new Uri(pagePath).AbsoluteUri, 640, 480))
                .GetAwaiter()
                .GetResult();
            var browserPid = session.ProcessId;
            var profile = session.ProfileDirectory;

            using (var process = Process.GetProcessById(browserPid))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3_000);
            }

            Thread.Sleep(700);

            try
            {
                session.CaptureAsync().GetAwaiter().GetResult();
                throw new InvalidOperationException("Expected TARGET_CLOSED after the browser was killed.");
            }
            catch (TargetException ex)
            {
                Assert(ex.Code == "TARGET_CLOSED", $"unexpected code {ex.Code}: {ex.Message}");
            }

            Assert(manager.Close(session.Id), "close after browser loss returns true");
            Assert(!Directory.Exists(profile), "profile deleted for a killed browser");
        }
        finally
        {
            manager.Dispose();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }),
    ("DPI-unaware target is scaled to physical pixels", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive)
        {
            Console.WriteLine("SKIP DPI-unaware capture: no interactive desktop.");
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "ghostscreen-dpi-unaware-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDirectory);

        using var fixture = StartDpiUnawareFixtureOffscreen();
        Thread.Sleep(300);
        var manager = new TargetManager(new TargetJournal(
            Path.Combine(tempDirectory, "targets.json"),
            Path.Combine(tempDirectory, "profiles")));

        try
        {
            var before = WindowEnumerator.Describe(fixture.Handle)!;
            Assert(
                before.DpiAwareness == WindowDpiAwareness.Unaware,
                $"expected Unaware, got {before.DpiAwareness}");

            var session = manager.Attach(fixture.Handle, confirm: true);
            var info = WindowEnumerator.Describe(fixture.Handle)!;
            var result = session.Capture(new CaptureOptions(3_000, 0));

            Assert(
                result.Frame.Width == info.ClientBounds.Width &&
                result.Frame.Height == info.ClientBounds.Height,
                $"scaled capture {result.Frame.Width}x{result.Frame.Height} != client {info.ClientBounds.Width}x{info.ClientBounds.Height}");

            var blackRatio = BlackRatio(result.Frame);
            Assert(
                blackRatio < 0.10,
                $"DPI-unaware capture still has a black padding region: {blackRatio:P1}");

            manager.Close(session.Id);
        }
        finally
        {
            manager.Dispose();
            fixture.Close();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Best effort.
            }
        }
    }),
    ("WPF UIA fallback executes and restores focus", () =>
    {
        if (!Environment.UserInteractive ||
            !System.Windows.Forms.SystemInformation.UserInteractive)
        {
            Console.WriteLine("SKIP WPF UIA: no interactive desktop.");
            return;
        }

        if (Environment.GetEnvironmentVariable("GHOSTSCREEN_UIA_TESTS") != "1")
        {
            Console.WriteLine("SKIP WPF UIA: focus-stealing fallback test; set GHOSTSCREEN_UIA_TESTS=1 to run it.");
            return;
        }

        using var fixture = new WpfFixture();
        Thread.Sleep(300);
        var before = SystemState.Capture();
        if (before.ForegroundWindow == IntPtr.Zero)
        {
            Console.WriteLine("SKIP WPF UIA: no foreground window.");
            return;
        }

        using var manager = new TargetManager();
        try
        {
            var session = manager.Attach(fixture.Handle, confirm: true);
            Thread.Sleep(250);

            var buttonObservation = session.Observe(150);
            var buttonElement = buttonObservation.Elements.FirstOrDefault(element => element.AutomationId == "wpfButton")
                ?? throw new InvalidOperationException("UIA element not observed: wpfButton");
            Console.WriteLine(
                $"WPF button ref={buttonElement.Ref} patterns=[{string.Join(",", buttonElement.Patterns)}] bounds={buttonElement.Bounds}");
            var buttonRef = buttonElement.Ref;
            var click = session.ClickRef(buttonRef);
            Console.WriteLine($"WPF click ok={click.Ok} strategy={click.Strategy} focus={click.Focus} clicks={fixture.Clicks}");
            Assert(click.Ok && click.Strategy == "uia", $"WPF click strategy={click.Strategy}");
            Assert(click.Focus != FocusRestoreStatus.Stolen, $"WPF click focus={click.Focus}");
            WaitUntil(() => fixture.Clicks == 1, "WPF button click was not dispatched");
            AssertForeground(before.ForegroundWindow, "WPF click");

            var clientX = buttonElement.Bounds.X - session.Info.ClientBounds.X + buttonElement.Bounds.Width / 2;
            var clientY = buttonElement.Bounds.Y - session.Info.ClientBounds.Y + buttonElement.Bounds.Height / 2;
            var pointClick = session.ClickPoint(clientX, clientY);
            Console.WriteLine($"WPF point-click ok={pointClick.Ok} strategy={pointClick.Strategy} focus={pointClick.Focus}");
            Assert(pointClick.Ok && pointClick.Strategy == "uia-point", $"WPF point strategy={pointClick.Strategy}");
            WaitUntil(() => fixture.Clicks == 2, "WPF coordinate click was not dispatched");
            AssertForeground(before.ForegroundWindow, "WPF coordinate click");

            var set = session.SetText("wpfInput", "wpf value");
            Console.WriteLine(
                $"WPF set_text ok={set.Ok} strategy={set.Strategy} focus={set.Focus} " +
                $"fg=0x{SystemState.Capture().ForegroundWindow.ToInt64():X} original=0x{before.ForegroundWindow.ToInt64():X}");
            Assert(set.Ok && set.Strategy == "uia", $"WPF set_text strategy={set.Strategy}");
            Assert(set.Focus != FocusRestoreStatus.Stolen, $"WPF set_text focus={set.Focus}");
            WaitUntil(() => fixture.Text == "wpf value", "WPF text was not updated");
            Console.WriteLine($"WPF set_text after wait fg=0x{SystemState.Capture().ForegroundWindow.ToInt64():X}");
            AssertForeground(before.ForegroundWindow, "WPF set_text");

            var checkRef = FindRef(session, element => element.AutomationId == "wpfCheck", "wpfCheck");
            var check = session.ClickRef(checkRef);
            Assert(check.Ok, "WPF checkbox did not toggle");
            WaitUntil(() => fixture.Checked, "WPF checkbox state was not updated");
            AssertForeground(before.ForegroundWindow, "WPF checkbox");

            var itemRef = FindRef(session, element => element.Name == "Two", "list item Two");
            var item = session.ClickRef(itemRef);
            Assert(item.Ok, "WPF list item did not select");
            WaitUntil(() => fixture.SelectedIndex == 1, "WPF list selection was not updated");
            AssertForeground(before.ForegroundWindow, "WPF list item");

            var labelRef = FindRef(session, element => element.AutomationId == "wpfLabel", "wpfLabel");
            var error = AssertThrows<TargetException>(() => session.ClickRef(labelRef));
            Assert(error.Code == "ELEMENT_NOT_ACTIONABLE", $"WPF label code={error.Code}");

            var strictError = AssertThrows<TargetException>(
                () => session.ClickElement("Wpf Click", strictNoFocusSteal: true));
            Assert(strictError.Code == "FOCUS_STEAL_NOT_ALLOWED", $"WPF strict code={strictError.Code}");
            Assert(fixture.Clicks == 2, "strict mode must not execute the UIA fallback");
        }
        finally
        {
            manager.CloseAll();
            fixture.Dispose();
        }
    }),
    ("MCP attach/capture/observe/act/close", RunMcpRoundTrip),
    ("MCP web open/capture/observe/act/close", RunWebMcpRoundTrip),
};

var failures = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        AssertNoVisibleTestWindows();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static FixtureInstance StartFixtureOffscreen()
{
    var point = DisplayInfo.Get().GetParkPosition(new Rect(120, 120, 680, 420));
    return FixtureHost.Start(point.X, point.Y);
}

static FixtureInstance StartDpiUnawareFixtureOffscreen()
{
    var point = DisplayInfo.Get().GetParkPosition(new Rect(120, 120, 680, 420));
    return FixtureHost.StartDpiUnaware(point.X, point.Y);
}

static void AssertRestored(Rect original, Rect restored, string label)
{
    var display = DisplayInfo.Get();
    var exact = Math.Abs(restored.X - original.X) <= 2 && Math.Abs(restored.Y - original.Y) <= 2;

    // SetWindowPlacement clamps a fully off-screen normal position back into the
    // work area. The recovery test keeps its original off-screen to avoid flashing
    // a window on the test runner's desktop, so accept that OS clamp.
    var clampedFromOffscreen = display.IsFullyOffscreen(original) && !display.IsFullyOffscreen(restored);

    Assert(exact || clampedFromOffscreen, $"{label}: restored {restored} vs original {original}");
}

static void AssertNoVisibleTestWindows()
{
    var display = DisplayInfo.Get();
    var ownPid = (uint)Environment.ProcessId;
    var visible = WindowEnumerator.List(includeInvisible: false, includeOwnProcess: true)
        .Where(window => window.ProcessId == ownPid)
        .Where(window => !display.IsFullyOffscreen(window.Bounds))
        .ToArray();

    if (visible.Length > 0)
    {
        throw new InvalidOperationException(
            "test left visible window(s): " + string.Join(", ", visible.Select(window => window.ToString())));
    }
}

static string FindRef(TargetSession session, Func<UiaElement, bool> predicate, string label)
{
    var observation = session.Observe(150);
    var element = observation.Elements.FirstOrDefault(predicate)
        ?? throw new InvalidOperationException($"UIA element not observed: {label}");
    return element.Ref;
}

static void AssertForeground(IntPtr expected, string label)
{
    var actual = SystemState.Capture().ForegroundWindow;
    var info = WindowEnumerator.Describe(actual);
    Assert(
        actual == expected,
        $"{label}: foreground 0x{actual.ToInt64():X} ({info?.ProcessName} \"{info?.Title}\") != expected 0x{expected.ToInt64():X}");
}

static TException AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException ex)
    {
        return ex;
    }

    throw new InvalidOperationException($"expected {typeof(TException).Name}");
}

static void WaitUntil(Func<bool> condition, string message, int timeoutMs = 1_500)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        if (condition())
        {
            return;
        }

        Thread.Sleep(25);
    }

    throw new InvalidOperationException(message);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static bool IsProcessAlive(int pid)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        return !process.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static double BlackRatio(CaptureFrame frame)
{
    var pixels = frame.Width * frame.Height;
    if (pixels <= 0)
    {
        return 1;
    }

    var black = 0;
    for (var index = 0; index < frame.Bgra.Length; index += 4)
    {
        if (frame.Bgra[index] < 8 &&
            frame.Bgra[index + 1] < 8 &&
            frame.Bgra[index + 2] < 8)
        {
            black++;
        }
    }

    return (double)black / pixels;
}

static void RunWebMcpRoundTrip()
{
    if (!Environment.UserInteractive ||
        !System.Windows.Forms.SystemInformation.UserInteractive)
    {
        Console.WriteLine("SKIP web round-trip: no interactive desktop.");
        return;
    }

    if (EdgeBrowser.FindEdge() is null)
    {
        Console.WriteLine("SKIP web round-trip: Microsoft Edge not found.");
        return;
    }

    var tempDirectory = Path.Combine(
        Path.GetTempPath(),
        "ghostscreen-web-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDirectory);
    var pagePath = Path.Combine(tempDirectory, "fixture.html");
    File.WriteAllText(pagePath, WebFixture.Html, new UTF8Encoding(false));
    var url = new Uri(pagePath).AbsoluteUri;

    using var manager = new TargetManager();
    using var client = new McpTestClient(manager);

    try
    {
        client.Call("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "ghostscreen-web-test", version = "0" },
        });

        var tools = client.Call("tools/list");
        var toolNames = tools.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();
        foreach (var expected in new[] { "open_web", "snapshot", "navigate", "viewport", "console", "network", "get_html" })
        {
            Assert(toolNames.Contains(expected), $"{expected} tool");
        }

        var open = client.Tool("open_web", new { url, width = 900, height = 700 });
        using var openDocument = JsonDocument.Parse(McpTestClient.Text(open));
        var target = openDocument.RootElement;
        var targetId = target.GetProperty("id").GetString()!;
        Assert(target.GetProperty("kind").GetString() == "web", "target kind web");
        Assert(target.GetProperty("url").GetString()!.Contains("fixture.html", StringComparison.Ordinal), "target url");
        var browserPid = target.GetProperty("process").GetProperty("pid").GetInt32();
        Assert(browserPid > 0, "browser pid");

        var targets = JsonDocument.Parse(McpTestClient.Text(client.Tool("list_targets", new { })));
        Assert(
            targets.RootElement.EnumerateArray().Any(item =>
                item.GetProperty("id").GetString() == targetId &&
                item.GetProperty("kind").GetString() == "web"),
            "list_targets contains web target");

        var shot = client.Tool("screenshot", new { target_id = targetId });
        var shotText = McpTestClient.Text(shot);
        var png = Convert.FromBase64String(McpTestClient.Image(shot));
        Assert(PngInfo.HasSignature(png), "web png signature");
        Assert(shotText.Contains("\"backend\":\"cdp\"", StringComparison.Ordinal), "cdp backend");
        var artifactDirectory = Path.Combine(Environment.CurrentDirectory, "spike", "out");
        Directory.CreateDirectory(artifactDirectory);
        File.WriteAllBytes(Path.Combine(artifactDirectory, "web-shot.png"), png);

        var fullShot = client.Tool("screenshot", new { target_id = targetId, full_page = true });
        var fullText = McpTestClient.Text(fullShot);
        var fullPng = Convert.FromBase64String(McpTestClient.Image(fullShot));
        var fullSize = PngInfo.TryReadSize(fullPng);
        Assert(fullText.Contains("\"region\":\"full_page\"", StringComparison.Ordinal), "full page region");
        Assert(
            fullSize is { Width: 900 } && fullSize.Value.Height > 3000,
            $"full page size {fullSize}");
        File.WriteAllBytes(Path.Combine(artifactDirectory, "web-full.png"), fullPng);

        var clipShot = client.Tool("screenshot", new
        {
            target_id = targetId,
            clip = new { x = 20, y = 20, width = 200, height = 60 },
        });
        var clipText = McpTestClient.Text(clipShot);
        var clipPng = Convert.FromBase64String(McpTestClient.Image(clipShot));
        var clipSize = PngInfo.TryReadSize(clipPng);
        Assert(clipText.Contains("\"region\":\"clip\"", StringComparison.Ordinal), "clip region");
        Assert(clipSize is { Width: 200, Height: 60 }, $"clip size {clipSize}");

        static string HtmlOf(McpTestClient client, string targetId, string selector)
        {
            var result = client.Tool("get_html", new { target_id = targetId, selector });
            using var document = JsonDocument.Parse(McpTestClient.Text(result));
            return document.RootElement.GetProperty("html").GetString() ?? string.Empty;
        }

        static int[] ParseRect(string text)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                text,
                @"\((-?\d+),(-?\d+) (\d+)x(\d+)\)");
            if (!match.Success)
            {
                throw new InvalidOperationException($"Cannot parse rect '{text}'.");
            }

            return
            [
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value),
                int.Parse(match.Groups[4].Value),
            ];
        }

        var observation = client.Tool("observe", new { target_id = targetId, max_nodes = 80 });
        var elements = JsonDocument.Parse(McpTestClient.Text(observation))
            .RootElement.GetProperty("elements").EnumerateArray().ToArray();
        var button = elements.First(element =>
            element.GetProperty("name").GetString() == "Click Me");
        var buttonRef = button.GetProperty("refId").GetString()!;
        var buttonBounds = ParseRect(button.GetProperty("bounds").GetString()!);
        var inputRef = elements.First(element =>
            element.GetProperty("tag").GetString() == "input").GetProperty("refId").GetString()!;

        var clickRef = client.Tool("click_element", new { target_id = targetId, @ref = buttonRef });
        Assert(McpTestClient.Text(clickRef).Contains("cdp-ref", StringComparison.Ordinal), "web click by ref");
        Assert(HtmlOf(client, targetId, "#status").Contains("clicked 1", StringComparison.Ordinal), "web status clicked 1");

        var clickPoint = client.Tool("click", new
        {
            target_id = targetId,
            x = buttonBounds[0] + (buttonBounds[2] / 2),
            y = buttonBounds[1] + (buttonBounds[3] / 2),
        });
        Assert(McpTestClient.Text(clickPoint).Contains("\"strategy\":\"cdp\"", StringComparison.Ordinal), "web click strategy");
        Assert(HtmlOf(client, targetId, "#status").Contains("clicked 2", StringComparison.Ordinal), "web status clicked 2");

        var setText = client.Tool("set_text", new
        {
            target_id = targetId,
            @ref = inputRef,
            text = "mcp 世界",
        });
        Assert(McpTestClient.Text(setText).Contains("\"ok\":true", StringComparison.Ordinal), "web set_text ok");
        Assert(HtmlOf(client, targetId, "#mirror").Contains("mcp 世界", StringComparison.Ordinal), "web input mirror");

        var key = client.Tool("key", new { target_id = targetId, keys = new[] { "CTRL", "S" } });
        Assert(McpTestClient.Text(key).Contains("\"strategy\":\"cdp\"", StringComparison.Ordinal), "web key strategy");
        Assert(HtmlOf(client, targetId, "#keys").Contains("ctrl+s", StringComparison.Ordinal), "web ctrl+s");

        var dragElement = elements.First(element =>
            element.GetProperty("name").GetString() == "drag");
        var dragBounds = ParseRect(dragElement.GetProperty("bounds").GetString()!);
        var drag = client.Tool("drag", new
        {
            target_id = targetId,
            from_x = dragBounds[0] + 40,
            from_y = dragBounds[1] + 30,
            to_x = dragBounds[0] + 120,
            to_y = dragBounds[1] + 30,
            duration_ms = 120,
        });
        Assert(McpTestClient.Text(drag).Contains("\"strategy\":\"cdp\"", StringComparison.Ordinal), "web drag strategy");
        Assert(HtmlOf(client, targetId, "#dragResult").Contains("done", StringComparison.Ordinal), "web drag done");

        // Scroll after the drag: scrolling moves the page and would invalidate pre-scroll coordinates.
        var scroll = client.Tool("scroll", new { target_id = targetId, x = 400, y = 300, delta = -120 });
        Assert(McpTestClient.Text(scroll).Contains("\"strategy\":\"cdp\"", StringComparison.Ordinal), "web scroll strategy");
        var wheelHtml = HtmlOf(client, targetId, "#wheel");
        Assert(
            !wheelHtml.Contains("wheel: </div>", StringComparison.Ordinal),
            $"web scroll reached the page: {wheelHtml}");

        var console = client.Tool("console", new { target_id = targetId, limit = 20 });
        Assert(McpTestClient.Text(console).Contains("boom", StringComparison.Ordinal), "web console captured");

        var network = client.Tool("network", new { target_id = targetId, limit = 20 });
        var networkText = McpTestClient.Text(network);
        Assert(
            networkText.Contains("ghostscreen-missing", StringComparison.Ordinal),
            $"web network failure captured: {networkText}");

        var viewport = client.Tool("viewport", new
        {
            target_id = targetId,
            width = 390,
            height = 844,
            device_scale_factor = 2,
            mobile = true,
        });
        var viewportText = McpTestClient.Text(viewport);
        Assert(viewportText.Contains("\"width\":390", StringComparison.Ordinal), $"web viewport: {viewportText}");

        var mobileShot = client.Tool("screenshot", new { target_id = targetId });
        var mobileText = McpTestClient.Text(mobileShot);
        var mobilePng = Convert.FromBase64String(McpTestClient.Image(mobileShot));
        var mobileSize = PngInfo.TryReadSize(mobilePng);
        Assert(mobileText.Contains("\"css_width\":390", StringComparison.Ordinal), "mobile css width");
        Assert(mobileSize is { Width: 780, Height: 1688 }, $"mobile image size {mobileSize}");

        // Overflow fixture is only shown with ?wide=1 so normal mobile checks stay stable.
        client.Tool("navigate", new { target_id = targetId, url = url + "?wide=1" });

        var snapshot = client.Tool("snapshot", new
        {
            target_id = targetId,
            viewports = new object[]
            {
                new { width = 400, height = 800, device_scale_factor = 1, mobile = false },
                new { width = 900, height = 700, device_scale_factor = 1, mobile = false },
            },
        });
        var snapshotImages = snapshot.GetProperty("content").EnumerateArray()
            .Count(item => item.GetProperty("type").GetString() == "image");
        Assert(snapshotImages == 2, $"snapshot images {snapshotImages}");

        using var snapshotDocument = JsonDocument.Parse(McpTestClient.Text(snapshot));
        var snapshotShots = snapshotDocument.RootElement.GetProperty("shots").EnumerateArray().ToArray();
        Assert(snapshotShots.Length == 2, "snapshot shot count");
        Assert(
            snapshotShots[0].GetProperty("overflow").GetProperty("horizontal_overflow").GetBoolean(),
            "narrow viewport reports horizontal overflow");
        var offenders = snapshotShots[0].GetProperty("overflow").GetProperty("offenders")
            .EnumerateArray().ToArray();
        Assert(
            offenders.Any(offender => offender.GetProperty("selector").GetString() == "#wide"),
            "overflow offender includes #wide");
        Assert(
            !snapshotShots[1].GetProperty("overflow").GetProperty("horizontal_overflow").GetBoolean(),
            "wide viewport has no horizontal overflow");
        File.WriteAllBytes(
            Path.Combine(artifactDirectory, "web-overflow.png"),
            Convert.FromBase64String(McpTestClient.Image(snapshot)));

        // Loading the normal page at the restored viewport proves snapshot restored it.
        client.Tool("navigate", new { target_id = targetId, url });
        var targetsAfterSnapshot = JsonDocument.Parse(McpTestClient.Text(client.Tool("list_targets", new { })));
        var restoredViewport = targetsAfterSnapshot.RootElement.EnumerateArray()
            .First(item => item.GetProperty("id").GetString() == targetId)
            .GetProperty("viewport");
        Assert(
            restoredViewport.GetProperty("width").GetInt32() == 390 &&
            restoredViewport.GetProperty("device_scale_factor").GetDouble() == 2,
            $"snapshot restores the original viewport: {restoredViewport.GetRawText()}");

        var navigate = client.Tool("navigate", new { target_id = targetId, url = url + "?v=2" });
        Assert(McpTestClient.Text(navigate).Contains("v=2", StringComparison.Ordinal), "web navigate");

        var close = client.Tool("close_target", new { target_id = targetId });
        Assert(McpTestClient.Text(close).Contains("\"ok\":true", StringComparison.Ordinal), "web close ok");

        var browserExited = false;
        try
        {
            using var process = Process.GetProcessById(browserPid);
            browserExited = process.HasExited;
        }
        catch (ArgumentException)
        {
            browserExited = true;
        }

        Assert(browserExited, "headless browser exited after close_target");
    }
    finally
    {
        manager.CloseAll();

        try
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
        catch
        {
            // Edge may still hold the file briefly.
        }
    }
}

static void RunMcpRoundTrip()
{
    if (!Environment.UserInteractive ||
        !System.Windows.Forms.SystemInformation.UserInteractive)
    {
        Console.WriteLine("SKIP MCP round-trip: no interactive desktop.");
        return;
    }

    using var fixture = StartFixtureOffscreen();
    Thread.Sleep(300);

    var beforeState = SystemState.Capture();
    using var manager = new TargetManager();
    var reader = new QueueReader();
    var writer = new QueueWriter();
    var server = new McpServer(manager, reader, writer);
    var serverTask = Task.Run(server.RunAsync);

    try
    {
        var nextId = 0;
        JsonElement Call(string method, object? parameters = null)
        {
            nextId++;
            var message = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = nextId,
                ["method"] = method,
            };

            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            reader.Push(JsonSerializer.Serialize(message));
            var line = writer.ReadLine() ?? throw new InvalidOperationException("MCP returned no response.");
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(error.ToString());
            }

            return document.RootElement.GetProperty("result").Clone();
        }

        static string TextContent(JsonElement result)
        {
            foreach (var item in result.GetProperty("content").EnumerateArray())
            {
                if (item.GetProperty("type").GetString() == "text")
                {
                    return item.GetProperty("text").GetString() ?? string.Empty;
                }
            }

            throw new InvalidOperationException("No text content.");
        }

        static string ImageContent(JsonElement result)
        {
            foreach (var item in result.GetProperty("content").EnumerateArray())
            {
                if (item.GetProperty("type").GetString() == "image")
                {
                    return item.GetProperty("data").GetString() ?? string.Empty;
                }
            }

            throw new InvalidOperationException("No image content.");
        }

        var initialize = Call("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "ghostscreen-test", version = "0" },
        });
        Assert(initialize.GetProperty("serverInfo").GetProperty("name").GetString() == "GhostScreen", "serverInfo");

        reader.Push(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));

        var tools = Call("tools/list");
        var toolNames = tools.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();
        Assert(toolNames.Contains("attach_window"), "attach_window tool");
        Assert(toolNames.Contains("screenshot"), "screenshot tool");
        Assert(toolNames.Contains("click"), "click tool");
        Assert(toolNames.Contains("scroll"), "scroll tool");
        Assert(toolNames.Contains("drag"), "drag tool");
        Assert(toolNames.Contains("key"), "key tool");

        var list = Call("tools/call", new
        {
            name = "list_windows",
            arguments = new { include_invisible = false, include_own_process = true },
        });
        Assert(TextContent(list).Contains("GhostScreen Fixture", StringComparison.Ordinal), "list_windows contains fixture title");

        var attach = Call("tools/call", new
        {
            name = "attach_window",
            arguments = new
            {
                hwnd = $"0x{fixture.Handle.ToInt64():X}",
                confirm = true,
            },
        });
        var attachText = TextContent(attach);
        using var attachDocument = JsonDocument.Parse(attachText);
        var targetId = attachDocument.RootElement.GetProperty("id").GetString()!;
        Assert(!string.IsNullOrWhiteSpace(targetId), "target id");

        var shot = Call("tools/call", new
        {
            name = "screenshot",
            arguments = new { target_id = targetId, wait_stable_ms = 120 },
        });
        var shotMetadata = TextContent(shot);
        var png = Convert.FromBase64String(ImageContent(shot));
        Assert(png.Length > 100, "png has content");
        Assert(png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47, "png signature");
        var artifactDirectory = Path.Combine(Environment.CurrentDirectory, "spike", "out");
        Directory.CreateDirectory(artifactDirectory);
        File.WriteAllBytes(Path.Combine(artifactDirectory, "mcp-shot.png"), png);
        Assert(shotMetadata.Contains("printwindow", StringComparison.Ordinal), "printwindow backend");

        var observe = Call("tools/call", new
        {
            name = "observe",
            arguments = new { target_id = targetId, max_nodes = 80 },
        });
        var observeText = TextContent(observe);
        using var observeDocument = JsonDocument.Parse(observeText);
        var observationEpoch = observeDocument.RootElement.GetProperty("epoch").GetInt32();
        var observedElements = observeDocument.RootElement.GetProperty("elements").EnumerateArray().ToArray();
        Assert(observationEpoch > 0, "observe epoch");
        var labelElement = observedElements.First(element =>
            element.GetProperty("automation_id").GetString() == "lblStatus");
        var labelRef = labelElement.GetProperty("refId").GetString()!;
        var refused = Call("tools/call", new
        {
            name = "click_element",
            arguments = new { target_id = targetId, @ref = labelRef },
        });
        Assert(
            TextContent(refused).Contains("ELEMENT_NOT_ACTIONABLE", StringComparison.Ordinal),
            $"label click must report ELEMENT_NOT_ACTIONABLE; got {TextContent(refused)}");
        var buttonElement = observedElements.First(element =>
            element.GetProperty("name").GetString() == "Click Me");
        var buttonRef = buttonElement.GetProperty("refId").GetString()!;
        Assert(observedElements.Any(element =>
                element.GetProperty("automation_id").GetString() == "txtInput"), "observe txtInput");

        var click = Call("tools/call", new
        {
            name = "click_element",
            arguments = new { target_id = targetId, @ref = buttonRef },
        });
        Assert(TextContent(click).Contains("\"strategy\":\"message\"", StringComparison.Ordinal), "click by ref uses messages");
        WaitUntil(() => fixture.Status.EndsWith("clicked 1", StringComparison.Ordinal), $"fixture status clicked 1; got {fixture.Status}");

        var clickPoint = Call("tools/call", new
        {
            name = "click",
            arguments = new { target_id = targetId, x = 420, y = 33 },
        });
        Assert(TextContent(clickPoint).Contains("\"strategy\":\"message\"", StringComparison.Ordinal), "coordinate click strategy");
        WaitUntil(() => fixture.Status.EndsWith("clicked 2", StringComparison.Ordinal), $"fixture status clicked 2; got {fixture.Status}");

        var scroll = Call("tools/call", new
        {
            name = "scroll",
            arguments = new { target_id = targetId, x = 490, y = 365, delta = 120 },
        });
        Assert(TextContent(scroll).Contains("\"ok\":true", StringComparison.Ordinal), "scroll ok");
        Assert(fixture.WheelText.Contains("wheel 120", StringComparison.Ordinal), $"wheel text: {fixture.WheelText}");

        var drag = Call("tools/call", new
        {
            name = "drag",
            arguments = new
            {
                target_id = targetId,
                from_x = 380,
                from_y = 270,
                to_x = 500,
                to_y = 300,
                duration_ms = 200,
            },
        });
        Assert(TextContent(drag).Contains("\"ok\":true", StringComparison.Ordinal), "drag ok");
        Assert(fixture.DragText.Contains("drag done", StringComparison.Ordinal), $"drag text: {fixture.DragText}");

        var key = Call("tools/call", new
        {
            name = "key",
            arguments = new { target_id = targetId, keys = new[] { "F5" } },
        });
        Assert(TextContent(key).Contains("\"ok\":true", StringComparison.Ordinal), "key ok");
        Assert(
            fixture.KeyHistory.Contains("key F5", StringComparison.Ordinal),
            $"key history: {fixture.KeyHistory}");

        var setText = Call("tools/call", new
        {
            name = "set_text",
            arguments = new { target_id = targetId, automation_id = "txtInput", text = "mcp 世界" },
        });
        Assert(TextContent(setText).Contains("\"ok\":true", StringComparison.Ordinal), "set_text ok");
        Assert(TextContent(setText).Contains("\"strategy\":\"message\"", StringComparison.Ordinal), "set_text uses messages");
        Assert(fixture.InputText == "mcp 世界", "fixture input text");

        var close = Call("tools/call", new
        {
            name = "close_target",
            arguments = new { target_id = targetId },
        });
        Assert(TextContent(close).Contains("\"ok\":true", StringComparison.Ordinal), "close ok");

        var afterState = SystemState.Capture();
        Assert(
            afterState.ForegroundWindow != fixture.Handle,
            "parked fixture took the foreground during MCP actions");

        var humanInput = beforeState.LastInputTick != afterState.LastInputTick;
        if (humanInput)
        {
            // The desktop is shared with a human; their window switch or cursor
            // move is not a GhostScreen violation.
            Console.WriteLine("SKIP MCP focus invariant: human input detected during the test.");
        }
        else
        {
            Assert(
                beforeState.ForegroundWindow == afterState.ForegroundWindow,
                $"foreground changed during MCP actions: 0x{beforeState.ForegroundWindow.ToInt64():X} -> 0x{afterState.ForegroundWindow.ToInt64():X}");
            Assert(
                beforeState.CursorX == afterState.CursorX && beforeState.CursorY == afterState.CursorY,
                $"cursor moved during MCP actions: ({beforeState.CursorX},{beforeState.CursorY}) -> ({afterState.CursorX},{afterState.CursorY})");
            Assert(
                beforeState.ClipboardSequence == afterState.ClipboardSequence,
                $"clipboard changed during MCP actions: {beforeState.ClipboardSequence} -> {afterState.ClipboardSequence}");
        }
    }
    finally
    {
        reader.Complete();
        try
        {
            serverTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Surface the test failure rather than hanging.
        }

        manager.CloseAll();
        fixture.Close();
    }
}

internal sealed class QueueReader : TextReader
{
    private readonly BlockingCollection<string> _lines = new();

    public void Push(string line) => _lines.Add(line);

    public void Complete() => _lines.CompleteAdding();

    public override string? ReadLine() =>
        _lines.TryTake(out var line, Timeout.Infinite) ? line : null;

    public override Task<string?> ReadLineAsync() => Task.FromResult(ReadLine());
}

internal sealed class QueueWriter : TextWriter
{
    private readonly BlockingCollection<string> _lines = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override Task WriteLineAsync(string? value)
    {
        _lines.Add(value ?? string.Empty);
        return Task.CompletedTask;
    }

    public override Task FlushAsync() => Task.CompletedTask;

    public string? ReadLine(int timeoutMs = 5_000) =>
        _lines.TryTake(out var line, timeoutMs) ? line : null;
}
