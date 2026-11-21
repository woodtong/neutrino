using System.Threading;
using System.Windows.Forms;
using GhostScreen.Core;

namespace GhostScreen.NotifierApp;

/// <summary>
/// Standalone notifier host. Runs when the tray is not up, so a failing MCP/daemon
/// can still reach the human. Holds a single instance (one pipe listener, one icon),
/// and exits after a short idle period so it does not linger in the notification area.
/// </summary>
internal static class Program
{
    private const string MutexName = @"Global\GhostScreen.Notifier";
    private static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(10);

    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var owned);
        if (!owned)
        {
            return; // another host already owns the pipe and the icon
        }

        using var context = new NotifierContext();
        Application.Run(context);
    }

    private sealed class NotifierContext : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly NotifierServer _server;
        private readonly System.Windows.Forms.Timer _idle;

        public NotifierContext()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("退出通知", null, (_, _) => ExitThread());

            _icon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "中微子通知",
                ContextMenuStrip = menu,
                Visible = true,
            };

            _idle = new System.Windows.Forms.Timer { Interval = (int)IdleExit.TotalMilliseconds };
            _idle.Tick += OnIdleTick;

            _server = new NotifierServer(_icon);
            _server.Activity += (_, _) => _idle.Stop();
            _server.Start();
            _idle.Start();
        }

        private void OnIdleTick(object? sender, EventArgs e)
        {
            if (_server.PendingCount == 0)
            {
                ExitThread();
            }
        }

        protected override void ExitThreadCore()
        {
            _idle.Stop();
            _idle.Dispose();
            _server.Dispose();
            _icon.Visible = false;
            _icon.Dispose();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _icon.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
