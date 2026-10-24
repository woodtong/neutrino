using System.Diagnostics;
using System.IO;
using System.Text;
using GhostScreen.Daemon;
using GhostScreen.Web;
using GhostScreen.Windows;

DpiAwareness.EnablePerMonitorV2();

var cycles = 20;
var urlArg = (string?)null;

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--cycles" when index + 1 < args.Length:
            cycles = int.Parse(args[++index]);
            break;
        case "--url" when index + 1 < args.Length:
            urlArg = args[++index];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[index]}");
            return 2;
    }
}

var tempDirectory = Path.Combine(
    Path.GetTempPath(),
    "ghostscreen-stress-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tempDirectory);

var pagePath = Path.Combine(tempDirectory, "page.html");
File.WriteAllText(
    pagePath,
    """
    <!doctype html>
    <html><head><meta charset="utf-8"><title>GhostScreen Stress</title></head>
    <body style="font:16px sans-serif">
      <button id="btn">Click Me</button>
      <div id="status">status: idle</div>
      <script>
        document.getElementById('btn').addEventListener('click', () => {
          document.getElementById('status').textContent = 'status: clicked';
        });
      </script>
    </body></html>
    """,
    new UTF8Encoding(false));

var url = urlArg ?? new Uri(pagePath).AbsoluteUri;
var profileRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "GhostScreen",
    "web-profiles");

var process = Process.GetCurrentProcess();
var baselineHandles = process.HandleCount;
var failures = 0;

using var manager = new TargetManager();
var stopwatch = Stopwatch.StartNew();

for (var cycle = 1; cycle <= cycles; cycle++)
{
    try
    {
        var session = await manager.OpenWebAsync(new WebLaunchOptions(url, 640, 480));
        var browserPid = session.ProcessId;
        var capture = await session.CaptureAsync();
        if (capture.Width <= 0 || capture.Height <= 0)
        {
            throw new InvalidOperationException("Empty capture.");
        }

        manager.Close(session.Id);

        if (IsProcessAlive(browserPid))
        {
            throw new InvalidOperationException($"Browser {browserPid} still alive after close.");
        }
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"cycle {cycle}: FAIL {ex.GetType().Name}: {ex.Message}");
    }

    if (cycle % 10 == 0 || cycle == cycles)
    {
        var managedKb = GC.GetTotalMemory(forceFullCollection: true) / 1024;
        var handles = Process.GetCurrentProcess().HandleCount;
        var privateMb = Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024);
        var profiles = Directory.Exists(profileRoot)
            ? Directory.GetDirectories(profileRoot).Length
            : 0;

        Console.WriteLine(
            $"cycle {cycle}/{cycles}: {stopwatch.Elapsed.TotalSeconds:F1}s " +
            $"managed={managedKb}KB private={privateMb}MB " +
            $"handles={handles} (+{handles - baselineHandles}) profiles={profiles}");
    }
}

var finalProfiles = Directory.Exists(profileRoot)
    ? Directory.GetDirectories(profileRoot).Length
    : 0;

Console.WriteLine(
    failures == 0 && finalProfiles == 0
        ? $"PASS {cycles} cycles, no failures, profiles=0"
        : $"FAIL cycles={cycles} failures={failures} profiles={finalProfiles}");

try
{
    Directory.Delete(tempDirectory, recursive: true);
}
catch
{
    // Best effort.
}

manager.CloseAll();
return failures == 0 && finalProfiles == 0 ? 0 : 1;

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
