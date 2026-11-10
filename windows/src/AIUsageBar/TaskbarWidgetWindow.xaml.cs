using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using static AIUsageBar.Core.Localization;
using Forms = System.Windows.Forms;

namespace AIUsageBar;

/// <summary>One agent in the widget: ringed official icon and, depending on the style, its percentage.</summary>
internal sealed record WidgetChip(
    ProviderId Id,
    ImageSource Icon,
    string Percent,
    Visibility PercentVisibility,
    Brush PercentBrush,
    Thickness ChipPadding,
    string Spoken);

internal sealed class TaskbarWidgetViewModel
{
    public required IReadOnlyList<WidgetChip> Chips { get; init; }
}

/// <summary>
/// A summary of every agent drawn over the Windows taskbar. Windows 11 has no
/// supported way to put content in the taskbar, so this is a small borderless
/// window kept above it, hidden for full-screen apps and with an auto-hidden
/// taskbar. Hover shows the details, a click opens the popup, a drag moves it
/// and a right click opens its options.
/// </summary>
public partial class TaskbarWidgetWindow : Window
{
    private const double IconDips = 24;
    /// <summary>The keep-in-place timer ticks once a second: rescan the taskbar buttons every 5 s.</summary>
    private const int ScanEveryTicks = 5;

    private readonly UsageStore store;
    private readonly Action<ProviderId> openPopup;
    private readonly IntPtr handle;
    private readonly DispatcherTimer keepInPlace = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer hoverOpen = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer hoverClose = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private ProviderId? hoverId;
    private FrameworkElement? hoverTarget;
    private bool taskbarOnTop;
    private IReadOnlyList<PixelRect> taskbarItems = [];
    private bool scanning;
    private int ticksSinceScan = ScanEveryTicks;

    private System.Windows.Point? pressStart;
    private double pressOffset;
    private bool dragging;

    internal TaskbarWidgetWindow(UsageStore store, Action<ProviderId> openPopup)
    {
        InitializeComponent();
        this.store = store;
        this.openPopup = openPopup;
        handle = new WindowInteropHelper(this).EnsureHandle();
        Native.HideFromAltTab(handle);

        keepInPlace.Tick += (_, _) => KeepInPlace();
        hoverOpen.Tick += (_, _) => { hoverOpen.Stop(); if (hoverTarget is not null && hoverId is { } id) ShowHover(hoverTarget, id); };
        hoverClose.Tick += (_, _) => CloseHover();
        HoverCard.MouseEnter += (_, _) => hoverClose.Stop();
        HoverCard.MouseLeave += (_, _) => hoverClose.Start();

        Pill.MouseMove += OnPillMouseMove;
        Pill.MouseLeave += (_, _) => { hoverOpen.Stop(); hoverClose.Start(); };
        Pill.PreviewMouseLeftButtonDown += OnPress;
        Pill.PreviewMouseLeftButtonUp += OnRelease;
        Pill.MouseRightButtonUp += OnContextMenu;
    }

    /// <summary>Rebuilds the chips from the store (usage changed, settings changed, theme changed).</summary>
    internal void Refresh()
    {
        if (!store.Settings.ShowTaskbarWidget || store.VisibleProviders.Count == 0)
        {
            HideWidget();
            return;
        }

        var scale = Native.Scale(Native.Taskbar());
        ApplyTheme();
        DataContext = new TaskbarWidgetViewModel { Chips = BuildChips(scale) };
        if (Hover.IsOpen && hoverId is { } id) HoverContent.Content = ProviderViewModel.Build(id, store.State(id));
        KeepInPlace();
        if (!keepInPlace.IsEnabled) keepInPlace.Start();
    }

    private void HideWidget()
    {
        keepInPlace.Stop();
        CloseHover();
        if (IsVisible) Hide();
    }

    // MARK: Look

    private static bool IsDark => Theme.TaskbarIsDark;

