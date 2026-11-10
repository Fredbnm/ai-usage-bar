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
public enum ProviderId { Claude, GrokBot }

public static class ProviderIdExtensions
{
    public static string DisplayName(this ProviderId id) => id == ProviderId.Claude ? "Claude" : "Grok Bot";

    /// <summary>The app whose login is reused, as named in messages.</summary>
    public static string LoginSource(this ProviderId id) => id == ProviderId.Claude ? "Claude Code" : "Grok Bot";

    /// <summary>Stable key for settings and cache files.</summary>
    public static string Key(this ProviderId id) => id == ProviderId.Claude ? "claude" : "grokBot";
}

/// <summary>Everything the UI needs to draw one service.</summary>
public sealed record ProviderSnapshot(
    ProviderId Provider,
    string? PlanName,
    IReadOnlyList<LimitItem> Items,
    IReadOnlyList<BreakdownItem> Breakdown,
    IReadOnlyList<SnapshotNote> Notes);
