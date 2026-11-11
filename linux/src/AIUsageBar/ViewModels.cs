using System.Windows.Input;
using AIUsageBar.Core;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar;

/// <summary>Plain snapshots of the store for the popup, rebuilt on every change (as on Windows).</summary>
internal sealed class PopupViewModel
{
    public required string Title { get; init; }
    public required string UpdatedText { get; init; }
    public required string RefreshTip { get; init; }
    public required string SettingsTip { get; init; }
    public required bool RefreshVisible { get; init; }
    public required bool LoadingVisible { get; init; }
    public required ICommand RefreshCommand { get; init; }
    /// <summary>Only the selected agent, or all of them with "All AIs together".</summary>
    public required IReadOnlyList<ProviderViewModel> Providers { get; init; }
    public required IReadOnlyList<TabViewModel> Tabs { get; init; }
    public required TabViewModel AgentsTab { get; init; }
    public required bool TabStripVisible { get; init; }
    public required AgentsViewModel Agents { get; init; }
    public required UpdateBannerViewModel Update { get; init; }
    public required NoticeViewModel Notice { get; init; }

    public static PopupViewModel Build(UsageStore store, Updater updater, string? notice = null)
    {
        var loading = store.IsLoading;
        var current = store.CurrentTab;
        var onAgents = current == PopupTabs.AgentsKey;
        return new PopupViewModel
        {
            Tabs = TabViewModel.Build(store, current),
            AgentsTab = TabViewModel.AgentsTab(store, current),
            TabStripVisible = store.ShowTabStrip,
            Agents = AgentsViewModel.Build(store, onAgents),
            Update = UpdateBannerViewModel.Build(updater),
            Notice = new NoticeViewModel(notice ?? "", notice is not null),
            Title = T("Uso de IA", "AI Usage"),
            UpdatedText = store.LastUpdated is { } updated ? UsageFormat.Updated(updated) : T("Ainda sem dados", "No data yet"),
            RefreshTip = T("Atualizar agora", "Refresh now"),
            SettingsTip = T("Opções", "Options"),
            RefreshVisible = !loading,
            LoadingVisible = loading,
            RefreshCommand = new RelayCommand(async () => await store.RefreshAsync(force: true)),
            Providers = [.. store.VisibleProviders
                .Where(id => store.ShowsAllProviders || (!onAgents && id.Key() == current))
                .Select(id => ProviderViewModel.Build(id, store.State(id)))],
        };
    }
}

/// <summary>A one-off hint above the tabs, e.g. that the desktop shows no tray icons.</summary>
internal sealed record NoticeViewModel(string Text, bool Visible);

/// <summary>One tab in the strip: the agent's icon, name and its fullest limit.</summary>
internal sealed record TabViewModel(
    string Key,
    Bitmap? Icon,
    bool IconVisible,
    double IconOpacity,
    string Name,
    bool NameVisible,
    bool StatusVisible,
    double Width,
    string StatusText,
    IBrush StatusBrush,
    IBrush UnderlineBrush,
    FontWeight NameWeight,
    string AutomationName,
    ICommand SelectCommand)
{
    /// <summary>Room for the agent tabs in the strip, in device-independent pixels (the popup's 308 minus the "+" tab).</summary>
    private const double StripWidth = 278;

    /// <summary>A tab that shows only its icon and percentage (Inter, the bundled font, is wider than Segoe UI: 46 on Windows).</summary>
    private const double CompactWidth = 50;

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
        foreach (var id in store.VisibleProviders)
        {
            var state = store.State(id);
            var summary = TabSummary.Make(state.Snapshot, state.Error is not null, store.Settings.TabLimitFor(id));
            var isCurrent = id.Key() == current;
            var spoken = summary.Text is "!" or "–"
                ? T("sem dados", "no data")
                : T($"{summary.Text} usado", $"{summary.Text} used");
            var (width, showName, showStatus) = Layout(store.VisibleProviders.Count, isCurrent);
            tabs.Add(new TabViewModel(
                id.Key(),
                TabIcons.Get(id, Theme.IsDark),
                true,
                isCurrent ? 1 : 0.65,
                id.Descriptor().ShortName,
                showName,
                showStatus,
                width,
                summary.IsWarning && summary.Text != "!" ? summary.Text + " ⚠" : summary.Text,
                summary.Text == "–" ? Theme.SecondaryText : LimitViewModel.SeverityBrush(summary.IsWarning ? Severity.Warning : summary.Severity),
                isCurrent ? Theme.Accent : Brushes.Transparent,
                isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
                $"{id.DisplayName()}, {spoken}",
                new RelayCommand(() => store.SelectTab(id.Key()))));
        }
        return tabs;
    }

    /// <summary>The trailing "+" tab that opens the Agents tab; narrower than the agent tabs.</summary>
    public static TabViewModel AgentsTab(UsageStore store, string current)
    {
        var onAgents = current == PopupTabs.AgentsKey;
        return new TabViewModel(
            PopupTabs.AgentsKey,
            null,
            false,
            1,
            "+",
            true,
            false,
            30,
            "",
            Theme.SecondaryText,
            onAgents ? Theme.Accent : Brushes.Transparent,
            onAgents ? FontWeight.SemiBold : FontWeight.Normal,
            T("Agentes e conexões", "Agents and connections"),
            new RelayCommand(() => store.SelectTab(PopupTabs.AgentsKey)));
    }
}

