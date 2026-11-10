using System.Text.Json.Nodes;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar.Core;

/// <summary>
/// Reads <c>GET https://api.anthropic.com/api/oauth/usage</c>, the endpoint
/// Claude Code's <c>/usage</c> uses. See docs/usage-api.md.
/// </summary>
public static class ClaudeUsage
{
    public sealed record Parsed(IReadOnlyList<LimitItem> Items, IReadOnlyList<BreakdownItem> Breakdown);

    public static Parsed? Parse(byte[] data)
    {
        var root = Json.Parse(data);
        if (root is not JsonObject) return null;
        return new Parsed(Items(root), Breakdown(root));
    }

    /// <summary>
    /// Limits in display order. Prefers the <c>limits</c> array; falls back to
    /// the older per-window fields if it is missing.
    /// </summary>
    private static IReadOnlyList<LimitItem> Items(JsonNode root)
    {
        var items = new List<LimitItem>();
        if (Json.Arr(root, "limits") is { Count: > 0 } limits)
        {
            for (var index = 0; index < limits.Count; index++)
            {
                var limit = limits[index];
                if (Json.Str(limit, "kind") is not { } kind || Json.Num(limit, "percent") is not { } percent) continue;
                var scope = Json.Obj(limit, "scope");
                var scopeName = Json.Str(Json.Obj(scope, "model"), "display_name", "displayName")
                    ?? Json.Str(Json.Obj(scope, "surface"), "display_name", "displayName");
                items.Add(new LimitItem(
                    $"{index}-{kind}-{scopeName}",
                    kind,
                    scopeName,
                    percent,
                    SeverityParser.From(Json.Str(limit, "severity"), percent),
                    Json.Date(limit, "resets_at", "resetsAt"),
                    Group(kind, Json.Str(limit, "group"))));
            }
            if (items.Count > 0) return items;
        }

        (string Id, string Kind, string? Scope, string Key, LimitGroup Group)[] windows =
        [
            ("session", "session", null, "five_hour", LimitGroup.Session),
            ("weekly_all", "weekly_all", null, "seven_day", LimitGroup.Weekly),
            ("weekly_opus", "weekly_scoped", "Opus", "seven_day_opus", LimitGroup.Weekly),
            ("weekly_sonnet", "weekly_scoped", "Sonnet", "seven_day_sonnet", LimitGroup.Weekly),
        ];
        foreach (var window in windows)
        {
            var node = Json.Obj(root, window.Key);
            if (Json.Num(node, "utilization") is not { } percent) continue;
            items.Add(new LimitItem(window.Id, window.Kind, window.Scope, percent,
                SeverityParser.From(null, percent), Json.Date(node, "resets_at", "resetsAt"), window.Group));
        }
        return items;
    }

    private static IReadOnlyList<BreakdownItem> Breakdown(JsonNode root)
    {
        var rows = Json.Arr(Json.Obj(root, "seven_day_breakdown"), "rows");
        if (rows is null) return [];
        return rows
            .Select(row => (Key: Json.Str(row, "key"), Name: Json.Str(row, "display_name", "displayName"), Percent: Json.Num(row, "percent")))
            .Where(row => row.Key is not null && row.Percent is > 0)
            .OrderByDescending(row => row.Percent)
            .Select(row => new BreakdownItem(row.Key!, row.Name, row.Percent!.Value))
            .ToList();
    }

    public static string Title(string kind, string? scopeName) => kind switch
    {
        "session" => T("Sessão atual", "Current session"),
        "weekly_all" => T("Semanal · todos os modelos", "Weekly · all models"),
        "weekly" => T("Semanal", "Weekly"),
        "premium_requests" => T("Requisições premium · mês", "Premium requests · month"),
        "chat" => "Chat",
        "completions" => T("Sugestões de código", "Code completions"),
        "weekly_scoped" => T("Semanal", "Weekly") + " · " + (scopeName ?? T("limite específico", "specific limit")),
        _ => BreakdownItem.Capitalize(kind.Replace('_', ' ')) + (scopeName is null ? "" : $" · {scopeName}"),
    };

    private static LimitGroup Group(string kind, string? group)
    {
        if (kind == "session" || group == "session") return LimitGroup.Session;
        if (group == "weekly" || kind.StartsWith("weekly", StringComparison.Ordinal)) return LimitGroup.Weekly;
        return LimitGroup.Other;
    }
}
