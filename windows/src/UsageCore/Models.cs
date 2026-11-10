using static AIUsageBar.Core.Localization;

namespace AIUsageBar.Core;

public enum Severity { Normal, Warning, Critical }

public static class SeverityParser
{
    /// <summary>
    /// Uses the API's own severity when it is one we know, otherwise falls
    /// back to thresholds on the percentage.
    /// </summary>
    public static Severity From(string? api, double percent)
    {
        var value = api?.ToLowerInvariant();
        switch (value)
        {
            case "normal" or "ok" or "low":
                return Severity.Normal;
            case "warning" or "warn" or "medium" or "elevated":
                return Severity.Warning;
            case not null when new[] { "crit", "exceed", "high", "block", "danger", "error", "max" }.Any(value.Contains):
                return Severity.Critical;
            default:
                return percent >= 90 ? Severity.Critical : percent >= 75 ? Severity.Warning : Severity.Normal;
        }
    }
}

public enum LimitGroup { Session, Weekly, Other }

/// <summary>A limit ready to be displayed.</summary>
public sealed record LimitItem(
    string Id,
    string Kind,
    string? ScopeName,
    double Percent,
    Severity Severity,
    DateTimeOffset? ResetsAt,
    LimitGroup Group)
{
    /// <summary>Built when shown, so it follows the language setting.</summary>
    public string Title => ClaudeUsage.Title(Kind, ScopeName);
}

/// <summary>Share of the week's usage per product (Claude Code, Chat, Cowork…).</summary>
public sealed record BreakdownItem(string Id, string? DisplayName, double Percent)
{
    public string Name => Id switch
    {
        "chat" => "Chat",
        "other" => T("Outros", "Other"),
        _ => DisplayName ?? Capitalize(Id.Replace('_', ' ')),
    };

    internal static string Capitalize(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
}

public enum NoteKind { PooledAllowance, BankedResets, TrialUntil, OnDemandEnabled }

/// <summary>Extra facts shown under a service's limits.</summary>
public sealed record SnapshotNote(NoteKind Kind, int Count = 0, DateTimeOffset? Date = null)
{
    public string Text => Kind switch
    {
        NoteKind.PooledAllowance => T("Uso compartilhado da equipe, sem limite individual.", "Shared team allowance, no individual limit."),
        NoteKind.BankedResets when Count == 1 => T("1 reset guardado disponível", "1 banked reset available"),
        NoteKind.BankedResets => T($"{Count} resets guardados disponíveis", $"{Count} banked resets available"),
        NoteKind.TrialUntil => T($"Teste grátis até {UsageFormat.ShortDate(Date!.Value)}", $"Free trial until {UsageFormat.ShortDate(Date!.Value)}"),
        NoteKind.OnDemandEnabled => T("Uso sob demanda ligado", "On-demand usage on"),
        _ => "",
    };
}

/// <summary>A service whose plan usage the app shows.</summary>
public enum ProviderId { Claude, Copilot, GrokBot }

/// <summary>
/// Everything that differs per service and is not parsing: names, tray
/// choices, where "open" leads. Tabs, tray, settings and onboarding read
/// from here, so adding a service means adding one entry.
/// </summary>
public sealed record ProviderDescriptor(
    ProviderId Id,
    string DisplayName,
    string ShortName,
    string LoginSource,
    string Key,
    IReadOnlyList<TrayDisplay> TrayOptions,
    TrayDisplay DefaultTray,
    Func<string> WeeklyLabel,
    Func<string> OpenTip,
    Func<string> SetupHint,
    Uri? HelpUrl);

public static class ProviderCatalog
{
    public static readonly IReadOnlyList<ProviderDescriptor> All =
    [
        new(ProviderId.Claude, "Claude", "Claude", "Claude Code", "claude",
            [TrayDisplay.Session, TrayDisplay.Weekly, TrayDisplay.Hidden], TrayDisplay.Session,
            () => T("Semanal (todos os modelos)", "Weekly (all models)"),
            () => T("Ver uso no claude.ai", "View usage on claude.ai"),
            () => T("Entre com o comando claude no terminal ou pelo app do Claude.",
                    "Sign in with the claude command in a terminal or in the Claude app."),
            new Uri("https://claude.ai/settings/usage")),
        new(ProviderId.Copilot, "GitHub Copilot", "Copilot", "GitHub Copilot", "copilot",
            [TrayDisplay.Weekly, TrayDisplay.Hidden], TrayDisplay.Weekly,
            () => T("Mensal", "Monthly"),
            () => T("Gerenciar o Copilot no github.com", "Manage Copilot on github.com"),
            () => T("Entre no GitHub Copilot no VS Code ou rode gh auth login.",
                    "Sign in to GitHub Copilot in VS Code, or run gh auth login."),
            new Uri("https://github.com/settings/copilot")),
        new(ProviderId.GrokBot, "Grok Bot", "Grok", "Grok Bot", "grokBot",
            [TrayDisplay.Weekly, TrayDisplay.Hidden], TrayDisplay.Weekly,
            () => T("Semanal", "Weekly"),
            () => T("Abrir o Grok Bot", "Open Grok Bot"),
            () => T("Instale o Grok Bot, abra-o e entre na sua conta.",
                    "Install Grok Bot, open it and sign in."),
            null),
    ];

    public static ProviderDescriptor For(ProviderId id) => All.First(d => d.Id == id);
}

public static class ProviderIdExtensions
{
    public static ProviderDescriptor Descriptor(this ProviderId id) => ProviderCatalog.For(id);

    public static string DisplayName(this ProviderId id) => id.Descriptor().DisplayName;

    /// <summary>
    /// What the tray draws when there is no app icon to borrow. "Co", not "C":
    /// Copilot would otherwise look like Claude (and "G" like Grok Bot).
    /// </summary>
    public static string Initial(this ProviderId id) => id == ProviderId.Copilot ? "Co" : id.DisplayName()[..1];

    /// <summary>The app whose login is reused, as named in messages.</summary>
    public static string LoginSource(this ProviderId id) => id.Descriptor().LoginSource;

    /// <summary>Stable key for settings and cache files.</summary>
    public static string Key(this ProviderId id) => id.Descriptor().Key;
}

/// <summary>Everything the UI needs to draw one service.</summary>
public sealed record ProviderSnapshot(
    ProviderId Provider,
    string? PlanName,
    IReadOnlyList<LimitItem> Items,
    IReadOnlyList<BreakdownItem> Breakdown,
    IReadOnlyList<SnapshotNote> Notes);
