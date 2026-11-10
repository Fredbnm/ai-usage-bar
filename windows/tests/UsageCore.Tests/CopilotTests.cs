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

    private string Extension(string root, string name, string? icon, string? packageIcon = "assets/copilot.png")
    {
        var folder = Path.Combine(directory, root, name);
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllText(Path.Combine(folder, "package.json"), packageIcon is null ? "{}" : $$"""{"icon":"{{packageIcon}}"}""");
        if (icon is not null) File.WriteAllText(Path.Combine(folder, icon), "png");
        return folder;
    }

    [Fact]
    public void FindsCopilotsOfficialIconInTheNewestExtension()
    {
        var older = Extension("a", "github.copilot-chat-0.9.1", "assets/copilot.png");
        var newer = Extension("b", "github.copilot-chat-0.33.2025103102", "assets/copilot.png");
        Extension("b", "github.copilot-1.388.0", "assets/Copilot-App-Icon.png", "assets/Copilot-App-Icon.png");
        Extension("b", "ms-python.python-2024.1.0", "assets/copilot.png");

        var found = CopilotIcon.Find([Path.Combine(directory, "a"), Path.Combine(directory, "b")]);
        Assert.Equal(Path.Combine(newer, "assets", "copilot.png"), found);
        Assert.NotEqual(Path.Combine(older, "assets", "copilot.png"), found);
        Assert.True(CopilotIcon.VersionOf("github.copilot-chat-0.33.2025103102") > CopilotIcon.VersionOf("github.copilot-chat-0.9.1"));
    }

    [Fact]
    public void CopilotIconFallsBackAndNeverLeavesTheExtensionFolder()
    {
        // No "icon" field: the usual assets/copilot.png.
        Extension("c", "github.copilot-chat-1.0.0", "assets/copilot.png", packageIcon: null);
        Assert.NotNull(CopilotIcon.Find([Path.Combine(directory, "c")]));

        // A path escaping the extension folder is ignored.
        Extension("d", "github.copilot-chat-1.0.0", null, packageIcon: "../../outside.png");
        Assert.Null(CopilotIcon.Find([Path.Combine(directory, "d")]));
        Assert.Null(CopilotIcon.Find([Path.Combine(directory, "missing")]));
    }

    [Fact]
    public void DarkArtworkIsExtractedFromALightSquare()
    {
        using var icon = new SkiaSharp.SKBitmap(256, 256);
        using (var canvas = new SkiaSharp.SKCanvas(icon))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            using var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(0x20, 0x26, 0x30), IsAntialias = true };
            canvas.DrawCircle(128, 120, 70, paint);
        }
        var glyph = AIUsageBar.Core.Rendering.GlyphExtractor.DarkArtwork(icon);
        Assert.NotNull(glyph);
        Assert.InRange(glyph.CenterX, 120, 136);
        Assert.InRange(glyph.CenterY, 112, 128);
        Assert.InRange(glyph.Radius, 55, 80);
    }

    // A 3440x1440 monitor with a 48 px taskbar and a 290 px tray area, like a real one.
    private static readonly PixelRect Monitor = new(0, 0, 3440, 1440);
    private static readonly PixelRect Taskbar = new(0, 1392, 3440, 1440);
    private static readonly PixelRect Tray = new(3150, 1392, 3440, 1440);

    [Fact]
    public void WidgetSitsLeftOfTheTrayOrAtTheLeftEdge()
    {
        var (x, y) = TaskbarLayout.Place(Taskbar, Tray, 260, 40, TaskbarPlacement.LeftOfTray, 0, 8);
        Assert.Equal(3150 - 8 - 260, x);
        Assert.Equal(1392 + 4, y);                   // centred vertically

        var left = TaskbarLayout.Place(Taskbar, Tray, 260, 40, TaskbarPlacement.LeftEdge, 0, 8);
        Assert.Equal(8, left.X);
    }

    [Fact]
    public void DraggingMovesTheWidgetAndIsKeptOnTheTaskbar()
    {
        Assert.Equal(2882 - 100, TaskbarLayout.Place(Taskbar, Tray, 260, 40, TaskbarPlacement.LeftOfTray, -100, 8).X);
        Assert.Equal(8 + 300, TaskbarLayout.Place(Taskbar, Tray, 260, 40, TaskbarPlacement.LeftEdge, 300, 8).X);
        // Dragged past either end: clamped.
        Assert.Equal(0, TaskbarLayout.Place(Taskbar, Tray, 260, 40, TaskbarPlacement.LeftEdge, -5000, 8).X);
        Assert.Equal(3440 - 260, TaskbarLayout.Place(Taskbar, Tray, 260, 40, TaskbarPlacement.LeftOfTray, 5000, 8).X);
    }

    [Fact]
    public void LeftEdgeUsesTheFirstFreeGapBetweenTaskbarButtons()
    {
        // A centred Windows 11 taskbar: Widgets at the far left, Start/Search in the middle, then apps.
        PixelRect[] items =
        [
            new(6, 1392, 158, 1440),        // Widgets
            new(1124, 1392, 1169, 1440),    // Start
            new(1171, 1392, 1391, 1440),    // Search
            new(1437, 1392, 1481, 1440),    // pinned app
        ];
        Assert.Equal(158 + 8, TaskbarLayout.Place(Taskbar, Tray, 260, 40, TaskbarPlacement.LeftEdge, 0, 8, items).X);

        // Too wide for that gap: it moves on past Start and Search, to the next free spot.
        var wide = TaskbarLayout.Place(Taskbar, Tray, 1100, 40, TaskbarPlacement.LeftEdge, 0, 8, items);
        Assert.True(wide.X >= 1481 + 8);

        // A left-aligned taskbar has no gap at the left: it goes after the last button.
        PixelRect[] packed = [new(12, 1392, 57, 1440), new(59, 1392, 279, 1440), new(283, 1392, 327, 1440), new(331, 1392, 375, 1440)];
        Assert.Equal(375 + 8, TaskbarLayout.Place(Taskbar, Tray, 260, 40, TaskbarPlacement.LeftEdge, 0, 8, packed).X);

        // The tray side does not look at the buttons (the user can drag it).
        Assert.Equal(3150 - 8 - 260, TaskbarLayout.Place(Taskbar, Tray, 260, 40, TaskbarPlacement.LeftOfTray, 0, 8, packed).X);
        // No items known: the plain edge.
        Assert.Equal(8, TaskbarLayout.Place(Taskbar, Tray, 260, 40, TaskbarPlacement.LeftEdge, 0, 8, null).X);
    }

    [Fact]
    public void UnknownTrayAreaIsEstimated()
    {
        var withoutTray = TaskbarLayout.Place(Taskbar, null, 260, 40, TaskbarPlacement.LeftOfTray, 0, 8);
        Assert.Equal(3440 - 320 - 8 - 260, withoutTray.X);
        // A nonsense rectangle (zero width, or outside the taskbar) is treated the same way.
        Assert.Equal(withoutTray, TaskbarLayout.Place(Taskbar, new PixelRect(0, 0, 0, 0), 260, 40, TaskbarPlacement.LeftOfTray, 0, 8));
    }

    [Fact]
    public void WidgetHidesWithAnAutoHiddenTaskbarAndForFullScreenApps()
    {
        Assert.True(TaskbarLayout.IsHorizontal(Taskbar));
        Assert.False(TaskbarLayout.IsHorizontal(new PixelRect(0, 0, 60, 1400)));

        Assert.False(TaskbarLayout.IsTaskbarHidden(Taskbar, Monitor));
        Assert.True(TaskbarLayout.IsTaskbarHidden(new PixelRect(0, 1438, 3440, 1486), Monitor)); // slid off screen
        Assert.True(TaskbarLayout.IsTaskbarHidden(new PixelRect(0, 0, 0, 0), Monitor));

        Assert.False(TaskbarLayout.ShouldHideForNotificationState(1));  // not present
        Assert.False(TaskbarLayout.ShouldHideForNotificationState(5));  // normal
        Assert.True(TaskbarLayout.ShouldHideForNotificationState(2));   // full-screen app
        Assert.True(TaskbarLayout.ShouldHideForNotificationState(3));   // full-screen game
        Assert.True(TaskbarLayout.ShouldHideForNotificationState(4));   // presentation
    }

    [Fact]
    public void PercentTextKeepsPlaceholders()
    {
        var snapshot = CopilotUsage.Snapshot(Bytes(Sample));
        var segment = TraySegment.Make(ProviderId.Copilot, TrayDisplay.Weekly, snapshot, null)!;
        Assert.Equal("61%", TaskbarLayout.PercentText(segment));
        Assert.Equal("–", TaskbarLayout.PercentText(TraySegment.Make(ProviderId.Copilot, TrayDisplay.Weekly, null, null)!));
        Assert.Equal("!", TaskbarLayout.PercentText(TraySegment.Make(ProviderId.Copilot, TrayDisplay.Weekly, null, "boom")!));
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
