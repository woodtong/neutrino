using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using GhostScreen.Windows;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfLabel = System.Windows.Controls.Label;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace GhostScreen.Tests;

/// <summary>
/// Off-screen WPF target used to exercise the UIA fallback: WPF controls have
/// no child HWNDs, so element actions cannot use window messages.
/// </summary>
public sealed class WpfFixture : IDisposable
{
    private Window _window = null!;
    private WpfTextBox _input = null!;
    private WpfLabel _label = null!;
    private WpfCheckBox _check = null!;
    private WpfListBox _list = null!;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _handle;
    private int _clicks;
    private string _text = "initial";
    private bool _checked;
    private int _selected = -1;

    public WpfFixture()
    {
        var display = DisplayInfo.Get();
        var left = display.VirtualScreen.Right + 200;
        var top = display.PrimaryMonitor.Y + 200;

        _thread = new Thread(() =>
        {
            _window = new Window
            {
                Title = "WPF Fixture",
                Left = left,
                Top = top,
                Width = 360,
                Height = 440,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };

            var panel = new WpfStackPanel { Margin = new Thickness(20) };
            var button = new WpfButton
            {
                Content = "Wpf Click",
                Width = 160,
                Height = 40,
                HorizontalAlignment = WpfHorizontalAlignment.Left,
            };
            AutomationProperties.SetAutomationId(button, "wpfButton");
            button.Click += (_, _) => _clicks++;

            _input = new WpfTextBox
            {
                Text = "initial",
                Width = 240,
                Height = 30,
                Margin = new Thickness(0, 20, 0, 0),
                HorizontalAlignment = WpfHorizontalAlignment.Left,
            };
            AutomationProperties.SetAutomationId(_input, "wpfInput");
            _input.TextChanged += (_, _) => _text = _input.Text;

            _check = new WpfCheckBox
            {
                Content = "Wpf Check",
                Margin = new Thickness(0, 20, 0, 0),
            };
            AutomationProperties.SetAutomationId(_check, "wpfCheck");
            _check.Checked += (_, _) => _checked = true;
            _check.Unchecked += (_, _) => _checked = false;

            _list = new WpfListBox
            {
                Width = 200,
                Height = 100,
                Margin = new Thickness(0, 20, 0, 0),
            };
            _list.Items.Add("One");
            _list.Items.Add("Two");
            _list.Items.Add("Three");
            AutomationProperties.SetAutomationId(_list, "wpfList");
            _list.SelectionChanged += (_, _) => _selected = _list.SelectedIndex;

            _label = new WpfLabel
            {
                Content = "Wpf Label",
                Margin = new Thickness(0, 20, 0, 0),
            };
            AutomationProperties.SetAutomationId(_label, "wpfLabel");

            panel.Children.Add(button);
            panel.Children.Add(_input);
            panel.Children.Add(_check);
            panel.Children.Add(_list);
            panel.Children.Add(_label);
            _window.Content = panel;
            _window.Loaded += (_, _) =>
            {
                _handle = new WindowInteropHelper(_window).Handle;
                _ready.Set();
            };

            _window.Show();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WpfFixture-UI",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || _handle == IntPtr.Zero)
        {
            throw new TimeoutException("WPF fixture did not show a window.");
        }
    }

    public IntPtr Handle => _handle;

    public int Clicks => (int)_window.Dispatcher.Invoke(() => _clicks);

    public string Text => (string)_window.Dispatcher.Invoke(() => _text);

    public bool Checked => (bool)_window.Dispatcher.Invoke(() => _checked);

    public int SelectedIndex => (int)_window.Dispatcher.Invoke(() => _selected);

    public void Dispose()
    {
        try
        {
            if (!_window.Dispatcher.HasShutdownStarted)
            {
                _window.Dispatcher.Invoke(() => _window.Close());
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
