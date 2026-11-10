using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace AIUsageBar.Core;

/// <summary>
/// A GitHub token another tool already stored: the Copilot plugin of VS Code or
/// JetBrains, the <c>gh</c> CLI, or an environment variable. Read only: it is
/// never written, refreshed, cached or logged.
/// </summary>
public sealed record CopilotCredentials(string Token, string Source)
{
    /// <summary>Never prints the token.</summary>
    public override string ToString() => $"CopilotCredentials(source: {Source}, token: <redacted>)";
}

public static class CopilotCredentialsStore
{
    /// <summary>
    /// <c>%LOCALAPPDATA%\github-copilot</c> on Windows, <c>~/.config/github-copilot</c> elsewhere.
    /// </summary>
    public static string DefaultDirectory => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "github-copilot")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "github-copilot");

    private static readonly string[] Files = ["apps.json", "hosts.json"];

    /// <summary>
    /// The token from the editor plugin files, then from the environment.
    /// <c>gh auth token</c> is tried last, by <see cref="CopilotProvider"/>,
    /// because it starts a process.
    /// </summary>
    public static CopilotCredentials? Find(string? directory = null, Func<string, string?>? environment = null)
    {
        foreach (var file in Files)
        {
            try
            {
                var path = Path.Combine(directory ?? DefaultDirectory, file);
                if (File.Exists(path) && ParseHosts(File.ReadAllBytes(path)) is { } token)
                {
                    return new CopilotCredentials(token, file);
                }
            }
            catch (Exception)
            {
                // An unreadable file is the same as a missing one.
            }
        }

        var read = environment ?? Environment.GetEnvironmentVariable;
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
        {
            if (read(name) is { Length: > 0 } value) return new CopilotCredentials(value, name);
        }
        return null;
    }

    /// <summary>
    /// <c>apps.json</c>: <c>{"github.com:&lt;client id&gt;": {"user", "oauth_token"}}</c>;
    /// <c>hosts.json</c>: <c>{"github.com": {"user", "oauth_token"}}</c>.
    /// Only github.com entries count (not GitHub Enterprise hosts).
    /// </summary>
    public static string? ParseHosts(byte[] data)
    {
        if (Json.Parse(data) is not JsonObject root) return null;
        foreach (var (key, value) in root)
        {
            if (key != "github.com" && !key.StartsWith("github.com:", StringComparison.Ordinal)) continue;
            if (Json.Str(value, "oauth_token", "oauthToken") is { Length: > 0 } token) return token;
        }
        return null;
    }

    /// <summary>The token <c>gh</c> holds for github.com, or null.</summary>
    public static CopilotCredentials? FromGhCli()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gh", "auth token --hostname github.com")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 && output.Length > 0 ? new CopilotCredentials(output, "gh") : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>GET api.github.com/copilot_internal/user</c>: what the Copilot settings
/// page shows. Not documented by GitHub; every field is optional.
/// </summary>
public static class CopilotUsage
{
    public static ProviderSnapshot? Snapshot(byte[] data)
    {
        var root = Json.Parse(data);
        if (root is not JsonObject) return null;

        var reset = Json.Date(root, "quota_reset_date", "quotaResetDate", "limited_user_reset_date");
        var items = new List<LimitItem>();

        var snapshots = Json.Obj(root, "quota_snapshots", "quotaSnapshots");
        foreach (var (kind, key) in new[] { ("premium_requests", "premium_interactions"), ("chat", "chat"), ("completions", "completions") })
        {
            if (Percent(Json.Obj(snapshots, key)) is not { } percent) continue;
            items.Add(Item(kind, percent, reset));
        }

        // Free plan: monthly_quotas (limit) and limited_user_quotas (left).
        if (items.Count == 0)
        {
            var limits = Json.Obj(root, "monthly_quotas");
            var left = Json.Obj(root, "limited_user_quotas");
            foreach (var kind in new[] { "chat", "completions" })
            {
                if (Json.Num(limits, kind) is not { } limit || limit <= 0 || Json.Num(left, kind) is not { } remaining) continue;
                items.Add(Item(kind, (limit - remaining) / limit * 100, reset));
            }
        }

        if (items.Count == 0) return null;
        // The premium allowance first: it is the one that runs out.
        items = [.. items.OrderBy(i => i.Kind == "premium_requests" ? 0 : 1)];
        return new ProviderSnapshot(ProviderId.Copilot, PlanName(Json.Str(root, "copilot_plan", "copilotPlan")), items, [], []);
    }

