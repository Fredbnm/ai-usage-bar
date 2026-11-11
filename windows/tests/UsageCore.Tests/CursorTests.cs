using System.Text;
using AIUsageBar.Core;

namespace UsageCore.Tests;

public class CursorTests
{
    public CursorTests() => Localization.Current = AppLanguage.Portuguese;

    /// <summary>Shape of a real legacy Pro+ answer (numbers changed): dates are epoch milliseconds as strings, money is cents.</summary>
    private const string LegacyMonthly = """
        {"billingCycleStart": "1791495353000", "billingCycleEnd": "1794173753000",
         "planUsage": {"totalSpend": 19058, "includedSpend": 7000, "bonusSpend": 12058, "limit": 7000,
                       "remainingBonus": false, "autoPercentUsed": 13.586666666666666, "apiPercentUsed": 100,
                       "totalPercentUsed": 15.525865580448064},
         "spendLimitUsage": {"limitType": "user"},
         "someFutureField": {"x": 1}}
        """;

    private static ProviderSnapshot Parse(string json, string? plan = "Pro") =>
        CursorUsage.Snapshot(Encoding.UTF8.GetBytes(json), plan) ?? throw new Xunit.Sdk.XunitException("no snapshot");

    [Fact]
    public void LegacyPlanShowsTheMonthlyCycleAndBothPools()
    {
        var snapshot = Parse(LegacyMonthly, "Pro+");
        Assert.Equal(ProviderId.Cursor, snapshot.Provider);
        Assert.Equal("Pro+", snapshot.PlanName);
        Assert.Equal(["Mensal", "Mensal · Auto", "Mensal · API"], snapshot.Items.Select(i => i.Title));
        Assert.Equal(["16%", "14%", "100%"], snapshot.Items.Select(i => UsageFormat.Percent(i.Percent)));
        Assert.Equal(Severity.Critical, snapshot.Items[2].Severity);
        Assert.All(snapshot.Items, item => Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1794173753000), item.ResetsAt));
        Assert.Equal(LimitGroup.Weekly, snapshot.Items[0].Group);
        Assert.Equal(["US$ 70,00 usados de US$ 70,00 incluídos no plano + US$ 120,58 de bônus"], snapshot.Notes.Select(n => n.Text));
    }

    [Fact]
    public void TheWeeklyPoolOfTheNewPlansIsLabelledWeekly()
    {
        var snapshot = Parse("""
            {"billingCycleStart": "2026-10-19T00:00:00Z", "billingCycleEnd": "2026-10-26T00:00:00Z",
             "planUsage": {"totalPercentUsed": 7.09}}
            """);
        Assert.Equal(["Semanal"], snapshot.Items.Select(i => i.Title));
        Assert.Equal(7.09, snapshot.Items[0].Percent);
        Assert.Empty(snapshot.Notes);
    }

    [Fact]
    public void UnusedValuesLeftOutByProto3CountAsZero()
    {
        var snapshot = Parse("""{"billingCycleStart": "1794096000000", "billingCycleEnd": "1796688000000", "planUsage": {"limit": 2000}}""");
        Assert.Equal(["Mensal"], snapshot.Items.Select(i => i.Title));
        Assert.Equal(0, snapshot.Items[0].Percent);
        Assert.Equal(["US$ 0,00 usados de US$ 20,00 incluídos no plano"], snapshot.Notes.Select(n => n.Text));

        var oneLane = Parse("""{"planUsage": {"apiPercentUsed": 40, "totalPercentUsed": 20}}""");
        Assert.Equal(["Mensal", "Mensal · Auto", "Mensal · API"], oneLane.Items.Select(i => i.Title));
        Assert.Equal([20, 0, 40], oneLane.Items.Select(i => i.Percent));
    }

    [Fact]
    public void WithoutTheHeadlineTheIncludedSpendGivesThePercentage()
    {
        var snapshot = Parse("""{"plan_usage": {"included_spend": "500", "limit": "2000"}}""");
        Assert.Equal(25, snapshot.Items[0].Percent);
    }

    [Fact]
    public void OnDemandSpendingPrefersTheUsersOwnCap()
    {
        Assert.Equal(["Sob demanda: US$ 4,47 de US$ 50,00"], OnDemandNotes("""{"individualUsed": 447, "individualLimit": 5000, "pooledLimit": 100000}"""));
        Assert.Equal(["Sob demanda da equipe: US$ 1.234,50 de US$ 5.000,00"], OnDemandNotes("""{"pooledLimit": 500000, "pooledUsed": 123450, "limitType": "team"}"""));
        Assert.Equal(["Sob demanda: US$ 4,47"], OnDemandNotes("""{"totalSpend": 447}"""));
        Assert.Empty(OnDemandNotes("""{"limitType": "user"}"""));

        static IEnumerable<string> OnDemandNotes(string spend) =>
            Parse($$"""{"planUsage": {}, "spendLimitUsage": {{spend}}}""").Notes.Select(n => n.Text);
    }

    [Fact]
    public void AnswersWithNothingToShowAreNotASnapshot()
    {
        Assert.Null(CursorUsage.Snapshot("{}"u8.ToArray(), "Pro"));
        Assert.Null(CursorUsage.Snapshot("[]"u8.ToArray(), "Pro"));
        Assert.Null(CursorUsage.Snapshot("not json"u8.ToArray(), "Pro"));
    }

    [Fact]
    public void NamesThePlanFromTheMembershipType()
    {
        Assert.Equal("Pro", CursorUsage.PlanName("pro"));
        Assert.Equal("Pro+", CursorUsage.PlanName("pro_plus"));
        Assert.Equal("Ultra", CursorUsage.PlanName("ULTRA"));
        Assert.Equal("Hobby", CursorUsage.PlanName("free"));
        Assert.Equal("Pro Trial", CursorUsage.PlanName("free_trial"));
        Assert.Equal("Enterprise", CursorUsage.PlanName("enterprise"));
        Assert.Null(CursorUsage.PlanName(" "));
        Assert.Null(CursorUsage.PlanName(null));
    }

    [Fact]
    public void TextsInEnglish()
    {
        Localization.Current = AppLanguage.English;
        var snapshot = Parse(LegacyMonthly);
        Assert.Equal(["Monthly", "Monthly · Auto", "Monthly · API"], snapshot.Items.Select(i => i.Title));
        Assert.Equal(["$70.00 used of $70.00 included in the plan + $120.58 bonus"], snapshot.Notes.Select(n => n.Text));
        Assert.Equal("On-demand: $4.47 of $50.00", new SnapshotNote(NoteKind.OnDemandSpend, Amount: 447, Limit: 5000).Text);
        Assert.Equal("Monthly", TrayDisplay.Weekly.Label(ProviderId.Cursor));
        Assert.Equal("Monthly · API", TabLimit.Title("monthly_scoped:API"));
        Assert.Equal("No Cursor login found. Open Cursor and sign in.", UsageException.Describe(UsageErrorKind.NotLoggedIn, ProviderId.Cursor));
        Assert.Equal("Could not read the Cursor login right now. Trying again soon.", UsageException.Describe(UsageErrorKind.LoginUnreadable, ProviderId.Cursor));
        Assert.Equal(
            "The Cursor login expired. Open Cursor once to renew it and the meter picks up again.",
            UsageException.Describe(UsageErrorKind.UnauthorizedExpired, ProviderId.Cursor));
    }

    [Fact]
    public void TheTrayAndTheTabFollowTheWholeCycle()
    {
        var snapshot = Parse(LegacyMonthly);
        var segment = TraySegment.Make(ProviderId.Cursor, TrayDisplay.Weekly, snapshot, null)!;
        Assert.Equal("16", segment.Number);
        Assert.StartsWith("Cursor\nMensal: 16%", segment.Tooltip);
        Assert.Equal([TrayDisplay.Weekly, TrayDisplay.Hidden], TrayOptions.For(ProviderId.Cursor));
        Assert.Equal("100%", TabSummary.Make(snapshot, hasError: false).Text);
        Assert.Equal("16%", TabSummary.Make(snapshot, hasError: false, limit: "monthly").Text);
        Assert.Equal(["highest", "monthly", "monthly_scoped:Auto", "monthly_scoped:API"], TabLimit.Options(snapshot.Items, TabLimit.Highest));
        Assert.Equal("Cu", ProviderId.Cursor.Initial());
        Assert.Equal(ProviderId.Cursor, PopupTabs.ProviderOf("cursor"));
    }

    [Fact]
    public void ReadsTheLoginFromTheStateDatabase()
    {
        using var database = new StateDatabase();
        database.Put(CursorLogin.AccessTokenKey, "header.payload.signature");
        database.Put(CursorLogin.MembershipTypeKey, "pro");
        database.Put("cursorAuth/cachedEmail", "someone@example.com");

        var login = CursorLogin.Load(database.Path);
        Assert.Equal("header.payload.signature", login.AccessToken);
        Assert.Equal("Pro", login.PlanName);
        Assert.DoesNotContain("header", login.ToString());
        Assert.True(new CursorProvider(database.Path).IsAvailable());
    }

    [Fact]
    public void AcceptsAQuotedTokenAndNoMembership()
    {
        using var database = new StateDatabase();
        database.Put(CursorLogin.AccessTokenKey, "\"quoted.token.value\"");
        var login = CursorLogin.Load(database.Path);
        Assert.Equal("quoted.token.value", login.AccessToken);
        Assert.Null(login.PlanName);
    }

    [Fact]
    public void ASignedOutCursorIsNotAvailable()
    {
        using var database = new StateDatabase();
        database.Put("workbench.panel.width", "300");
        Assert.Equal(UsageErrorKind.NotLoggedIn, Assert.Throws<UsageException>(() => CursorLogin.Load(database.Path)).Kind);
        Assert.False(new CursorProvider(database.Path).IsAvailable());

        database.Put(CursorLogin.AccessTokenKey, "");
        Assert.False(new CursorProvider(database.Path).IsAvailable());

        var missing = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"), "state.vscdb");
        Assert.False(new CursorProvider(missing).IsAvailable());
        Assert.Equal(UsageErrorKind.NotLoggedIn, Assert.Throws<UsageException>(() => CursorLogin.Load(missing)).Kind);
    }

    [Fact]
    public void AFileThatIsNotTheStateDatabaseHasNoLogin()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "this is not a database, just some text that is long enough to have a header");
            Assert.Equal(UsageErrorKind.NotLoggedIn, Assert.Throws<UsageException>(() => CursorLogin.Load(path)).Kind);

            File.Delete(path);
            Sqlite.Execute(path, "CREATE TABLE other (x);");
            Assert.Equal(UsageErrorKind.NotLoggedIn, Assert.Throws<UsageException>(() => CursorLogin.Load(path)).Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnExpiredTokenIsReportedWithoutAskingTheServer()
    {
        using var database = new StateDatabase();
        // {"alg":"none"}.{"exp":1}.
        database.Put(CursorLogin.AccessTokenKey, "eyJhbGciOiJub25lIn0.eyJleHAiOjF9.");
        var error = await Assert.ThrowsAsync<UsageException>(() => new CursorProvider(database.Path).FetchAsync());
        Assert.Equal(UsageErrorKind.UnauthorizedExpired, error.Kind);
    }

    [Fact]
    public void CacheKeepsThePlanFromTheLogin()
    {
        var cache = ProviderCache.From("Pro", Encoding.UTF8.GetBytes(LegacyMonthly)).Encode();
        var snapshot = new CursorProvider("unused").SnapshotFromCache(cache)!;
        Assert.Equal("Pro", snapshot.PlanName);
        Assert.Equal(3, snapshot.Items.Count);
    }

    /// <summary>A throwaway <c>state.vscdb</c> with VS Code's key/value table.</summary>
    private sealed class StateDatabase : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory().FullName;

        public StateDatabase() => Sqlite.Execute(Path, "CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);");

        public string Path => System.IO.Path.Combine(directory, "state.vscdb");

        public void Put(string key, string value) =>
            Sqlite.Execute(Path, $"INSERT INTO ItemTable VALUES ('{key.Replace("'", "''")}', '{value.Replace("'", "''")}');");

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
