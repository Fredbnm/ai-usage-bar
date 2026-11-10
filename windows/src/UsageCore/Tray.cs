using static AIUsageBar.Core.Localization;

namespace AIUsageBar.Core;

/// <summary>How each service's tray icon is drawn.</summary>
public enum TrayStyle
{
    /// <summary>The service's glyph inside a ring that fills with usage.</summary>
    Icon,
    /// <summary>The percentage inside the ring.</summary>
    Percent,
}

/// <summary>
/// What a service's tray icon tracks. Windows tray icons are 16–32 px
/// squares, too small for two stacked numbers, so there is no
/// "session + weekly" here: the popup shows everything.
/// </summary>
public enum TrayDisplay { Session, Weekly, Hidden }

public static class TrayOptions
{
    public static IReadOnlyList<TrayDisplay> For(ProviderId provider) => provider == ProviderId.Claude
        ? [TrayDisplay.Session, TrayDisplay.Weekly, TrayDisplay.Hidden]
        : [TrayDisplay.Weekly, TrayDisplay.Hidden];

    public static TrayDisplay Default(ProviderId provider) =>
        provider == ProviderId.Claude ? TrayDisplay.Session : TrayDisplay.Weekly;

    public static string Label(this TrayDisplay display, ProviderId provider) => display switch
    {
        TrayDisplay.Session => T("Sessão atual", "Current session"),
        TrayDisplay.Weekly when provider == ProviderId.Claude => T("Semanal (todos os modelos)", "Weekly (all models)"),
        TrayDisplay.Weekly => T("Semanal", "Weekly"),
        _ => T("Não mostrar", "Hide"),
    };

    public static string Label(this TrayStyle style) => style == TrayStyle.Icon
        ? T("Ícone da IA com anel", "AI icon with ring")
        : T("Número no anel", "Number in ring");
}

/// <summary>One service's tray icon: a ring and, depending on the style, a glyph or a number.</summary>
public sealed record TraySegment(ProviderId Provider, double? RingPercent, Severity Severity, string Number, string Tooltip)
{
    /// <summary>Windows truncates tray tooltips at 127 characters.</summary>
    public const int TooltipLimit = 127;

    public static TraySegment? Make(ProviderId provider, TrayDisplay display, ProviderSnapshot? snapshot, string? error)
    {
        if (display == TrayDisplay.Hidden) return null;

        var items = snapshot?.Items ?? [];
        if (items.Count == 0)
        {
            var status = error ?? T("Carregando…", "Loading…");
            return new TraySegment(provider, null, Severity.Normal, error is null ? "–" : "!", Clip($"{provider.DisplayName()}: {status}"));
        }

        var session = items.FirstOrDefault(i => i.Group == LimitGroup.Session);
        var weekly = items.FirstOrDefault(i => i.Kind == "weekly_all") ?? items.FirstOrDefault(i => i.Group == LimitGroup.Weekly);
        var shown = (display == TrayDisplay.Session ? session ?? weekly : weekly ?? session)
            ?? items.MaxBy(i => i.Percent)!;

        var lines = new List<string> { provider.DisplayName() };
        lines.AddRange(items.Select(i => $"{i.Title}: {UsageFormat.Percent(i.Percent)}"));
        if (error is not null) lines.Add("⚠ " + error);
        return new TraySegment(provider, shown.Percent, shown.Severity, FormatNumber(shown.Percent), Clip(string.Join('\n', lines)));
    }

    /// <summary>"88", "&lt;1", "100": the percentage without the sign, to fit the ring.</summary>
    public static string FormatNumber(double percent) => UsageFormat.Percent(percent).TrimEnd('%');

    private static string Clip(string text) => text.Length <= TooltipLimit ? text : text[..(TooltipLimit - 1)] + "…";
}
