using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIUsageBar.Core;
using static AIUsageBar.Core.Localization;
using Forms = System.Windows.Forms;

namespace AIUsageBar;

/// <summary>The popup that opens from the tray, anchored next to the taskbar.</summary>
public partial class PopupWindow : Window
{
    private readonly UsageStore store;
    private readonly Updater updater;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTime lastHidden = DateTime.MinValue;

    internal PopupWindow(UsageStore store, Updater updater)
    {
        InitializeComponent();
        this.store = store;
        this.updater = updater;
        updater.Changed += Refresh;
        Deactivated += (_, _) => HidePopup();
        // Keeps "Updated 2 min ago" and reset countdowns current while open.
        clock.Tick += (_, _) => Refresh();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private System.Drawing.Point anchor;

    public void Refresh()
    {
        if (!IsVisible) return;
        var height = ActualHeight;
        DataContext = PopupViewModel.Build(store, updater);
        // Another tab can be taller or shorter: keep the popup against the taskbar.
        UpdateLayout();
        if (Math.Abs(ActualHeight - height) > 0.5) PlaceNear(anchor);
    }

    /// <summary>Opens, or closes if already open. A provider's own tray icon opens on its tab.</summary>
    public void Toggle(ProviderId? provider = null)
    {
        if (IsVisible)
        {
            HidePopup();
            return;
        }
        // A click on the tray icon first deactivates (hides) the popup; don't reopen it.
        if (DateTime.Now - lastHidden < TimeSpan.FromMilliseconds(300)) return;
        if (provider is { } id && store.VisibleProviders.Contains(id) && store.Settings.OnboardingDismissed)
        {
            store.SelectProvider(id);
        }
        ShowNearCursor();
    }

    /// <summary>Left/Right arrows and Ctrl+Tab move between tabs.</summary>
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var direction = e.Key switch
        {
            System.Windows.Input.Key.Right => 1,
            System.Windows.Input.Key.Left => -1,
            System.Windows.Input.Key.Tab when System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) =>
                System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? -1 : 1,
            _ => 0,
        };
        if (direction == 0 || !store.ShowTabStrip) return;
        string[] keys = [.. store.VisibleProviders.Select(id => id.Key()), PopupTabs.AgentsKey];
        store.SelectTab(PopupTabs.Step(keys, store.CurrentTab, direction));
        e.Handled = true;
    }

    public void ShowNearCursor()
    {
        DataContext = PopupViewModel.Build(store, updater);

        // Show off-screen first so the height is known, then place it.
        Left = -10_000;
        Top = -10_000;
        Show();
        UpdateLayout();
        anchor = Forms.Cursor.Position;
        PlaceNear(anchor);

        Activate();
        clock.Start();
        _ = store.RefreshIfStaleAsync();
        updater.CheckIfStale();
    }

    /// <summary>Next to the taskbar, wherever it is docked, centred on the click.</summary>
    private void PlaceNear(System.Drawing.Point cursor)
    {
        var screen = Forms.Screen.FromPoint(cursor);
        var toDip = PresentationSource.FromVisual(this)!.CompositionTarget!.TransformFromDevice;
        var work = screen.WorkingArea;
        var bounds = screen.Bounds;
        var topLeft = toDip.Transform(new Point(work.Left, work.Top));
        var bottomRight = toDip.Transform(new Point(work.Right, work.Bottom));
        var pointer = toDip.Transform(new Point(cursor.X, cursor.Y));
        double width = ActualWidth, height = ActualHeight;
        const double margin = 4;

        double ClampX(double x) => Math.Clamp(x, topLeft.X + margin, Math.Max(topLeft.X + margin, bottomRight.X - width - margin));
        double ClampY(double y) => Math.Clamp(y, topLeft.Y + margin, Math.Max(topLeft.Y + margin, bottomRight.Y - height - margin));

        if (work.Top > bounds.Top)
        {
            (Left, Top) = (ClampX(pointer.X - width / 2), topLeft.Y + margin);          // taskbar at the top
        }
        else if (work.Left > bounds.Left)
        {
            (Left, Top) = (topLeft.X + margin, ClampY(pointer.Y - height / 2));        // on the left
        }
        else if (work.Right < bounds.Right)
        {
            (Left, Top) = (bottomRight.X - width - margin, ClampY(pointer.Y - height / 2)); // on the right
        }
        else
        {
            (Left, Top) = (ClampX(pointer.X - width / 2), bottomRight.Y - height - margin); // at the bottom
        }
    }

    private void HidePopup()
    {
        if (!IsVisible) return;
        Hide();
        lastHidden = DateTime.Now;
        clock.Stop();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SettingsButton, Placement = PlacementMode.Top };
        var settings = store.Settings;

        var style = new MenuItem { Header = T("Estilo na bandeja", "Tray style") };
        foreach (var option in Enum.GetValues<TrayStyle>())
        {
            style.Items.Add(Check(option.Label(), settings.Style == option, () => store.SetStyle(option)));
        }
        menu.Items.Add(style);

        var layout = new MenuItem { Header = T("Pop-up", "Popup") };
        foreach (var option in Enum.GetValues<PopupLayout>())
        {
            layout.Items.Add(Check(option.Label(), settings.Layout == option, () => store.SetLayout(option)));
        }
        menu.Items.Add(layout);

        var agents = new MenuItem { Header = T("Configurar agentes…", "Set up agents…") };
        agents.Click += (_, _) => store.ShowAgents();
        menu.Items.Add(agents);
        menu.Items.Add(TaskbarMenu.Submenu(store));

        foreach (var id in store.VisibleProviders)
        {
            var agent = new MenuItem { Header = id.DisplayName() };
            var tray = new MenuItem { Header = T("Na bandeja", "In the tray") };
            foreach (var option in TrayOptions.For(id))
            {
                tray.Items.Add(Check(option.Label(id), settings.DisplayFor(id) == option, () => store.SetDisplay(id, option)));
            }
            agent.Items.Add(tray);
            if (settings.Layout == PopupLayout.Tabs) agent.Items.Add(TabLimitMenu(id, T("Na aba", "In its tab")));
            menu.Items.Add(agent);
        }

        menu.Items.Add(Check(T("Cores de alerta na bandeja", "Alert colors in tray"), settings.ColorAlerts,
            () => store.SetColorAlerts(!settings.ColorAlerts)));
        menu.Items.Add(new Separator());

        var language = new MenuItem { Header = T("Idioma", "Language") };
        foreach (var option in Enum.GetValues<AppLanguage>())
        {
            language.Items.Add(Check(option.NativeName(), settings.LanguageValue == option, () => store.SetLanguage(option)));
        }
        menu.Items.Add(language);

        var startup = StartupRegistration.IsEnabled;
        menu.Items.Add(Check(T("Abrir ao iniciar o Windows", "Start with Windows"), startup,
            () => StartupRegistration.SetEnabled(!startup)));
        menu.Items.Add(new Separator());

        var checkUpdates = new MenuItem { Header = T("Procurar atualizações", "Check for updates") };
        checkUpdates.Click += async (_, _) => await updater.CheckAsync(manual: true);
        menu.Items.Add(checkUpdates);
        menu.Items.Add(new MenuItem { Header = T($"Versão {updater.CurrentText}", $"Version {updater.CurrentText}"), IsEnabled = false });
        menu.Items.Add(new Separator());

        var quit = new MenuItem { Header = T("Sair do medidor", "Quit") };
        quit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(quit);

        menu.IsOpen = true;
    }

    /// <summary>What an agent's tab shows: the fullest limit or any one of its limits.</summary>
    private MenuItem TabLimitMenu(ProviderId id, string header)
    {
        var item = new MenuItem { Header = header };
        foreach (var option in TabLimitItems(id)) item.Items.Add(option);
        return item;
    }

    private IEnumerable<MenuItem> TabLimitItems(ProviderId id)
    {
        var current = store.Settings.TabLimitFor(id);
        return store.TabLimitOptions(id).Select(key => Check(TabLimit.Title(key), key == current, () => store.SetTabLimit(id, key)));
    }

    /// <summary>Right-clicking an agent's tab picks what the tab shows.</summary>
    private void OnTabRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TabViewModel tab } element || PopupTabs.ProviderOf(tab.Key) is not { } id) return;
        var menu = new ContextMenu { PlacementTarget = element, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = T("Mostrar na aba", "Show in this tab"), IsEnabled = false });
        foreach (var option in TabLimitItems(id)) menu.Items.Add(option);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem Check(string header, bool isChecked, Action action)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Renders the popup off-screen to a PNG (used by <c>--snapshot</c> and CI).</summary>
    public void RenderToPng(string path, double scale = 2)
    {
        DataContext = PopupViewModel.Build(store, updater);
        ShowActivated = false;
        Left = -10_000;
        Top = -10_000;
        Show();
        UpdateLayout();
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(ActualWidth * scale), (int)Math.Ceiling(ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render((Visual)Content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        Hide();
    }
}
