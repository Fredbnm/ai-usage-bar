using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar.Core;

/// <summary>Decrypts data protected for the current Windows user (DPAPI).</summary>
public interface ISecretUnprotector
{
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>Where Grok Bot (an Electron app) keeps its data on Windows and Linux.</summary>
public static class GrokBotPaths
{
    /// <summary>
    /// Electron's <c>userData</c> folder: <c>%APPDATA%\Grok Bot</c> on Windows,
    /// <c>~/.config/Grok Bot</c> (or <c>$XDG_CONFIG_HOME/Grok Bot</c>) on Linux.
    /// </summary>
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Grok Bot");

    /// <summary>Names Electron may have stored the keyring entry under on Linux (the app's name).</summary>
    public static readonly IReadOnlyList<string> KeyringApplications = ["Grok Bot", "grok-bot", "grokbot"];

    public static string SecretsFile(string dataDirectory) => Path.Combine(dataDirectory, "sand-secrets.json");

    /// <summary>Chromium's "Local State": holds the DPAPI-protected encryption key.</summary>
    public static string LocalStateFile(string dataDirectory) => Path.Combine(dataDirectory, "Local State");
}

public abstract record StoredToken
{
    public sealed record Encrypted(byte[] Blob) : StoredToken;
    public sealed record Plaintext(string Token) : StoredToken;
}

/// <summary>Locates the active account's access token inside <c>sand-secrets.json</c>.</summary>
public static class GrokBotSecrets
{
    private const string AccessTokenKey = "cursor-access-token";
    private const string ScopedPrefix = "scoped:v1:";
    private const string PlaintextPrefix = "plaintext:v1:";

    /// <summary>
    /// Layout (Grok Bot 0.79):
    /// <c>{"version":1, "cursor-accounts":"{\"active\":\"&lt;scope&gt;\",\"accounts\":{\"&lt;scope&gt;\":{\"cursor-access-token\":\"&lt;base64&gt;\"}}}"}</c>.
    /// Older files keep <c>cursor-access-token</c> at the top level, optionally
    /// as <c>scoped:v1:&lt;64 hex&gt;:&lt;base64&gt;</c>.
    /// </summary>
    public static StoredToken ActiveAccessToken(byte[] fileData)
    {
        var root = Json.Parse(fileData) as JsonObject ?? throw new UsageException(UsageErrorKind.Decoding);

        string? value = null;
        if (Json.Str(root, "cursor-accounts") is { } accountsJson &&
            Json.Parse(Encoding.UTF8.GetBytes(accountsJson)) is JsonObject accounts &&
            Json.Str(accounts, "active") is { } active)
        {
            value = Json.Str(Json.Obj(Json.Obj(accounts, "accounts"), active), AccessTokenKey);
        }
        value ??= Json.Str(root, AccessTokenKey);
        if (string.IsNullOrEmpty(value)) throw new UsageException(UsageErrorKind.NotLoggedIn);

        try
        {
            if (value.StartsWith(PlaintextPrefix, StringComparison.Ordinal))
            {
                return new StoredToken.Plaintext(Encoding.UTF8.GetString(Convert.FromBase64String(value[PlaintextPrefix.Length..])));
            }
            var base64 = value;
            if (value.StartsWith(ScopedPrefix, StringComparison.Ordinal))
            {
                var colon = value.IndexOf(':', ScopedPrefix.Length);
                if (colon < 0) throw new UsageException(UsageErrorKind.Decoding);
                base64 = value[(colon + 1)..];
            }
            return new StoredToken.Encrypted(Convert.FromBase64String(base64));
        }
        catch (FormatException error)
        {
            throw new UsageException(UsageErrorKind.Decoding, inner: error);
        }
    }
}

/// <summary>
/// Chromium/Electron <c>OSCrypt</c> on Windows. The AES-256 key sits in
/// "Local State" as base64("DPAPI" + DPAPI blob); each value is
/// "v10" + 12-byte nonce + ciphertext + 16-byte GCM tag.
/// </summary>
public static class ElectronSafeStorage
{
    private static readonly byte[] DpapiPrefix = "DPAPI"u8.ToArray();
    private static readonly byte[] VersionPrefix = "v10"u8.ToArray();
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>The DPAPI blob of the key, from the "Local State" JSON.</summary>
    public static byte[] ProtectedKey(byte[] localStateJson)
    {
        var encoded = Json.Str(Json.Obj(Json.Parse(localStateJson), "os_crypt"), "encrypted_key")
            ?? throw new UsageException(UsageErrorKind.NotLoggedIn);
        byte[] raw;
        try { raw = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw new UsageException(UsageErrorKind.Decoding, inner: error); }
        if (!raw.AsSpan().StartsWith(DpapiPrefix)) throw new UsageException(UsageErrorKind.Decoding);
        return raw[DpapiPrefix.Length..];
    }

