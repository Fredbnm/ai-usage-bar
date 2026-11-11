using System.Windows.Threading;
using AIUsageBar.Core;

namespace AIUsageBar;

/// <summary>One agent in the Agents tab. <paramref name="HasData"/>: its usage loaded at least once on this computer.</summary>
internal sealed record AgentRow(ProviderId Id, SetupStatus Status, bool IsShown, bool HasData, string? Plan);

internal sealed class ProviderState
{
    public ProviderSnapshot? Snapshot { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    public UsageException? Error { get; set; }
    public bool IsLoading { get; set; }
    public DateTimeOffset? BackoffUntil { get; set; }
}

/// <summary>
/// Owns the per-service state and the settings. Lives on the UI thread;
/// fetches run in the background and their results are applied back here.
/// </summary>
internal sealed class UsageStore
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan RateLimitBackoff = TimeSpan.FromMinutes(10);

    private readonly IReadOnlyList<IUsageProvider> providers;
    private readonly Dictionary<ProviderId, ProviderState> states = [];
    private DispatcherTimer? timer;

    public UsageStore(AppSettings settings, IReadOnlyList<IUsageProvider> providers)
    {
        Settings = settings;
        this.providers = providers;
        UpdateVisibleProviders();
        LoadCache();
    }

    /// <summary>Fixed data, no network: used by <c>--snapshot</c>.</summary>
    public UsageStore(AppSettings settings, IEnumerable<ProviderSnapshot> snapshots)
    {
        Settings = settings;
        providers = [];
        foreach (var snapshot in snapshots)
        {
            states[snapshot.Provider] = new ProviderState { Snapshot = snapshot, LastUpdated = DateTimeOffset.Now };
        }
        VisibleProviders = [.. states.Keys.Order()];
    }

    public event Action? Changed;

    public AppSettings Settings { get; }

    /// <summary>Services shown, in display order.</summary>
    public IReadOnlyList<ProviderId> VisibleProviders { get; private set; } = [];

    // MARK: Tabs and onboarding

    private string? requestedTab;

    /// <summary>The tab the popup shows: a provider's key or <see cref="PopupTabs.AgentsKey"/>.</summary>
    public string CurrentTab =>
        PopupTabs.Resolve(requestedTab ?? Settings.SelectedProvider, VisibleProviders, Settings.OnboardingDismissed);

    public bool ShowTabStrip => Settings.Layout == PopupLayout.Tabs && PopupTabs.ShowStrip(VisibleProviders.Count);

    /// <summary>"All AIs together": every agent on one screen, unless the Agents tab was asked for.</summary>
    public bool ShowsAllProviders => Settings.Layout == PopupLayout.List && CurrentTab != PopupTabs.AgentsKey;

    public void SelectTab(string key)
    {
        requestedTab = key;
        if (key != PopupTabs.AgentsKey) Settings.SelectedProvider = key;
        Settings.Save();
        Changed?.Invoke();
    }

    public void SelectProvider(ProviderId id) => SelectTab(id.Key());

    /// <summary>Picks a tab without saving anything: used by <c>--snapshot --tab</c>.</summary>
    public void PreviewTab(string key) => requestedTab = key;

    /// <summary>"Set up agents…": opens the Agents tab, also with a single agent.</summary>
    public void ShowAgents() => SelectTab(PopupTabs.AgentsKey);

