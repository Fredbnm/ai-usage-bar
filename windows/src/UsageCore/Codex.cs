using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace AIUsageBar.Core;

/// <summary>
/// The ChatGPT login the Codex CLI and the Codex extension keep in
/// <c>~/.codex/auth.json</c>. Read only: the token is never written, refreshed,
/// cached or logged.
/// </summary>
public sealed record CodexCredentials(string AccessToken, string? AccountId)
{
    /// <summary>Never prints the token.</summary>
    public override string ToString() => $"CodexCredentials(account: {AccountId ?? "?"}, token: <redacted>)";

    /// <summary>
    /// <c>{"tokens": {"access_token", "refresh_token", "id_token", "account_id"}}</c>. A login with
    /// only an API key (<c>OPENAI_API_KEY</c>) has no ChatGPT plan to measure: not a login here.
    /// </summary>
    public static CodexCredentials? Parse(byte[] data)
    {
        var tokens = Json.Obj(Json.Parse(data), "tokens");
        return Json.Str(tokens, "access_token", "accessToken") is { Length: > 0 } token
            ? new CodexCredentials(token, Json.Str(tokens, "account_id", "accountId"))
            : null;
    }
}

public static class CodexCredentialsStore
{
    /// <summary><c>%USERPROFILE%\.codex\auth.json</c>, or <c>%CODEX_HOME%\auth.json</c> when that is set.</summary>
    public static string DefaultPath
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            var directory = string.IsNullOrEmpty(home)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : home;
            return Path.Combine(directory, "auth.json");
        }
    }

    public static CodexCredentials? TryLoad(string? path = null)
    {
        try { return File.Exists(path ?? DefaultPath) ? CodexCredentials.Parse(File.ReadAllBytes(path ?? DefaultPath)) : null; }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// <c>GET chatgpt.com/backend-api/wham/usage</c>: what the Codex usage page shows. Not
/// documented by OpenAI; the field names come from third-party tools. Every field is optional.
/// </summary>
public static class CodexUsage
{
    public static ProviderSnapshot? Snapshot(byte[] data, DateTimeOffset? now = null)
    {
        var root = Json.Parse(data);
        if (root is not JsonObject) return null;
        var current = now ?? DateTimeOffset.UtcNow;

        var limits = Json.Obj(root, "rate_limit", "rateLimit", "rate_limits");
        var limitReached = Json.Bool(limits, "limit_reached", "limitReached") == true;
        var items = new List<LimitItem>();
        var windows = new[] { Json.Obj(limits, "primary_window", "primaryWindow", "primary"), Json.Obj(limits, "secondary_window", "secondaryWindow", "secondary") };
        for (var index = 0; index < windows.Length; index++)
        {
            if (Item(windows[index], index, limitReached, current) is { } item) items.Add(item);
        }
        if (items.Count == 0) return null;
        return new ProviderSnapshot(ProviderId.Codex, PlanName(Json.Str(root, "plan_type", "planType")), items, [], []);
    }

    /// <summary>
    /// The first window is the 5-hour session and the second the week, told apart by
    /// <c>limit_window_seconds</c> when it is there, so a plan with only a weekly window is not mislabelled.
    /// </summary>
    private static LimitItem? Item(JsonObject? window, int index, bool limitReached, DateTimeOffset now)
    {
        if (window is null || Json.Num(window, "used_percent", "usedPercent") is not { } raw || !double.IsFinite(raw)) return null;
        var percent = Math.Clamp(raw, 0, 999);

        var seconds = Json.Num(window, "limit_window_seconds", "limitWindowSeconds");
        var isWeekly = seconds is { } s ? s >= 6 * 86_400 : index == 1;
        var reset = Json.Date(window, "reset_at", "resetAt")
            ?? (Json.Num(window, "reset_after_seconds", "resetAfterSeconds") is { } after && after >= 0 ? now.AddSeconds(after) : null);
        var exhausted = limitReached && percent >= 99.5;
        return new LimitItem(
            isWeekly ? "codex-weekly" : "codex-session",
            isWeekly ? "weekly" : "session",
            null,
            percent,
            exhausted ? Severity.Critical : SeverityParser.From(null, percent),
            reset,
            isWeekly ? LimitGroup.Weekly : LimitGroup.Session);
    }

    /// <summary><c>plus</c> → "Plus", <c>pro</c> → "Pro", <c>prolite</c> → "Prolite", <c>team</c> → "Team".</summary>
    internal static string? PlanName(string? plan) =>
        string.IsNullOrWhiteSpace(plan) ? null : BreakdownItem.Capitalize(plan.Replace('_', ' '));
}

public sealed class CodexProvider(string? authPath = null) : IUsageProvider
{
    public static readonly Uri Endpoint = new("https://chatgpt.com/backend-api/wham/usage");

    public ProviderId Id => ProviderId.Codex;

    public bool IsAvailable() => CodexCredentialsStore.TryLoad(authPath) is not null;

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellation = default)
    {
        var credentials = await Task.Run(() => CodexCredentialsStore.TryLoad(authPath), cancellation).ConfigureAwait(false)
            ?? throw new UsageException(UsageErrorKind.NotLoggedIn);
        // Codex renews its own token when it runs; this app never does.
        if (Jwt.Expiration(credentials.AccessToken) is { } expires && expires < DateTimeOffset.UtcNow)
        {
            throw new UsageException(UsageErrorKind.UnauthorizedExpired);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        if (credentials.AccountId is { Length: > 0 } account) request.Headers.Add("ChatGPT-Account-Id", account);
        var data = await Http.SendAsync(request, cancellation).ConfigureAwait(false);

        var snapshot = CodexUsage.Snapshot(data) ?? throw new UsageException(UsageErrorKind.Decoding);
        return new ProviderFetchResult(snapshot, ProviderCache.From(snapshot.PlanName, data).Encode());
    }

    public ProviderSnapshot? SnapshotFromCache(byte[] cache) =>
        ProviderCache.Decode(cache) is { } decoded ? CodexUsage.Snapshot(decoded.RawBytes) : null;
}
