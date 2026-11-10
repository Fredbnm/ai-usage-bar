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
    public required UpdateBannerViewModel Update { get; init; }

    public static PopupViewModel Build(UsageStore store, Updater updater)
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
            Update = UpdateBannerViewModel.Build(updater),
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
    Visibility StatusVisibility,
    double Width,
    string StatusText,
    Brush StatusBrush,
    Brush UnderlineBrush,
    FontWeight NameWeight,
    string AutomationName,
    ICommand SelectCommand)
{
    /// <summary>Room for the agent tabs in the strip, in device-independent pixels (the popup's 308 minus the "＋" tab).</summary>
    private const double StripWidth = 278;

    /// <summary>A tab that shows only its icon and percentage.</summary>
    private const double CompactWidth = 46;

    /// <summary>What a tab needs for icon + name + percentage, and for icon + name.</summary>
    private const double FullWidth = 110;
    private const double NameWidth = 72;

    /// <summary>
    /// Up to three agents share the strip equally and show everything. With more, the unselected tabs shrink to
    /// icon + percentage and the selected one takes the rest: icon, name and percentage if they fit, else the name
    /// without the percentage (the card below has it), else just icon + percentage.
    /// </summary>
    private static (double Width, bool Name, bool Status) Layout(int count, bool selected)
    {
        if (count <= 3) return (StripWidth / count, true, true);
        if (!selected) return (CompactWidth, false, true);
        var room = Math.Max(StripWidth - (count - 1) * CompactWidth, CompactWidth);
        return (room, room >= NameWidth, room >= FullWidth || room < NameWidth);
    }

    public static IReadOnlyList<TabViewModel> Build(UsageStore store, string current)
    {
        var tabs = new List<TabViewModel>();
        var iconColor = ((SolidColorBrush)Resource("PrimaryText")).Color;
        foreach (var id in store.VisibleProviders)
        {
            var state = store.State(id);
            var summary = TabSummary.Make(state.Snapshot, state.Error is not null);
            var isCurrent = id.Key() == current;
            var spoken = summary.Text is "!" or "–"
                ? T("sem dados", "no data")
                : T($"{summary.Text} usado", $"{summary.Text} used");
            var (width, showName, showStatus) = Layout(store.VisibleProviders.Count, isCurrent);
            tabs.Add(new TabViewModel(
                id.Key(),
                TabIcons.Get(id, iconColor),
                isCurrent ? 1 : 0.65,
                id.Descriptor().ShortName,
                showName ? Visibility.Visible : Visibility.Collapsed,
                showStatus ? Visibility.Visible : Visibility.Collapsed,
                width,
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
            Visibility.Visible,
            30,
            "",
            Resource("SecondaryText"),
            onAgents ? Resource("AccentBrush") : Brushes.Transparent,
            onAgents ? FontWeights.SemiBold : FontWeights.Normal,
            T("Agentes e conexões", "Agents and connections"),
            new RelayCommand(() => store.SelectTab(PopupTabs.AgentsKey)));
    }

    internal static Brush Resource(string key) => (Brush)System.Windows.Application.Current.Resources[key];
}

/// <summary>"New version" strip under the header: the update button, its progress, or the result of a manual check.</summary>
internal sealed record UpdateBannerViewModel(
    Visibility Visibility,
    string Icon,
    Brush IconBrush,
    Brush Background,
    string Title,
    FontWeight TitleWeight,
    string Subtitle,
    Visibility SubtitleVisibility,
    string ButtonText,
    string ButtonTip,
    Visibility ButtonVisibility,
    ICommand Command)
{
    private const string DownloadIcon = "\uE896";
    private const string BusyIcon = "\uE895";
    private const string DoneIcon = "\uE73E";
    private const string WarningIcon = "\uE7BA";

    public static UpdateBannerViewModel Build(Updater updater)
    {
        var version = updater.Available?.Version.ToString() ?? "";
        var current = updater.CurrentText;
        var accent = TabViewModel.Resource("AccentBrush");
        var background = TabViewModel.Resource(updater.Phase == UpdatePhase.Failed ? "ErrorBackground" : "UpdateBackground");
        var install = new RelayCommand(async () => await updater.InstallAsync());

        UpdateBannerViewModel Line(string icon, Brush brush, string title) => new(
            updater.ShowsBanner ? Visibility.Visible : Visibility.Collapsed, icon, brush, background, title, FontWeights.Normal,
            "", Visibility.Collapsed, "", "", Visibility.Collapsed, install);

        return updater.Phase switch
        {
            UpdatePhase.Checking => Line(BusyIcon, accent, T("Procurando atualizações…", "Checking for updates…")),
            UpdatePhase.Downloading => Line(BusyIcon, accent,
                T($"Baixando a versão {version}… {updater.Progress:P0}", $"Downloading version {version}… {updater.Progress:P0}")),
            UpdatePhase.Installing => Line(BusyIcon, accent, T("Instalando… o medidor reabre sozinho.", "Installing… the meter reopens by itself.")),
            UpdatePhase.UpToDate => Line(DoneIcon, LimitViewModel.SeverityBrush(Severity.Normal),
                T($"Você já tem a versão mais recente ({current}).", $"You're on the latest version ({current}).")),
            UpdatePhase.Failed => Line(WarningIcon, TabViewModel.Resource("WarningBrush"), updater.Failure ?? "") with
            {
                ButtonText = T("Abrir página", "Open page"),
                ButtonVisibility = updater.Available is null ? Visibility.Collapsed : Visibility.Visible,
                Command = new RelayCommand(updater.OpenReleasePage),
            },
            _ => Line(DownloadIcon, accent, T($"Nova versão {version}", $"New version {version}")) with
            {
                TitleWeight = FontWeights.SemiBold,
                Subtitle = T($"Você está com a {current}", $"You have {current}"),
                SubtitleVisibility = Visibility.Visible,
                ButtonText = updater.CanInstallInPlace ? T("Atualizar", "Update") : T("Baixar", "Download"),
                ButtonTip = updater.CanInstallInPlace
                    ? T("Baixa, instala e reabre o medidor", "Downloads, installs and reopens the meter")
                    : T("Abre a página de download", "Opens the download page"),
                ButtonVisibility = Visibility.Visible,
            },
        };
    }
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
