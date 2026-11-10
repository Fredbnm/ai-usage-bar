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
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTime lastHidden = DateTime.MinValue;

    internal PopupWindow(UsageStore store)
    {
        InitializeComponent();
        this.store = store;
        Deactivated += (_, _) => HidePopup();
        // Keeps "Updated 2 min ago" and reset countdowns current while open.
        clock.Tick += (_, _) => Refresh();
    }

    public void Refresh()
    {
        if (IsVisible) DataContext = PopupViewModel.Build(store);
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            HidePopup();
            return;
        }
        // A click on the tray icon first deactivates (hides) the popup; don't reopen it.
        if (DateTime.Now - lastHidden < TimeSpan.FromMilliseconds(300)) return;
        ShowNearCursor();
    }

    public void ShowNearCursor()
    {
        DataContext = PopupViewModel.Build(store);

        // Show off-screen first so the height is known, then place it.
        Left = -10_000;
        Top = -10_000;
        Show();
        UpdateLayout();
        PlaceNear(Forms.Cursor.Position);

        Activate();
        clock.Start();
        _ = store.RefreshIfStaleAsync();
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

        foreach (var id in store.VisibleProviders)
        {
            var item = new MenuItem { Header = id.DisplayName() + T(" na bandeja", " in tray") };
            foreach (var option in TrayOptions.For(id))
            {
                item.Items.Add(Check(option.Label(id), settings.DisplayFor(id) == option, () => store.SetDisplay(id, option)));
            }
            menu.Items.Add(item);
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

        var quit = new MenuItem { Header = T("Sair do medidor", "Quit") };
        quit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(quit);

        menu.IsOpen = true;
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
        DataContext = PopupViewModel.Build(store);
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