/// <summary>"New version" strip under the header: the update button, its progress, or the result of a manual check.</summary>
internal sealed record UpdateBannerViewModel(
    bool Visible,
    Geometry Icon,
    IBrush IconBrush,
    IBrush Background,
    string Title,
    FontWeight TitleWeight,
    string Subtitle,
    bool SubtitleVisible,
    string ButtonText,
    string ButtonTip,
    bool ButtonVisible,
    ICommand Command)
{
    public static UpdateBannerViewModel Build(Updater updater)
    {
        var version = updater.Available?.Version.ToString() ?? "";
        var current = updater.CurrentText;
        var background = updater.Phase == UpdatePhase.Failed ? Theme.ErrorBackground : Theme.UpdateBackground;
        var install = new RelayCommand(async () => await updater.InstallAsync());

        UpdateBannerViewModel Line(Geometry icon, IBrush brush, string title) => new(
            updater.ShowsBanner, icon, brush, background, title, FontWeight.Normal,
            "", false, "", "", false, install);

        return updater.Phase switch
        {
            UpdatePhase.Checking => Line(Icons.Busy, Theme.Accent, T("Procurando atualizações…", "Checking for updates…")),
            UpdatePhase.Downloading => Line(Icons.Busy, Theme.Accent,
                T($"Baixando a versão {version}… {updater.Progress:P0}", $"Downloading version {version}… {updater.Progress:P0}")),
            UpdatePhase.Installing => Line(Icons.Busy, Theme.Accent, T("Instalando… o medidor reabre sozinho.", "Installing… the meter reopens by itself.")),
            UpdatePhase.UpToDate => Line(Icons.Check, LimitViewModel.SeverityBrush(Severity.Normal),
                T($"Você já tem a versão mais recente ({current}).", $"You're on the latest version ({current}).")),
            UpdatePhase.Failed => Line(Icons.Warning, Theme.Warning, updater.Failure ?? "") with
            {
                ButtonText = T("Abrir página", "Open page"),
                ButtonVisible = updater.Available is not null,
                Command = new RelayCommand(updater.OpenReleasePage),
            },
            _ => Line(Icons.Download, Theme.Accent, T($"Nova versão {version}", $"New version {version}")) with
            {
                TitleWeight = FontWeight.SemiBold,
                Subtitle = T($"Você está com a {current}", $"You have {current}"),
                SubtitleVisible = true,
                ButtonText = updater.CanInstallInPlace ? T("Atualizar", "Update") : T("Baixar", "Download"),
                ButtonTip = updater.CanInstallInPlace
                    ? T("Baixa, instala e reabre o medidor", "Downloads, installs and reopens the meter")
                    : T("Abre a página de download", "Opens the download page"),
                ButtonVisible = true,
            },
        };
    }
}

/// <summary>The Agents tab: every supported agent, whether it is connected, and first-run help.</summary>
internal sealed class AgentsViewModel
{
    public required bool Visible { get; init; }
    public required string Heading { get; init; }
    public required string Welcome { get; init; }
    public required bool WelcomeVisible { get; init; }
    public required IReadOnlyList<AgentRowViewModel> Rows { get; init; }
    public required string DoneText { get; init; }
    public required ICommand DoneCommand { get; init; }

    public static AgentsViewModel Build(UsageStore store, bool visible)
    {
        var firstRun = !store.Settings.OnboardingDismissed;
        var rows = visible ? store.AgentRows() : [];
        return new AgentsViewModel
        {
            Visible = visible,
            Heading = T("AGENTES DE IA", "AI AGENTS"),
            Welcome = T(
                "Aqui estão os agentes que o medidor sabe ler. Os que você já usa neste computador aparecem sozinhos; para os outros, siga a dica.",
                "These are the agents the meter can read. The ones you already use on this computer show up on their own; for the others, follow the hint."),
            WelcomeVisible = firstRun,
            Rows = [.. rows.Select(row => AgentRowViewModel.Build(store, row))],
            // After the first run the same button goes back, which matters with
            // one agent, where there is no tab strip to leave this tab by.
            DoneText = firstRun ? T("Concluir", "Done") : T("Voltar", "Back"),
            DoneCommand = new RelayCommand(store.DismissOnboarding),
        };
    }
}

