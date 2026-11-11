using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using SkiaSharp;

namespace UsageCore.Tests;

public class GrokBotTests
{
    public GrokBotTests() => Localization.Current = AppLanguage.Portuguese;

    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Nonce = Enumerable.Range(100, 12).Select(i => (byte)i).ToArray();

    /// <summary>Stands in for DPAPI: "protected" bytes are the key reversed.</summary>
    private sealed class FakeUnprotector : ISecretUnprotector
    {
        public byte[] Unprotect(byte[] protectedData) => [.. protectedData.Reverse()];
    }

    [Fact]
    public void ElectronSafeStorageRoundTrip()
    {
        var blob = ElectronSafeStorage.Encrypt("hello grok bot", Key, Nonce);
        Assert.Equal("v10"u8.ToArray(), blob[..3]);
        Assert.Equal("hello grok bot", ElectronSafeStorage.Decrypt(blob, Key));

        var wrongKey = Key.Select(b => (byte)(b ^ 0xFF)).ToArray();
        Assert.Throws<UsageException>(() => ElectronSafeStorage.Decrypt(blob, wrongKey));
        Assert.Throws<UsageException>(() => ElectronSafeStorage.Decrypt("v11short"u8.ToArray(), Key));
    }

    [Fact]
    public void ReadsTheProtectedKeyFromLocalState()
    {
        var localState = LocalState([1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, ElectronSafeStorage.ProtectedKey(localState));
        Assert.Throws<UsageException>(() => ElectronSafeStorage.ProtectedKey("""{"os_crypt":{"encrypted_key":"AAAA"}}"""u8.ToArray()));
    }

    [Fact]
    public void FindsTheActiveAccountToken()
    {
        var blob = Convert.ToBase64String(ElectronSafeStorage.Encrypt("token", Key, Nonce));
        var accounts = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["active"] = "aaa",
            ["accounts"] = new Dictionary<string, object>
            {
                ["bbb"] = new Dictionary<string, string> { ["cursor-access-token"] = "T3RoZXI=" },
                ["aaa"] = new Dictionary<string, string> { ["cursor-access-token"] = blob },
            },
        });
        var file = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["version"] = 1, ["cursor-accounts"] = accounts });
        var stored = Assert.IsType<StoredToken.Encrypted>(GrokBotSecrets.ActiveAccessToken(file));
        Assert.Equal("token", ElectronSafeStorage.Decrypt(stored.Blob, Key));
    }

    [Fact]
    public void ReadsLegacyScopedAndPlaintextTokens()
    {
        var blob = Convert.ToBase64String(ElectronSafeStorage.Encrypt("token", Key, Nonce));
        var scoped = Encoding.UTF8.GetBytes($$"""{"cursor-access-token": "scoped:v1:{{new string('a', 64)}}:{{blob}}"}""");
        Assert.IsType<StoredToken.Encrypted>(GrokBotSecrets.ActiveAccessToken(scoped));

        var plain = Encoding.UTF8.GetBytes($$"""{"cursor-access-token": "plaintext:v1:{{Convert.ToBase64String("tok"u8.ToArray())}}"}""");
        Assert.Equal("tok", Assert.IsType<StoredToken.Plaintext>(GrokBotSecrets.ActiveAccessToken(plain)).Token);

        var error = Assert.Throws<UsageException>(() => GrokBotSecrets.ActiveAccessToken("""{"version":1}"""u8.ToArray()));
        Assert.Equal(UsageErrorKind.NotLoggedIn, error.Kind);
    }

    [Fact]
    public void ProviderDecryptsTheLoginWithDpapiKey()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var accounts = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["active"] = "s",
                ["accounts"] = new Dictionary<string, object>
                {
                    ["s"] = new Dictionary<string, string> { ["cursor-access-token"] = Convert.ToBase64String(ElectronSafeStorage.Encrypt("the-token", Key, Nonce)) },
                },
            });
            File.WriteAllText(GrokBotPaths.SecretsFile(directory), JsonSerializer.Serialize(new Dictionary<string, object> { ["version"] = 1, ["cursor-accounts"] = accounts }));
            File.WriteAllBytes(GrokBotPaths.LocalStateFile(directory), LocalState([.. Key.Reverse()]));

            var provider = new GrokBotProvider(new FakeUnprotector(), directory);
            Assert.True(provider.IsAvailable());
            Assert.Equal("the-token", provider.AccessToken());

            var noDpapi = new GrokBotProvider(null, directory);
            Assert.Equal(UsageErrorKind.SecretUnavailable, Assert.Throws<UsageException>(noDpapi.AccessToken).Kind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Stands in for the Linux keyring.</summary>
    private sealed class FakeKeyring(string application, string password) : IKeyringReader
    {
        public int Lookups { get; private set; }

        public string? Password(string name)
        {
            Lookups++;
            return name == application ? password : null;
        }
    }

    [Fact]
    public void LinuxSafeStorageRoundTrip()
    {
        // "v10": written without a keyring, fixed password.
        var v10 = LinuxSafeStorage.Encrypt("no keyring", "v10", LinuxSafeStorage.FixedPassword);
        Assert.False(LinuxSafeStorage.NeedsKeyring(v10));
        Assert.Equal("no keyring", LinuxSafeStorage.Decrypt(v10, keyringPassword: null));

        // "v11": the password comes from the keyring.
        var v11 = LinuxSafeStorage.Encrypt("from the keyring", "v11", "s3cret");
        Assert.True(LinuxSafeStorage.NeedsKeyring(v11));
        Assert.Equal("from the keyring", LinuxSafeStorage.Decrypt(v11, "s3cret"));
        Assert.Equal(UsageErrorKind.SecretUnavailable, Assert.Throws<UsageException>(() => LinuxSafeStorage.Decrypt(v11, null)).Kind);
        Assert.Equal(UsageErrorKind.Decoding, Assert.Throws<UsageException>(() => LinuxSafeStorage.Decrypt(v11, "wrong")).Kind);
        Assert.Equal(UsageErrorKind.Decoding, Assert.Throws<UsageException>(() => LinuxSafeStorage.Decrypt("v12abc"u8.ToArray(), "x")).Kind);
    }

    /// <summary>
    /// A known Chromium value: "v10" + AES-128-CBC of "hello" with the fixed password, as Chrome on Linux
    /// writes it without a keyring. Guards the key derivation against drifting away from Chromium's.
    /// </summary>
    [Fact]
    public void LinuxSafeStorageMatchesChromiumsKeyDerivation()
    {
        var key = Rfc2898DeriveBytes.Pbkdf2("peanuts"u8, "saltysalt"u8, 1, HashAlgorithmName.SHA1, 16);
        Assert.Equal("fd621fe5a2b402539dfa147ca9272778", Convert.ToHexStringLower(key));
    }

    [Fact]
    public void ReadsTheTokenWithTheLinuxKeyring()
    {
        var directory = Path.Combine(Path.GetTempPath(), "grokbot-linux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var blob = LinuxSafeStorage.Encrypt("the-linux-token", "v11", "keyring-password");
            File.WriteAllText(GrokBotPaths.SecretsFile(directory),
                JsonSerializer.Serialize(new Dictionary<string, object> { ["cursor-access-token"] = Convert.ToBase64String(blob) }));

            var keyring = new FakeKeyring("grok-bot", "keyring-password");
            var provider = new GrokBotProvider(null, directory, keyring);
            Assert.Equal("the-linux-token", provider.AccessToken());
            var lookups = keyring.Lookups;
            Assert.Equal("the-linux-token", provider.AccessToken());
            Assert.Equal(lookups, keyring.Lookups); // kept in memory for the run

            var noEntry = new GrokBotProvider(null, directory, new FakeKeyring("someone-else", "x"));
            Assert.Equal(UsageErrorKind.SecretUnavailable, Assert.Throws<UsageException>(noEntry.AccessToken).Kind);

            // Written without a keyring ("v10"): no lookup at all.
            File.WriteAllText(GrokBotPaths.SecretsFile(directory), JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["cursor-access-token"] = Convert.ToBase64String(LinuxSafeStorage.Encrypt("basic-token", "v10", LinuxSafeStorage.FixedPassword)),
            }));
            var unused = new FakeKeyring("grok-bot", "x");
            Assert.Equal("basic-token", new GrokBotProvider(null, directory, unused).AccessToken());
            Assert.Equal(0, unused.Lookups);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReadsJwtExpiration()
    {
        // {"alg":"none"}.{"exp":4102444800}.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4_102_444_800), Jwt.Expiration("eyJhbGciOiJub25lIn0.eyJleHAiOjQxMDI0NDQ4MDB9."));
        Assert.Null(Jwt.Expiration("not-a-jwt"));
    }

    [Fact]
    public void SnapshotFromTheRealResponseShape()
    {
        var json = """
        {"currentPeriodStart":"2026-11-07T04:47:36.822Z","nextResetTimestampUtc":"2026-11-14T04:47:36.822Z",
         "usagePercent":0.30335861321776814,"hasAvailableUsage":true,"hasNonZeroIncludedLimit":true,
         "onDemandSettings":{"visible":true,"eligible":true},"availableBankedResetCount":"2",
         "grokPlanLabel":"Grok Bot Plan","cursorPlanName":"Premium","prepaidAvailableCents":"0"}
        """;
        var snapshot = GrokUsage.Snapshot(Encoding.UTF8.GetBytes(json))!;
        Assert.Equal("Premium", snapshot.PlanName);
        Assert.Equal("Semanal", snapshot.Items.Single().Title);
        Assert.Equal("<1%", UsageFormat.Percent(snapshot.Items.Single().Percent));
        Assert.Equal(["2 resets guardados disponíveis"], snapshot.Notes.Select(n => n.Text));
        Localization.Current = AppLanguage.English;
        Assert.Equal(["2 banked resets available"], snapshot.Notes.Select(n => n.Text));
    }

    [Fact]
    public void ExhaustedPooledAndEmptyResponses()
    {
        var exhausted = GrokUsage.Snapshot("""{"usage_percent": 100, "has_available_usage": false}"""u8.ToArray())!;
        Assert.Equal(Severity.Critical, exhausted.Items.Single().Severity);
        var pooled = GrokUsage.Snapshot("""{"usesPooledEnterpriseAllowance": true, "usagePercent": 12}"""u8.ToArray())!;
        Assert.Empty(pooled.Items);
        Assert.Equal(NoteKind.PooledAllowance, pooled.Notes.Single().Kind);
        Assert.Null(GrokUsage.Snapshot("{}"u8.ToArray()));
        Assert.Equal("SuperGrok Heavy", GrokUsage.Snapshot("""{"usagePercent": 1, "grokPlanLabel": "SuperGrok Heavy"}"""u8.ToArray())!.PlanName);
    }

    private static byte[] LocalState(byte[] protectedKey) => Encoding.UTF8.GetBytes(
        $$$"""{"os_crypt":{"encrypted_key":"{{{Convert.ToBase64String([.. "DPAPI"u8.ToArray(), .. protectedKey])}}}"}}""");
}

