using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIUsageBar.Core;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar;

/// <summary>Plain snapshots of the store for the popup, rebuilt on every change.</summary>
internal sealed class PopupViewModel
{
    public required string Title { get; init; }
    public required string UpdatedText { get; init; }
    public required string RefreshTip { get; init; }
    public required string SettingsTip { get; init; }
    public required Visibility RefreshVisibility { get; init; }
    public required Visibility LoadingVisibility { get; init; }
    public required ICommand RefreshCommand { get; init; }
    /// <summary>Only the selected agent: the popup shows one agent at a time.</summary>
    public required IReadOnlyList<ProviderViewModel> Providers { get; init; }
    public required IReadOnlyList<TabViewModel> Tabs { get; init; }
    public required TabViewModel AgentsTab { get; init; }
    public required Visibility TabStripVisibility { get; init; }
    public required AgentsViewModel Agents { get; init; }
    public required Visibility AgentsVisibility { get; init; }

    public static PopupViewModel Build(UsageStore store)
    {
        var loading = store.IsLoading;
        var current = store.CurrentTab;
        var onAgents = current == PopupTabs.AgentsKey;
        return new PopupViewModel
        {
            Tabs = TabViewModel.Build(store, current),
            AgentsTab = TabViewModel.AgentsTab(store, current),
            TabStripVisibility = store.ShowTabStrip ? Visibility.Visible : Visibility.Collapsed,
            Agents = AgentsViewModel.Build(store),
            AgentsVisibility = onAgents ? Visibility.Visible : Visibility.Collapsed,
            Title = T("Uso de IA", "AI Usage"),
            UpdatedText = store.LastUpdated is { } updated ? UsageFormat.Updated(updated) : T("Ainda sem dados", "No data yet"),
            RefreshTip = T("Atualizar agora", "Refresh now"),
            SettingsTip = T("Opções", "Options"),
            RefreshVisibility = loading ? Visibility.Collapsed : Visibility.Visible,
            LoadingVisibility = loading ? Visibility.Visible : Visibility.Collapsed,
            RefreshCommand = new RelayCommand(async () => await store.RefreshAsync(force: true)),
            Providers = [.. store.VisibleProviders
                .Where(id => !onAgents && id.Key() == current)
                .Select(id => ProviderViewModel.Build(id, store.State(id)))],
        };
    }
}

/// <summary>One tab in the strip: the agent's icon, name and its fullest limit.</summary>
internal sealed record TabViewModel(
    string Key,
    ImageSource? Icon,
    double IconOpacity,
    string Name,
    Visibility NameVisibility,
    string StatusText,
    Brush StatusBrush,
    Brush UnderlineBrush,
    FontWeight NameWeight,
    string AutomationName,
    ICommand SelectCommand)
{
    /// <summary>With this many agents or more, only the selected tab keeps its name, so the strip never overflows.</summary>
    private const int NamesFitUpTo = 3;

    public static IReadOnlyList<TabViewModel> Build(UsageStore store, string current)
    {
        var tabs = new List<TabViewModel>();
        var iconColor = ((SolidColorBrush)Resource("PrimaryText")).Color;
        var showAllNames = store.VisibleProviders.Count <= NamesFitUpTo;
        foreach (var id in store.VisibleProviders)
        {
            var state = store.State(id);
            var summary = TabSummary.Make(state.Snapshot, state.Error is not null);
            var isCurrent = id.Key() == current;
            var spoken = summary.Text is "!" or "–"
                ? T("sem dados", "no data")
                : T($"{summary.Text} usado", $"{summary.Text} used");
            tabs.Add(new TabViewModel(
                id.Key(),
                TabIcons.Get(id, iconColor),
                isCurrent ? 1 : 0.65,
                id.Descriptor().ShortName,
                showAllNames || isCurrent ? Visibility.Visible : Visibility.Collapsed,
                summary.IsWarning && summary.Text != "!" ? summary.Text + " ⚠" : summary.Text,
                summary.Text == "–" ? Resource("SecondaryText") : LimitViewModel.SeverityBrush(summary.IsWarning ? Severity.Warning : summary.Severity),
                isCurrent ? Resource("AccentBrush") : Brushes.Transparent,
                isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                $"{id.DisplayName()}, {spoken}",
                new RelayCommand(() => store.SelectTab(id.Key()))));
        }
        return tabs;
    }

    /// <summary>The trailing "＋" tab that opens the Agents tab; narrower than the agent tabs.</summary>
    public static TabViewModel AgentsTab(UsageStore store, string current)
    {
        var onAgents = current == PopupTabs.AgentsKey;
        return new TabViewModel(
            PopupTabs.AgentsKey,
            null,
            1,
            "＋",
            Visibility.Visible,
            "",
            Resource("SecondaryText"),
            onAgents ? Resource("AccentBrush") : Brushes.Transparent,
            onAgents ? FontWeights.SemiBold : FontWeights.Normal,
            T("Agentes e conexões", "Agents and connections"),
            new RelayCommand(() => store.SelectTab(PopupTabs.AgentsKey)));
    }

    internal static Brush Resource(string key) => (Brush)System.Windows.Application.Current.Resources[key];
}

/// <summary>The Agents tab: every supported agent, whether it is connected, and first-run help.</summary>
internal sealed class AgentsViewModel
{
    public required string Heading { get; init; }
    public required string Welcome { get; init; }
    public required Visibility WelcomeVisibility { get; init; }
    public required IReadOnlyList<AgentRowViewModel> Rows { get; init; }
    public required string DoneText { get; init; }
    public required Visibility DoneVisibility { get; init; }
    public required ICommand DoneCommand { get; init; }

