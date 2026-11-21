using System.Drawing;
using System.Windows.Forms;
using GhostScreen.Core;

namespace GhostScreen.NotifierApp;

/// <summary>
/// The one window the notifier shows: a non-activating card with a title, a few
/// lines of body text, optional actions and an optional countdown with a bar.
/// The consent prompt is just a <see cref="NotifierPrompt"/> with three actions —
/// keeping one form means a broken MCP path and a consent ticket look and behave
/// the same to the human.
/// </summary>
internal sealed class PromptForm : Form
{
    private const int BaseWidth = 380;
    private const int BaseButtonWidth = 92;
    private const int BaseButtonHeight = 30;
    private const int BasePadding = 14;
    private const int BaseBarHeight = 6;

    private readonly NotifierPrompt _prompt;
    private readonly Label _body;
    private readonly Label _countdown;
    private readonly Panel _track;
    private readonly Panel _fill;
    private readonly TableLayoutPanel _status;
    private readonly TableLayoutPanel _layout;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly DateTimeOffset _deadline;
    private readonly bool _hasCountdown;
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _finishing;

    public PromptForm(NotifierPrompt prompt)
    {
        _prompt = prompt;
        _hasCountdown = prompt.TimeoutMs > 0 && !string.IsNullOrWhiteSpace(prompt.CountdownFormat);
        _deadline = DateTimeOffset.UtcNow.AddMilliseconds(prompt.TimeoutMs);

        Text = prompt.Title;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(248, 249, 251);
        Padding = new Padding(BasePadding);
        AutoScaleMode = AutoScaleMode.None; // FitToContent scales by DeviceDpi instead
        ClientSize = new Size(BaseWidth, 210);
        KeyPreview = true;

        var title = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Text = prompt.Title,
            ForeColor = SeverityColor(prompt.Severity),
            Margin = new Padding(0, 0, 0, 2),
        };

