using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AIUsageBar.Core;

/// <summary>
/// The Google login of the Gemini CLI, in <c>~/.gemini/oauth_creds.json</c>. Read only:
/// the token is never written, refreshed, cached or logged.
/// </summary>
public sealed record GeminiCredentials(string AccessToken, DateTimeOffset? ExpiresAt)
{
    public bool IsExpired => ExpiresAt is { } expires && expires < DateTimeOffset.UtcNow;

    /// <summary>Never prints the token.</summary>
    public override string ToString() => $"GeminiCredentials(expiresAt: {ExpiresAt}, token: <redacted>)";

    /// <summary><c>{"access_token", "refresh_token", "expiry_date" (ms since epoch), "id_token"}</c>.</summary>
    public static GeminiCredentials? Parse(byte[] data)
    {
        var root = Json.Parse(data);
        if (Json.Str(root, "access_token", "accessToken") is not { Length: > 0 } token) return null;
        var expiry = Json.Num(root, "expiry_date", "expiryDate");
        return new GeminiCredentials(token, expiry is { } ms && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null);
    }
}

public static class GeminiCredentialsStore
{
    /// <summary><c>%USERPROFILE%\.gemini\oauth_creds.json</c>. Only the Google sign-in leaves this file (not an API key).</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");

    public static GeminiCredentials? TryLoad(string? path = null)
    {
        try { return File.Exists(path ?? DefaultPath) ? GeminiCredentials.Parse(File.ReadAllBytes(path ?? DefaultPath)) : null; }
        catch (Exception) { return null; }
    }
}

/// <summary>
/// The Code Assist backend the Gemini CLI talks to: <c>loadCodeAssist</c> names the project and tier,
/// <c>retrieveUserQuota</c> the remaining share of each model's quota. Not documented by Google; the
/// field names come from third-party tools. Every field is optional.
/// </summary>
public static class GeminiUsage
{
    /// <summary><c>cloudaicompanionProject</c> is a string, or an object with an <c>id</c>.</summary>
    public static string? Project(byte[] loadCodeAssistResponse)
    {
        var root = Json.Parse(loadCodeAssistResponse);
        if (Json.Str(root, "cloudaicompanionProject") is { Length: > 0 } text) return text;
        return Json.Str(Json.Obj(root, "cloudaicompanionProject"), "id", "name") is { Length: > 0 } id ? id : null;
    }

    /// <summary><c>currentTier.id</c>: "free-tier" → "Free", "standard-tier" → "Standard", "legacy-tier" → "Legacy".</summary>
    public static string? PlanName(byte[] loadCodeAssistResponse)
    {
        var tier = Json.Obj(Json.Parse(loadCodeAssistResponse), "currentTier", "current_tier");
        if (Json.Str(tier, "id") is { Length: > 0 } id)
        {
            var name = id.Replace("-tier", "", StringComparison.OrdinalIgnoreCase).Replace('-', ' ').Replace('_', ' ');
            return BreakdownItem.Capitalize(name);
        }
        return Json.Str(tier, "name") is { Length: > 0 } label ? label : null;
    }

    /// <summary>
    /// One limit per model: used = <c>1 − remainingFraction</c>. The Pro model first (it is the one that
    /// runs out), at most five. The first is the one the tray ring follows.
    /// </summary>
    public static ProviderSnapshot? Snapshot(byte[] quotaResponse, string? plan)
    {
        var buckets = Json.Arr(Json.Parse(quotaResponse), "buckets");
        if (buckets is null) return null;

        var byModel = new Dictionary<string, (double Used, DateTimeOffset? Reset)>();
        foreach (var bucket in buckets.OfType<JsonObject>())
        {
            if (Json.Str(bucket, "modelId", "model_id", "model") is not { Length: > 0 } model ||
                Json.Num(bucket, "remainingFraction", "remaining_fraction") is not { } remaining || !double.IsFinite(remaining)) continue;
            var used = Math.Clamp((1 - remaining) * 100, 0, 100);
            var reset = Json.Date(bucket, "resetTime", "reset_time");
            // The same model can appear once per token type: the fullest one counts.
            if (!byModel.TryGetValue(model, out var known) || used > known.Used) byModel[model] = (used, reset);
        }

        var items = byModel
            .OrderBy(entry => Family(entry.Key)).ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(5)
            .Select((entry, index) => new LimitItem(
                $"gemini-{entry.Key}", "daily_scoped", ModelName(entry.Key), entry.Value.Used,
                SeverityParser.From(null, entry.Value.Used), entry.Value.Reset,
                index == 0 ? LimitGroup.Weekly : LimitGroup.Other))
            .ToList();
        return items.Count == 0 ? null : new ProviderSnapshot(ProviderId.Gemini, plan, items, [], []);
    }

    private static int Family(string model)
    {
        var name = model.ToLowerInvariant();
        return name.Contains("pro") ? 0 : name.Contains("lite") ? 2 : name.Contains("flash") ? 1 : 3;
    }

    /// <summary>"gemini-2.5-pro" → "2.5 Pro", "gemini-2.5-flash-lite" → "2.5 Flash Lite".</summary>
    internal static string ModelName(string model)
    {
        var name = model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) ? model["gemini-".Length..] : model;
        return BreakdownItem.Capitalize(name.Replace('-', ' ').Replace('_', ' '));
    }
}

public sealed class GeminiProvider(string? credentialsPath = null) : IUsageProvider
{
    public static readonly Uri LoadCodeAssistEndpoint = new("https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist");
    public static readonly Uri QuotaEndpoint = new("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota");

    public ProviderId Id => ProviderId.Gemini;

    public bool IsAvailable() => GeminiCredentialsStore.TryLoad(credentialsPath) is not null;

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellation = default)
    {
        var credentials = await Task.Run(() => GeminiCredentialsStore.TryLoad(credentialsPath), cancellation).ConfigureAwait(false)
            ?? throw new UsageException(UsageErrorKind.NotLoggedIn);
        // The Gemini CLI renews its own token when it runs; this app never does.
        if (credentials.IsExpired) throw new UsageException(UsageErrorKind.UnauthorizedExpired);

        var account = await PostAsync(
            LoadCodeAssistEndpoint, """{"metadata":{"ideType":"IDE_UNSPECIFIED","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}}""",
            credentials, cancellation).ConfigureAwait(false);
        var project = GeminiUsage.Project(account) ?? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
        var plan = GeminiUsage.PlanName(account);

        var body = project is { Length: > 0 } ? new JsonObject { ["project"] = project }.ToJsonString() : "{}";
        var quota = await PostAsync(QuotaEndpoint, body, credentials, cancellation).ConfigureAwait(false);

        var snapshot = GeminiUsage.Snapshot(quota, plan) ?? throw new UsageException(UsageErrorKind.Decoding);
        return new ProviderFetchResult(snapshot, ProviderCache.From(plan, quota).Encode());
    }

    public ProviderSnapshot? SnapshotFromCache(byte[] cache) =>
        ProviderCache.Decode(cache) is { } decoded ? GeminiUsage.Snapshot(decoded.RawBytes, decoded.Plan) : null;

    private static Task<byte[]> PostAsync(Uri endpoint, string json, GeminiCredentials credentials, CancellationToken cancellation)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        return Send(request, cancellation);

        static async Task<byte[]> Send(HttpRequestMessage request, CancellationToken cancellation)
        {
            using (request) return await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        }
    }
}
