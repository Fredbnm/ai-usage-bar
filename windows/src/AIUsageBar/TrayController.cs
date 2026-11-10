using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using static AIUsageBar.Core.Localization;
using Forms = System.Windows.Forms;

namespace AIUsageBar;

/// <summary>
/// One tray icon per service (Windows tray icons are small squares, so the
/// services cannot share one like on macOS). Left click opens the popup,
/// right click a small menu.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly UsageStore store;
    private readonly PopupWindow popup;
    private readonly TaskbarWidgetWindow widget;
    private readonly Dictionary<ProviderId, Forms.NotifyIcon> icons = [];
    private readonly Dictionary<ProviderId, TrayIconImage> images = [];
    private Forms.NotifyIcon? placeholder;

    public TrayController(UsageStore store, Updater updater)
    {
        this.store = store;
        popup = new PopupWindow(store, updater);
        widget = new TaskbarWidgetWindow(store, id => popup.Toggle(id));
        store.Changed += Refresh;
        Refresh();
    }

    public void Refresh()
    {
        var size = Forms.SystemInformation.SmallIconSize.Width;
        var darkTaskbar = Theme.TaskbarIsDark;
        var wanted = store.VisibleProviders
            .Select(id => (Id: id, Segment: TraySegment.Make(
                id, store.Settings.DisplayFor(id), store.State(id).Snapshot, store.State(id).Error?.MessageFor(id))))
            .Where(entry => entry.Segment is not null)
            .ToList();

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
            }
            var glyph = store.Settings.Style == TrayStyle.Icon ? InstalledApps.Glyph(id) : null;
            using var bitmap = TrayIconRenderer.Render(segment, id, store.Settings.Style, glyph, store.Settings.ColorAlerts, darkTaskbar, size);
            var image = new TrayIconImage(bitmap);
            icon.Icon = image.Icon;
            icon.Text = segment!.Tooltip;
            icon.ContextMenuStrip = BuildMenu(icon.ContextMenuStrip);
            icon.Visible = true;
            if (images.Remove(id, out var previous)) previous.Dispose();
            images[id] = image;
        }

        // Every service hidden: keep one icon so the app stays reachable.
        if (wanted.Count == 0)
        {
            placeholder ??= CreateIcon();
            placeholder.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            placeholder.Text = "AI Usage Bar";
            placeholder.ContextMenuStrip = BuildMenu(placeholder.ContextMenuStrip);
            placeholder.Visible = true;
        }
        else if (placeholder is not null)
        {
            placeholder.Visible = false;
            placeholder.Dispose();
            placeholder = null;
        }

        popup.Refresh();
        widget.Refresh();
    }

    private Forms.NotifyIcon CreateIcon(ProviderId? provider = null)
    {
        var icon = new Forms.NotifyIcon();
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) popup.Toggle(provider);
        };
        return icon;
    }

    private Forms.ContextMenuStrip BuildMenu(Forms.ContextMenuStrip? previous)
    {
        previous?.Dispose();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(T("Abrir", "Open"), null, (_, _) => popup.ShowNearCursor());
        menu.Items.Add(T("Atualizar agora", "Refresh now"), null, async (_, _) => await store.RefreshAsync(force: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(T("Sair do medidor", "Quit"), null, (_, _) => System.Windows.Application.Current.Shutdown());
        return menu;
    }

    private void RemoveIcon(ProviderId id)
    {
        if (icons.Remove(id, out var icon))
        {
            icon.Visible = false;
            icon.ContextMenuStrip?.Dispose();
            icon.Dispose();
        }
        if (images.Remove(id, out var image)) image.Dispose();
    }

    public void Dispose()
    {
        store.Changed -= Refresh;
        widget.Shutdown();
        foreach (var id in icons.Keys.ToList()) RemoveIcon(id);
        if (placeholder is not null)
        {
            placeholder.Visible = false;
            placeholder.Dispose();
        }
        popup.Close();
    }
}
