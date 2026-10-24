using System.Diagnostics;
using System.Text.Json;
using GhostScreen.Windows;

namespace GhostScreen.Daemon;

public sealed record RecoveryReport(
    int RestoredApps,
    int SkippedApps,
    int KilledBrowsers,
    int DeletedProfiles,
    int SweptProfiles,
    IReadOnlyList<string> Messages)
{
    public bool IsEmpty =>
        RestoredApps == 0 &&
        SkippedApps == 0 &&
        KilledBrowsers == 0 &&
        DeletedProfiles == 0 &&
        SweptProfiles == 0 &&
        Messages.Count == 0;

    public override string ToString() =>
        $"restored={RestoredApps} skipped={SkippedApps} killedBrowsers={KilledBrowsers} " +
        $"deletedProfiles={DeletedProfiles} sweptProfiles={SweptProfiles}";
}

/// <summary>
/// Crash journal: parks, web targets, and their original placement. On the next
/// start, parked windows are restored and orphaned headless browsers are killed.
/// Writes go through a temp file so a crash cannot leave a half-written journal.
/// </summary>
public sealed class TargetJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _profileRoot;
    private readonly List<JournalEntry> _entries = new();
    private string? _loadWarning;

    public TargetJournal(string? path = null, string? profileRoot = null)
    {
        _path = path ?? DefaultPath;
        _profileRoot = profileRoot ?? DefaultProfileRoot;
        Load();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GhostScreen",
        "state",
        "targets.json");

    public static string DefaultProfileRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GhostScreen",
        "web-profiles");

    public void RecordApp(string id, TargetSession session)
    {
        var snapshot = session.Parked.ToSnapshot(session.Info);
        var entry = new JournalEntry
        {
            Kind = "app",
            Id = id,
            Hwnd = snapshot.Hwnd,
            Pid = snapshot.ProcessId,
            ProcessName = snapshot.ProcessName,
            ClassName = snapshot.ClassName,
            Title = snapshot.Title,
            Placement = snapshot.Placement,
            ExStyle = snapshot.OriginalExStyle,
            WasVisible = snapshot.WasVisible,
            StyleChanged = snapshot.StyleChanged,
            CreatedAtUnixMs = snapshot.ParkedAtUnixMs,
        };

        lock (_sync)
        {
            _entries.RemoveAll(candidate => candidate.Id == id);
            _entries.Add(entry);
            Save();
        }
    }

    public void RecordWeb(string id, WebTargetSession session)
    {
        long startMs = 0;
        try
        {
            startMs = new DateTimeOffset(session.ProcessStartTime.ToUniversalTime())
                .ToUnixTimeMilliseconds();
        }
        catch
        {
            // The browser may have exited already; the pid check still applies.
        }

        var entry = new JournalEntry
        {
            Kind = "web",
            Id = id,
            Pid = session.ProcessId,
            ProcessName = "msedge",
            Title = session.Title,
            Url = session.Url,
            Profile = session.ProfileDirectory,
            ProcessStartUnixMs = startMs,
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        lock (_sync)
        {
            _entries.RemoveAll(candidate => candidate.Id == id);
            _entries.Add(entry);
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_sync)
        {
            if (_entries.RemoveAll(candidate => candidate.Id == id) > 0)
            {
                SaveBestEffort();
            }
        }
    }

    /// <summary>Restores what the previous process left behind. Never throws.</summary>
    public RecoveryReport Recover()
    {
        JournalEntry[] entries;
        lock (_sync)
        {
            entries = _entries.ToArray();
            _entries.Clear();
        }

        var messages = new List<string>();
        if (_loadWarning is not null)
        {
            messages.Add(_loadWarning);
            _loadWarning = null;
        }

        var restored = 0;
        var skipped = 0;
        var killed = 0;
        var deleted = 0;

        foreach (var entry in entries)
        {
            try
            {
                if (entry.Kind == "app" && entry.Placement is not null)
                {
                    var snapshot = new ParkedWindowSnapshot(
                        entry.Hwnd,
                        entry.Pid,
                        entry.ProcessName ?? string.Empty,
                        entry.ClassName ?? string.Empty,
                        entry.Title ?? string.Empty,
                        entry.Placement,
                        entry.ExStyle,
                        entry.WasVisible,
                        entry.StyleChanged,
                        entry.CreatedAtUnixMs);

                    if (WindowRecovery.TryRestore(snapshot, out var reason))
                    {
                        restored++;
                    }
                    else
                    {
                        skipped++;
                        messages.Add($"app {entry.Id}: {reason}");
                    }
                }
                else if (entry.Kind == "web")
                {
                    if (TryKillBrowser(entry))
                    {
                        killed++;
                    }

                    if (!string.IsNullOrWhiteSpace(entry.Profile) &&
                        TryDeleteDirectory(entry.Profile))
                    {
                        deleted++;
                    }
                }
            }
            catch (Exception ex)
            {
                messages.Add($"{entry.Kind} {entry.Id}: {ex.Message}");
            }
        }

        var swept = SweepOldProfiles(messages);

        lock (_sync)
        {
            SaveBestEffort();
        }

        return new RecoveryReport(restored, skipped, killed, deleted, swept, messages);
    }

    private int SweepOldProfiles(List<string> messages)
    {
        if (!Directory.Exists(_profileRoot))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        var swept = 0;

        foreach (var directory in Directory.GetDirectories(_profileRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff &&
                    TryDeleteDirectory(directory))
                {
                    swept++;
                }
            }
            catch (Exception ex)
            {
                messages.Add($"sweep {Path.GetFileName(directory)}: {ex.Message}");
            }
        }

        return swept;
    }

    private static bool TryKillBrowser(JournalEntry entry)
    {
        if (entry.Pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(entry.Pid);
            if (process.HasExited ||
                !string.Equals(process.ProcessName, "msedge", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (entry.ProcessStartUnixMs > 0)
            {
                var startMs = new DateTimeOffset(process.StartTime.ToUniversalTime())
                    .ToUnixTimeMilliseconds();
                if (Math.Abs(startMs - entry.ProcessStartUnixMs) > 2_000)
                {
                    return false;
                }
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(3_000);
            return process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return true;
            }
            catch
            {
                Thread.Sleep(150);
            }
        }

        return false;
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var file = JsonSerializer.Deserialize<JournalFile>(json, JsonOptions);
            if (file?.Targets is not null)
            {
                _entries.AddRange(file.Targets);
            }
        }
        catch (Exception ex)
        {
            _loadWarning = $"journal unreadable: {ex.Message}";
            TryMoveAside();
        }
    }

    private void TryMoveAside()
    {
        try
        {
            var corrupt = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Move(_path, corrupt, overwrite: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private void Save()
    {
        try
        {
            SaveCore();
        }
        catch (Exception ex)
        {
            throw new TargetException("STATE_WRITE_FAILED", $"Cannot write crash journal: {ex.Message}");
        }
    }

    private void SaveBestEffort()
    {
        try
        {
            SaveCore();
        }
        catch
        {
            // Shutdown/cleanup must not fail because the journal is read-only.
        }
    }

    private void SaveCore()
    {
        if (_entries.Count == 0)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(new JournalFile { Targets = _entries }, JsonOptions);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, _path, overwrite: true);
    }

    private sealed class JournalFile
    {
        public int Version { get; set; } = 1;

        public List<JournalEntry> Targets { get; set; } = new();
    }

    private sealed class JournalEntry
    {
        public string Kind { get; set; } = "app";

        public string Id { get; set; } = string.Empty;

        public long Hwnd { get; set; }

        public int Pid { get; set; }

        public string? ProcessName { get; set; }

        public string? ClassName { get; set; }

        public string? Title { get; set; }

        public WindowPlacementData? Placement { get; set; }

        public long ExStyle { get; set; }

        public bool WasVisible { get; set; }

        public bool StyleChanged { get; set; }

        public string? Url { get; set; }

        public string? Profile { get; set; }

        public long ProcessStartUnixMs { get; set; }

        public long CreatedAtUnixMs { get; set; }
    }
}
