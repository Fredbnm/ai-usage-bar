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
    public required IReadOnlyList<ProviderViewModel> Providers { get; init; }

    public static PopupViewModel Build(UsageStore store)
    {
        var loading = store.IsLoading;
        return new PopupViewModel
        {
            Title = T("Uso de IA", "AI Usage"),
            UpdatedText = store.LastUpdated is { } updated ? UsageFormat.Updated(updated) : T("Ainda sem dados", "No data yet"),
            RefreshTip = T("Atualizar agora", "Refresh now"),
            SettingsTip = T("Opções", "Options"),
            RefreshVisibility = loading ? Visibility.Collapsed : Visibility.Visible,
            LoadingVisibility = loading ? Visibility.Visible : Visibility.Collapsed,
            RefreshCommand = new RelayCommand(async () => await store.RefreshAsync(force: true)),
            Providers = [.. store.VisibleProviders.Select(id => ProviderViewModel.Build(id, store.State(id)))],
        };
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
            OpenTip = id == ProviderId.Claude ? T("Ver uso no claude.ai", "View usage on claude.ai") : T("Abrir o Grok Bot", "Open Grok Bot"),
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

    private static Brush SeverityBrush(Severity severity)
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
