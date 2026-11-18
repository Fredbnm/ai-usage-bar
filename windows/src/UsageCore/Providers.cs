using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIUsageBar.Core;

public sealed record ProviderFetchResult(ProviderSnapshot Snapshot, byte[] Cache);

public interface IUsageProvider
{
    ProviderId Id { get; }

    /// <summary>
    /// Whether the service looks set up on this machine. Services that are not
    /// available are hidden instead of showing an error.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// For the Agents tab: whether the login is there. Defaults to
    /// <see cref="IsAvailable"/>; Claude overrides it because it is always
    /// shown even without a login.
    /// </summary>
    SetupStatus Detect() => IsAvailable() ? SetupStatus.Connected : SetupStatus.NeedsLogin;

    Task<ProviderFetchResult> FetchAsync(CancellationToken cancellation = default);

    ProviderSnapshot? SnapshotFromCache(byte[] cache);

    /// <summary>
    /// Which login this agent would use right now, or null when it has no way
    /// to tell its logins apart. A wait earned by one login does not apply to
    /// the next, so the store asks again as soon as this changes.
    /// </summary>
    string? LoginIdentity() => null;
}

/// <summary>
/// What gets persisted between launches: the plan name (it comes from the
/// login, not the usage response) and the raw usage response. Never a token.
/// </summary>
internal sealed record ProviderCache(string? Plan, string Raw, string? Account = null)
{
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static ProviderCache? Decode(byte[] data)
    {
        try { return JsonSerializer.Deserialize<ProviderCache>(data); }
        catch (JsonException) { return null; }
    }

    public static ProviderCache From(string? plan, byte[] raw, string? account = null) =>
        new(plan, Convert.ToBase64String(raw), account);

    public byte[] RawBytes => Convert.FromBase64String(Raw);
}

internal static class Http
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Sends the request and maps HTTP failures to <see cref="UsageException"/>. <paramref name="client"/>:
    /// one with other settings (the status pages' shorter timeout); the shared one by default.
    /// </summary>
    public static async Task<byte[]> SendAsync(HttpRequestMessage request, CancellationToken cancellation, HttpClient? client = null)
    {
        request.Headers.UserAgent.ParseAdd("ai-usage-bar");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await (client ?? Client).SendAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            throw new UsageException(UsageErrorKind.Network, inner: error);
        }
        using (response)
        {
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
            throw Failure((int)response.StatusCode, name => Header(response, name), DateTimeOffset.Now);
        }
    }

    /// <summary>The error for a failed response; <paramref name="header"/> looks a response header up by name.</summary>
    public static UsageException Failure(int status, Func<string, string?> header, DateTimeOffset now)
    {
        var retryAfter = header("Retry-After");
        var remaining = header("x-ratelimit-remaining");
        if (RateLimitBackoff.IsRateLimit(status, retryAfter, remaining))
        {
            var wait = RateLimitBackoff.ServerWait(retryAfter, remaining, header("x-ratelimit-reset"), now);
            return new UsageException(UsageErrorKind.RateLimited, status, retryAfter: wait);
        }
        return status is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden
            ? new UsageException(UsageErrorKind.Unauthorized, status)
            : new UsageException(UsageErrorKind.Http, status);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}

public sealed class ClaudeProvider(string? credentialsPath = null) : IUsageProvider
{
    public static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    public const string BetaHeader = "oauth-2025-04-20";

    public ProviderId Id => ProviderId.Claude;

    /// <summary>Always shown: a missing login is reported instead of hidden.</summary>
    public bool IsAvailable() => true;

    public SetupStatus Detect() =>
        File.Exists(credentialsPath ?? ClaudeCredentialsStore.DefaultPath) ? SetupStatus.Connected : SetupStatus.NeedsLogin;

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellation = default)
    {
        var credentials = await Task.Run(() => ClaudeCredentialsStore.Load(credentialsPath), cancellation).ConfigureAwait(false);

        byte[] data;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
            request.Headers.Add("anthropic-beta", BetaHeader);
            data = await Http.SendAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (UsageException error) when (error.Kind == UsageErrorKind.Unauthorized && credentials.IsExpired)
        {
            throw new UsageException(UsageErrorKind.UnauthorizedExpired, error.StatusCode);
        }

        var snapshot = SnapshotFromResponse(data, credentials.PlanName) ?? throw new UsageException(UsageErrorKind.Decoding);
        return new ProviderFetchResult(
            snapshot,
            ProviderCache.From(credentials.PlanName, data, credentials.Account).Encode());
    }

    public ProviderSnapshot? SnapshotFromCache(byte[] cache) => SnapshotFromCache(cache, CurrentAccount());

    /// <summary>
    /// <paramref name="account"/>: the login on this machine now, or null when
    /// it could not be read. Numbers cached under a different account are
    /// dropped instead of being shown as yours; signing in elsewhere otherwise
    /// left the previous account's percentages on screen.
    /// </summary>
    internal static ProviderSnapshot? SnapshotFromCache(byte[] cache, string? account)
    {
        if (ProviderCache.Decode(cache) is not { } decoded) return null;
        // Only when the mismatch is certain: a login that cannot be read right
        // now is no reason to blank numbers that are probably still ours.
        if (decoded.Account is { } cached && account is not null && cached != account) return null;
        return SnapshotFromResponse(decoded.RawBytes, decoded.Plan);
    }

    public string? LoginIdentity() => TryLoad()?.LoginIdentity;

    private string? CurrentAccount() => TryLoad()?.Account;

    private ClaudeCredentials? TryLoad()
    {
        try { return ClaudeCredentialsStore.Load(credentialsPath); }
        catch (UsageException) { return null; }
    }

    public static ProviderSnapshot? SnapshotFromResponse(byte[] data, string? plan)
    {
        if (ClaudeUsage.Parse(data) is not { Items.Count: > 0 } parsed) return null;
        return new ProviderSnapshot(ProviderId.Claude, plan, parsed.Items, parsed.Breakdown, []);
    }
}