public class RenderingTests
{
    /// <summary>An orange tile with a white plus sign, like a tiny app icon.</summary>
    private static SKBitmap FakeIcon(bool withArtwork)
    {
        var bitmap = new SKBitmap(128, 128);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using var tile = new SKPaint { Color = new SKColor(0xD9, 0x77, 0x57), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(12, 12, 116, 116), 24, 24, tile);
        if (withArtwork)
        {
            using var white = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(new SKRect(58, 30, 70, 98), white);
            canvas.DrawRect(new SKRect(30, 58, 98, 70), white);
        }
        return bitmap;
    }

    [Fact]
    public void ExtractsWhiteArtworkCentredOnItsHub()
    {
        using var icon = FakeIcon(withArtwork: true);
        var glyph = GlyphExtractor.WhiteArtwork(icon)!;
        Assert.InRange(glyph.CenterX, 124, 132);  // 64 px of 128 → 128 of 256
        Assert.InRange(glyph.CenterY, 124, 132);
        Assert.InRange(glyph.Radius, 65, 80);

        using var plain = FakeIcon(withArtwork: false);
        Assert.Null(GlyphExtractor.WhiteArtwork(plain));
    }

    [Theory]
    [InlineData(TrayStyle.Icon)]
    [InlineData(TrayStyle.Percent)]
    public void RendersTrayIconsAtEverySize(TrayStyle style)
    {
        using var icon = FakeIcon(withArtwork: true);
        var glyph = GlyphExtractor.WhiteArtwork(icon);
        var segment = new TraySegment(ProviderId.Claude, 88, Severity.Warning, "88", "Claude");
        foreach (var size in new[] { 16, 20, 24, 32 })
        {
            using var bitmap = TrayIconRenderer.Render(segment, ProviderId.Claude, style, glyph, colorAlerts: true, darkTaskbar: true, size);
            Assert.Equal(size, bitmap.Width);
            // The top of the ring carries the orange alert color.
            var top = bitmap.GetPixel(size / 2, 1);
            Assert.True(top.Red > 200 && top.Blue < 80, $"expected orange at the top of the ring, got {top}");
        }

        using var plain = TrayIconRenderer.Render(segment, ProviderId.Claude, style, glyph, colorAlerts: false, darkTaskbar: true, 32);
        var ring = plain.GetPixel(16, 1);
        Assert.True(ring.Red > 200 && ring.Blue > 200, $"alert colors off should keep the ring white, got {ring}");
    }
}