    public static string Decrypt(byte[] blob, byte[] key)
    {
        if (blob.Length < VersionPrefix.Length + NonceSize + TagSize || !blob.AsSpan().StartsWith(VersionPrefix))
        {
            throw new UsageException(UsageErrorKind.Decoding);
        }
        var nonce = blob.AsSpan(VersionPrefix.Length, NonceSize);
        var cipherStart = VersionPrefix.Length + NonceSize;
        var cipherLength = blob.Length - cipherStart - TagSize;
        var ciphertext = blob.AsSpan(cipherStart, cipherLength);
        var tag = blob.AsSpan(blob.Length - TagSize, TagSize);
        var plaintext = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException error)
        {
            throw new UsageException(UsageErrorKind.Decoding, inner: error);
        }
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>The inverse of <see cref="Decrypt"/>; used by tests.</summary>
    public static byte[] Encrypt(string text, byte[] key, byte[] nonce)
    {
        var plaintext = Encoding.UTF8.GetBytes(text);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return [.. VersionPrefix, .. nonce, .. ciphertext, .. tag];
    }
}

/// <summary>
/// The desktop keyring on Linux (GNOME Keyring, KWallet… through libsecret): where Electron keeps the
/// password behind its "v11" values. Returns null when there is no such entry or no keyring.
/// </summary>
public interface IKeyringReader
{
    string? Password(string application);
}

/// <summary>
/// Chromium/Electron <c>OSCrypt</c> on Linux. "v10" values were written without a keyring and use a fixed
/// password; "v11" values use a password kept in the keyring under the app's name. Either way:
/// AES-128-CBC, IV of 16 spaces, key = PBKDF2-SHA1(password, "saltysalt", 1 iteration, 16 bytes).
/// </summary>
public static class LinuxSafeStorage
{
    public const string FixedPassword = "peanuts";
    private static readonly byte[] Salt = "saltysalt"u8.ToArray();
    private static readonly byte[] Iv = [.. Enumerable.Repeat((byte)' ', 16)];
    private const int PrefixSize = 3;

    /// <summary>Whether the value needs the keyring's password ("v11").</summary>
    public static bool NeedsKeyring(byte[] blob) => blob.AsSpan().StartsWith("v11"u8);

    /// <param name="blob">"v10" or "v11" followed by the ciphertext.</param>
    /// <param name="keyringPassword">Only read for "v11" values; null then means the keyring did not answer.</param>
    public static string Decrypt(byte[] blob, string? keyringPassword)
    {
        var password = blob.AsSpan().StartsWith("v10"u8) ? FixedPassword
            : NeedsKeyring(blob) ? keyringPassword ?? throw new UsageException(UsageErrorKind.SecretUnavailable)
            : throw new UsageException(UsageErrorKind.Decoding);
        if (blob.Length <= PrefixSize || (blob.Length - PrefixSize) % 16 != 0) throw new UsageException(UsageErrorKind.Decoding);
        try
        {
            using var aes = Aes.Create();
            aes.Key = Key(password);
            return Encoding.UTF8.GetString(aes.DecryptCbc(blob.AsSpan(PrefixSize), Iv, PaddingMode.PKCS7));
        }
        catch (CryptographicException error)
        {
            throw new UsageException(UsageErrorKind.Decoding, inner: error);
        }
    }

