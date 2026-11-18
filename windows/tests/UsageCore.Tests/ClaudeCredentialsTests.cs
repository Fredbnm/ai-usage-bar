using AIUsageBar.Core;

namespace UsageCore.Tests;

/// <summary>Where the Claude Code login is looked for on Windows and Linux.</summary>
public class ClaudeCredentialsTests
{
    private const string Home = "/home/someone";

    private static string Directory(params (string Key, string? Value)[] environment)
    {
        var map = environment.ToDictionary(entry => entry.Key, entry => entry.Value);
        return ClaudeCredentialsStore.ConfigDirectory(
            key => map.TryGetValue(key, out var value) ? value : null,
            Home);
    }

    [Fact]
    public void DefaultsToTheHomeDirectory()
    {
        Assert.Equal(Path.Combine(Home, ".claude"), Directory());
    }

    [Fact]
    public void ConfigDirectoryIsUsedWhenSet()
    {
        Assert.Equal("/opt/claude", Directory(("CLAUDE_CONFIG_DIR", "/opt/claude")));
    }

    [Fact]
    public void SecureStorageDirectoryWins()
    {
        Assert.Equal("/opt/secure", Directory(
            ("CLAUDE_CONFIG_DIR", "/opt/claude"),
            ("CLAUDE_SECURESTORAGE_CONFIG_DIR", "/opt/secure")));
    }

    [Fact]
    public void AnEmptyValueMeansTheDefault()
    {
        var expected = Path.Combine(Home, ".claude");
        Assert.Equal(expected, Directory(("CLAUDE_CONFIG_DIR", "")));
        Assert.Equal(expected, Directory(
            ("CLAUDE_CONFIG_DIR", "/opt/claude"),
            ("CLAUDE_SECURESTORAGE_CONFIG_DIR", "")));
    }

    private static ClaudeCredentials Credentials(string? plan, string? tier, string? org = null) =>
        new("t", null, plan, tier, org);

    [Fact]
    public void TheSubscriptionNamesThePlan()
    {
        Assert.Equal("Team", Credentials("team", "default_claude_max_5x").PlanName);
        Assert.Equal("Enterprise", Credentials("enterprise", "default_claude_max_5x").PlanName);
        Assert.Equal("Pro", Credentials("pro", null).PlanName);
    }

    [Fact]
    public void TheTierSeparatesTheMaxPlans()
    {
        Assert.Equal("Max 5x", Credentials("max", "default_claude_max_5x").PlanName);
        Assert.Equal("Max 20x", Credentials("max", "default_claude_max_20x").PlanName);
        Assert.Equal("Max", Credentials("max", null).PlanName);
    }

    [Fact]
    public void TheTierIsTheFallbackWithoutASubscription()
    {
        Assert.Equal("Max 20x", Credentials(null, "default_claude_max_20x").PlanName);
        Assert.Null(Credentials(null, null).PlanName);
    }

    [Fact]
    public void TheAccountComesFromTheOrganization()
    {
        var json = """
            {"organizationUuid": "org-1", "claudeAiOauth": {"accessToken": "a",
              "expiresAt": 4102444800000, "subscriptionType": "team", "rateLimitTier": "default_claude_max_5x"}}
            """;
        var parsed = ClaudeCredentials.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal("org-1", parsed.OrganizationUuid);
        Assert.Equal("org-1", parsed.Account);
        Assert.Equal("Team", parsed.PlanName);
    }

    [Fact]
    public void TheLoginIdentityMovesWithTheTokenButTheAccountDoesNot()
    {
        var first = new ClaudeCredentials("a", DateTimeOffset.FromUnixTimeSeconds(100), "max", null, "org-1");
        var renewed = new ClaudeCredentials("b", DateTimeOffset.FromUnixTimeSeconds(200), "max", null, "org-1");
        var other = new ClaudeCredentials("c", DateTimeOffset.FromUnixTimeSeconds(100), "max", null, "org-2");

        Assert.NotEqual(first.LoginIdentity, renewed.LoginIdentity);
        Assert.Equal(first.Account, renewed.Account);
        Assert.NotEqual(first.Account, other.Account);
    }

    [Fact]
    public void AnExpiredLoginIsReportedWithoutLeakingTheToken()
    {
        var expired = new ClaudeCredentials(
            "sk-ant-oat-secret", DateTimeOffset.UtcNow.AddDays(-180), "max", "default_claude_max_5x");
        Assert.True(expired.IsExpired);
        Assert.Equal("Max 5x", expired.PlanName);
        Assert.DoesNotContain("sk-ant-oat-secret", expired.ToString());
    }
}
