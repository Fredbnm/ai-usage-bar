using System.Windows.Controls;
using AIUsageBar.Core;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar;

/// <summary>The taskbar widget's options, shared by the gear menu and the widget's own right-click menu.</summary>
internal static class TaskbarMenu
{
    /// <summary>The gear menu's "Taskbar widget" submenu.</summary>
    public static MenuItem Submenu(UsageStore store)
    {
        var root = new MenuItem { Header = T("Widget na barra de tarefas", "Taskbar widget") };
        AddOptions(root.Items, store, includeToggle: true);
        return root;
    }

    public static void AddOptions(ItemCollection items, UsageStore store, bool includeToggle)
    {
        var settings = store.Settings;
        if (includeToggle)
        {
            items.Add(Check(T("Mostrar na barra de tarefas", "Show on the taskbar"), settings.ShowTaskbarWidget,
                () => store.SetTaskbarWidget(!settings.ShowTaskbarWidget)));
            items.Add(new Separator());
        }

        var position = new MenuItem { Header = T("Posição", "Position") };
        position.Items.Add(Check(T("À esquerda da bandeja do sistema", "Left of the system tray"),
            settings.TaskbarPlacement == TaskbarPlacement.LeftOfTray, () => store.SetTaskbarPlacement(TaskbarPlacement.LeftOfTray)));
        position.Items.Add(Check(T("Na borda esquerda, junto do Iniciar e da Pesquisa", "Left edge, next to Start and Search"),
            settings.TaskbarPlacement == TaskbarPlacement.LeftEdge, () => store.SetTaskbarPlacement(TaskbarPlacement.LeftEdge)));
        items.Add(position);

        var style = new MenuItem { Header = T("Aparência", "Style") };
        style.Items.Add(Check(T("Ícone com anel e percentual", "Icon with ring and percentage"),
            settings.TaskbarStyle == TaskbarStyle.RingAndPercent, () => store.SetTaskbarStyle(TaskbarStyle.RingAndPercent)));
        style.Items.Add(Check(T("Só o ícone com anel", "Icon with ring only"),
            settings.TaskbarStyle == TaskbarStyle.RingOnly, () => store.SetTaskbarStyle(TaskbarStyle.RingOnly)));
        items.Add(style);

        var reset = new MenuItem { Header = T("Restaurar posição", "Reset position"), IsEnabled = Math.Abs(settings.TaskbarOffset) > 0.5 };
        reset.Click += (_, _) => store.ResetTaskbarOffset();
        items.Add(reset);
    }

    private static MenuItem Check(string header, bool isChecked, Action action)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }
}
