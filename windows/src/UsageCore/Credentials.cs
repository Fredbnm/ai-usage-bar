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
    string? RateLimitTier,
    string? OrganizationUuid = null)
{
    public bool IsExpired => ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow;

    /// <summary>
    /// "Max 5x", "Max 20x", "Pro", "Team"… <c>SubscriptionType</c> names the
    /// plan; <c>RateLimitTier</c> only tells the Max tiers apart, and is read
    /// for those alone. A Team account carries the tier
    /// <c>default_claude_max_5x</c>, so reading the tier first labelled it
    /// "Max 5x".
    /// </summary>
    public string? PlanName
    {
        get
        {
            var plan = (SubscriptionType ?? "").ToLowerInvariant();
            if (plan.Length == 0 || plan == "max")
            {
                var tier = (RateLimitTier ?? "").ToLowerInvariant();
                if (tier.Contains("max_20x")) return "Max 20x";
                if (tier.Contains("max_5x")) return "Max 5x";
            }
            if (plan.Length == 0) return null;
            return char.ToUpperInvariant(plan[0]) + plan[1..];
        }
    }

    /// <summary>
    /// Which account these numbers belong to. A token renewal leaves it alone,
    /// so cached usage survives one and is dropped only on a real sign-in
    /// change.
    /// </summary>
    public string? Account => OrganizationUuid;

    /// <summary>
    /// The login itself: the account plus the life of the current token, which
    /// moves whenever Claude Code signs in again or renews. Carries no part of
    /// the token.
    /// </summary>
    public string LoginIdentity =>
        $"{OrganizationUuid ?? "none"}|{(ExpiresAt is { } at ? at.ToUnixTimeSeconds().ToString() : "none")}";

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
            Json.Str(oauth, "rateLimitTier"),
            Json.Str(Json.Parse(data), "organizationUuid"));
    }
}

public static class ClaudeCredentialsStore
{
    /// <summary>
    /// <c>%USERPROFILE%\.claude\.credentials.json</c>, or the same file under
    /// <c>%CLAUDE_SECURESTORAGE_CONFIG_DIR%</c>, else under
    /// <c>%CLAUDE_CONFIG_DIR%</c>, when either names a directory.
    /// </summary>
    public static string DefaultPath => Path.Combine(ConfigDirectory(), ".credentials.json");

    /// <summary>
    /// The directory Claude Code keeps its login in. It reads
    /// <c>CLAUDE_SECURESTORAGE_CONFIG_DIR</c> first and only falls back to
    /// <c>CLAUDE_CONFIG_DIR</c> when that one is unset; either set to an empty
    /// value means the default directory.
    /// </summary>
    internal static string ConfigDirectory(Func<string, string?>? readEnvironment = null, string? home = null)
    {
        var read = readEnvironment ?? Environment.GetEnvironmentVariable;
        var fallback = Path.Combine(
            home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

        var secureStorage = read("CLAUDE_SECURESTORAGE_CONFIG_DIR");
        if (secureStorage is not null) return secureStorage.Length == 0 ? fallback : secureStorage;

        var configDir = read("CLAUDE_CONFIG_DIR");
        return string.IsNullOrEmpty(configDir) ? fallback : configDir;
    }

    public static ClaudeCredentials Load(string? path = null)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path ?? DefaultPath); }
        catch (Exception error) { throw new UsageException(UsageErrorKind.NotLoggedIn, inner: error); }
        return ClaudeCredentials.Parse(data);
    }
}
