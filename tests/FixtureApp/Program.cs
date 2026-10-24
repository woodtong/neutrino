using System.Drawing;
using System.Windows.Forms;

namespace FixtureApp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var dpiUnaware = args.Contains("--dpi-unaware", StringComparer.OrdinalIgnoreCase);
        Application.SetHighDpiMode(dpiUnaware ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var form = new FixtureForm();
        var x = GetIntArg(args, "--x");
        var y = GetIntArg(args, "--y");
        if (x is not null && y is not null)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(x.Value, y.Value);
        }

        Application.Run(form);
    }

    private static int? GetIntArg(string[] args, string name)
    {
        var index = Array.FindIndex(args, argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
            ? value
            : null;
    }
}

/// <summary>Deterministic WinForms target for capture/UIA/message tests.</summary>
internal sealed class FixtureForm : Form
{
    private readonly TextBox _input;
    private readonly Button _button;
    private readonly Label _status;
    private readonly Label _animation;
    private readonly ListBox _list;
    private readonly Panel _drag;
    private readonly Panel _wheel;
    private readonly System.Windows.Forms.Timer _timer;
    private int _clicks;
    private int _frame;
    private bool _dragging;
    private string _dragText = "drag idle";
    private string _wheelText = "wheel none";
    private string _keyText = "key none";
    private readonly List<string> _keyHistory = new();

    public FixtureForm()
    {
        Text = "GhostScreen Fixture";
        ClientSize = new Size(680, 420);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(120, 120);
        BackColor = Color.White;
        KeyPreview = true;
        KeyDown += (_, args) =>
        {
            _keyText = $"key {args.KeyCode}";
            _keyHistory.Add(_keyText);
        };

        _input = new TextBox
        {
            Name = "txtInput",
            AccessibleName = "Input",
            Left = 20,
            Top = 20,
            Width = 320,
            Text = "initial",
        };

        _button = new Button
        {
            Name = "btnClick",
            AccessibleName = "Click Me",
            Text = "Click Me",
            Left = 360,
            Top = 18,
            Width = 120,
            Height = 30,
        };

        _status = new Label
        {
            Name = "lblStatus",
            AccessibleName = "Status",
            Text = "status: idle",
            Left = 20,
            Top = 70,
            Width = 520,
            Height = 24,
            Font = new Font("Segoe UI", 10, FontStyle.Regular),
        };

        _button.Click += (_, _) =>
        {
            _clicks++;
            _status.Text = $"status: clicked {_clicks}";
        };

        _list = new ListBox
        {
            Name = "lstItems",
            Left = 20,
            Top = 110,
            Width = 300,
            Height = 200,
        };
        _list.Items.AddRange(["Alpha", "Bravo", "Charlie", "Delta", "Echo"]);

        _animation = new Label
        {
            Name = "lblAnimation",
            AccessibleName = "Animation",
            Text = "frame 0",
            Left = 360,
            Top = 110,
            Width = 260,
            Height = 120,
            BackColor = Color.LightSkyBlue,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
        };

        _drag = new Panel
        {
            Name = "pnlDrag",
            AccessibleName = "Drag area",
            Left = 360,
            Top = 245,
            Width = 260,
            Height = 75,
            BackColor = Color.LightGreen,
        };
        _drag.MouseDown += (_, args) =>
        {
            _dragging = true;
            _dragText = $"drag start {args.X},{args.Y}";
        };
        _drag.MouseMove += (_, args) =>
        {
            if (_dragging)
            {
                _dragText = $"drag {args.X},{args.Y}";
            }
        };
        _drag.MouseUp += (_, args) =>
        {
            _dragging = false;
            _dragText = $"drag done {args.X},{args.Y}";
        };

        _wheel = new Panel
        {
            Name = "pnlWheel",
            AccessibleName = "Wheel area",
            Left = 360,
            Top = 330,
            Width = 260,
            Height = 70,
            BackColor = Color.LightPink,
        };
        _wheel.MouseWheel += (_, args) =>
        {
            _wheelText = $"wheel {args.Delta}";
        };

        Controls.AddRange([_input, _button, _status, _list, _animation, _drag, _wheel, new Panel
        {
            Name = "pnlMarker",
            AccessibleName = "Marker",
            Left = 600,
            Top = 20,
            Width = 40,
            Height = 40,
            BackColor = Color.Magenta,
        }]);

        _timer = new System.Windows.Forms.Timer { Interval = 100 };
        _timer.Tick += (_, _) =>
        {
            _frame++;
            _animation.Text = $"frame {_frame}";
            _animation.BackColor = _frame % 2 == 0 ? Color.LightSkyBlue : Color.Khaki;
        };
        _timer.Start();
    }

    public string StatusText => _status.Text;

    public string InputText => _input.Text;

    public string DragText => _dragText;

    public string WheelText => _wheelText;

    public string KeyText => _keyText;

    public string KeyHistory => string.Join("|", _keyHistory);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return parameters;
        }
    }
}

/// <summary>Starts the fixture in-process on a dedicated STA thread (no visible launch flash).</summary>
public static class FixtureHost
{
    public static FixtureInstance Start(int? x = null, int? y = null) =>
        StartCore(x, y, dpiUnaware: false);

    /// <summary>Creates the window on a thread whose DPI context is unaware (for DPI tests).</summary>
    public static FixtureInstance StartDpiUnaware(int? x = null, int? y = null) =>
        StartCore(x, y, dpiUnaware: true);

    private static FixtureInstance StartCore(int? x, int? y, bool dpiUnaware)
    {
        FixtureForm? form = null;
        using var ready = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            if (dpiUnaware)
            {
                SetThreadDpiAwarenessContext(new IntPtr(-1));
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            form = new FixtureForm();
            if (x.HasValue && y.HasValue)
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(x.Value, y.Value);
            }

            form.Shown += (_, _) => ready.Set();
            Application.Run(form);
        })
        {
            IsBackground = true,
            Name = "FixtureApp-UI",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)) || form is null)
        {
            throw new TimeoutException("FixtureApp did not show a window.");
        }

        return new FixtureInstance(form);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}

public sealed class FixtureInstance : IDisposable
{
    private readonly FixtureForm _form;

    internal FixtureInstance(FixtureForm form) => _form = form;

    public IntPtr Handle => _form.Handle;

    public string Status => (string)_form.Invoke(new Func<string>(() => _form.StatusText));

    public string InputText => (string)_form.Invoke(new Func<string>(() => _form.InputText));

    public string DragText => (string)_form.Invoke(new Func<string>(() => _form.DragText));

    public string WheelText => (string)_form.Invoke(new Func<string>(() => _form.WheelText));

    public string KeyText => (string)_form.Invoke(new Func<string>(() => _form.KeyText));

    public string KeyHistory => (string)_form.Invoke(new Func<string>(() => _form.KeyHistory));

    /// <summary>Blocks the UI thread for a while to simulate a hung application.</summary>
    public void Hang(int milliseconds)
    {
        _form.BeginInvoke(new Action(() => Thread.Sleep(milliseconds)));
    }

    public void Close()
    {
        try
        {
            if (!_form.IsDisposed)
            {
                _form.Invoke(new Action(() => _form.Close()));
            }
        }
        catch
        {
            // Best effort.
        }
    }

    public void Dispose() => Close();
}
