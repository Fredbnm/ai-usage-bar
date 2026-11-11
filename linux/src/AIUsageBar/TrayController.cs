using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar;

/// <summary>
/// One tray icon per service, as on Windows. On Linux these are StatusNotifierItems (KDE, Cinnamon, Xfce,
/// Budgie, and GNOME with the AppIndicator extension, which Ubuntu ships). A click opens the popup;
/// the menu has Open, Refresh and Quit, since some panels open the menu on every click.
/// </summary>
internal sealed class TrayController : IDisposable
{
    /// <summary>Drawn larger than any panel shows it; the panel scales it down to its own size.</summary>
    private const int IconPixels = 48;

    private readonly UsageStore store;
    private readonly PopupWindow popup;
    private readonly TrayIcons all = [];
    private readonly Dictionary<ProviderId, TrayIcon> icons = [];
    private readonly Dictionary<ProviderId, string> drawn = [];
    private TrayIcon? placeholder;
    private AppLanguage? menuLanguage;

    public TrayController(UsageStore store, Updater updater)
    {
        this.store = store;
        popup = new PopupWindow(store, updater);
        TrayIcon.SetIcons(Application.Current!, all);
        store.Changed += Refresh;
        updater.Changed += popup.Refresh;
        Refresh();
    }

    public PopupWindow Popup => popup;

    public void Refresh()
    {
        var darkPanel = Theme.PanelIsDark(store.Settings.IconColor);
        var wanted = store.VisibleProviders
            .Select(id => (Id: id, Segment: TraySegment.Make(
                id, store.Settings.DisplayFor(id), store.State(id).Snapshot, store.State(id).Error?.MessageFor(id))))
            .Where(entry => entry.Segment is not null)
            .ToList();

        // The menus are rebuilt only when the language changes: re-exporting them on every refresh makes some panels flicker.
        var relabel = menuLanguage != Localization.Current;
        menuLanguage = Localization.Current;

        foreach (var id in icons.Keys.Except(wanted.Select(w => w.Id)).ToList())
        {
            RemoveIcon(id);
        }

        foreach (var (id, segment) in wanted)
        {
            if (!icons.TryGetValue(id, out var icon))
            {
                icon = CreateIcon(id);
                icons[id] = icon;
                all.Add(icon);
            }
            else if (relabel)
            {
                icon.Menu = BuildMenu();
            }
            var glyph = store.Settings.Style == TrayStyle.Icon ? InstalledApps.Glyph(id) : null;
            // Redraw only what changed: each new picture is sent to the panel.
            var key = $"{segment!.RingPercent}|{segment.Severity}|{segment.Number}|{store.Settings.Style}|{store.Settings.ColorAlerts}|{darkPanel}|{glyph is null}";
            if (!drawn.TryGetValue(id, out var previous) || previous != key)
            {
                using var bitmap = TrayIconRenderer.Render(segment, id, store.Settings.Style, glyph, store.Settings.ColorAlerts, darkPanel, IconPixels);
                icon.Icon = new WindowIcon(new MemoryStream(Images.Png(bitmap)));
                drawn[id] = key;
            }
            icon.ToolTipText = segment.Tooltip;
            icon.IsVisible = true;
        }

        // Every service hidden: keep one icon so the app stays reachable.
        if (wanted.Count == 0)
        {
            if (placeholder is null)
            {
                placeholder = CreateIcon(null);
                placeholder.Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AIUsageBar/Assets/AppIcon.png")));
                placeholder.ToolTipText = "AI Usage Bar";
                all.Add(placeholder);
            }
            else if (relabel)
            {
                placeholder.Menu = BuildMenu();
            }
            placeholder.IsVisible = true;
        }
        else if (placeholder is not null)
        {
            all.Remove(placeholder);
            placeholder.Dispose();
            placeholder = null;
        }

        popup.Refresh();
    }

    private TrayIcon CreateIcon(ProviderId? provider)
    {
        var icon = new TrayIcon { Menu = BuildMenu() };
        icon.Clicked += (_, _) => popup.Toggle(provider);
        return icon;
    }

    private NativeMenu BuildMenu()
    {
        NativeMenuItem Item(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            return item;
        }

        return
        [
            Item(T("Abrir", "Open"), () => popup.ShowNearPointer()),
            Item(T("Atualizar agora", "Refresh now"), () => _ = store.RefreshAsync(force: true)),
            new NativeMenuItemSeparator(),
            Item(T("Sair do medidor", "Quit"), Quit),
        ];
    }

    public static void Quit() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    private void RemoveIcon(ProviderId id)
    {
        if (icons.Remove(id, out var icon))
        {
            all.Remove(icon);
            icon.Dispose();
        }
        drawn.Remove(id);
    }

    public void Dispose()
    {
        store.Changed -= Refresh;
        foreach (var id in icons.Keys.ToList()) RemoveIcon(id);
        if (placeholder is not null)
        {
            all.Remove(placeholder);
            placeholder.Dispose();
        }
        popup.Close();
    }
}
