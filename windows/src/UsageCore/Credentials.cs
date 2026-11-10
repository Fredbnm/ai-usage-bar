using System.Text.Json.Nodes;

namespace AIUsageBar.Core;

/// <summary>
/// The Claude Code login (OAuth) this app piggybacks on. Read only: the
/// token is never written, refreshed or logged.
/// </summary>
public sealed record ClaudeCredentials(
    string AccessToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType,
    string? RateLimitTier)
{
    public bool IsExpired => ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow;

    /// <summary>"Max 5x", "Max 20x", "Pro", "Team"…</summary>
    public string? PlanName
    {
        get
        {
            var tier = (RateLimitTier ?? "").ToLowerInvariant();
            if (tier.Contains("max_20x")) return "Max 20x";
            if (tier.Contains("max_5x")) return "Max 5x";
            if (string.IsNullOrEmpty(SubscriptionType)) return null;
            return char.ToUpperInvariant(SubscriptionType[0]) + SubscriptionType[1..];
        }
    }

    /// <summary>Never prints the token.</summary>
    public override string ToString() => $"ClaudeCredentials(plan: {PlanName ?? "?"}, expiresAt: {ExpiresAt}, token: <redacted>)";

    /// <summary>Parses the JSON Claude Code stores (<c>{"claudeAiOauth": {...}}</c>).</summary>
    public static ClaudeCredentials Parse(byte[] data)
    {
        var oauth = Json.Obj(Json.Parse(data), "claudeAiOauth");
        var token = Json.Str(oauth, "accessToken");
        if (string.IsNullOrEmpty(token)) throw new UsageException(UsageErrorKind.Decoding);
        var expiresMs = Json.Num(oauth, "expiresAt");
        return new ClaudeCredentials(
            token,
            expiresMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null,
            Json.Str(oauth, "subscriptionType"),
            Json.Str(oauth, "rateLimitTier"));
    }
}

public static class ClaudeCredentialsStore
{
    /// <summary>
    /// <c>%USERPROFILE%\.claude\.credentials.json</c>, or
    /// <c>%CLAUDE_CONFIG_DIR%\.credentials.json</c> when that is set.
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var baseDir = string.IsNullOrEmpty(configDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : configDir;
            return Path.Combine(baseDir, ".credentials.json");
        }
    }

    public static ClaudeCredentials Load(string? path = null)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path ?? DefaultPath); }
        catch (Exception error) { throw new UsageException(UsageErrorKind.NotLoggedIn, inner: error); }
        return ClaudeCredentials.Parse(data);
    }
}