    public static AgentsViewModel Build(UsageStore store)
    {
        var firstRun = !store.Settings.OnboardingDismissed;
        var rows = store.CurrentTab == PopupTabs.AgentsKey ? store.AgentRows() : [];
        return new AgentsViewModel
        {
            Heading = T("AGENTES DE IA", "AI AGENTS"),
            Welcome = T(
                "Aqui estão os agentes que o medidor sabe ler. Os que já têm login neste PC aparecem sozinhos; para os outros, siga a dica.",
                "These are the agents the meter can read. The ones already signed in on this PC show up on their own; for the others, follow the hint."),
            WelcomeVisibility = firstRun ? Visibility.Visible : Visibility.Collapsed,
            Rows = [.. rows.Select(row => AgentRowViewModel.Build(store, row))],
            // After the first run the same button goes back, which matters with
            // one agent, where there is no tab strip to leave this tab by.
            DoneText = firstRun ? T("Concluir", "Done") : T("Voltar", "Back"),
            DoneVisibility = Visibility.Visible,
            DoneCommand = new RelayCommand(store.DismissOnboarding),
        };
    }
}

internal sealed record AgentRowViewModel(
    string Name,
    string Status,
    string Hint,
    Visibility HintVisibility,
    string Icon,
    Brush IconBrush,
    bool IsShown,
    Visibility ToggleVisibility,
    string ToggleText,
    ICommand ToggleCommand)
{
    public static AgentRowViewModel Build(UsageStore store, AgentRow row)
    {
        var descriptor = row.Id.Descriptor();
        var connected = row.Status == SetupStatus.Connected;
        return new AgentRowViewModel(
            descriptor.DisplayName,
            connected
                ? T("Conectado", "Connected") + (row.Plan is { } plan ? $" · {plan}" : "")
                : T("Login não encontrado", "No login found"),
            descriptor.SetupHint(),
            connected ? Visibility.Collapsed : Visibility.Visible,
            connected ? "" : "",
            LimitViewModel.SeverityBrush(connected ? Severity.Normal : Severity.Warning),
            !row.Hidden,
            connected ? Visibility.Visible : Visibility.Collapsed,
            T("Mostrar", "Show"),
            new RelayCommand(() => store.SetProviderHidden(row.Id, !row.Hidden)));
    }
}

internal sealed class ProviderViewModel
{
    public required string Name { get; init; }
    public required string? Plan { get; init; }
    public required Visibility PlanVisibility { get; init; }
    public required string OpenTip { get; init; }
    public required ICommand OpenCommand { get; init; }
    public required string? ErrorText { get; init; }
    public required Visibility ErrorVisibility { get; init; }
    public required IReadOnlyList<LimitViewModel> Limits { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required string BreakdownHeader { get; init; }
    public required IReadOnlyList<BreakdownViewModel> Breakdown { get; init; }
    public required Visibility BreakdownVisibility { get; init; }
    public required string LoadingText { get; init; }
    public required Visibility LoadingVisibility { get; init; }

    public static ProviderViewModel Build(ProviderId id, ProviderState state)
    {
        var snapshot = state.Snapshot;
        var breakdown = snapshot?.Breakdown ?? [];
        return new ProviderViewModel
        {
            Name = id.DisplayName(),
            Plan = snapshot?.PlanName,
            PlanVisibility = snapshot?.PlanName is null ? Visibility.Collapsed : Visibility.Visible,
            OpenTip = id.Descriptor().OpenTip(),
            OpenCommand = new RelayCommand(() => InstalledApps.Open(id)),
            ErrorText = state.Error?.MessageFor(id),
            ErrorVisibility = state.Error is null ? Visibility.Collapsed : Visibility.Visible,
            Limits = [.. (snapshot?.Items ?? []).Select(LimitViewModel.Build)],
            Notes = [.. (snapshot?.Notes ?? []).Select(note => "ⓘ  " + note.Text)],
            BreakdownHeader = T("ONDE FOI O USO DA SEMANA", "WHERE THIS WEEK’S USAGE WENT"),
            Breakdown = [.. breakdown.Select(row => new BreakdownViewModel(row.Name, UsageFormat.Percent(row.Percent), row.Percent / 100))],
            BreakdownVisibility = breakdown.Count == 0 ? Visibility.Collapsed : Visibility.Visible,
            LoadingText = T("Carregando…", "Loading…"),
            LoadingVisibility = snapshot is null && state.Error is null ? Visibility.Visible : Visibility.Collapsed,
        };
    }
}

internal sealed record LimitViewModel(string Title, string PercentText, double Fraction, Brush Brush, string ResetText, Visibility ResetVisibility)
{
    public static LimitViewModel Build(LimitItem item) => new(
        item.Title,
        UsageFormat.Percent(item.Percent),
        Math.Min(item.Percent / 100, 1),
        SeverityBrush(item.Severity),
        item.ResetsAt is { } reset ? UsageFormat.Reset(reset) : "",
        item.ResetsAt is null ? Visibility.Collapsed : Visibility.Visible);

    internal static Brush SeverityBrush(Severity severity)
    {
        var key = severity switch
        {
            Severity.Warning => "WarningBrush",
            Severity.Critical => "CriticalBrush",
            _ => "AccentBrush",
        };
        return (Brush)System.Windows.Application.Current.Resources[key];
    }
}

internal sealed record BreakdownViewModel(string Name, string PercentText, double Fraction);

internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
