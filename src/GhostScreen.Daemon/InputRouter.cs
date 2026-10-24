using GhostScreen.Windows;

namespace GhostScreen.Daemon;

/// <summary>Chooses the message-input strategy and refuses it for Chromium/Electron.</summary>
public static class InputRouter
{
    private static readonly HashSet<string> MessageUnsupported = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge",
        "chrome",
        "Code",
        "msedgewebview2",
    };

    public static bool SupportsMessageInput(WindowInfo info) =>
        !MessageUnsupported.Contains(info.ProcessName);

    public static void EnsureMessageInput(WindowInfo info)
    {
        if (!SupportsMessageInput(info))
        {
            throw new TargetException(
                "INPUT_UNSUPPORTED",
                $"Process '{info.ProcessName}' ignores window messages. Use UIA refs/names, or a CDP target for Chromium/Electron.");
        }
    }

    public static string[] GetInputStrategies(WindowInfo info) =>
        SupportsMessageInput(info) ? ["uia", "message"] : ["uia"];
}