    /// <summary>The inverse of <see cref="Decrypt"/>; used by tests.</summary>
    public static byte[] Encrypt(string text, string version, string password)
    {
        using var aes = Aes.Create();
        aes.Key = Key(password);
        return [.. Encoding.ASCII.GetBytes(version), .. aes.EncryptCbc(Encoding.UTF8.GetBytes(text), Iv, PaddingMode.PKCS7)];
    }

    private static byte[] Key(string password) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Salt, 1, HashAlgorithmName.SHA1, 16);
}

internal static class Jwt
{
    /// <summary>
    /// The <c>exp</c> claim, if the token is a JWT. The signature is not
    /// checked: this only picks a better error message.
    /// </summary>
    public static DateTimeOffset? Expiration(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        try
        {
            var exp = Json.Num(Json.Parse(Convert.FromBase64String(payload)), "exp");
            return exp is { } seconds ? DateTimeOffset.FromUnixTimeSeconds((long)seconds) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// <c>aiserver.v1.DashboardService/GetSandUsageStatus</c>: what Grok Bot's own
/// Settings → Usage screen shows. Connect/proto3 JSON.
/// </summary>
public static class GrokUsage
{
    public static ProviderSnapshot? Snapshot(byte[] data, DateTimeOffset? now = null)
    {
        var root = Json.Parse(data);
        if (root is not JsonObject) return null;
        var current = now ?? DateTimeOffset.UtcNow;

        var items = new List<LimitItem>();
        var pooled = Json.Bool(root, "usesPooledEnterpriseAllowance", "uses_pooled_enterprise_allowance") == true;
        if (!pooled && Json.Num(root, "usagePercent", "usage_percent") is { } raw && double.IsFinite(raw))
        {
            var percent = Math.Max(raw, 0);
            var exhausted = Json.Bool(root, "hasAvailableUsage", "has_available_usage") == false;
            items.Add(new LimitItem(
                "grok-weekly", "weekly", null, percent,
                exhausted ? Severity.Critical : SeverityParser.From(null, percent),
                Json.Date(root, "nextResetTimestampUtc", "next_reset_timestamp_utc"),
                LimitGroup.Weekly));
        }

        var notes = new List<SnapshotNote>();
        if (pooled) notes.Add(new SnapshotNote(NoteKind.PooledAllowance));
        if (Json.Num(root, "availableBankedResetCount", "available_banked_reset_count") is { } resets && resets > 0)
        {
            notes.Add(new SnapshotNote(NoteKind.BankedResets, Count: (int)resets));
        }
        if (Json.Date(root, "sandTrialExpiresAt", "sand_trial_expires_at") is { } trialEnd && trialEnd > current)
        {
            notes.Add(new SnapshotNote(NoteKind.TrialUntil, Date: trialEnd));
        }
        if (Json.Bool(Json.Obj(root, "onDemandSettings", "on_demand_settings"), "enabled") == true)
        {
            notes.Add(new SnapshotNote(NoteKind.OnDemandEnabled));
        }

        if (items.Count == 0 && notes.Count == 0) return null;
        return new ProviderSnapshot(ProviderId.GrokBot, PlanName(root), items, [], notes);
    }

    /// <summary>
    /// <c>grokPlanLabel</c> is often the generic "Grok Bot Plan"; the Cursor
    /// plan name ("Premium", "Pro"…) says more then.
    /// </summary>
    internal static string? PlanName(JsonNode root)
    {
        var label = Json.Str(root, "grokPlanLabel", "grok_plan_label")?.Trim() ?? "";
        var core = label;
        foreach (var affix in new[] { "Grok Bot", "Plan" })
        {
            if (core.StartsWith(affix, StringComparison.OrdinalIgnoreCase)) core = core[affix.Length..].Trim();
            if (core.EndsWith(affix, StringComparison.OrdinalIgnoreCase)) core = core[..^affix.Length].Trim();
        }
        if (core.Length > 0) return label;
        var cursor = Json.Str(root, "cursorPlanName", "cursor_plan_name")?.Trim() ?? "";
        return cursor.Length == 0 ? null : char.ToUpperInvariant(cursor[0]) + cursor[1..];
    }
}
