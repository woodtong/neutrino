using System.Windows.Automation;
using GhostScreen.Core;

namespace GhostScreen.Input;

public sealed record UiaElement(
    string Ref,
    string Name,
    string AutomationId,
    string Role,
    Rect Bounds,
    bool Enabled,
    bool Focused,
    string[] Patterns)
{
    public override string ToString() =>
        $"{Ref} {Role} name=\"{Name}\" id=\"{AutomationId}\" bounds={Bounds} enabled={Enabled} focused={Focused} patterns=[{string.Join(",", Patterns)}]";
}

/// <summary>Read/write the UIA tree without requiring foreground focus.</summary>
public static class UiaDriver
{
    public static IReadOnlyList<UiaElement> Snapshot(IntPtr hwnd, int maxNodes = 200)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var result = new List<UiaElement>();
        var index = 0;

        foreach (AutomationElement element in elements)
        {
            if (result.Count >= maxNodes)
            {
                break;
            }

            try
            {
                var current = element.Current;
                var role = current.ControlType?.ProgrammaticName?.Replace("ControlType.", string.Empty) ?? "Unknown";
                var bounds = current.BoundingRectangle;
                var name = current.Name ?? string.Empty;
                var automationId = current.AutomationId ?? string.Empty;

                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(automationId))
                {
                    continue;
                }

                result.Add(new UiaElement(
                    $"e{index++}",
                    name,
                    automationId,
                    role,
                    new Rect((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height),
                    current.IsEnabled,
                    current.HasKeyboardFocus,
                    GetPatterns(element)));
            }
            catch (ElementNotAvailableException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                // The tree changed while walking it; return what we have.
                break;
            }
        }

        return result;
    }

    public static UiaElement? FindByName(IntPtr hwnd, string name, TreeScope scope = TreeScope.Descendants)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var element = root.FindFirst(scope, new PropertyCondition(AutomationElement.NameProperty, name));
        return element is null ? null : ToElement(element);
    }

    public static UiaElement? FindByAutomationId(IntPtr hwnd, string automationId, TreeScope scope = TreeScope.Descendants)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var element = root.FindFirst(scope, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        return element is null ? null : ToElement(element);
    }

    public static bool Invoke(IntPtr hwnd, string name)
    {
        var element = FindRawByName(hwnd, name)
            ?? throw new ElementNotAvailableException($"UIA element not found: {name}");

        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException($"Element \"{name}\" does not support InvokePattern.");
        }

        ((InvokePattern)pattern).Invoke();
        return true;
    }

    public static bool InvokeByAutomationId(IntPtr hwnd, string automationId)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var element = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

        if (element is null)
        {
            throw new ElementNotAvailableException($"UIA element not found: {automationId}");
        }

        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException($"Element \"{automationId}\" does not support InvokePattern.");
        }

        ((InvokePattern)pattern).Invoke();
        return true;
    }

    /// <summary>
    /// Perform the best available semantic action for an element: Invoke, Toggle,
    /// SelectionItem, or ExpandCollapse. Returns false when the element exposes
    /// none of them (the caller should report ELEMENT_NOT_ACTIONABLE).
    /// </summary>
    public static bool Activate(IntPtr hwnd, UiaElement element)
    {
        var raw = FindRaw(hwnd, element)
            ?? throw new ElementNotAvailableException(
                $"UIA element not found: {element.Name} ({element.AutomationId})");

        return ActivateRaw(raw);
    }

    /// <summary>
    /// Find the smallest actionable element containing a screen point. Works for
    /// off-screen single-HWND surfaces (WPF/UWP) where window-message hit testing
    /// has no child HWND. Callers can gate the action before activating the
    /// returned element.
    /// </summary>
    public static AutomationElement? FindActionAtPoint(IntPtr hwnd, int screenX, int screenY)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        AutomationElement? best = null;
        var bestArea = double.MaxValue;
        var inspected = 0;

        foreach (AutomationElement element in elements)
        {
            if (++inspected > 1_000)
            {
                break;
            }

            try
            {
                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty ||
                    screenX < bounds.Left ||
                    screenX >= bounds.Right ||
                    screenY < bounds.Top ||
                    screenY >= bounds.Bottom)
                {
                    continue;
                }

                var area = bounds.Width * bounds.Height;
                if (area >= bestArea || !HasAction(element))
                {
                    continue;
                }

                best = element;
                bestArea = area;
            }
            catch (ElementNotAvailableException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                // The tree changed while walking it; stop with what we have.
                break;
            }
        }

        return best;
    }

    /// <summary>Activate a specific raw element found by <see cref="FindActionAtPoint"/>.</summary>
    public static bool ActivateElement(AutomationElement element) => ActivateRaw(element);

    private static bool HasAction(AutomationElement element)
    {
        try
        {
            return element.TryGetCurrentPattern(InvokePattern.Pattern, out _) ||
                   element.TryGetCurrentPattern(TogglePattern.Pattern, out _) ||
                   element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _) ||
                   element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool ActivateRaw(AutomationElement raw)
    {
        if (raw.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return true;
        }

        if (raw.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
            return true;
        }

        if (raw.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            return true;
        }

        if (raw.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        {
            var pattern = (ExpandCollapsePattern)expand;
            if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            {
                pattern.Expand();
            }
            else
            {
                pattern.Collapse();
            }

            return true;
        }

        return false;
    }

    private static AutomationElement? FindRaw(IntPtr hwnd, UiaElement element)
    {
        var root = AutomationElement.FromHandle(hwnd);
        return !string.IsNullOrWhiteSpace(element.AutomationId)
            ? root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, element.AutomationId))
            : root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, element.Name));
    }

    public static bool SetValue(IntPtr hwnd, string automationId, string value)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var element = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

        if (element is null)
        {
            throw new ElementNotAvailableException($"UIA element not found: {automationId}");
        }

        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException($"Element \"{automationId}\" does not support ValuePattern.");
        }

        ((ValuePattern)pattern).SetValue(value);
        return true;
    }

    public static AutomationElement? FindRawByName(IntPtr hwnd, string name)
    {
        var root = AutomationElement.FromHandle(hwnd);
        return root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name));
    }

    private static UiaElement ToElement(AutomationElement element)
    {
        var current = element.Current;
        var bounds = current.BoundingRectangle;
        return new UiaElement(
            "e0",
            current.Name ?? string.Empty,
            current.AutomationId ?? string.Empty,
            current.ControlType?.ProgrammaticName?.Replace("ControlType.", string.Empty) ?? "Unknown",
            new Rect((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height),
            current.IsEnabled,
            current.HasKeyboardFocus,
            GetPatterns(element));
    }

    private static string[] GetPatterns(AutomationElement element)
    {
        var patterns = new List<string>();

        if (Supports(element, InvokePattern.Pattern)) patterns.Add("Invoke");
        if (Supports(element, ValuePattern.Pattern)) patterns.Add("Value");
        if (Supports(element, TextPattern.Pattern)) patterns.Add("Text");
        if (Supports(element, TogglePattern.Pattern)) patterns.Add("Toggle");
        if (Supports(element, SelectionItemPattern.Pattern)) patterns.Add("SelectionItem");
        if (Supports(element, ExpandCollapsePattern.Pattern)) patterns.Add("ExpandCollapse");
        if (Supports(element, ScrollPattern.Pattern)) patterns.Add("Scroll");

        return patterns.ToArray();
    }

    private static bool Supports(AutomationElement element, AutomationPattern pattern)
    {
        try
        {
            return element.TryGetCurrentPattern(pattern, out _);
        }
        catch
        {
            return false;
        }
    }
}
