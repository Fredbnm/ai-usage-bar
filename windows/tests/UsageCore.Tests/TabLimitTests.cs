using AIUsageBar.Core;

namespace UsageCore.Tests;

public class TabLimitTests
{
    private static readonly LimitItem[] Items =
    [
        new("0-session", "session", null, 64, Severity.Normal, null, LimitGroup.Session),
        new("1-weekly_all", "weekly_all", null, 41, Severity.Normal, null, LimitGroup.Weekly),
        new("2-weekly_scoped-Fable", "weekly_scoped", "Fable", 83, Severity.Warning, null, LimitGroup.Weekly),
    ];

    public TabLimitTests() => Localization.Current = AppLanguage.English;

    private static ProviderSnapshot Snapshot(IReadOnlyList<LimitItem> items) => new(ProviderId.Claude, "Max 5x", items, [], []);

    [Fact]
    public void DefaultsToTheFullestLimit()
    {
        var summary = TabSummary.Make(Snapshot(Items), hasError: false);
        Assert.Equal("83%", summary.Text);
        Assert.Equal(Severity.Warning, summary.Severity);
    }

    [Theory]
    [InlineData("weekly_all", "41%")]
    [InlineData("session", "64%")]
    [InlineData("weekly_scoped:Fable", "83%")]
    public void ShowsThePickedLimit(string key, string text) =>
        Assert.Equal(text, TabSummary.Make(Snapshot(Items), hasError: false, key).Text);

    [Fact]
    public void FallsBackToTheFullestWhenThePickIsGone()
    {
        Assert.Equal("64%", TabSummary.Make(Snapshot(Items[..2]), hasError: false, "weekly_scoped:Fable").Text);
        Assert.Equal("–", TabSummary.Make(null, hasError: false, "session").Text);
        Assert.Equal("!", TabSummary.Make(null, hasError: true, "session").Text);
    }

    [Fact]
    public void ListsTheFullestFirstThenEachLimit()
    {
        Assert.Equal(["highest", "session", "weekly_all", "weekly_scoped:Fable"], TabLimit.Options(Items, TabLimit.Highest));
        // A pick that is not in the current data stays listed, so it keeps its checkmark.
        Assert.Equal(["highest", "session", "weekly_scoped:Opus"], TabLimit.Options(Items[..1], "weekly_scoped:Opus"));
    }

    [Theory]
    [InlineData("highest", "Highest usage")]
    [InlineData("session", "Current session")]
    [InlineData("weekly_all", "Weekly · all models")]
    [InlineData("weekly_scoped:Fable", "Weekly · Fable")]
    [InlineData("premium_requests", "Premium requests · month")]
    [InlineData("daily_scoped:2.5 Pro", "Daily · 2.5 Pro")]
    public void TitlesFollowTheLimit(string key, string title) => Assert.Equal(title, TabLimit.Title(key));
}
