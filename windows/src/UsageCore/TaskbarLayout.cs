namespace AIUsageBar.Core;

/// <summary>Where the taskbar widget sits.</summary>
public enum TaskbarPlacement
{
    /// <summary>Just before the clock and tray icons.</summary>
    LeftOfTray,
    /// <summary>At the left edge of the taskbar, next to Start and Search.</summary>
    LeftEdge,
}

/// <summary>What each agent shows in the taskbar widget.</summary>
public enum TaskbarStyle
{
    /// <summary>The agent's icon in a progress ring, and the percentage.</summary>
    RingAndPercent,
    /// <summary>Only the ringed icon; the numbers are in the hover card.</summary>
    RingOnly,
}

/// <summary>A rectangle in device pixels (what <c>GetWindowRect</c> returns).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? new PixelRect(left, top, left, top) : new PixelRect(left, top, right, bottom);
    }
}

/// <summary>
/// Where to put a small window over the Windows taskbar, and when to hide it.
/// Windows 11 has no supported way to add content to the taskbar, so the widget
/// is a borderless window drawn over free space; this keeps that arithmetic
/// free of UI so it can be tested.
/// </summary>
public static class TaskbarLayout
{
    /// <summary>Gap to the tray area or the taskbar edge, in device-independent pixels.</summary>
    public const double MarginDips = 8;

    /// <summary>The widget is for horizontal taskbars (Windows 11 has no other kind).</summary>
    public static bool IsHorizontal(PixelRect taskbar) => taskbar.Width > taskbar.Height;

    /// <summary>
    /// An auto-hidden taskbar slides almost out of the monitor: the widget would
    /// be left floating on the screen edge, so it hides with it.
    /// </summary>
    public static bool IsTaskbarHidden(PixelRect taskbar, PixelRect monitor)
    {
        if (taskbar.Height <= 0 || taskbar.Width <= 0) return true;
        var visible = taskbar.Intersect(monitor);
        return visible.Height < taskbar.Height * 0.8 || visible.Width < taskbar.Width * 0.8;
    }

    /// <summary>
    /// <c>SHQueryUserNotificationState</c>: 2 = a full-screen app is running (busy),
    /// 3 = a full-screen Direct3D game, 4 = presentation mode. Stay out of the way.
    /// </summary>
    public static bool ShouldHideForNotificationState(int state) => state is 2 or 3 or 4;

    /// <summary>
    /// Top-left of the widget in device pixels, vertically centred on the taskbar.
    /// <paramref name="offsetPixels"/> is the user's drag from the anchor (to the
    /// right is positive). When the tray area is unknown its width is estimated.
    /// <paramref name="items"/> are the taskbar's own buttons (Widgets, Start,
    /// Search, pinned apps…): <see cref="TaskbarPlacement.LeftEdge"/> uses the
    /// first free gap from the left instead of sitting on top of them.
    /// </summary>
    public static (int X, int Y) Place(
        PixelRect taskbar, PixelRect? tray, int width, int height,
        TaskbarPlacement placement, int offsetPixels, int marginPixels,
        IReadOnlyList<PixelRect>? items = null)
    {
        var y = taskbar.Top + (taskbar.Height - height) / 2;
        var anchor = placement switch
        {
            TaskbarPlacement.LeftEdge => FirstFreeGap(taskbar.Left + marginPixels, width, marginPixels, items),
            _ => TrayLeft(taskbar, tray) - marginPixels - width,
        };
        var x = Math.Clamp(anchor + offsetPixels, taskbar.Left, Math.Max(taskbar.Left, taskbar.Right - width));
        return (x, y);
    }

    /// <summary>
    /// Starting at <paramref name="x"/>, moves right past every item the widget
    /// would overlap. With a centred taskbar the gap is between Widgets and
    /// Start; with a left-aligned one it is after the last app.
    /// </summary>
    private static int FirstFreeGap(int x, int width, int margin, IReadOnlyList<PixelRect>? items)
    {
        if (items is null) return x;
        foreach (var item in items.Where(i => i.Width > 0).OrderBy(i => i.Left))
        {
            if (x < item.Right && x + width > item.Left) x = item.Right + margin;
        }
        return x;
    }

    private static int TrayLeft(PixelRect taskbar, PixelRect? tray) =>
        tray is { Width: > 0 } known && known.Left > taskbar.Left && known.Left < taskbar.Right
            ? known.Left
            : taskbar.Right - Math.Min(taskbar.Width / 4, 320);

    /// <summary>The percentage as the widget writes it: "64%", "&lt;1%", or "–" / "!" while loading / failed.</summary>
    public static string PercentText(TraySegment segment) =>
        segment.Number is "–" or "!" ? segment.Number : segment.Number + "%";
}
