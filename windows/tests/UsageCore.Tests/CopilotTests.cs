using System.Text;
using AIUsageBar.Core;

namespace UsageCore.Tests;

public class CopilotTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "copilot-tests-" + Guid.NewGuid().ToString("N"));

    public CopilotTests()
    {
        Localization.Current = AppLanguage.English;
        Directory.CreateDirectory(directory);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    // Same shape as docs/sample-copilot-usage.json.
    private const string Sample = """
        {
          "copilot_plan": "individual_pro",
          "quota_reset_date": "2026-12-01",
          "quota_snapshots": {
            "premium_interactions": { "entitlement": 300, "remaining": 117, "percent_remaining": 39.0, "unlimited": false },
            "chat": { "unlimited": true, "percent_remaining": 100.0 },
            "completions": { "unlimited": true, "percent_remaining": 100.0 }
          }
        }
        """;

    [Fact]
    public void ReadsTheTokenFromAppsJson()
    {
        File.WriteAllText(Path.Combine(directory, "apps.json"),
            """{"github.com:Iv1.abc":{"user":"octocat","oauth_token":"ghu_secret"}}""");
        var found = CopilotCredentialsStore.Find(directory, _ => null);
        Assert.Equal("ghu_secret", found?.Token);
        Assert.Equal("apps.json", found?.Source);
    }

    [Fact]
    public void FallsBackToHostsJsonThenEnvironment()
    {
        File.WriteAllText(Path.Combine(directory, "hosts.json"), """{"github.com":{"oauth_token":"ghu_hosts"}}""");
        Assert.Equal("ghu_hosts", CopilotCredentialsStore.Find(directory, _ => null)?.Token);

        File.Delete(Path.Combine(directory, "hosts.json"));
        var fromEnv = CopilotCredentialsStore.Find(directory, name => name == "GITHUB_TOKEN" ? "ghp_env" : null);
        Assert.Equal("ghp_env", fromEnv?.Token);
        Assert.Null(CopilotCredentialsStore.Find(directory, _ => null));
    }

    [Fact]
    public void IgnoresEnterpriseHostsAndBrokenFiles()
    {
        Assert.Null(CopilotCredentialsStore.ParseHosts(Bytes("""{"ghe.example.com":{"oauth_token":"x"}}""")));
        Assert.Null(CopilotCredentialsStore.ParseHosts(Bytes("not json")));
        File.WriteAllText(Path.Combine(directory, "apps.json"), "{ broken");
        Assert.Null(CopilotCredentialsStore.Find(directory, _ => null));
    }

    [Fact]
    public void NeverPrintsTheToken()
    {
        var text = new CopilotCredentials("ghu_secret", "apps.json").ToString();
        Assert.DoesNotContain("ghu_secret", text);
    }

    [Fact]
    public void ParsesPremiumRequestsAndSkipsUnlimitedQuotas()
    {
        var snapshot = CopilotUsage.Snapshot(Bytes(Sample));
        Assert.NotNull(snapshot);
        Assert.Equal(ProviderId.Copilot, snapshot.Provider);
        Assert.Equal("Pro", snapshot.PlanName);
        var item = Assert.Single(snapshot.Items);
        Assert.Equal("premium_requests", item.Kind);
        Assert.Equal(61, item.Percent, 3);
        Assert.Equal(LimitGroup.Weekly, item.Group);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), item.ResetsAt);
    }

    [Fact]
    public void UsesEntitlementWhenThePercentIsMissing()
    {
        var snapshot = CopilotUsage.Snapshot(Bytes(
            """{"quota_snapshots":{"premium_interactions":{"entitlement":300,"remaining":75}}}"""));
        Assert.Equal(75, Assert.Single(snapshot!.Items).Percent, 3);
    }

    [Fact]
    public void ReadsTheFreePlanQuotas()
    {
        var snapshot = CopilotUsage.Snapshot(Bytes(
            """{"copilot_plan":"free","monthly_quotas":{"chat":50,"completions":2000},"limited_user_quotas":{"chat":40,"completions":1500},"limited_user_reset_date":"2026-12-01"}"""));
        Assert.Equal("Free", snapshot!.PlanName);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.Equal(20, snapshot.Items.First(i => i.Kind == "chat").Percent, 3);
        Assert.Equal(25, snapshot.Items.First(i => i.Kind == "completions").Percent, 3);
    }

    [Fact]
    public void NamesThePlans()
    {
        Assert.Equal("Pro", CopilotUsage.PlanName("individual_pro"));
        Assert.Equal("Pro+", CopilotUsage.PlanName("individual_pro_plus"));
        Assert.Equal("Business", CopilotUsage.PlanName("business"));
        Assert.Null(CopilotUsage.PlanName(" "));
    }

    [Fact]
    public void UnknownShapesGiveNoSnapshot()
    {
        Assert.Null(CopilotUsage.Snapshot(Bytes("[]")));
        Assert.Null(CopilotUsage.Snapshot(Bytes("""{"copilot_plan":"business"}""")));
        Assert.Null(CopilotUsage.Snapshot(Bytes("garbage")));
    }

    [Fact]
    public void CacheRoundTrips()
    {
        var provider = new CopilotProvider(directory, _ => null, useGhCli: false);
        var cache = ProviderCache.From("Pro", Bytes(Sample)).Encode();
        Assert.Equal(61, provider.SnapshotFromCache(cache)!.Items[0].Percent, 3);
        Assert.Null(provider.SnapshotFromCache([1, 2, 3]));
    }

    [Fact]
    public async Task WithoutALoginItIsHiddenAndFetchReportsNotLoggedIn()
    {
        var provider = new CopilotProvider(directory, _ => null, useGhCli: false);
        Assert.False(provider.IsAvailable());
        var error = await Assert.ThrowsAsync<UsageException>(() => provider.FetchAsync());
        Assert.Equal(UsageErrorKind.NotLoggedIn, error.Kind);
        Assert.Contains("gh auth login", error.MessageFor(ProviderId.Copilot));
    }

    [Fact]
    public void TabSummaryShowsTheFullestLimitOrAWarning()
    {
        var snapshot = CopilotUsage.Snapshot(Bytes(
            """{"quota_snapshots":{"premium_interactions":{"percent_remaining":5},"chat":{"percent_remaining":60}}}"""))!;
        var summary = TabSummary.Make(snapshot, hasError: false);
        Assert.Equal("95%", summary.Text);
        Assert.Equal(Severity.Critical, summary.Severity);
        Assert.False(summary.IsWarning);

        Assert.Equal("!", TabSummary.Make(null, hasError: true).Text);
        Assert.Equal("–", TabSummary.Make(null, hasError: false).Text);
        Assert.True(TabSummary.Make(snapshot, hasError: true).IsWarning);
    }

    [Fact]
    public void ResolvesTheTabToShow()
    {
        ProviderId[] connected = [ProviderId.Claude, ProviderId.Copilot];

        // First run, or nothing connected: the Agents tab.
        Assert.Equal(PopupTabs.AgentsKey, PopupTabs.Resolve("claude", connected, onboardingDone: false));
        Assert.Equal(PopupTabs.AgentsKey, PopupTabs.Resolve("claude", [], onboardingDone: true));
        // A saved agent is kept; one that is gone (or none saved) falls back to the first.
        Assert.Equal("copilot", PopupTabs.Resolve("copilot", connected, onboardingDone: true));
        Assert.Equal("claude", PopupTabs.Resolve("grokBot", connected, onboardingDone: true));
        Assert.Equal("claude", PopupTabs.Resolve(null, connected, onboardingDone: true));
        Assert.Equal(PopupTabs.AgentsKey, PopupTabs.Resolve(PopupTabs.AgentsKey, connected, onboardingDone: true));
    }

    [Fact]
    public void TheStripNeedsTwoAgentsAndStepsWrap()
    {
        Assert.False(PopupTabs.ShowStrip(1));
        Assert.True(PopupTabs.ShowStrip(2));
        string[] keys = ["claude", "copilot", PopupTabs.AgentsKey];
        Assert.Equal("copilot", PopupTabs.Step(keys, "claude", 1));
        Assert.Equal("claude", PopupTabs.Step(keys, PopupTabs.AgentsKey, 1));
        Assert.Equal(PopupTabs.AgentsKey, PopupTabs.Step(keys, "claude", -1));
        Assert.Equal(ProviderId.Copilot, PopupTabs.ProviderOf("copilot"));
        Assert.Null(PopupTabs.ProviderOf(PopupTabs.AgentsKey));
    }

    [Fact]
    public void DetectReportsTheLogin()
    {
        var claude = new ClaudeProvider(Path.Combine(directory, ".credentials.json"));
        Assert.Equal(SetupStatus.NeedsLogin, claude.Detect());
        File.WriteAllText(Path.Combine(directory, ".credentials.json"), "{}");
        Assert.Equal(SetupStatus.Connected, claude.Detect());

        var copilot = new CopilotProvider(directory, _ => null, useGhCli: false);
        Assert.Equal(SetupStatus.NeedsLogin, ((IUsageProvider)copilot).Detect());
        File.WriteAllText(Path.Combine(directory, "hosts.json"), """{"github.com":{"oauth_token":"ghu_x"}}""");
        Assert.Equal(SetupStatus.Connected, ((IUsageProvider)copilot).Detect());
    }

    [Fact]
    public void TabIconsAreDrawnInTheGivenColor()
    {
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            using var icon = AIUsageBar.Core.Rendering.TabIconRenderer.Render(id, null, SkiaSharp.SKColors.White, 32);
            Assert.Equal(32, icon.Width);
            var opaque = Enumerable.Range(0, 32 * 32).Count(i => icon.GetPixel(i % 32, i / 32).Alpha > 0);
            Assert.InRange(opaque, 20, 32 * 32 - 1);   // something drawn, not a filled square
            Assert.Equal(0, icon.GetPixel(0, 0).Alpha); // transparent corners
            Assert.All(Enumerable.Range(0, 32 * 32).Select(i => icon.GetPixel(i % 32, i / 32)).Where(p => p.Alpha > 200),
                p => Assert.True(p.Red > 200 && p.Green > 200 && p.Blue > 200));
        }
    }

    [Fact]
    public void EveryProviderHasADescriptor()
    {
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            var descriptor = id.Descriptor();
            Assert.Equal(id, descriptor.Id);
            Assert.Contains(descriptor.DefaultTray, descriptor.TrayOptions);
            Assert.NotEmpty(descriptor.Key);
        }
        Assert.Equal(Enum.GetValues<ProviderId>().Length, ProviderCatalog.All.Select(d => d.Key).Distinct().Count());
        Assert.Equal("Monthly", TrayDisplay.Weekly.Label(ProviderId.Copilot));
    }
}
