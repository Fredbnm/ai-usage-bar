namespace AIUsageBar.Core;

/// <summary>Whether a service's login was found on this machine.</summary>
public enum SetupStatus { Connected, NeedsLogin }

/// <summary>What one tab shows next to the agent's name.</summary>
public sealed record TabSummary(string Text, Severity Severity, bool IsWarning)
{
    /// <summary>
    /// The fullest limit's percentage, so an agent about to run out stands out
    /// without opening its tab; "!" when it failed and there is nothing to show.
    /// </summary>
    public static TabSummary Make(ProviderSnapshot? snapshot, bool hasError)
    {
        if (snapshot is { Items.Count: > 0 })
        {
            var fullest = snapshot.Items.MaxBy(i => i.Percent)!;
            return new TabSummary(UsageFormat.Percent(fullest.Percent), fullest.Severity, hasError);
        }
        return hasError ? new TabSummary("!", Severity.Warning, true) : new TabSummary("–", Severity.Normal, false);
    }
}

/// <summary>Which tab the popup shows, kept free of UI so it can be tested.</summary>
public static class PopupTabs
{
    /// <summary>The setup / onboarding tab.</summary>
    public const string AgentsKey = "agents";

    /// <summary>One agent needs no strip: the popup looks as it always did.</summary>
    public static bool ShowStrip(int connectedCount) => connectedCount >= 2;

    /// <summary>
    /// The tab to show. First run (or an explicit request) and "nothing
    /// connected" open the Agents tab; a saved agent that is gone falls back to
    /// the first connected one.
    /// </summary>
    public static string Resolve(string? requested, IReadOnlyList<ProviderId> connected, bool onboardingDone)
    {
        if (connected.Count == 0 || !onboardingDone) return AgentsKey;
        if (requested == AgentsKey) return AgentsKey;
        return connected.FirstOrDefault(id => id.Key() == requested) is { } match && requested is not null
            ? match.Key()
            : connected[0].Key();
    }

    /// <summary>The agent of a provider tab, or null for the Agents tab.</summary>
    public static ProviderId? ProviderOf(string key) =>
        ProviderCatalog.All.FirstOrDefault(d => d.Key == key)?.Id;

    /// <summary>Next / previous tab key, wrapping, for arrow keys and Ctrl+Tab.</summary>
    public static string Step(IReadOnlyList<string> keys, string current, int direction)
    {
        if (keys.Count == 0) return current;
        var index = keys.ToList().IndexOf(current);
        return keys[((index < 0 ? 0 : index) + direction + keys.Count) % keys.Count];
    }
}
