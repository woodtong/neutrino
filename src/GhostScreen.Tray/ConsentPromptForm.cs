using System.Drawing;
using System.Windows.Forms;
using GhostScreen.Core;

namespace GhostScreen.TrayApp;

/// <summary>
/// Non-activating consent prompt shown by the tray. It never takes focus; the
/// server auto-authorizes at the deadline, and the agent retries with consent_id.
/// </summary>
internal sealed class ConsentPromptForm : Form
{
    private readonly StatusConsent _consent;
    private readonly Func<bool, Task<bool>> _decide;
    private readonly Label _countdown;
    private readonly Button _allow;
    private readonly Button _deny;
    private readonly System.Windows.Forms.Timer _timer;
    private DateTimeOffset _deadline;
    private bool _deciding;
    private bool _closed;

    public ConsentPromptForm(StatusConsent consent, Func<bool, Task<bool>> decide)
    {
        _consent = consent;
        _decide = decide;
        _deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(0, consent.RemainingMs));

        Text = "中微子 · 协商请求";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(380, 154);
        BackColor = Color.FromArgb(248, 249, 251);
        Padding = new Padding(14);

        var title = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 24,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Text = "智能体请求操作",
        };

        var body = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 58,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            Text =
                $"Agent: {Short(consent.Agent, 28)}\r\n" +
                $"目标: {Short(consent.TargetTitle, 36)}\r\n" +
                $"动作: {consent.Action}",
        };

        _countdown = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 20,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            ForeColor = Color.DimGray,
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 38,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };

        _allow = new Button { Text = "允许", Width = 92, Height = 30 };
        _deny = new Button { Text = "拒绝", Width = 92, Height = 30 };
        _allow.Click += async (_, _) => await DecideAsync(allow: true);
        _deny.Click += async (_, _) => await DecideAsync(allow: false);
        buttons.Controls.Add(_allow);
        buttons.Controls.Add(_deny);

        Controls.Add(buttons);
        Controls.Add(_countdown);
        Controls.Add(body);
        Controls.Add(title);

        _timer = new System.Windows.Forms.Timer { Interval = 500 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        UpdateCountdown();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            parameters.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return parameters;
        }
    }

    public string ConsentId => _consent.Id;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task DecideAsync(bool allow)
    {
        if (_deciding || _closed)
        {
            return;
        }

        _deciding = true;
        _allow.Enabled = false;
        _deny.Enabled = false;
        _countdown.Text = allow ? "正在执行…" : "已拒绝";

        try
        {
            await _decide(allow);
        }
        catch
        {
            // The tray refresh will show the next state; never block the UI.
        }
        finally
        {
            _closed = true;
            Close();
        }
    }

    private void Tick()
    {
        if (_closed)
        {
            return;
        }

        if (DateTimeOffset.UtcNow >= _deadline)
        {
            _closed = true;
            Close();
            return;
        }

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        var seconds = Math.Max(0, (int)Math.Ceiling((_deadline - DateTimeOffset.UtcNow).TotalSeconds));
        _countdown.Text = $"若未操作，{seconds} 秒后自动允许";
    }

    private static string Short(string value, int max) =>
        string.IsNullOrWhiteSpace(value)
            ? "(unknown)"
            : value.Length <= max ? value : value[..max] + "…";
}
