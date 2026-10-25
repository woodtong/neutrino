using GhostScreen.Windows;

namespace GhostScreen.Input;

/// <summary>Classic window-message input. Only works for apps that process WM_* in their WndProc.</summary>
public static class MessageInput
{
    public static bool ClickButton(IntPtr parent, string title)
    {
        var children = WindowEnumerator.ChildrenLite(parent);
        var button = children.FirstOrDefault(c => c.ClassName.Contains("BUTTON", StringComparison.OrdinalIgnoreCase) &&
                                                  (string.IsNullOrEmpty(title) || c.Title.Equals(title, StringComparison.OrdinalIgnoreCase)))
                     ?? children.FirstOrDefault(c => c.ClassName.Contains("BUTTON", StringComparison.OrdinalIgnoreCase));

        if (button is null)
        {
            return false;
        }

        NativeMethods.SendMessage(button.Handle, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        return true;
    }

    public static bool SetEditText(IntPtr parent, string classNameHint, string text)
    {
        var edit = WindowEnumerator.ChildrenLite(parent)
            .FirstOrDefault(c => c.ClassName.Contains(classNameHint, StringComparison.OrdinalIgnoreCase));

        if (edit is null)
        {
            return false;
        }

        NativeMethods.SendMessage(edit.Handle, NativeMethods.WM_SETTEXT, IntPtr.Zero, text);
        return true;
    }

    /// <summary>
    /// Click the deepest child HWND at a screen point with window messages. Returns
    /// false when the point only resolves to the top-level window (for example a
    /// WPF/UWP surface), in which case the caller must not silently switch to UIA.
    /// </summary>
    public static bool TryClickChildAtScreenPoint(
        IntPtr topLevel,
        int screenX,
        int screenY,
        out string className)
    {
        className = string.Empty;
        var child = FindTargetWindow(topLevel, screenX, screenY);
        if (child is null)
        {
            return false;
        }

        className = child.ClassName;
        var target = child.Handle;

        if (!IsClickableClass(className))
        {
            return false;
        }

        if (className.Contains("BUTTON", StringComparison.OrdinalIgnoreCase))
        {
            NativeMethods.SendMessage(target, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return true;
        }

        var point = new NativeMethods.POINT { X = screenX, Y = screenY };
        if (!NativeMethods.ScreenToClient(target, ref point))
        {
            return false;
        }

        var lParam = (IntPtr)((point.Y << 16) | (point.X & 0xFFFF));
        NativeMethods.SendMessage(target, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam);
        NativeMethods.SendMessage(target, NativeMethods.WM_LBUTTONDOWN, (IntPtr)1, lParam);
        NativeMethods.SendMessage(target, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, lParam);
        return true;
    }

    /// <summary>
    /// Set text on the deepest EDIT/RICHEDIT child at a screen point. Returns false
    /// when there is no HWND-backed editable control at that point.
    /// </summary>
    public static bool TrySetChildTextAtScreenPoint(
        IntPtr topLevel,
        int screenX,
        int screenY,
        string text,
        out string className)
    {
        className = string.Empty;
        var child = FindTargetWindow(topLevel, screenX, screenY);
        if (child is null)
        {
            return false;
        }

        className = child.ClassName;
        var target = child.Handle;
        if (!className.Contains("EDIT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        NativeMethods.SendMessage(target, NativeMethods.WM_SETTEXT, IntPtr.Zero, text);
        return true;
    }

    public static bool TypeChars(IntPtr hwnd, string text)
    {
        foreach (var character in text)
        {
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_CHAR, (IntPtr)character, IntPtr.Zero);
        }

        return true;
    }

    /// <summary>
    /// Click a client-area point by posting WM_* to the deepest child window
    /// under that point. Works for classic Win32/WinForms/WPF controls; Chromium
    /// ignores these messages and should use UIA/CDP instead.
    /// </summary>
    public static bool ClickClientPoint(IntPtr topLevel, int clientX, int clientY)
    {
        if (clientX < 0 || clientY < 0)
        {
            return false;
        }


        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(topLevel, ref origin))
        {
            return false;
        }

        var screenX = origin.X + clientX;
        var screenY = origin.Y + clientY;
        var target = FindTargetWindow(topLevel, screenX, screenY)?.Handle ?? topLevel;

        var point = new NativeMethods.POINT { X = screenX, Y = screenY };
        if (!NativeMethods.ScreenToClient(target, ref point))
        {
            return false;
        }

        var packed = (point.Y << 16) | (point.X & 0xFFFF);
        var lParam = (IntPtr)packed;
        NativeMethods.SendMessage(target, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, lParam);
        NativeMethods.SendMessage(target, NativeMethods.WM_LBUTTONDOWN, (IntPtr)1, lParam);
        NativeMethods.SendMessage(target, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, lParam);
        return true;
    }

    public static bool ScrollClientPoint(IntPtr topLevel, int clientX, int clientY, int delta)
    {
        if (clientX < 0 || clientY < 0)
        {
            return false;
        }


        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(topLevel, ref origin))
        {
            return false;
        }

        var screenX = origin.X + clientX;
        var screenY = origin.Y + clientY;
        var target = FindTargetWindow(topLevel, screenX, screenY)?.Handle ?? topLevel;
        var lParam = (IntPtr)((screenY << 16) | (screenX & 0xFFFF));
        var wParam = (IntPtr)((delta & 0xFFFF) << 16);
        NativeMethods.SendMessage(target, NativeMethods.WM_MOUSEWHEEL, wParam, lParam);
        return true;
    }

    public static bool DragClientPoint(
        IntPtr topLevel,
        int startClientX,
        int startClientY,
        int endClientX,
        int endClientY,
        int durationMs)
    {
        if (startClientX < 0 || startClientY < 0 || endClientX < 0 || endClientY < 0)
        {
            return false;
        }


        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(topLevel, ref origin))
        {
            return false;
        }

        var startScreenX = origin.X + startClientX;
        var startScreenY = origin.Y + startClientY;
        var endScreenX = origin.X + endClientX;
        var endScreenY = origin.Y + endClientY;
        var target = FindTargetWindow(topLevel, startScreenX, startScreenY)?.Handle ?? topLevel;

        var start = new NativeMethods.POINT { X = startScreenX, Y = startScreenY };
        var end = new NativeMethods.POINT { X = endScreenX, Y = endScreenY };
        if (!NativeMethods.ScreenToClient(target, ref start) ||
            !NativeMethods.ScreenToClient(target, ref end))
        {
            return false;
        }

        var startParam = (IntPtr)((start.Y << 16) | (start.X & 0xFFFF));
        NativeMethods.SendMessage(target, NativeMethods.WM_MOUSEMOVE, IntPtr.Zero, startParam);
        NativeMethods.SendMessage(target, NativeMethods.WM_LBUTTONDOWN, (IntPtr)1, startParam);

        var duration = Math.Max(durationMs, 0);
        var steps = Math.Clamp(duration / 20, 4, 20);
        for (var i = 1; i <= steps; i++)
        {
            var x = start.X + (end.X - start.X) * i / steps;
            var y = start.Y + (end.Y - start.Y) * i / steps;
            var lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
            NativeMethods.SendMessage(target, NativeMethods.WM_MOUSEMOVE, (IntPtr)1, lParam);
            Thread.Sleep(duration / steps);
        }

        var endParam = (IntPtr)((end.Y << 16) | (end.X & 0xFFFF));
        NativeMethods.SendMessage(target, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, endParam);
        return true;
    }