/// <summary>
/// Reads Grok Bot's login: its tokens sit in <c>sand-secrets.json</c>,
/// encrypted with Electron <c>safeStorage</c>. On Windows its key is protected by DPAPI
/// in "Local State"; on Linux (when a <paramref name="keyring"/> is given) the values
/// follow Chromium's Linux format, see <see cref="LinuxSafeStorage"/>. The token is
/// decrypted in memory for the request and is never written, refreshed or logged.
/// </summary>
public sealed class GrokBotProvider(ISecretUnprotector? unprotector, string? dataDirectory = null, IKeyringReader? keyring = null) : IUsageProvider
{
    public static readonly Uri Endpoint = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus");

    private readonly string directory = dataDirectory ?? GrokBotPaths.DefaultDataDirectory;
    private readonly Lock gate = new();
    private byte[]? key;
    private string? keyringPassword;

    public ProviderId Id => ProviderId.GrokBot;

    public bool IsAvailable() => File.Exists(GrokBotPaths.SecretsFile(directory));

    public async Task<ProviderFetchResult> FetchAsync(CancellationToken cancellation = default)
    {
        var token = await Task.Run(AccessToken, cancellation).ConfigureAwait(false);
        var isExpired = Jwt.Expiration(token) is { } expires && expires < DateTimeOffset.UtcNow;
        if (isExpired) throw new UsageException(UsageErrorKind.UnauthorizedExpired);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Connect-Protocol-Version", "1");
        // Grok Bot's privacy flag; we never send content, only read usage.
        request.Headers.Add("x-ghost-mode", "true");
        var data = await Http.SendAsync(request, cancellation).ConfigureAwait(false);

        var snapshot = GrokUsage.Snapshot(data) ?? throw new UsageException(UsageErrorKind.Decoding);
        return new ProviderFetchResult(snapshot, ProviderCache.From(null, data).Encode());
    }

    public ProviderSnapshot? SnapshotFromCache(byte[] cache) =>
        ProviderCache.Decode(cache) is { } decoded ? GrokUsage.Snapshot(decoded.RawBytes) : null;

    internal string AccessToken()
    {
        byte[] file;
        try { file = File.ReadAllBytes(GrokBotPaths.SecretsFile(directory)); }
        catch (Exception error) { throw new UsageException(UsageErrorKind.NotLoggedIn, inner: error); }

        switch (GrokBotSecrets.ActiveAccessToken(file))
        {
            case StoredToken.Plaintext plain:
                return plain.Token;
            case StoredToken.Encrypted encrypted when keyring is not null:
                try
                {
                    return LinuxSafeStorage.Decrypt(encrypted.Blob, LinuxSafeStorage.NeedsKeyring(encrypted.Blob) ? KeyringPassword(keyring) : null);
                }
                catch (UsageException)
                {
                    lock (gate) { keyringPassword = null; }
                    throw;
                }
            case StoredToken.Encrypted encrypted:
                try
                {
                    return ElectronSafeStorage.Decrypt(encrypted.Blob, Key());
                }
                catch (UsageException)
                {
                    // The key may have been rotated: read it again next time.
                    lock (gate) { key = null; }
                    throw;
                }
            default:
                throw new UsageException(UsageErrorKind.Decoding);
        }
    }

    /// <summary>Linux: the keyring's password for "v11" values, read once per run and kept in memory only.</summary>
    private string KeyringPassword(IKeyringReader reader)
    {
        lock (gate)
        {
            if (keyringPassword is not null) return keyringPassword;
            foreach (var application in GrokBotPaths.KeyringApplications)
            {
                if (reader.Password(application) is { Length: > 0 } password) return keyringPassword = password;
            }
            throw new UsageException(UsageErrorKind.SecretUnavailable);
        }
    }

    /// <summary>Read once per run and kept in memory only.</summary>
    private byte[] Key()
    {
        lock (gate)
        {
            if (key is not null) return key;
            if (unprotector is null) throw new UsageException(UsageErrorKind.SecretUnavailable);
            byte[] localState;
            try { localState = File.ReadAllBytes(GrokBotPaths.LocalStateFile(directory)); }
            catch (Exception error) { throw new UsageException(UsageErrorKind.NotLoggedIn, inner: error); }
            try
            {
                key = unprotector.Unprotect(ElectronSafeStorage.ProtectedKey(localState));
            }
            catch (Exception error) when (error is not UsageException)
            {
                throw new UsageException(UsageErrorKind.SecretUnavailable, inner: error);
            }
            return key;
        }
    }
}
