using System.Text;
using AIUsageBar.Core;

// Localization.Current is process-wide.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UsageCore.Tests;

public class ClaudeTests
{
    public ClaudeTests() => Localization.Current = AppLanguage.Portuguese;

    /// <summary>Same shape as the real response, with an unknown field and a broken entry.</summary>
    internal const string Sample = """
    {
      "five_hour": {"utilization": 42.0, "resets_at": "2026-11-10T16:50:00.554353+00:00"},
      "seven_day": {"utilization": 30.0, "resets_at": "2026-11-12T03:00:00.554376+00:00"},
      "seven_day_opus": null,
      "some_new_field": {"weird": [1, 2, 3]},
      "limits": [
        {"kind": "session", "group": "session", "percent": 42, "severity": "normal",
         "resets_at": "2026-11-10T16:50:00.554353+00:00", "scope": null, "is_active": true},
        {"kind": "weekly_all", "group": "weekly", "percent": 30, "severity": "normal",
         "resets_at": "2026-11-12T03:00:00.554376+00:00", "scope": null, "is_active": false},
        {"kind": "weekly_scoped", "group": "weekly", "percent": 91, "severity": "critical",
         "resets_at": "2026-11-12T02:59:59.554553+00:00",
         "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": "unexpected-string"}, "is_active": false},
        {"kind": "broken_entry"}
      ],
      "seven_day_breakdown": {"rows": [
        {"key": "claude_code", "display_name": "Claude Code", "percent": 70},
        {"key": "chat", "display_name": "Chats", "percent": 30},
        {"key": "other", "display_name": "Other", "percent": 0}
      ]}
    }
    """;

    private static ProviderSnapshot Snapshot() =>
        ClaudeProvider.SnapshotFromResponse(Encoding.UTF8.GetBytes(Sample), "Max 5x")!;

    [Fact]
    public void DecodesLimits()
    {
        var items = Snapshot().Items;
        Assert.Equal(["Sessão atual", "Semanal · todos os modelos", "Semanal · Fable"], items.Select(i => i.Title));
        Assert.Equal([42.0, 30, 91], items.Select(i => i.Percent));
        Assert.Equal([LimitGroup.Session, LimitGroup.Weekly, LimitGroup.Weekly], items.Select(i => i.Group));
        Assert.Equal(Severity.Critical, items[2].Severity);
        Assert.NotNull(items[0].ResetsAt);
    }

    [Fact]
    public void BreakdownSkipsZeroAndTranslates()
    {
        Assert.Equal(["Claude Code", "Chat"], Snapshot().Breakdown.Select(b => b.Name));
    }

    [Fact]
    public void FallsBackToLegacyWindows()
    {
        var json = """{"five_hour": {"utilization": 10}, "seven_day": {"utilization": 20}, "seven_day_opus": {"utilization": 5}}""";
        var items = ClaudeProvider.SnapshotFromResponse(Encoding.UTF8.GetBytes(json), null)!.Items;
        Assert.Equal(["Sessão atual", "Semanal · todos os modelos", "Semanal · Opus"], items.Select(i => i.Title));
    }

    [Fact]
    public void TitlesFollowTheLanguage()
    {
        var snapshot = Snapshot();
        Localization.Current = AppLanguage.English;
        Assert.Equal(["Current session", "Weekly · all models", "Weekly · Fable"], snapshot.Items.Select(i => i.Title));
    }

    [Fact]
    public void EmptyResponseIsNotASnapshot()
    {
        Assert.Null(ClaudeProvider.SnapshotFromResponse("{}"u8.ToArray(), null));
        Assert.Null(ClaudeProvider.SnapshotFromResponse("not json"u8.ToArray(), null));
    }

    [Fact]
    public void ParsesCredentialsWithoutLeakingTheToken()
    {
        var json = """{"claudeAiOauth": {"accessToken": "secret-abc123", "expiresAt": 4102444800000, "subscriptionType": "max", "rateLimitTier": "default_claude_max_5x"}}""";
        var credentials = ClaudeCredentials.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal("Max 5x", credentials.PlanName);
        Assert.False(credentials.IsExpired);
        Assert.DoesNotContain("secret-abc123", credentials.ToString());
        Assert.Equal("Pro", new ClaudeCredentials("t", DateTimeOffset.UnixEpoch, "pro", null).PlanName);
        Assert.Throws<UsageException>(() => ClaudeCredentials.Parse("{}"u8.ToArray()));
    }