    private static LimitItem Item(string kind, double percent, DateTimeOffset? reset)
    {
        percent = Math.Clamp(percent, 0, 999);
        return new LimitItem(
            $"copilot-{kind}", kind, null, percent, SeverityParser.From(null, percent), reset,
            kind == "premium_requests" ? LimitGroup.Weekly : LimitGroup.Other);
    }

    /// <summary>
    /// Percent used of one quota, or null for unlimited / unreadable ones.
    /// Prefers <c>percent_remaining</c>, then <c>entitlement</c> and <c>remaining</c>.
    /// </summary>
    private static double? Percent(JsonObject? quota)
    {
        if (quota is null || Json.Bool(quota, "unlimited") == true) return null;
        if (Json.Num(quota, "percent_remaining", "percentRemaining") is { } left && double.IsFinite(left)) return 100 - left;
        if (Json.Num(quota, "entitlement") is { } entitlement && entitlement > 0 && Json.Num(quota, "remaining") is { } remaining)
        {
            return (entitlement - remaining) / entitlement * 100;
        }
        return null;
    }

    /// <summary><c>individual_pro</c> → "Pro", <c>business</c> → "Business".</summary>
    internal static string? PlanName(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan)) return null;
        var words = plan.Replace("individual_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("copilot_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_plus", "+", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ');
        return BreakdownItem.Capitalize(words);
    }
}

public sealed class CopilotProvider(string? directory = null, Func<string, string?>? environment = null, bool useGhCli = true) : IUsageProvider
{
    public static readonly Uri Endpoint = new("https://api.github.com/copilot_internal/user");

    public ProviderId Id => ProviderId.Copilot;

    public bool IsAvailable() => Credentials() is not null;

    private static readonly TimeSpan GhLookupTtl = TimeSpan.FromMinutes(10);
    private readonly Lock gate = new();
    private CopilotCredentials? ghCredentials;
    private DateTimeOffset ghCheckedAt = DateTimeOffset.MinValue;

    private CopilotCredentials? Credentials() =>
        CopilotCredentialsStore.Find(directory, environment) ?? (useGhCli ? GhCredentials() : null);

    /// <summary>
    /// <c>IsAvailable</c> runs on every refresh, so the <c>gh</c> process is
    /// started at most once per <see cref="GhLookupTtl"/>.
    /// </summary>
    private CopilotCredentials? GhCredentials()
    {
        lock (gate)
        {
            if (DateTimeOffset.UtcNow - ghCheckedAt > GhLookupTtl || (ghCredentials is null && DateTimeOffset.UtcNow - ghCheckedAt > TimeSpan.FromMinutes(1)))
            {
                ghCredentials = CopilotCredentialsStore.FromGhCli();
                ghCheckedAt = DateTimeOffset.UtcNow;
            }
            return ghCredentials;
        }
    }

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellation = default)
    {
        var credentials = await Task.Run(Credentials, cancellation).ConfigureAwait(false)
            ?? throw new UsageException(UsageErrorKind.NotLoggedIn);

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", credentials.Token);
        // The endpoint answers editor clients; identify as one.
        request.Headers.Add("Editor-Version", "vscode/1.99.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var data = await Http.SendAsync(request, cancellation).ConfigureAwait(false);

        var snapshot = CopilotUsage.Snapshot(data) ?? throw new UsageException(UsageErrorKind.Decoding);
        return new ProviderFetchResult(snapshot, ProviderCache.From(snapshot.PlanName, data).Encode());
    }

    public ProviderSnapshot? SnapshotFromCache(byte[] cache) =>
        ProviderCache.Decode(cache) is { } decoded ? CopilotUsage.Snapshot(decoded.RawBytes) : null;
}