    public void DismissOnboarding()
    {
        Settings.OnboardingDismissed = true;
        requestedTab = VisibleProviders.Count > 0 ? VisibleProviders[0].Key() : null;
        Settings.Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// The Agents tab's "Show" box. Ticking it shows the agent even when its usage does not load
    /// (with the error), unticking hides it even when it works.
    /// </summary>
    public void SetProviderHidden(ProviderId id, bool hidden)
    {
        UpdateSettings(() =>
        {
            Settings.HiddenProviders.RemoveAll(key => key == id.Key());
            Settings.ShownProviders.RemoveAll(key => key == id.Key());
            (hidden ? Settings.HiddenProviders : Settings.ShownProviders).Add(id.Key());
            UpdateVisibleProviders();
        });
        _ = RefreshAsync(force: true);
    }

    /// <summary>One row per supported agent, connected or not, for the Agents tab.</summary>
    public IReadOnlyList<AgentRow> AgentRows() =>
    [
        .. ProviderCatalog.All.Select(descriptor =>
        {
            var provider = providers.FirstOrDefault(p => p.Id == descriptor.Id);
            var status = provider?.Detect() ?? (State(descriptor.Id).Snapshot is null ? SetupStatus.NeedsLogin : SetupStatus.Connected);
            var snapshot = State(descriptor.Id).Snapshot;
            return new AgentRow(descriptor.Id, status, VisibleProviders.Contains(descriptor.Id), snapshot is not null, snapshot?.PlanName);
        }),
    ];

    public ProviderState State(ProviderId id) => states.TryGetValue(id, out var state) ? state : states[id] = new ProviderState();

    public bool IsLoading => VisibleProviders.Any(id => State(id).IsLoading);

    /// <summary>The oldest successful update, so a stale service is not hidden.</summary>
    public DateTimeOffset? LastUpdated =>
        VisibleProviders.Select(id => State(id).LastUpdated).Where(d => d is not null).Min();

    // MARK: Settings

    public void SetStyle(TrayStyle style) => UpdateSettings(() => Settings.Style = style);

    public void SetLayout(PopupLayout layout) => UpdateSettings(() => Settings.Layout = layout);

    public void SetDisplay(ProviderId provider, TrayDisplay display) =>
        UpdateSettings(() => Settings.Displays[provider.Key()] = display);

    public void SetTabLimit(ProviderId provider, string key) =>
        UpdateSettings(() => Settings.TabLimits[provider.Key()] = key);

    /// <summary>What the agent's tab can show, for its settings menu.</summary>
    public IReadOnlyList<string> TabLimitOptions(ProviderId provider) =>
        TabLimit.Options(State(provider).Snapshot?.Items ?? [], Settings.TabLimitFor(provider));

    public void SetTaskbarWidget(bool enabled) => UpdateSettings(() => Settings.ShowTaskbarWidget = enabled);

    /// <summary>
    /// Changing the anchor drops the drag offset, which only makes sense relative to it.
    /// Picking a position is a request to see the widget there, so it also turns the widget on.
    /// </summary>
    public void SetTaskbarPlacement(TaskbarPlacement placement) => UpdateSettings(() =>
    {
        Settings.TaskbarPlacement = placement;
        Settings.TaskbarOffset = 0;
        Settings.ShowTaskbarWidget = true;
    });

    /// <summary>Picking a style also turns the widget on, like <see cref="SetTaskbarPlacement"/>.</summary>
    public void SetTaskbarStyle(TaskbarStyle style) => UpdateSettings(() =>
    {
        Settings.TaskbarStyle = style;
        Settings.ShowTaskbarWidget = true;
    });

    public void ResetTaskbarOffset() => UpdateSettings(() =>
    {
        Settings.TaskbarOffset = 0;
        Settings.ShowTaskbarWidget = true;
    });

    /// <summary>Writes the settings without notifying: the widget saves once when a drag ends.</summary>
    public void SaveSettings() => Settings.Save();

    public void SetColorAlerts(bool enabled) => UpdateSettings(() => Settings.ColorAlerts = enabled);

    public void SetLanguage(AppLanguage language) => UpdateSettings(() =>
    {
        Settings.LanguageValue = language;
        Localization.Current = language;
    });

    private void UpdateSettings(Action change)
    {
        change();
        Settings.Save();
        Changed?.Invoke();
    }

    // MARK: Refreshing

    public void Start()
    {
        _ = RefreshAsync(force: true);
        timer = new DispatcherTimer { Interval = RefreshInterval };
        timer.Tick += async (_, _) => await RefreshAsync(force: false);
        timer.Start();
    }

    /// <summary>Called when the popup opens.</summary>
    public Task RefreshIfStaleAsync()
    {
        var fresh = VisibleProviders.All(id =>
            State(id) is { Error: null, LastUpdated: { } updated } && DateTimeOffset.Now - updated < TimeSpan.FromMinutes(1));
        return fresh ? Task.CompletedTask : RefreshAsync(force: true);
    }

    /// <summary>Also fetches agents that are signed in but not shown yet, so one appears as soon as its usage loads.</summary>
    public async Task RefreshAsync(bool force)
    {
        UpdateVisibleProviders();
        var now = DateTimeOffset.Now;
        var candidates = Candidates().Select(p => p.Id).ToHashSet();
        var targets = providers
            .Where(p => candidates.Contains(p.Id) && !State(p.Id).IsLoading)
            .Where(p => force || State(p.Id).BackoffUntil is not { } backoff || backoff <= now)
            .ToList();
        if (targets.Count == 0) return;

        foreach (var provider in targets) State(provider.Id).IsLoading = true;
        Changed?.Invoke();

        var results = await Task.WhenAll(targets.Select(async provider =>
        {
            try
            {
                return (provider.Id, Result: await provider.FetchAsync(), Error: (UsageException?)null);
            }
            catch (UsageException error)
            {
                return (provider.Id, Result: (ProviderFetchResult?)null, Error: error);
            }
            catch (Exception error)
            {
                return (provider.Id, Result: (ProviderFetchResult?)null, Error: new UsageException(UsageErrorKind.Decoding, inner: error));
            }
        }));

        foreach (var (id, result, error) in results)
        {
            var state = State(id);
            state.IsLoading = false;
            if (result is not null)
            {
                state.Snapshot = result.Snapshot;
                state.LastUpdated = DateTimeOffset.Now;
                state.Error = null;
                state.BackoffUntil = null;
                SaveCache(id, result.Cache);
            }
            else
            {
                state.Error = error;
                if (error?.Kind == UsageErrorKind.RateLimited) state.BackoffUntil = DateTimeOffset.Now + RateLimitBackoff;
            }
        }
        UpdateVisibleProviders();
        Changed?.Invoke();
    }

    /// <summary>Signed in on this computer and not switched off by the user.</summary>
    private IEnumerable<IUsageProvider> Candidates() =>
        providers.Where(p => !Settings.HiddenProviders.Contains(p.Id.Key()) && p.IsAvailable());

    /// <summary>
    /// A login alone is not enough to show an agent: the gh CLI, for one, can be signed in without a
    /// Copilot plan. An agent shows up once its usage has loaded on this computer (it stays after that,
    /// also when its login later expires), or when the user ticked Show for it.
    /// </summary>
    private void UpdateVisibleProviders() =>
        VisibleProviders = [.. Candidates()
            .Where(p => State(p.Id).Snapshot is not null || Settings.ShownProviders.Contains(p.Id.Key()))
            .Select(p => p.Id)];

    // MARK: Cache (usage responses only, never tokens)

    private static string CachePath(ProviderId id) => Path.Combine(AppSettings.Directory, "cache", $"{id.Key()}.json");

    private static void SaveCache(ProviderId id, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath(id))!);
            File.WriteAllBytes(CachePath(id), data);
        }
        catch (Exception)
        {
            // The cache only speeds up the next launch.
        }
    }

    private void LoadCache()
    {
        foreach (var provider in providers)
        {
            var path = CachePath(provider.Id);
            try
            {
                if (!File.Exists(path) || provider.SnapshotFromCache(File.ReadAllBytes(path)) is not { } snapshot) continue;
                states[provider.Id] = new ProviderState
                {
                    Snapshot = snapshot,
                    LastUpdated = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                };
            }
            catch (Exception)
            {
                // Ignore an unreadable cache.
            }
        }
    }
}