    [Fact]
    public void MissingCredentialsFileMeansNotLoggedIn()
    {
        var error = Assert.Throws<UsageException>(() => ClaudeCredentialsStore.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())));
        Assert.Equal(UsageErrorKind.NotLoggedIn, error.Kind);
    }
}

public class FormatTests
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    // Tue 2026-11-10 12:34 in São Paulo (UTC-3).
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 34, 0, TimeSpan.FromHours(-3));
    private static readonly DateTimeOffset Thursday = new(2026, 11, 12, 0, 0, 0, TimeSpan.FromHours(-3));
    private static readonly DateTimeOffset Tomorrow = new(2026, 11, 11, 18, 0, 0, TimeSpan.FromHours(-3));
    private static readonly DateTimeOffset NextMonth = new(2026, 12, 8, 0, 0, 0, TimeSpan.FromHours(-3));

    [Fact]
    public void Portuguese()
    {
        Localization.Current = AppLanguage.Portuguese;
        Assert.Equal("Reinicia em 1 h 16 min · 13:50", UsageFormat.Reset(Now.AddMinutes(76), Now, SaoPaulo));
        Assert.StartsWith("Reinicia qui", UsageFormat.Reset(Thursday, Now, SaoPaulo));
        Assert.EndsWith("às 00:00 · em 1 d 11 h", UsageFormat.Reset(Thursday, Now, SaoPaulo));
        Assert.Equal("Reinicia amanhã às 18:00 · em 1 d 5 h", UsageFormat.Reset(Tomorrow, Now, SaoPaulo));
        Assert.Equal("Reinicia dia 08/12 às 00:00 · em 27 d 11 h", UsageFormat.Reset(NextMonth, Now, SaoPaulo));
        // 23:59:59.55 reads the same as the 00:00 reset next to it.
        Assert.Equal(UsageFormat.Reset(Thursday, Now, SaoPaulo), UsageFormat.Reset(Thursday.AddSeconds(-0.45), Now, SaoPaulo));
        Assert.Equal("Reiniciando…", UsageFormat.Reset(Now.AddSeconds(-1), Now, SaoPaulo));
        Assert.Equal("Atualizado há 10 min", UsageFormat.Updated(Now.AddMinutes(-10), Now, SaoPaulo));
    }

    [Fact]
    public void English()
    {
        Localization.Current = AppLanguage.English;
        Assert.Equal("Resets in 1 h 16 min · 1:50 PM", UsageFormat.Reset(Now.AddMinutes(76), Now, SaoPaulo));
        Assert.Equal("Resets Thu at 12:00 AM · in 1 d 11 h", UsageFormat.Reset(Thursday, Now, SaoPaulo));
        Assert.Equal("Resets tomorrow at 6:00 PM · in 1 d 5 h", UsageFormat.Reset(Tomorrow, Now, SaoPaulo));
        Assert.Equal("Resets Dec 8 at 12:00 AM · in 27 d 11 h", UsageFormat.Reset(NextMonth, Now, SaoPaulo));
        Assert.Equal("Updated just now", UsageFormat.Updated(Now.AddSeconds(-10), Now, SaoPaulo));
        Assert.Equal("Nov 12", UsageFormat.ShortDate(Thursday, SaoPaulo));
    }

    [Fact]
    public void Durations()
    {
        Assert.Equal("1 min", UsageFormat.Duration(TimeSpan.FromSeconds(20)));
        Assert.Equal("1 h 16 min", UsageFormat.Duration(TimeSpan.FromMinutes(76)));
        Assert.Equal("1 d 1 h", UsageFormat.Duration(TimeSpan.FromHours(25)));
        Assert.Equal("<1%", UsageFormat.Percent(0.3));
        Assert.Equal("42%", UsageFormat.Percent(41.6));
    }

    [Fact]
    public void ParsesLongFractionsAndBadDates()
    {
        var micro = Json.ParseDate("2026-11-10T16:50:00.554353+00:00")!.Value;
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 16, 50, 0, TimeSpan.Zero).AddTicks(5_543_530), micro);
        Assert.NotNull(Json.ParseDate("2026-11-13T03:00:00.123456789Z"));
        Assert.Null(Json.ParseDate("not a date"));
    }

    [Fact]
    public void SeverityFallback()
    {
        Assert.Equal(Severity.Normal, SeverityParser.From("normal", 99));
        Assert.Equal(Severity.Warning, SeverityParser.From("warning", 10));
        Assert.Equal(Severity.Critical, SeverityParser.From("exceeded", 10));
        Assert.Equal(Severity.Warning, SeverityParser.From("something_new", 80));
        Assert.Equal(Severity.Critical, SeverityParser.From(null, 95));
    }

    [Fact]
    public void ErrorMessagesNameTheRightApp()
    {
        Localization.Current = AppLanguage.English;
        Assert.Contains("Grok Bot", UsageException.Describe(UsageErrorKind.UnauthorizedExpired, ProviderId.GrokBot));
        Assert.Contains("Claude Code", UsageException.Describe(UsageErrorKind.UnauthorizedExpired, ProviderId.Claude));
        Assert.Contains("503", UsageException.Describe(UsageErrorKind.Http, ProviderId.Claude, 503));
    }
}

