using AIUsageBar.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar;

/// <summary>The popup that opens from the tray, placed next to the panel the tray lives in.</summary>
public partial class PopupWindow : Window
{
    private const double EdgeMargin = 4;

    private readonly UsageStore store;
    private readonly Updater updater;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTime lastHidden = DateTime.MinValue;
    private PixelPoint? anchor;
    private bool menuOpen;

    /// <summary>Shown above the tabs until the popup is closed, e.g. that the desktop has no tray.</summary>
    internal string? Notice { get; set; }

    /// <summary>For the XAML previewer only.</summary>
    public PopupWindow() : this(new UsageStore(new AppSettings { IsTransient = true }, Array.Empty<ProviderSnapshot>()), new Updater(current: null))
    {
    }

    internal PopupWindow(UsageStore store, Updater updater)
    {
        InitializeComponent();
        this.store = store;
        this.updater = updater;
        Deactivated += (_, _) =>
        {
            // The settings menu is a popup of its own; it takes the focus while it is open.
            if (!menuOpen) HidePopup();
        };
        Opened += (_, _) => FitTransparency();
        // Keeps "Updated 2 min ago" and reset countdowns current while open.
        clock.Tick += (_, _) => Refresh();
        KeyDown += OnKeyDown;
        // Fluent scroll bars float over the content: make room for one when the agents do not fit.
        Body.ScrollChanged += (_, _) =>
            BodyContent.Margin = new Thickness(0, 0, Body.Extent.Height > Body.Viewport.Height + 0.5 ? 12 : 0, 0);
        // Another tab can be taller or shorter, and the first layout may differ from the estimate:
        // keep the popup against the panel whenever its real size changes.
        SizeChanged += (_, _) =>
        {
            if (IsVisible) Place(anchor);
        };
    }

    public void Refresh()
    {
        if (!IsVisible) return;
        DataContext = PopupViewModel.Build(store, updater, Notice);
    }

    /// <summary>Opens, or closes if already open. A provider's own tray icon opens on its tab.</summary>
    public void Toggle(ProviderId? provider = null)
    {
        if (IsVisible)
        {
            HidePopup();
            return;
        }
        // Clicking the tray icon first takes the focus away (which hides the popup); don't reopen it.
        if (DateTime.Now - lastHidden < TimeSpan.FromMilliseconds(400)) return;
        if (provider is { } id && store.VisibleProviders.Contains(id) && store.Settings.OnboardingDismissed)
        {
            store.SelectProvider(id);
        }
        ShowNearPointer();
    }

    public void ShowNearPointer()
    {
        anchor = X11Pointer.Position();
        DataContext = PopupViewModel.Build(store, updater, Notice);
        LimitHeight(anchor);
        // Placed before it is shown, from the measured height, so it does not jump; corrected once laid out.
        Place(anchor, measure: true);
        Show();
        Activate();
        Dispatcher.UIThread.Post(() => { if (IsVisible) Place(anchor); }, DispatcherPriority.Loaded);
        clock.Start();
        _ = store.RefreshIfStaleAsync();
        updater.CheckIfStale();
    }

    /// <summary>Without a compositor there is no transparency: drop the shadow margin and the rounded corners.</summary>
    private void FitTransparency()
    {
        if (ActualTransparencyLevel != WindowTransparencyLevel.None) return;
        Card.Margin = new Thickness(0);
        Card.CornerRadius = new CornerRadius(0);
        Card.BoxShadow = default;
        Background = Card.Background;
    }

    private Avalonia.Platform.Screen? ScreenAt(PixelPoint? point) =>
        (point is { } p ? Screens.ScreenFromPoint(p) : null) ?? Screens.Primary ?? Screens.All.FirstOrDefault();

    /// <summary>
    /// Keeps the popup within the screen: the agents scroll and the header, tabs and settings button stay
    /// visible. Room is left for the header, the update banner, the tab strip and the footer.
    /// </summary>
    private void LimitHeight(PixelPoint? point)
    {
        if (ScreenAt(point) is not { } screen) return;
        Body.MaxHeight = Math.Max(240, screen.WorkingArea.Height / screen.Scaling - 240);
    }

