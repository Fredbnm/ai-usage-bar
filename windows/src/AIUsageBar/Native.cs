using System.Runtime.InteropServices;
using System.Windows.Automation;
using AIUsageBar.Core;

namespace AIUsageBar;

/// <summary>The few Win32 calls the taskbar widget needs.</summary>
internal static class Native
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>The primary taskbar, or <see cref="IntPtr.Zero"/> (Explorer restarting).</summary>
    public static IntPtr Taskbar() => FindWindow("Shell_TrayWnd", null);

    public static PixelRect? RectOf(IntPtr window) =>
        window != IntPtr.Zero && GetWindowRect(window, out var r) ? new PixelRect(r.Left, r.Top, r.Right, r.Bottom) : null;

    /// <summary>The clock and tray icons, a child of the taskbar.</summary>
    public static PixelRect? TrayArea(IntPtr taskbar) =>
        taskbar == IntPtr.Zero ? null : RectOf(FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null));

    /// <summary>
    /// The taskbar's own buttons (Widgets, Start, Search, Task View, pinned and running apps),
    /// from UI Automation. Slow (tens of milliseconds): call it off the UI thread.
    /// Empty when the taskbar does not expose them (the widget then ignores them).
    /// </summary>
    public static IReadOnlyList<PixelRect> TaskbarItems(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero) return [];
        try
        {
            var buttons = AutomationElement.FromHandle(taskbar).FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var items = new List<PixelRect>();
            foreach (AutomationElement button in buttons)
            {
                var info = button.Current;
                var isTaskbarButton = info.ClassName == "Taskbar.TaskListButtonAutomationPeer"
                    || info.AutomationId is "StartButton" or "SearchButton" or "TaskViewButton" or "WidgetsButton";
                var bounds = info.BoundingRectangle;
                if (!isTaskbarButton || bounds.IsEmpty || double.IsInfinity(bounds.Width)) continue;
                items.Add(new PixelRect((int)bounds.Left, (int)bounds.Top, (int)bounds.Right, (int)bounds.Bottom));
            }
            return items;
        }
        catch (Exception)
        {
            // Explorer busy or restarting: no items this time.
            return [];
        }
    }

    /// <summary>Device pixels per device-independent pixel for the window's monitor.</summary>
    public static double Scale(IntPtr window)
    {
        var dpi = window == IntPtr.Zero ? 96 : GetDpiForWindow(window);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    /// <summary>0 when unknown; see <see cref="TaskbarLayout.ShouldHideForNotificationState"/>.</summary>
    public static int NotificationState() => SHQueryUserNotificationState(out var state) == 0 ? state : 0;

    /// <summary>Keeps the window above the taskbar without taking focus.</summary>
    public static void PlaceTopmost(IntPtr window, int x, int y, int width, int height) =>
        SetWindowPos(window, HwndTopmost, x, y, width, height, SwpNoActivate | SwpShowWindow);

    /// <summary>No Alt+Tab entry for the widget.</summary>
    public static void HideFromAltTab(IntPtr window) =>
        SetWindowLongPtr(window, GwlExStyle, new IntPtr(GetWindowLongPtr(window, GwlExStyle).ToInt64() | WsExToolWindow));
}