public class TrayTests
{
    public TrayTests() => Localization.Current = AppLanguage.Portuguese;

    private static ProviderSnapshot Claude() =>
        ClaudeProvider.SnapshotFromResponse(Encoding.UTF8.GetBytes(ClaudeTests.Sample), "Max 5x")!;

    [Fact]
    public void SessionAndWeeklySegments()
    {
        var session = TraySegment.Make(ProviderId.Claude, TrayDisplay.Session, Claude(), null)!;
        Assert.Equal("42", session.Number);
        Assert.Equal(42, session.RingPercent);
        // Weekly means "all models", not the per-model Fable limit (91%).
        var weekly = TraySegment.Make(ProviderId.Claude, TrayDisplay.Weekly, Claude(), null)!;
        Assert.Equal("30", weekly.Number);
        Assert.Null(TraySegment.Make(ProviderId.Claude, TrayDisplay.Hidden, Claude(), null));
    }

    [Fact]
    public void TooltipListsEveryLimitAndFitsWindows()
    {
        var segment = TraySegment.Make(ProviderId.Claude, TrayDisplay.Session, Claude(), null)!;
        Assert.Equal("Claude\nSessão atual: 42%\nSemanal · todos os modelos: 30%\nSemanal · Fable: 91%", segment.Tooltip);
        var long_ = TraySegment.Make(ProviderId.Claude, TrayDisplay.Session, Claude(), new string('x', 300))!;
        Assert.True(long_.Tooltip.Length <= TraySegment.TooltipLimit);
    }

    [Fact]
    public void WithoutDataShowsDashOrBang()
    {
        Assert.Equal("–", TraySegment.Make(ProviderId.GrokBot, TrayDisplay.Weekly, null, null)!.Number);
        var failed = TraySegment.Make(ProviderId.GrokBot, TrayDisplay.Weekly, null, "boom")!;
        Assert.Equal("!", failed.Number);
        Assert.Null(failed.RingPercent);
    }

    [Fact]
    public void OptionsPerService()
    {
        Assert.Equal([TrayDisplay.Session, TrayDisplay.Weekly, TrayDisplay.Hidden], TrayOptions.For(ProviderId.Claude));
        Assert.Equal([TrayDisplay.Weekly, TrayDisplay.Hidden], TrayOptions.For(ProviderId.GrokBot));
        Assert.Equal(TrayDisplay.Session, TrayOptions.Default(ProviderId.Claude));
        Localization.Current = AppLanguage.English;
        Assert.Equal("Number in ring", TrayStyle.Percent.Label());
        Assert.Equal("Weekly", TrayDisplay.Weekly.Label(ProviderId.GrokBot));
    }
}
