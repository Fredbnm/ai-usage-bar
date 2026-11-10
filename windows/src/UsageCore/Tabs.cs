namespace AIUsageBar.Core;

/// <summary>Whether a service's login was found on this machine.</summary>
public enum SetupStatus { Connected, NeedsLogin }

/// <summary>What one tab shows next to the agent's name.</summary>
public sealed record TabSummary(string Text, Severity Severity, bool IsWarning)
{
    /// <summary>
    /// The chosen limit's percentage (see <see cref="TabLimit"/>), by default the fullest one so an
    /// agent about to run out stands out without opening its tab; "!" when it failed and there is
    /// nothing to show.
    /// </summary>
    public static TabSummary Make(ProviderSnapshot? snapshot, bool hasError, string limit = TabLimit.Highest)
    {
        if (snapshot is not null && TabLimit.Item(snapshot.Items, limit) is { } shown)
        {
            return new TabSummary(UsageFormat.Percent(shown.Percent), shown.Severity, hasError);
        }
        return hasError ? new TabSummary("!", Severity.Warning, true) : new TabSummary("–", Severity.Normal, false);
    }
}

/// <summary>
/// Which limit an agent's tab shows: the fullest one (the default) or one the user picked, saved as a
/// key built from the limit's kind and scope ("weekly_scoped:Fable"), which stays the same between refreshes.
/// </summary>
public static class TabLimit
{
    public const string Highest = "highest";

    public static string Key(LimitItem item) => item.ScopeName is { } scope ? $"{item.Kind}:{scope}" : item.Kind;

    /// <summary>The picked limit; the fullest one when the pick is "highest" or the limit is gone.</summary>
    public static LimitItem? Item(IReadOnlyList<LimitItem> items, string key) =>
        (key != Highest ? items.FirstOrDefault(item => Key(item) == key) : null) ?? items.MaxBy(item => item.Percent);

    /// <summary>
    /// The choices for one agent: the fullest limit first, then each of its limits in display order.
    /// A saved pick missing from the current data stays listed so it still shows its checkmark.
    /// </summary>
    public static IReadOnlyList<string> Options(IReadOnlyList<LimitItem> items, string saved)
    {
        var keys = new List<string> { Highest };
        foreach (var item in items)
        {
            if (!keys.Contains(Key(item))) keys.Add(Key(item));
        }
        if (!keys.Contains(saved)) keys.Add(saved);
        return keys;
    }

    public static string Title(string key)
    {
        if (key == Highest) return Localization.T("Maior uso", "Highest usage");
        var parts = key.Split(':', 2);
        return ClaudeUsage.Title(parts[0], parts.Length > 1 ? parts[1] : null);
    }
}

/// <summary>How the popup lays out the agents.</summary>
public enum PopupLayout
{
    /// <summary>One tab per agent, once two or more are connected.</summary>
    Tabs,
    /// <summary>Every agent one below the other on a single screen, as before tabs.</summary>
    List,
}

public static class PopupLayoutLabels
{
    public static string Label(this PopupLayout layout) => layout == PopupLayout.Tabs
        ? Localization.T("Uma aba por agente", "One tab per agent")
        : Localization.T("Todas as IAs juntas", "All AIs together");
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