    /// <summary>
    /// Next to the panel, wherever it is docked, centred on the click when the pointer is known (X11).
    /// The panel is found from the screen's work area; when the desktop does not report one, from where the
    /// pointer is, and without either (Wayland) at the top right on GNOME and the bottom right elsewhere.
    /// </summary>
    private void Place(PixelPoint? pointer, bool measure = false)
    {
        if (ScreenAt(pointer) is not { } screen) return;
        var scale = screen.Scaling;
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;

        Size size;
        if (measure)
        {
            Card.Measure(new Size(Width, double.PositiveInfinity));
            size = new Size(Width, Card.DesiredSize.Height);
        }
        else
        {
            size = Bounds.Size;
        }
        var width = (int)Math.Ceiling(size.Width * scale);
        var height = (int)Math.Ceiling(size.Height * scale);
        var margin = (int)(EdgeMargin * scale);
        // The window's own transparent margin around the card already keeps it off the edges.
        if (Card.Margin.Left > 0) margin = 0;

        int ClampX(int x) => Math.Clamp(x, work.X + margin, Math.Max(work.X + margin, work.Right - width - margin));
        int ClampY(int y) => Math.Clamp(y, work.Y + margin, Math.Max(work.Y + margin, work.Bottom - height - margin));
        var x = pointer is { } p ? ClampX(p.X - width / 2) : work.Right - width - margin;

        PixelPoint position;
        if (work.Y > bounds.Y) position = new(x, work.Y + margin);                                       // panel at the top
        else if (work.Bottom < bounds.Bottom) position = new(x, work.Bottom - height - margin);           // at the bottom
        else if (work.X > bounds.X) position = new(work.X + margin, pointer is { } l ? ClampY(l.Y - height / 2) : work.Y + margin); // left
        else if (work.Right < bounds.Right) position = new(work.Right - width - margin, pointer is { } r ? ClampY(r.Y - height / 2) : work.Y + margin); // right
        else if (pointer is { } q) position = new(x, q.Y < bounds.Y + bounds.Height / 2 ? work.Y + margin : work.Bottom - height - margin);
        else position = new(x, AIUsageBar.Theme.IsGnome ? work.Y + margin : work.Bottom - height - margin);
        Position = position;
    }

    private void HidePopup()
    {
        if (!IsVisible) return;
        Hide();
        lastHidden = DateTime.Now;
        clock.Stop();
        Notice = null;
    }

