using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using FixtureApp;
using GhostScreen.Capture;
using GhostScreen.Daemon;
using GhostScreen.Web;
using GhostScreen.Windows;

DpiAwareness.EnablePerMonitorV2();

var minutes = 3.0;
var intervalSeconds = 5.0;
var nativeCount = 1;
var webCount = 1;
var outputPath = Path.Combine(Environment.CurrentDirectory, "spike", "out", "soak.jsonl");

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--minutes" when index + 1 < args.Length:
            minutes = double.Parse(args[++index]);
            break;
        case "--interval" when index + 1 < args.Length:
            intervalSeconds = double.Parse(args[++index]);
            break;
        case "--native" when index + 1 < args.Length:
            nativeCount = Math.Clamp(int.Parse(args[++index]), 1, 4);
            break;
        case "--web" when index + 1 < args.Length:
            webCount = Math.Clamp(int.Parse(args[++index]), 1, 4);
            break;
        case "--output" when index + 1 < args.Length:
            outputPath = args[++index];
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[index]}");
            return 2;
    }
}

var tempDirectory = Path.Combine(
    Path.GetTempPath(),
    "ghostscreen-soak-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tempDirectory);

var pagePath = Path.Combine(tempDirectory, "page.html");
File.WriteAllText(
    pagePath,
    """
    <!doctype html>
    <html><head><meta charset="utf-8"><title>GhostScreen Soak</title>
    <style>
      body { font: 16px sans-serif; margin: 0; }
      #btn { margin: 20px; padding: 10px 20px; }
      #status { margin: 20px; }
    </style></head>
    <body>
      <button id="btn">Click Me</button>
      <div id="status">status: idle</div>
      <div style="height:3000px"></div>
      <script>
        let clicks = 0;
        document.getElementById('btn').addEventListener('click', () => {
          clicks++;
          document.getElementById('status').textContent = 'status: clicked ' + clicks;
        });
      </script>
    </body></html>
    """,
    new UTF8Encoding(false));

var url = new Uri(pagePath).AbsoluteUri;
var display = DisplayInfo.Get();
var parkPoint = display.GetParkPosition(new GhostScreen.Core.Rect(120, 120, 680, 420));

using var manager = new TargetManager(new TargetJournal(
    Path.Combine(tempDirectory, "targets.json"),
    Path.Combine(tempDirectory, "profiles")));

var fixtures = new List<FixtureInstance>();
var natives = new List<TargetSession>();
for (var index = 0; index < nativeCount; index++)
{
    var fixture = FixtureHost.Start(parkPoint.X + (index * 800), parkPoint.Y);
    fixtures.Add(fixture);
    Thread.Sleep(250);
    natives.Add(manager.Attach(fixture.Handle, confirm: true));
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
using var writer = new StreamWriter(outputPath, append: false, new UTF8Encoding(false));
writer.AutoFlush = true;

var process = Process.GetCurrentProcess();
var baselineHandles = process.HandleCount;
var baselinePrivateMb = process.PrivateMemorySize64 / (1024 * 1024);
var failures = 0;
var webCycles = 0;
var firstHandles = 0L;
var lastHandles = 0L;
var firstPrivate = 0L;
var lastPrivate = 0L;
var maxPrivate = 0L;
var warmupHandles = 0L;
var warmupPrivate = 0L;
var started = DateTime.UtcNow;
var deadline = started.AddMinutes(minutes);
var warmupEnd = started.AddSeconds(Math.Min(60, Math.Max(15, minutes * 60 * 0.2)));
var cycle = 0;

Console.WriteLine(
    $"Soak: {minutes:0.0} min, interval {intervalSeconds:0.0}s, native={nativeCount}, web={webCount}, output {outputPath}");

while (DateTime.UtcNow < deadline)
{
    cycle++;

    try
    {
        foreach (var target in natives)
        {
            target.Capture(new CaptureOptions(3_000, 0));

            if (cycle % 3 == 0)
            {
                target.Observe(20);
            }
        }

        for (var index = 0; index < webCount; index++)
        {
            var web = manager
                .OpenWebAsync(new WebLaunchOptions(url, 640, 480))
                .GetAwaiter()
                .GetResult();
            var webShot = web.CaptureAsync().GetAwaiter().GetResult();
            if (webShot.Width <= 0)
            {
                throw new InvalidOperationException("web capture was empty");
            }

            manager.Close(web.Id);
            webCycles++;
        }

        if (cycle % 20 == 0)
        {
            foreach (var target in natives)
            {
                manager.Close(target.Id);
            }

            natives.Clear();
            foreach (var fixture in fixtures)
            {
                natives.Add(manager.Attach(fixture.Handle, confirm: true));
            }
        }
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"cycle {cycle}: FAIL {ex.GetType().Name}: {ex.Message}");
    }

    var current = Process.GetCurrentProcess();
    var handles = current.HandleCount;
    var privateMb = current.PrivateMemorySize64 / (1024 * 1024);
    var managedKb = GC.GetTotalMemory(forceFullCollection: true) / 1024;
    var profiles = Directory.Exists(TargetJournal.DefaultProfileRoot)
        ? Directory.GetDirectories(TargetJournal.DefaultProfileRoot).Length
        : 0;

    if (firstHandles == 0)
    {
        firstHandles = handles;
        firstPrivate = privateMb;
    }

    lastHandles = handles;
    lastPrivate = privateMb;
    maxPrivate = Math.Max(maxPrivate, privateMb);

    if (warmupHandles == 0 && DateTime.UtcNow >= warmupEnd)
    {
        warmupHandles = handles;
        warmupPrivate = privateMb;
        Console.WriteLine(
            $"warmup at {cycle}: handles={handles} private={privateMb}MB");
    }

    var sample = new
    {
        timestamp = DateTimeOffset.UtcNow,
        cycle,
        elapsed_seconds = (long)(DateTime.UtcNow - started).TotalSeconds,
        handles,
        handles_delta = handles - baselineHandles,
        private_mb = privateMb,
        managed_kb = managedKb,
        profiles,
        targets = manager.List().Count,
        web_cycles = webCycles,
        failures,
    };

    writer.WriteLine(JsonSerializer.Serialize(sample));

    if (cycle % 5 == 0 || DateTime.UtcNow >= deadline)
    {
        Console.WriteLine(
            $"cycle {cycle} ({(DateTime.UtcNow - started).TotalSeconds:F0}s): " +
            $"handles={handles} (+{handles - baselineHandles}) private={privateMb}MB managed={managedKb}KB " +
            $"profiles={profiles} web={webCycles}");
    }

    var remaining = deadline - DateTime.UtcNow;
    if (remaining <= TimeSpan.Zero)
    {
        break;
    }

    await Task.Delay(TimeSpan.FromSeconds(Math.Min(intervalSeconds, remaining.TotalSeconds)));
}

manager.CloseAll();
foreach (var fixture in fixtures)
{
    fixture.Close();
}

var handleGrowth = lastHandles - (warmupHandles > 0 ? warmupHandles : firstHandles);
var privateGrowth = lastPrivate - (warmupPrivate > 0 ? warmupPrivate : firstPrivate);
var profilesAtEnd = Directory.Exists(TargetJournal.DefaultProfileRoot)
    ? Directory.GetDirectories(TargetJournal.DefaultProfileRoot).Length
    : 0;

var passed = failures == 0 &&
             profilesAtEnd == 0 &&
             handleGrowth <= 64 &&
             privateGrowth <= 64;

Console.WriteLine(
    $"Soak result: minutes={minutes:0.0} cycles={cycle} native={nativeCount} web={webCount} webTargets={webCycles} failures={failures} " +
    $"warmupHandles={warmupHandles} handles {firstHandles}->{lastHandles} (growth after warmup {handleGrowth}) " +
    $"private {firstPrivate}->{lastPrivate}MB (max {maxPrivate}, growth after warmup {privateGrowth}) " +
    $"profiles={profilesAtEnd}");
Console.WriteLine(passed ? "PASS" : "FAIL");

try
{
    Directory.Delete(tempDirectory, recursive: true);
}
catch
{
    // Best effort.
}

return passed ? 0 : 1;