internal sealed record AgentRowViewModel(
    string Name,
    string Status,
    string Hint,
    bool HintVisible,
    Geometry Icon,
    IBrush IconBrush,
    bool IsShown,
    bool ToggleVisible,
    string ToggleText,
    ICommand ToggleCommand)
{
    public static AgentRowViewModel Build(UsageStore store, AgentRow row)
    {
        var descriptor = row.Id.Descriptor();
        var signedIn = row.Status == SetupStatus.Connected;
        var (status, hint, icon, brush) = (signedIn, row.HasData) switch
        {
            (false, _) => (T("Login não encontrado", "No login found"), descriptor.SetupHint(), Icons.Warning, Theme.Warning),
            (true, true) => (T("Conectado", "Connected") + (row.Plan is { } plan ? $" · {plan}" : ""), "", Icons.Check, LimitViewModel.SeverityBrush(Severity.Normal)),
            (true, false) => (
                T("Login encontrado, mas sem dados de uso", "Login found, but no usage data"),
                T($"Talvez esta conta não tenha o {descriptor.DisplayName}. Marque Mostrar para acompanhar mesmo assim.",
                  $"This account may not have {descriptor.DisplayName}. Tick Show to track it anyway."),
                Icons.Info,
                Theme.SecondaryText),
        };
        return new AgentRowViewModel(
            descriptor.DisplayName,
            status,
            hint,
            hint.Length > 0,
            icon,
            brush,
            row.IsShown,
            signedIn,
            T("Mostrar", "Show"),
            new RelayCommand(() => store.SetProviderHidden(row.Id, row.IsShown)));
    }
}

internal sealed class ProviderViewModel
{
    public required string Name { get; init; }
    public required string? Plan { get; init; }
    public required bool PlanVisible { get; init; }
    public required string OpenTip { get; init; }
    public required ICommand OpenCommand { get; init; }
    public required string? ErrorText { get; init; }
    public required bool ErrorVisible { get; init; }
    public required IReadOnlyList<LimitViewModel> Limits { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required string BreakdownHeader { get; init; }
    public required IReadOnlyList<BreakdownViewModel> Breakdown { get; init; }
    public required bool BreakdownVisible { get; init; }
    public required string LoadingText { get; init; }
    public required bool LoadingVisible { get; init; }

    public static ProviderViewModel Build(ProviderId id, ProviderState state)
    {
        var snapshot = state.Snapshot;
        var breakdown = snapshot?.Breakdown ?? [];
        return new ProviderViewModel
        {
            Name = id.DisplayName(),
            Plan = snapshot?.PlanName,
            PlanVisible = snapshot?.PlanName is not null,
            OpenTip = id.Descriptor().OpenTip(),
            OpenCommand = new RelayCommand(() => InstalledApps.Open(id)),
            ErrorText = state.Error?.MessageFor(id),
            ErrorVisible = state.Error is not null,
            Limits = [.. (snapshot?.Items ?? []).Select(LimitViewModel.Build)],
            Notes = [.. (snapshot?.Notes ?? []).Select(note => note.Text)],
            BreakdownHeader = T("ONDE FOI O USO DA SEMANA", "WHERE THIS WEEK’S USAGE WENT"),
            Breakdown = [.. breakdown.Select(row => new BreakdownViewModel(row.Name, UsageFormat.Percent(row.Percent), row.Percent / 100))],
            BreakdownVisible = breakdown.Count > 0,
            LoadingText = T("Carregando…", "Loading…"),
            LoadingVisible = snapshot is null && state.Error is null,
        };
    }
}

internal sealed record LimitViewModel(string Title, string PercentText, double Fraction, IBrush Brush, string ResetText, bool ResetVisible)
{
    public static LimitViewModel Build(LimitItem item) => new(
        item.Title,
        UsageFormat.Percent(item.Percent),
        Math.Min(item.Percent / 100, 1),
        SeverityBrush(item.Severity),
        item.ResetsAt is { } reset ? UsageFormat.Reset(reset) : "",
        item.ResetsAt is not null);

    internal static IBrush SeverityBrush(Severity severity) => severity switch
    {
        Severity.Warning => Theme.Warning,
        Severity.Critical => Theme.Critical,
        _ => Theme.Accent,
    };
}

internal sealed record BreakdownViewModel(string Name, string PercentText, double Fraction);

internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