    /// <summary>Esc closes; Left/Right arrows and Ctrl+Tab move between tabs.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePopup();
            e.Handled = true;
            return;
        }
        var direction = e.Key switch
        {
            Key.Right => 1,
            Key.Left => -1,
            Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Control) => e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1,
            _ => 0,
        };
        if (direction == 0 || !store.ShowTabStrip) return;
        string[] keys = [.. store.VisibleProviders.Select(id => id.Key()), PopupTabs.AgentsKey];
        store.SelectTab(PopupTabs.Step(keys, store.CurrentTab, direction));
        e.Handled = true;
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var settings = store.Settings;
        var menu = new ContextMenu { Placement = PlacementMode.TopEdgeAlignedRight };

        var style = Submenu(T("Estilo na bandeja", "Tray style"));
        foreach (var option in Enum.GetValues<TrayStyle>())
        {
            style.Items.Add(Check(option.Label(), settings.Style == option, () => store.SetStyle(option)));
        }
        menu.Items.Add(style);

        var color = Submenu(T("Cor dos ícones na bandeja", "Tray icon color"));
        foreach (var option in Enum.GetValues<TrayIconColor>())
        {
            color.Items.Add(Check(option.Label(), settings.IconColor == option, () => store.SetIconColor(option)));
        }
        menu.Items.Add(color);

        var layout = Submenu(T("Pop-up", "Popup"));
        foreach (var option in Enum.GetValues<PopupLayout>())
        {
            layout.Items.Add(Check(option.Label(), settings.Layout == option, () => store.SetLayout(option)));
        }
        menu.Items.Add(layout);

        menu.Items.Add(Action(T("Configurar agentes…", "Set up agents…"), store.ShowAgents));

        foreach (var id in store.VisibleProviders)
        {
            var agent = Submenu(id.DisplayName());
            var tray = Submenu(T("Na bandeja", "In the tray"));
            foreach (var option in TrayOptions.For(id))
            {
                tray.Items.Add(Check(option.Label(id), settings.DisplayFor(id) == option, () => store.SetDisplay(id, option)));
            }
            agent.Items.Add(tray);
            if (settings.Layout == PopupLayout.Tabs)
            {
                var tab = Submenu(T("Na aba", "In its tab"));
                foreach (var option in TabLimitItems(id)) tab.Items.Add(option);
                agent.Items.Add(tab);
            }
            menu.Items.Add(agent);
        }

        menu.Items.Add(Check(T("Cores de alerta na bandeja", "Alert colors in tray"), settings.ColorAlerts,
            () => store.SetColorAlerts(!settings.ColorAlerts)));
        menu.Items.Add(new Separator());

        var language = Submenu(T("Idioma", "Language"));
        foreach (var option in Enum.GetValues<AppLanguage>())
        {
            language.Items.Add(Check(option.NativeName(), settings.LanguageValue == option, () => store.SetLanguage(option)));
        }
        menu.Items.Add(language);

        var startup = Autostart.IsEnabled;
        menu.Items.Add(Check(T("Abrir ao entrar no sistema", "Start at login"), startup, () => Autostart.SetEnabled(!startup)));
        menu.Items.Add(new Separator());

        menu.Items.Add(Action(T("Procurar atualizações", "Check for updates"), () => _ = updater.CheckAsync(manual: true)));
        menu.Items.Add(new MenuItem { Header = T($"Versão {updater.CurrentText}", $"Version {updater.CurrentText}"), IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Action(T("Sair do medidor", "Quit"), TrayController.Quit));

        OpenMenu(menu, SettingsButton);
    }

    /// <summary>Right-clicking an agent's tab picks what the tab shows.</summary>
    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || sender is not Control { Tag: string key } element
            || PopupTabs.ProviderOf(key) is not { } id) return;
        var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
        menu.Items.Add(new MenuItem { Header = T("Mostrar na aba", "Show in this tab"), IsEnabled = false });
        foreach (var option in TabLimitItems(id)) menu.Items.Add(option);
        OpenMenu(menu, element);
        e.Handled = true;
    }

    private void OpenMenu(ContextMenu menu, Control target)
    {
        menuOpen = true;
        menu.Closed += (_, _) =>
        {
            menuOpen = false;
            // Clicked outside both the menu and the popup: close the popup too.
            Dispatcher.UIThread.Post(() => { if (!IsActive) HidePopup(); }, DispatcherPriority.Background);
        };
        menu.Open(target);
    }

    private IEnumerable<MenuItem> TabLimitItems(ProviderId id)
    {
        var current = store.Settings.TabLimitFor(id);
        return store.TabLimitOptions(id).Select(key => Check(TabLimit.Title(key), key == current, () => store.SetTabLimit(id, key)));
    }

    private static MenuItem Submenu(string header) => new() { Header = header };

    private static MenuItem Action(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Check(string header, bool isChecked, Action action)
    {
        var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Renders the popup to a PNG at twice its size (used by <c>--snapshot</c> and CI, without a display).</summary>
    internal void RenderToPng(string path, double scale = 2)
    {
        DataContext = PopupViewModel.Build(store, updater, Notice);
        Body.MaxHeight = 760;
        Show();
        Avalonia.Headless.HeadlessWindowExtensions.SetRenderScaling(this, scale);

        // The headless frame has no transparency: render on black and on white and work the alpha out from the
        // difference, so the rounded corners and the shadow come out as on a real desktop.
        using var onBlack = Capture(Brushes.Black);
        using var onWhite = Capture(Brushes.White);
        Hide();
        Background = Brushes.Transparent;

        using var result = new SKBitmap(new SKImageInfo(onBlack.Width, onBlack.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var y = 0; y < onBlack.Height; y++)
        for (var x = 0; x < onBlack.Width; x++)
        {
            var b = onBlack.GetPixel(x, y);
            var w = onWhite.GetPixel(x, y);
            var alpha = 255 - ((w.Red - b.Red) + (w.Green - b.Green) + (w.Blue - b.Blue)) / 3.0;
            alpha = Math.Clamp(alpha, 0, 255);
            byte Channel(byte black) => alpha <= 0 ? (byte)0 : (byte)Math.Clamp(Math.Round(black * 255.0 / alpha), 0, 255);
            result.SetPixel(x, y, new SKColor(Channel(b.Red), Channel(b.Green), Channel(b.Blue), (byte)Math.Round(alpha)));
        }
        File.WriteAllBytes(path, Images.Png(result));
    }

    private SKBitmap Capture(IBrush background)
    {
        Background = background;
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(this)
            ?? throw new InvalidOperationException("Nothing was rendered");
        using var png = new MemoryStream();
        frame.Save(png, PngBitmapEncoderOptions.Default);
        return SKBitmap.Decode(png.ToArray());
    }
}