        _body = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            Margin = new Padding(0),
            MaximumSize = new Size(BaseWidth - (2 * BasePadding), 0),
            Text = prompt.Body,
        };

        _countdown = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 9, FontStyle.Regular),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 6, 0, 0),
        };

        // A slim bar rather than a ProgressBar: no theme chrome, no minimum height,
        // and it scales cleanly with the rest of the layout.
        _track = new Panel
        {
            BackColor = Color.FromArgb(228, 231, 236),
            Height = BaseBarHeight,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 4, 0, 0),
        };
        _fill = new Panel
        {
            BackColor = SeverityColor(prompt.Severity),
            Height = BaseBarHeight,
            Width = 0,
            Dock = DockStyle.Left,
        };
        _track.Controls.Add(_fill);

        _status = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0),
            Visible = _hasCountdown,
        };
        _status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _status.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _status.RowStyles.Add(new RowStyle(SizeType.Absolute, BaseBarHeight));
        _status.Controls.Add(_countdown, 0, 0);
        _status.Controls.Add(_track, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 10, 0, 0),
        };

        var ids = prompt.ActionIds ?? [];
        var labels = prompt.ActionLabels ?? [];
        if (ids.Length == 0 || ids.Length != labels.Length)
        {
            buttons.Controls.Add(MakeButton(NotifierText.Dismiss, () => Finish(null), primary: true));
        }
        else
        {
            // Right-to-left flow: the first control added sits rightmost, which is
            // where the primary action (last in the list) belongs.
            for (var i = ids.Length - 1; i >= 0; i--)
            {
                var id = ids[i];
                buttons.Controls.Add(MakeButton(
                    labels[i],
                    () => Finish(id),
                    primary: i == ids.Length - 1));
            }
        }

        _layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0),
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (var i = 0; i < 4; i++)
        {
            _layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        _layout.Controls.Add(title, 0, 0);
        _layout.Controls.Add(_body, 0, 1);
        _layout.Controls.Add(_status, 0, 2);
        _layout.Controls.Add(buttons, 0, 3);
        Controls.Add(_layout);

        _timer = new System.Windows.Forms.Timer { Interval = 250 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        UpdateCountdown();

        // Moving to a monitor with a different scale re-runs the layout: the metrics
        // are multiplied by DeviceDpi, so the old size no longer matches the new font.
        DpiChanged += (_, _) =>
        {
            FitToContent();
            PositionBottomRight();
        };
    }

    /// <summary>Completes with the chosen action, or null when dismissed.</summary>
    public Task<string?> Completion => _completion.Task;

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

    protected override void OnShown(EventArgs e)
    {
        FitToContent();
        base.OnShown(e);
        PositionBottomRight();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        Finish(null, force: true);
        base.OnFormClosed(e);
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Finish(null);
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    private Button MakeButton(string text, Action onClick, bool primary)
    {
        var button = new Button
        {
            Text = text,
            Width = BaseButtonWidth,
            Height = BaseButtonHeight,
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = primary ? FlatStyle.System : FlatStyle.Standard,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Finish(string? action, bool force = false)
    {
        if (_finishing && !force)
        {
            return;
        }

        _finishing = true;
        _completion.TrySetResult(action);
        if (!IsDisposed && Visible)
        {
            Close();
        }
    }

    private void Tick()
    {
        if (!_hasCountdown)
        {
            return;
        }

        if (DateTimeOffset.UtcNow >= _deadline)
        {
            Finish(string.IsNullOrWhiteSpace(_prompt.TimeoutAction) ? null : _prompt.TimeoutAction);
            return;
        }

        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        if (!_hasCountdown)
        {
            return;
        }

        var remaining = _deadline - DateTimeOffset.UtcNow;
        var seconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        _countdown.Text = string.Format(_prompt.CountdownFormat!, seconds);

        var total = Math.Max(1, _prompt.TimeoutMs);
        var fraction = Math.Clamp(remaining.TotalMilliseconds / total, 0d, 1d);
        _fill.Width = (int)Math.Round(_track.ClientSize.Width * fraction);
    }

    private void FitToContent()
    {
        var scale = Math.Max(1f, DeviceDpi / 96f);
        var width = (int)Math.Round(BaseWidth * scale);
        var inner = width - (int)Math.Round(2 * BasePadding * scale);

        Padding = new Padding((int)Math.Round(BasePadding * scale));
        _body.MaximumSize = new Size(inner, 0);

        foreach (Control control in _layout.Controls)
        {
            if (control is FlowLayoutPanel panel)
            {
                foreach (Control child in panel.Controls)
                {
                    if (child is Button button)
                    {
                        button.Width = (int)Math.Round(BaseButtonWidth * scale);
                        button.Height = (int)Math.Round(BaseButtonHeight * scale);
                    }
                }
            }
        }

        _status.RowStyles[1].Height = (int)Math.Round(BaseBarHeight * scale);
        _track.Height = (int)Math.Round(BaseBarHeight * scale);
        _fill.Height = (int)Math.Round(BaseBarHeight * scale);

        var preferred = _layout.GetPreferredSize(new Size(inner, 0));
        ClientSize = new Size(width, preferred.Height + Padding.Vertical);
        UpdateCountdown();
    }

    private void PositionBottomRight()
    {
        // Show the card on the screen the human is actually looking at: the one with
        // the foreground window, else the one under the pointer. Defaulting to the
        // primary screen put the card on a monitor the reader might not be watching.
        var area = ActiveScreen().WorkingArea;
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    private static Screen ActiveScreen()
    {
        var foreground = GetForegroundWindow();
        return foreground != IntPtr.Zero
            ? Screen.FromHandle(foreground)
            : Screen.FromPoint(Cursor.Position);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private static Color SeverityColor(NotifierSeverity severity) => severity switch
    {
        NotifierSeverity.Error => Color.FromArgb(185, 28, 28),
        NotifierSeverity.Warning => Color.FromArgb(180, 83, 9),
        _ => Color.FromArgb(0, 120, 212),
    };
}