    private static ChildWindowInfo? FindTargetWindow(IntPtr topLevel, int screenX, int screenY)
    {
        return WindowEnumerator.ChildrenLite(topLevel)
            .Where(window => window.Bounds.Contains(screenX, screenY))
            .OrderBy(window => (long)window.Bounds.Width * window.Bounds.Height)
            .FirstOrDefault();
    }

    private static bool IsClickableClass(string className) =>
        className.Contains("BUTTON", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("EDIT", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("COMBOBOX", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("LISTBOX", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("LISTVIEW", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("SCROLLBAR", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("TREEVIEW", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("TRACKBAR", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("TABCONTROL", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("TOOLBAR", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("UPDOWN", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("DATETIMEPICK", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Post a key/hotkey as WM_KEYDOWN/WM_KEYUP. Modifier state is delivered as
    /// messages, which classic WinForms/Win32 apps may honor but Chromium ignores;
    /// UIA menu invocation is more reliable for hotkeys where available.
    /// </summary>
    public static bool PressKey(IntPtr hwnd, string key, IReadOnlyList<string> modifiers)
    {
        var main = GetVirtualKey(key);
        if (main == 0)
        {
            return false;
        }


        var modifierKeys = new List<int>();
        foreach (var modifier in modifiers)
        {
            var vk = GetVirtualKey(modifier);
            if (vk != 0)
            {
                modifierKeys.Add(vk);
            }
        }

        foreach (var modifier in modifierKeys)
        {
            SendKey(hwnd, modifier, down: true);
        }

        SendKey(hwnd, main, down: true);
        SendKey(hwnd, main, down: false);

        for (var i = modifierKeys.Count - 1; i >= 0; i--)
        {
            SendKey(hwnd, modifierKeys[i], down: false);
        }

        return true;
    }

    private static void SendKey(IntPtr hwnd, int virtualKey, bool down)
    {
        var scan = NativeMethods.MapVirtualKey((uint)virtualKey, 0) & 0xFF;
        var extended = virtualKey is 0x2E or 0x25 or 0x26 or 0x27 or 0x28 or 0x23 or 0x24 or 0x21 or 0x22 or 0x2D;
        var lParam = 1 | ((int)scan << 16) | (extended ? 1 << 24 : 0);
        if (!down)
        {
            lParam |= (1 << 30) | (1 << 31);
        }

        NativeMethods.SendMessage(
            hwnd,
            down ? NativeMethods.WM_KEYDOWN : NativeMethods.WM_KEYUP,
            (IntPtr)virtualKey,
            (IntPtr)lParam);
    }

    private static int GetVirtualKey(string name)
    {
        var key = name.Trim();
        if (key.Length == 1)
        {
            var character = char.ToUpperInvariant(key[0]);
            if (character is >= 'A' and <= 'Z')
            {
                return character;
            }

            if (character is >= '0' and <= '9')
            {
                return character;
            }

            if (character == ' ')
            {
                return 0x20;
            }
        }

        if (key.StartsWith('F') && int.TryParse(key[1..], out var function) && function is >= 1 and <= 24)
        {
            return 0x70 + function - 1;
        }

        return key.ToUpperInvariant() switch
        {
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" or "BACK" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" or "INS" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PRIOR" => 0x21,
            "PAGEDOWN" or "NEXT" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            "SPACE" => 0x20,
            "CTRL" or "CONTROL" => 0x11,
            "SHIFT" => 0x10,
            "ALT" or "MENU" => 0x12,
            "WIN" or "LWIN" => 0x5B,
            _ => 0,
        };
    }
}
