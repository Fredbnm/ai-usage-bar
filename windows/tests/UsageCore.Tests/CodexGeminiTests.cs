using System.Text;
using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using SkiaSharp;

namespace UsageCore.Tests;

public class CodexGeminiTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "agent-tests-" + Guid.NewGuid().ToString("N"));

    public CodexGeminiTests()
    {
        Localization.Current = AppLanguage.English;
        Directory.CreateDirectory(directory);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private string Write(string name, string text)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, text);
        return path;
    }

    // MARK: Codex

    private const string CodexSample = """
        {
          "plan_type": "plus",
          "rate_limit": {
            "limit_reached": false,
            "primary_window":   { "used_percent": 23, "limit_window_seconds": 18000,  "reset_at": 1795000000 },
            "secondary_window": { "used_percent": 48, "limit_window_seconds": 604800, "reset_after_seconds": 3600 }
          }
        }
        """;

    [Fact]
    public void ReadsTheCodexLogin()
    {
        var path = Write("auth.json", """{"OPENAI_API_KEY":null,"tokens":{"access_token":"eyJ.secret","refresh_token":"r","account_id":"acc-1"}}""");
        var credentials = CodexCredentialsStore.TryLoad(path);
        Assert.Equal("eyJ.secret", credentials?.AccessToken);
        Assert.Equal("acc-1", credentials?.AccountId);
        Assert.DoesNotContain("secret", credentials!.ToString());

        // An API-key-only login has no ChatGPT plan; a missing or broken file is no login.
        Assert.Null(CodexCredentialsStore.TryLoad(Write("key.json", """{"OPENAI_API_KEY":"sk-x"}""")));
        Assert.Null(CodexCredentialsStore.TryLoad(Write("broken.json", "{ nope")));
        Assert.Null(CodexCredentialsStore.TryLoad(Path.Combine(directory, "missing.json")));
    }

    [Fact]
    public void ParsesTheSessionAndWeeklyWindows()
    {
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
        var snapshot = CodexUsage.Snapshot(Bytes(CodexSample), now);
        Assert.NotNull(snapshot);
        Assert.Equal(ProviderId.Codex, snapshot.Provider);
        Assert.Equal("Plus", snapshot.PlanName);

        var session = snapshot.Items[0];
        Assert.Equal("session", session.Kind);
        Assert.Equal(LimitGroup.Session, session.Group);
        Assert.Equal(23, session.Percent, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795000000), session.ResetsAt);

        var weekly = snapshot.Items[1];
        Assert.Equal("weekly", weekly.Kind);
        Assert.Equal(LimitGroup.Weekly, weekly.Group);
        Assert.Equal(now.AddHours(1), weekly.ResetsAt);   // from reset_after_seconds
        Assert.Equal("Current session", session.Title);
        Assert.Equal("Weekly", weekly.Title);
    }

    [Fact]
    public void ACodexWindowIsToldApartByItsLength()
    {
        // Only a weekly window (some plans): it must not be called the session.
        var snapshot = CodexUsage.Snapshot(Bytes("""{"rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":604800}}}"""));
        var item = Assert.Single(snapshot!.Items);
        Assert.Equal("weekly", item.Kind);

        // Without lengths: first = session, second = weekly. A reached limit shows as critical.
        var plain = CodexUsage.Snapshot(Bytes("""{"rate_limit":{"limit_reached":true,"primary_window":{"used_percent":100},"secondary_window":{"used_percent":60}}}"""))!;
        Assert.Equal(["session", "weekly"], plain.Items.Select(i => i.Kind));
        Assert.Equal(Severity.Critical, plain.Items[0].Severity);
        Assert.Equal(Severity.Normal, plain.Items[1].Severity);
    }

    [Fact]
    public void UnknownCodexShapesGiveNoSnapshot()
    {
        Assert.Null(CodexUsage.Snapshot(Bytes("[]")));
        Assert.Null(CodexUsage.Snapshot(Bytes("""{"plan_type":"plus"}""")));
        Assert.Null(CodexUsage.Snapshot(Bytes("garbage")));
    }

    [Fact]
    public async Task CodexWithoutALoginIsHiddenAndAnExpiredOneSaysSo()
    {
        var missing = new CodexProvider(Path.Combine(directory, "none.json"));
        Assert.False(missing.IsAvailable());
        Assert.Equal(UsageErrorKind.NotLoggedIn, (await Assert.ThrowsAsync<UsageException>(() => missing.FetchAsync())).Kind);

        // A JWT whose exp is in the past: no request is made (this test has no network).
        var exp = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var past = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"exp\":" + exp + "}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expired = new CodexProvider(Write("old.json", "{\"tokens\":{\"access_token\":\"h." + past + ".s\"}}"));
        Assert.True(expired.IsAvailable());
        Assert.Equal(UsageErrorKind.UnauthorizedExpired, (await Assert.ThrowsAsync<UsageException>(() => expired.FetchAsync())).Kind);
        Assert.Contains("codex login", UsageException.Describe(UsageErrorKind.NotLoggedIn, ProviderId.Codex));
    }

    [Fact]
    public void CodexCacheRoundTrips()
    {
        var cache = ProviderCache.From("Plus", Bytes(CodexSample)).Encode();
        Assert.Equal(2, new CodexProvider().SnapshotFromCache(cache)!.Items.Count);
        Assert.Null(new CodexProvider().SnapshotFromCache([1, 2, 3]));
    }

    // MARK: Gemini

    private const string GeminiSample = """
        {"buckets":[
          {"modelId":"gemini-2.5-flash","tokenType":"REQUESTS","remainingFraction":0.91,"resetTime":"2026-11-11T08:00:00Z"},
          {"modelId":"gemini-2.5-pro","tokenType":"REQUESTS","remainingFraction":0.62,"resetTime":"2026-11-11T08:00:00Z"},
          {"modelId":"gemini-2.5-pro","tokenType":"INPUT_TOKENS","remainingFraction":0.80,"resetTime":"2026-11-11T08:00:00Z"},
          {"modelId":"gemini-2.5-flash-lite","remainingFraction":1.0}
        ]}
        """;

    [Fact]
    public void ReadsTheGeminiLogin()
    {
        var path = Write("oauth_creds.json", """{"access_token":"ya29.secret","refresh_token":"1//r","expiry_date":4102444800000,"id_token":"x"}""");
        var credentials = GeminiCredentialsStore.TryLoad(path);
        Assert.Equal("ya29.secret", credentials?.AccessToken);
        Assert.False(credentials!.IsExpired);
        Assert.DoesNotContain("secret", credentials.ToString());

        var old = GeminiCredentialsStore.TryLoad(Write("old.json", """{"access_token":"t","expiry_date":1000}"""));
        Assert.True(old!.IsExpired);
        Assert.Null(GeminiCredentialsStore.TryLoad(Write("empty.json", "{}")));
        Assert.Null(GeminiCredentialsStore.TryLoad(Path.Combine(directory, "missing.json")));
    }

    [Fact]
    public void ParsesGeminiQuotaPerModelProFirst()
    {
        var snapshot = GeminiUsage.Snapshot(Bytes(GeminiSample), "Free");
        Assert.NotNull(snapshot);
        Assert.Equal(ProviderId.Gemini, snapshot.Provider);
        Assert.Equal("Free", snapshot.PlanName);
        Assert.Equal(3, snapshot.Items.Count);   // two pro buckets collapse into one

        var pro = snapshot.Items[0];
        Assert.Equal("2.5 Pro", pro.ScopeName);
        Assert.Equal(38, pro.Percent, 3);         // the fullest bucket, 1 - 0.62
        Assert.Equal(LimitGroup.Weekly, pro.Group); // what the tray ring follows
        Assert.Equal("Daily · 2.5 Pro", pro.Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero), pro.ResetsAt);
        Assert.Equal(["2.5 Pro", "2.5 Flash", "2.5 Flash Lite"], snapshot.Items.Select(i => i.ScopeName));
        Assert.All(snapshot.Items.Skip(1), i => Assert.Equal(LimitGroup.Other, i.Group));
    }

    [Fact]
    public void FindsTheGeminiProjectAndPlan()
    {
        Assert.Equal("my-project-1", GeminiUsage.Project(Bytes("""{"cloudaicompanionProject":"my-project-1","currentTier":{"id":"free-tier"}}""")));
        Assert.Equal("obj-project", GeminiUsage.Project(Bytes("""{"cloudaicompanionProject":{"id":"obj-project"}}""")));
        Assert.Null(GeminiUsage.Project(Bytes("{}")));
        Assert.Equal("Free", GeminiUsage.PlanName(Bytes("""{"currentTier":{"id":"free-tier"}}""")));
        Assert.Equal("Standard", GeminiUsage.PlanName(Bytes("""{"currentTier":{"id":"standard-tier","name":"Gemini Code Assist"}}""")));
        Assert.Null(GeminiUsage.PlanName(Bytes("{}")));
    }

    [Fact]
    public async Task GeminiWithoutALoginIsHiddenAndAnExpiredOneSaysSo()
    {
        var missing = new GeminiProvider(Path.Combine(directory, "none.json"));
        Assert.False(missing.IsAvailable());
        Assert.Equal(UsageErrorKind.NotLoggedIn, (await Assert.ThrowsAsync<UsageException>(() => missing.FetchAsync())).Kind);

        var expired = new GeminiProvider(Write("old.json", """{"access_token":"t","expiry_date":1000}"""));
        Assert.Equal(UsageErrorKind.UnauthorizedExpired, (await Assert.ThrowsAsync<UsageException>(() => expired.FetchAsync())).Kind);
        Assert.Contains("Gemini CLI", UsageException.Describe(UsageErrorKind.UnauthorizedExpired, ProviderId.Gemini));
    }

    [Fact]
    public void UnknownGeminiShapesGiveNoSnapshot()
    {
        Assert.Null(GeminiUsage.Snapshot(Bytes("{}"), null));
        Assert.Null(GeminiUsage.Snapshot(Bytes("""{"buckets":[{"modelId":"x"}]}"""), null));
        Assert.Null(GeminiUsage.Snapshot(Bytes("garbage"), null));
        var cache = ProviderCache.From("Free", Bytes(GeminiSample)).Encode();
        Assert.Equal("Free", new GeminiProvider().SnapshotFromCache(cache)!.PlanName);
    }

    // MARK: Icons

    private string Extension(string name, string? packageIcon, string? file)
    {
        var folder = Path.Combine(directory, "ext", name);
        Directory.CreateDirectory(Path.Combine(folder, "resources"));
        File.WriteAllText(Path.Combine(folder, "package.json"), packageIcon is null ? "{}" : $$"""{"icon":"{{packageIcon}}"}""");
        if (file is not null) File.WriteAllText(Path.Combine(folder, file), "png");
        return folder;
    }

    [Fact]
    public void FindsEachAgentsIconInItsExtension()
    {
        var older = Extension("openai.chatgpt-25.9.1-win32-x64", "resources/blossom.dark.png", "resources/blossom.dark.png");
        var newer = Extension("openai.chatgpt-26.1021.22027-win32-x64", "resources/blossom.dark.png", "resources/blossom.dark.png");
        Extension("openai.codex-audio-26.1021.22027", "resources/x.png", "resources/x.png");   // not the Codex agent
        var gemini = Extension("google.geminicodeassist-2.1.0-universal", "resources/gemini.png", "resources/gemini.png");
        var roots = new[] { Path.Combine(directory, "ext") };

        Assert.Equal(Path.Combine(newer, "resources", "blossom.dark.png"), ExtensionIcon.FindCodex(roots));
        Assert.NotEqual(Path.Combine(older, "resources", "blossom.dark.png"), ExtensionIcon.FindCodex(roots));
        Assert.Equal(Path.Combine(gemini, "resources", "gemini.png"), ExtensionIcon.FindGemini(roots));
        Assert.Null(ExtensionIcon.FindCopilot(roots));
        Assert.Null(ExtensionIcon.FindCodex([Path.Combine(directory, "missing")]));

        // "26.1021.22027" is the version, not "win32-x64".
        Assert.True(ExtensionIcon.VersionOf("openai.chatgpt-26.1021.22027-win32-x64") > ExtensionIcon.VersionOf("openai.chatgpt-25.9.1-win32-x64"));
    }

    [Fact]
    public void SilhouetteFindsAColoredShapeOnATransparentOrPlainBackground()
    {
        foreach (var background in new[] { SKColors.Transparent, SKColors.White })
        {
            using var icon = new SKBitmap(256, 256);
            using (var canvas = new SKCanvas(icon))
            {
                canvas.Clear(background);
                using var paint = new SKPaint { Color = new SKColor(0x31, 0x86, 0xFF), IsAntialias = true };   // saturated blue
                canvas.DrawCircle(128, 120, 70, paint);
            }
            var glyph = GlyphExtractor.Silhouette(icon);
            Assert.NotNull(glyph);
            Assert.InRange(glyph.CenterX, 120, 136);
            Assert.InRange(glyph.Radius, 55, 80);
        }
    }

    [Fact]
    public void EveryAgentHasADistinctInitial()
    {
        var initials = Enum.GetValues<ProviderId>().Select(id => id.Initial()).ToList();
        Assert.Equal(initials.Count, initials.Distinct().Count());
    }
}