    private static Brush TextBrush(bool dark) => Frozen(dark ? Color.FromRgb(0xF5, 0xF5, 0xF5) : Color.FromRgb(0x1A, 0x1A, 0x1A));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void ApplyTheme()
    {
        var dark = IsDark;
        Resources["WidgetPill"] = Frozen(dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0x00, 0x00, 0x00));
        Resources["WidgetPillBorder"] = Frozen(dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        Resources["WidgetChipHover"] = Frozen(dark ? Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0x00, 0x00, 0x00));
        Resources["WidgetText"] = TextBrush(dark);
    }

    private IReadOnlyList<WidgetChip> BuildChips(double scale)
    {
        var dark = IsDark;
        var settings = store.Settings;
        var chips = new List<WidgetChip>();
        foreach (var id in store.VisibleProviders)
        {
            var state = store.State(id);
            // The widget is independent of the tray: an agent hidden there still shows here.
            var display = settings.DisplayFor(id) == TrayDisplay.Hidden ? TrayOptions.Default(id) : settings.DisplayFor(id);
            var segment = TraySegment.Make(id, display, state.Snapshot, state.Error?.MessageFor(id));
            if (segment is null) continue;

            using var ring = TrayIconRenderer.Render(
                segment, id, TrayStyle.Icon, InstalledApps.Glyph(id), settings.ColorAlerts, dark, (int)Math.Round(IconDips * scale));
            var withPercent = settings.TaskbarStyle == TaskbarStyle.RingAndPercent;
            var percent = TaskbarLayout.PercentText(segment);
            chips.Add(new WidgetChip(
                id,
                TabIcons.FromBitmap(ring, IconDips),
                percent,
                withPercent ? Visibility.Visible : Visibility.Collapsed,
                settings.ColorAlerts ? SeverityText(segment.Severity, dark) : TextBrush(dark),
                withPercent ? new Thickness(5, 0, 11, 0) : new Thickness(3, 0, 3, 0),
                $"{id.DisplayName()}, {percent}"));
        }
        return chips;
    }

    private static Brush SeverityText(Severity severity, bool dark) => severity switch
    {
        Severity.Warning => Frozen(Color.FromRgb(0xFF, 0x8C, 0x00)),
        Severity.Critical => Frozen(dark ? Color.FromRgb(0xFF, 0x5A, 0x65) : Color.FromRgb(0xE8, 0x11, 0x23)),
        _ => TextBrush(dark),
    };

    // MARK: Position

    /// <summary>
    /// Runs every second: follows the taskbar (monitor, size, DPI, Explorer
    /// restarts), stays above it, and steps aside for full-screen apps and an
    /// auto-hidden taskbar.
    /// </summary>
    private void KeepInPlace()
    {
        var taskbar = Native.Taskbar();
        if (!store.Settings.ShowTaskbarWidget || Native.RectOf(taskbar) is not { } bar || !TaskbarLayout.IsHorizontal(bar))
        {
            HideWidget();
            return;
        }
        var screen = Forms.Screen.FromHandle(taskbar).Bounds;
        var monitor = new PixelRect(screen.Left, screen.Top, screen.Right, screen.Bottom);
        if (TaskbarLayout.IsTaskbarHidden(bar, monitor) || TaskbarLayout.ShouldHideForNotificationState(Native.NotificationState()))
        {
            CloseHover();
            if (IsVisible) Hide();
            return;
        }

        var scale = Native.Scale(taskbar);
        ScanTaskbarItems(taskbar);
        Pill.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Pill.DesiredSize;
        if (size.Width <= 0) return;

        var (x, y) = TaskbarLayout.Place(
            bar, Native.TrayArea(taskbar),
            (int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
            store.Settings.TaskbarPlacement,
            (int)Math.Round(store.Settings.TaskbarOffset * scale),
            (int)Math.Round(TaskbarLayout.MarginDips * scale),
            taskbarItems);

        taskbarOnTop = bar.Top < monitor.Top + monitor.Height / 2;
        Width = size.Width;
        Height = size.Height;
        if (!IsVisible) Show();
        Native.PlaceTopmost(handle, x, y, (int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale));
    }

    /// <summary>
    /// Only the left-edge placement needs the taskbar's buttons. UI Automation is
    /// slow, so it runs in the background every few seconds and the next tick uses the result.
    /// </summary>
    private void ScanTaskbarItems(IntPtr taskbar)
    {
        if (store.Settings.TaskbarPlacement != TaskbarPlacement.LeftEdge)
        {
            taskbarItems = [];
            ticksSinceScan = ScanEveryTicks;
            return;
        }
        if (scanning || ++ticksSinceScan < ScanEveryTicks) return;
        scanning = true;
        ticksSinceScan = 0;
        _ = Task.Run(() => Native.TaskbarItems(taskbar)).ContinueWith(
            scan => Dispatcher.InvokeAsync(() =>
            {
                scanning = false;
                if (scan.IsCompletedSuccessfully) taskbarItems = scan.Result;
            }),
            TaskScheduler.Default);
    }

    // MARK: Hover card

    private static WidgetChip? ChipAt(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: "chip", DataContext: WidgetChip chip }) return chip;
        }
        return null;
    }

    private static FrameworkElement? ChipElement(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: "chip" } element) return element;
        }
        return null;
    }

    private void OnPillMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (pressStart is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            DragTo(start);
            return;
        }
        if (ChipAt(e.OriginalSource as DependencyObject) is not { } chip || ChipElement(e.OriginalSource as DependencyObject) is not { } element) return;

        hoverClose.Stop();
        hoverTarget = element;
        if (Hover.IsOpen)
        {
            if (hoverId != chip.Id) ShowHover(element, chip.Id);
        }
        else if (hoverId != chip.Id || !hoverOpen.IsEnabled)
        {
            hoverId = chip.Id;
            hoverOpen.Stop();
            hoverOpen.Start();
        }
    }

    private void ShowHover(FrameworkElement target, ProviderId id)
    {
        hoverId = id;
        HoverContent.Content = ProviderViewModel.Build(id, store.State(id));
        Hover.PlacementTarget = target;
        // Above a bottom taskbar, below a top one, so the card never covers the widget.
        Hover.Placement = taskbarOnTop ? PlacementMode.Bottom : PlacementMode.Top;
        Hover.IsOpen = true;
    }

    private void CloseHover()
    {
        hoverOpen.Stop();
        hoverClose.Stop();
        Hover.IsOpen = false;
        hoverId = null;
        hoverTarget = null;
    }

    // MARK: Click, drag, menu

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        CloseHover();
        pressStart = PointToScreen(e.GetPosition(this));
        pressOffset = store.Settings.TaskbarOffset;
        dragging = false;
        Pill.CaptureMouse();
    }

    private void DragTo(System.Windows.Point start)
    {
        var scale = Native.Scale(Native.Taskbar());
        var delta = (PointToScreen(Mouse.GetPosition(this)).X - start.X) / scale;
        if (!dragging && Math.Abs(delta) < 4) return;
        dragging = true;
        store.Settings.TaskbarOffset = pressOffset + delta;
        KeepInPlace();
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        var wasPressed = pressStart is not null;
        pressStart = null;
        Pill.ReleaseMouseCapture();
        if (!wasPressed) return;

        if (dragging)
        {
            dragging = false;
            store.SaveSettings();
        }
        else if (ChipAt(e.OriginalSource as DependencyObject) is { } chip)
        {
            openPopup(chip.Id);
        }
    }

    private void OnContextMenu(object sender, MouseButtonEventArgs e)
    {
        CloseHover();
        var menu = new ContextMenu { PlacementTarget = Pill, Placement = taskbarOnTop ? PlacementMode.Bottom : PlacementMode.Top };

        var refresh = new MenuItem { Header = T("Atualizar agora", "Refresh now") };
        refresh.Click += async (_, _) => await store.RefreshAsync(force: true);
        menu.Items.Add(refresh);
        menu.Items.Add(new Separator());
        TaskbarMenu.AddOptions(menu.Items, store, includeToggle: false);
        menu.Items.Add(new Separator());

        var hide = new MenuItem { Header = T("Ocultar da barra de tarefas", "Hide from the taskbar") };
        hide.Click += (_, _) => store.SetTaskbarWidget(false);
        menu.Items.Add(hide);
        menu.IsOpen = true;
        e.Handled = true;
    }

    internal void Shutdown()
    {
        keepInPlace.Stop();
        CloseHover();
        Close();
    }
}
