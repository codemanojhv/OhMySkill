using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace OhMySkill;

public static class UiAutomationService
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    public static UiTarget? AtCursor()
    {
        try
        {
            if (!GetCursorPos(out var cursor)) return null;
            return FromElement(AutomationElement.FromPoint(new Point(cursor.X, cursor.Y)));
        }
        catch { return null; }
    }

    public static UiTarget? Focused()
    {
        try { return FromElement(AutomationElement.FocusedElement); }
        catch { return null; }
    }

    private static UiTarget? FromElement(AutomationElement? element)
    {
        if (element is null) return null;
        var current = element.Current;
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out var pid);
        var processName = "unknown";
        try { processName = Process.GetProcessById((int)pid).ProcessName; } catch { }
        var title = new StringBuilder(512);
        GetWindowText(window, title, title.Capacity);
        var ancestors = new List<string>();
        var node = TreeWalker.RawViewWalker.GetParent(element);
        for (var i = 0; i < 4 && node is not null; i++)
        {
            if (!string.IsNullOrWhiteSpace(node.Current.Name)) ancestors.Add(node.Current.Name);
            node = TreeWalker.RawViewWalker.GetParent(node);
        }
        var bounds = current.BoundingRectangle;
        return new UiTarget(
            processName,
            title.ToString(),
            current.Name,
            current.AutomationId,
            current.ControlType?.ProgrammaticName,
            current.ClassName,
            current.HelpText,
            ancestors,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            current.IsPassword,
            current.IsEnabled);
    }
}
