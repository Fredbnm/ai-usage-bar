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

    Task<ProviderFetchResult> FetchAsync(CancellationToken cancellation = default);

    ProviderSnapshot? SnapshotFromCache(byte[] cache);
}

/// <summary>
/// What gets persisted between launches: the plan name (it comes from the
/// login, not the usage response) and the raw usage response. Never a token.
/// </summary>
internal sealed record ProviderCache(string? Plan, string Raw)
{
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(this);

    public static ProviderCache? Decode(byte[] data)
    {
        try { return JsonSerializer.Deserialize<ProviderCache>(data); }
        catch (JsonException) { return null; }
    }

    public static ProviderCache From(string? plan, byte[] raw) => new(plan, Convert.ToBase64String(raw));

    public byte[] RawBytes => Convert.FromBase64String(Raw);
}

internal static class Http
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Sends the request and maps HTTP failures to <see cref="UsageException"/>.</summary>
    public static async Task<byte[]> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        request.Headers.UserAgent.ParseAdd("ai-usage-bar");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try
        {
            response = await Client.SendAsync(request, cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            throw new UsageException(UsageErrorKind.Network, inner: error);
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new UsageException(UsageErrorKind.Unauthorized, status),
                HttpStatusCode.TooManyRequests => new UsageException(UsageErrorKind.RateLimited, status),
                _ => new UsageException(UsageErrorKind.Http, status),
            };
        }
    }
}

public sealed class ClaudeProvider(string? credentialsPath = null) : IUsageProvider
{
    public static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    public const string BetaHeader = "oauth-2025-04-20";

    public ProviderId Id => ProviderId.Claude;

    /// <summary>Always shown: a missing login is reported instead of hidden.</summary>
    public bool IsAvailable() => true;

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
        return new ProviderFetchResult(snapshot, ProviderCache.From(credentials.PlanName, data).Encode());
    }

    public ProviderSnapshot? SnapshotFromCache(byte[] cache) =>
        ProviderCache.Decode(cache) is { } decoded ? SnapshotFromResponse(decoded.RawBytes, decoded.Plan) : null;

    public static ProviderSnapshot? SnapshotFromResponse(byte[] data, string? plan)
    {
        if (ClaudeUsage.Parse(data) is not { Items.Count: > 0 } parsed) return null;
        return new ProviderSnapshot(ProviderId.Claude, plan, parsed.Items, parsed.Breakdown, []);
    }
}

/// <summary>
/// Reads Grok Bot's login: its tokens sit in <c>sand-secrets.json</c>,
/// encrypted with Electron <c>safeStorage</c>, whose key is protected by DPAPI
/// in "Local State". The token is decrypted in memory for the request and is
/// never written, refreshed or logged.
/// </summary>
public sealed class GrokBotProvider(ISecretUnprotector? unprotector, string? dataDirectory = null) : IUsageProvider
{
    public static readonly Uri Endpoint = new("https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus");

    private readonly string directory = dataDirectory ?? GrokBotPaths.DefaultDataDirectory;
    private readonly Lock gate = new();
    private byte[]? key;

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
