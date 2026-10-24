using System.Runtime.InteropServices;
using System.Windows.Forms;
using GhostScreen.Core;
using GhostScreen.TrayApp;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

using var mutex = new Mutex(initiallyOwned: true, "GhostScreen.Tray", out var created);
if (!created)
{
    return;
}

using var context = new TrayContext();
Application.Run(context);

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _targetsItem;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _panicItem;
    private readonly System.Windows.Forms.Timer _timer;
    private StatusClient? _client;
    private bool _refreshing;
    private bool _disposed;
    private ConsentPromptForm? _prompt;

    public TrayContext()
    {
        _statusItem = new ToolStripMenuItem("中微子 未运行") { Enabled = false };
        _targetsItem = new ToolStripMenuItem("Targets");
        _openItem = new ToolStripMenuItem("打开状态页", null, (_, _) => OpenPage());
        _panicItem = new ToolStripMenuItem("急停（关闭所有 target）", null, (_, _) => Panic());

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_targetsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_openItem);
        menu.Items.Add(_panicItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitThread()));

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "中微子",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenPage();

        _timer = new System.Windows.Forms.Timer { Interval = 2_000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        _ = RefreshAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            _prompt?.Close();
            _prompt = null;
            _icon.Visible = false;
            _icon.Dispose();
            _client?.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _disposed)
        {
            return;
        }

        _refreshing = true;

        try
        {
            var endpoint = StatusClient.TryReadEndpoint();
            if (endpoint is null)
            {
                _client?.Dispose();
                _client = null;
                SetState("中微子 未运行", running: false);
                UpdateTargets(null);
                UpdateConsent(null);
                return;
            }

            if (_client is null || _client.PageUrl != endpoint.Url)
            {
                _client?.Dispose();
                _client = new StatusClient(endpoint);
            }

            var snapshot = await _client.GetStatusAsync();
            if (snapshot is null)
            {
                SetState("中微子 状态不可达", running: false);
                UpdateTargets(null);
                UpdateConsent(null);
                return;
            }

            SetState(
                $"中微子 {snapshot.Version} · {snapshot.TargetCount} 个 target",
                running: true);
            UpdateTargets(snapshot.Targets);
            UpdateConsent(snapshot.Consents);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateConsent(IReadOnlyList<StatusConsent>? consents)
    {
        if (_prompt is not null)
        {
            if (consents is null || !consents.Any(consent => consent.Id == _prompt.ConsentId))
            {
                _prompt.Close();
                _prompt = null;
            }

            return;
        }

        if (consents is null || consents.Count == 0 || _client is null)
        {
            return;
        }

        var consent = consents[0];
        var form = new ConsentPromptForm(
            consent,
            async allow => _client is not null && await _client.DecideConsentAsync(consent.Id, allow));
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_prompt, form))
            {
                _prompt = null;
            }
        };
        _prompt = form;
        form.Show();
    }

    private void UpdateTargets(IReadOnlyList<StatusTarget>? targets)
    {
        var previous = _targetsItem.DropDownItems.Cast<ToolStripItem>().ToArray();
        _targetsItem.DropDownItems.Clear();
        foreach (var item in previous)
        {
            item.Dispose();
        }

        if (targets is null)
        {
            _targetsItem.DropDownItems.Add(new ToolStripMenuItem("未连接") { Enabled = false });
            return;
        }

        if (targets.Count == 0)
        {
            _targetsItem.DropDownItems.Add(new ToolStripMenuItem("无 target") { Enabled = false });
            return;
        }

        foreach (var target in targets)
        {
            var label = string.IsNullOrWhiteSpace(target.Title) ? target.Id : target.Title;
            if (label.Length > 40)
            {
                label = label[..40];
            }

            var suffix = target.DegradedReason is null
                ? target.State
                : $"{target.State} · {target.DegradedReason}";
            var item = new ToolStripMenuItem($"{label} · {suffix}");
            var id = target.Id;
            var title = target.Title;
            item.Click += async (_, _) => await CloseTargetAsync(id, title);
            _targetsItem.DropDownItems.Add(item);
        }
    }

    private async Task CloseTargetAsync(string id, string title)
    {
        if (_client is null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            $"关闭 target「{title}」并还原窗口？",
            "中微子",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        var closed = await _client.CloseTargetAsync(id);
        _statusItem.Text = closed ? "已关闭 target" : "关闭 target 失败";
        await RefreshAsync();
    }

    private void SetState(string oneLine, bool running)
    {
        _statusItem.Text = oneLine;
        var tooltip = oneLine.Length <= 60 ? oneLine : oneLine[..60];
        if (_icon.Text != tooltip)
        {
            _icon.Text = tooltip;
        }

        _openItem.Enabled = running;
        _panicItem.Enabled = running;
    }

    private void OpenPage()
    {
        var url = _client?.PageUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.Host != "127.0.0.1")
        {
            return;
        }

        var result = ShellExecuteW(IntPtr.Zero, "open", uri.AbsoluteUri, null, null, 1);
        if (result.ToInt64() <= 32)
        {
            _statusItem.Text = "无法打开浏览器";
        }
    }

    private async void Panic()
    {
        if (_client is null)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            "关闭所有 target 并还原窗口？",
            "中微子 急停",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (confirmation != DialogResult.Yes)
        {
            return;
        }

        var result = await _client.PanicAsync();
        _statusItem.Text = result is null
            ? "急停失败：状态页不可达"
            : $"已关闭 {result.Closed} 个 target";

        await RefreshAsync();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr ShellExecuteW(
        IntPtr hwnd,
        string operation,
        string file,
        string? parameters,
        string? directory,
        int showCommand);
}
