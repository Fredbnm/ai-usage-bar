using System.Windows.Threading;
using AIUsageBar.Core;

namespace AIUsageBar;

/// <summary>One agent in the Agents tab. <paramref name="HasData"/>: its usage loaded at least once on this computer.</summary>
internal sealed record AgentRow(ProviderId Id, SetupStatus Status, bool IsShown, bool HasData, string? Plan);

internal sealed class ProviderState
{
    /// <summary>What is shown: what the agent returned, plus for Claude any newer numbers from Claude Code.</summary>
    public ProviderSnapshot? Snapshot { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    /// <summary>What the agent itself returned last, and when.</summary>
    public ProviderSnapshot? Fetched { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
    public UsageException? Error { get; set; }
    public bool IsLoading { get; set; }
    /// <summary>The last request, successful or not.</summary>
    public DateTimeOffset? LastAttempt { get; set; }
    /// <summary>The login the last request used, when the agent can name one, so a sign-in change can lift a wait the previous login earned.</summary>
    public string? Login { get; set; }
    public RateLimitBackoff Backoff { get; } = new();
    /// <summary>Claude, with the Claude Code switch off: a rate limit with nothing to show suggests turning it on.</summary>
    public bool SuggestsClaudeCode { get; set; }

    /// <summary>What the service's status page said last; kept through a failed read for a while (<see cref="StatusSchedule.KeptFor"/>).</summary>
    public ServiceStatus? ServiceStatus { get; set; }
    /// <summary>The last successful read of the status page.</summary>
    public DateTimeOffset? StatusCheckedAt { get; set; }
    /// <summary>The last read of the status page, successful or not.</summary>
    public DateTimeOffset? StatusAttempt { get; set; }
    public bool IsCheckingStatus { get; set; }

    /// <summary>The status page reports a problem: a badge on the tray icon and a warning on the tab.</summary>
    public bool HasOutage => ServiceStatus?.IsProblem == true;

    /// <summary>Waiting out a "too many requests": a notice, not a warning.</summary>
    public bool IsRateLimited => Error?.IsRateLimit == true;

    /// <summary>An error worth flagging on the tray icon and the tab.</summary>
    public bool HasWarning => Error is not null && !IsRateLimited;

    public string? MessageFor(ProviderId id, DateTimeOffset? now = null)
    {
        if (Error?.MessageFor(id, Backoff.Until, now) is not { } message) return null;
        return SuggestsClaudeCode && IsRateLimited && Snapshot is null ? message + " " + ClaudeCodeBridge.Hint : message;
    }
}

/// <summary>
/// Owns the per-service state and the settings. Lives on the UI thread;
/// fetches run in the background and their results are applied back here.
/// </summary>
internal sealed class UsageStore
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(3);
    /// <summary>Opening the popup, and every <see cref="OpenTick"/> while it stays open, asks again the agents asked longer ago than this.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan OpenTick = TimeSpan.FromSeconds(15);
    /// <summary>How often the numbers Claude Code saved are looked at.</summary>
    public static readonly TimeSpan ClaudeCodeTick = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyList<IUsageProvider> providers;
    private readonly Dictionary<ProviderId, ProviderState> states = [];
    private DispatcherTimer? timer;
    /// <summary>Runs while the popup is open.</summary>
    private DispatcherTimer? openTimer;
    /// <summary>Asks a rate-limited agent again as soon as its wait ends.</summary>
    private DispatcherTimer? retryTimer;
    /// <summary>Claude Code's status line and the numbers it saved; null in previews and tests.</summary>
    private readonly ClaudeCodeBridge? claudeCode;
    private ClaudeCodeReading? claudeCodeReading;
    private DateTimeOffset? claudeCodeDate;
    private DispatcherTimer? claudeCodeTimer;
    /// <summary>Reads the services' status pages; null in previews and tests.</summary>
    private readonly IServiceStatusFetcher? statusFetcher;
    private DispatcherTimer? statusTimer;

    public UsageStore(
        AppSettings settings, IReadOnlyList<IUsageProvider> providers, ClaudeCodeBridge? claudeCode = null, IServiceStatusFetcher? statusFetcher = null)
    {
        Settings = settings;
        this.providers = providers;
        this.claudeCode = claudeCode;
        this.statusFetcher = statusFetcher;
        UpdateVisibleProviders();
        LoadCache();
        ReadsClaudeCode = claudeCode?.IsInstalled() ?? false;
        LoadClaudeCode();
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

    /// <summary>Pretends the status page says this: used by <c>--snapshot --service-status</c>.</summary>
    public void PreviewServiceStatus(ProviderId id, ServiceStatus status) => State(id).ServiceStatus = status;

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
        _ = RefreshAsync();
        _ = RefreshStatusAsync();
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

    public void SetUsageColors(bool enabled) => UpdateSettings(() => Settings.UsageColors = enabled);

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

    // MARK: Claude Code

    /// <summary>The ⚙ → Claude switch exists (not in previews).</summary>
    public bool CanReadClaudeCode => claudeCode is not null;

    /// <summary>Claude Code's status line runs this app and hands it the Claude numbers.</summary>
    public bool ReadsClaudeCode { get; private set; }

    /// <summary>What Claude Code's status line runs.</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Puts the app in Claude Code's status line, or puts the user's status line back.</summary>
    /// <exception cref="ClaudeCodeSetupException">Claude Code's settings could not be read or written.</exception>
    public void SetReadsClaudeCode(bool on)
    {
        if (claudeCode is null) return;
        try
        {
            if (on && ExecutablePath is { } path) claudeCode.Install(path);
            else if (!on) claudeCode.Uninstall();
        }
        finally
        {
            ReadsClaudeCode = claudeCode.IsInstalled();
            LoadClaudeCode(force: true);
            Changed?.Invoke();
        }
    }

    /// <summary>At launch: points Claude Code's status line at where the app is now, in case it moved.</summary>
    public void UpdateClaudeCodeCommand()
    {
        if (ReadsClaudeCode && ExecutablePath is { } path) claudeCode?.Refresh(path);
    }

    /// <summary>Reloads the numbers Claude Code saved when they changed.</summary>
    public void LoadClaudeCode(bool force = false)
    {
        if (claudeCode is null) return;
        var date = claudeCode.ReadingDate();
        if (force || date != claudeCodeDate)
        {
            claudeCodeDate = date;
            claudeCodeReading = claudeCode.LoadReading();
        }
        var visible = VisibleProviders;
        var changed = ShowClaudeCode();
        UpdateVisibleProviders();
        if (changed || !visible.SequenceEqual(VisibleProviders)) Changed?.Invoke();
        if (VisibleProviders.Except(visible).Any()) _ = RefreshStatusAsync();
    }

    /// <summary>For <c>--dump</c>.</summary>
    public ClaudeCodeReading? ClaudeCodeNumbers => claudeCodeReading?.Current(DateTimeOffset.Now);

    /// <summary>Claude's numbers: the endpoint's, with Claude Code's where they are newer. True when something changed.</summary>
    private bool ShowClaudeCode()
    {
        if (claudeCode is null) return false;
        var state = State(ProviderId.Claude);
        var (snapshot, updated) = ClaudeCodeUsage.Combine(state.Fetched, state.FetchedAt, claudeCodeReading, DateTimeOffset.Now);
        var changed = !ClaudeCodeUsage.Same(state.Snapshot, snapshot) || state.LastUpdated != updated || state.SuggestsClaudeCode == ReadsClaudeCode;
        state.Snapshot = snapshot;
        state.LastUpdated = updated;
        state.SuggestsClaudeCode = !ReadsClaudeCode;
        return changed;
    }

    // MARK: Refreshing

    public void Start()
    {
        _ = RefreshAsync();
        if (claudeCode is not null)
        {
            claudeCodeTimer = new DispatcherTimer { Interval = ClaudeCodeTick };
            claudeCodeTimer.Tick += (_, _) => LoadClaudeCode();
            claudeCodeTimer.Start();
        }
        timer = new DispatcherTimer { Interval = RefreshInterval };
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();

        _ = RefreshStatusAsync();
        statusTimer = new DispatcherTimer { Interval = StatusSchedule.Interval };
        statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        statusTimer.Start();
    }

    /// <summary>Refreshes what is stale now and every minute while the popup stays open.</summary>
    public void PopupOpened()
    {
        LoadClaudeCode();
        _ = RefreshIfStaleAsync();
        _ = RefreshStatusAsync();
        if (openTimer is null)
        {
            openTimer = new DispatcherTimer { Interval = OpenTick };
            openTimer.Tick += async (_, _) => await RefreshIfStaleAsync();
        }
        openTimer.Start();
    }

    public void PopupClosed() => openTimer?.Stop();

    /// <summary>The agents not asked in the last minute.</summary>
    public Task RefreshIfStaleAsync()
    {
        var now = DateTimeOffset.Now;
        return RefreshAsync(state => state.LastAttempt is not { } asked || now - asked >= StaleAfter);
    }

    /// <summary>
    /// Every agent, except one waiting out a "too many requests": clicking refresh then would only make
    /// the server wait longer, so its last numbers stay until the wait ends. Also fetches agents that are
    /// signed in but not shown yet, so one appears as soon as its usage loads.
    /// </summary>
    public Task RefreshAsync() => RefreshAsync(_ => true);

    /// <summary>The refresh button and "Refresh now": the usage, and the status pages not read in the last few seconds.</summary>
    public Task RefreshNowAsync() => Task.WhenAll(RefreshAsync(), RefreshStatusAsync(force: true));

    private async Task RefreshAsync(Func<ProviderState, bool> wanted)
    {
        var visible = VisibleProviders;
        UpdateVisibleProviders();
        var now = DateTimeOffset.Now;
        var candidates = Candidates().Select(p => p.Id).ToHashSet();
        var targets = providers
            .Where(p => candidates.Contains(p.Id)
                && State(p.Id) is { IsLoading: false } state && wanted(state)
                && (!state.Backoff.IsWaiting(now) || SignedInAgain(p, state)))
            .ToList();
        if (targets.Count == 0) return;

        foreach (var provider in targets)
        {
            State(provider.Id).IsLoading = true;
            State(provider.Id).LastAttempt = now;
            // Got through on a new login: the previous one's wait is spent.
            if (State(provider.Id).Backoff.IsWaiting(now)) State(provider.Id).Backoff.Reset();
        }
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
                state.Fetched = result.Snapshot;
                state.FetchedAt = state.LastUpdated;
                state.Error = null;
                state.Backoff.Reset();
                SaveCache(id, result.Cache);
            }
            else
            {
                state.Error = error;
                // A login the server rejects will be rejected again in three
                // minutes. Asking anyway, on every tick and every popup, is what
                // turns one 401 into a 429 nothing can wait out. The wait is
                // lifted as soon as the login changes.
                if (error is { IsRateLimit: true }) state.Backoff.Record(error.RetryAfter, DateTimeOffset.Now);
                else if (error.Kind is UsageErrorKind.Unauthorized or UsageErrorKind.UnauthorizedExpired
                         or UsageErrorKind.NotLoggedIn) state.Backoff.Record(null, DateTimeOffset.Now);
                // Only the failing path needs to remember the login, and only to
                // notice it changing: reading it costs a process on Claude.
                if (state.Backoff.IsWaiting(DateTimeOffset.Now))
                    state.Login = providers.FirstOrDefault(p => p.Id == id)?.LoginIdentity();
            }
        }
        ShowClaudeCode();
        ScheduleRetry();
        UpdateVisibleProviders();
        Changed?.Invoke();
        // An agent that just appeared gets its status without waiting for the next tick.
        if (VisibleProviders.Except(visible).Any()) _ = RefreshStatusAsync();
    }

    /// <summary>
    /// Reads the status pages of the agents shown, at most every 2 minutes each (15 seconds with
    /// <paramref name="force"/>, the refresh button). Independent of the usage refresh: other servers, no rate limit
    /// shared. A failed read keeps the last status for a while, so one bad answer does not make the badge blink.
    /// </summary>
    public async Task RefreshStatusAsync(bool force = false)
    {
        if (statusFetcher is null) return;
        var now = DateTimeOffset.Now;
        var targets = VisibleProviders
            .Select(id => (Id: id, Page: id.StatusPage()))
            .Where(target => target.Page is not null
                && State(target.Id) is { IsCheckingStatus: false } state && StatusSchedule.IsDue(state.StatusAttempt, now, force))
            .ToList();
        if (targets.Count == 0) return;

        foreach (var (id, _) in targets)
        {
            State(id).IsCheckingStatus = true;
            State(id).StatusAttempt = now;
        }

        var results = await Task.WhenAll(targets.Select(async target =>
        {
            try
            {
                return (target.Id, Status: (ServiceStatus?)await statusFetcher.FetchAsync(target.Page!));
            }
            catch (Exception)
            {
                return (target.Id, Status: (ServiceStatus?)null);
            }
        }));

        var changed = false;
        foreach (var (id, status) in results)
        {
            var state = State(id);
            state.IsCheckingStatus = false;
            var shown = state.ServiceStatus;
            if (status is not null)
            {
                state.ServiceStatus = status;
                state.StatusCheckedAt = DateTimeOffset.Now;
            }
            else
            {
                state.ServiceStatus = StatusSchedule.AfterFailure(state.ServiceStatus, state.StatusCheckedAt, DateTimeOffset.Now);
            }
            changed |= !Equals(shown, state.ServiceStatus);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Whether the agent's login is not the one that earned its current wait.</summary>
    private static bool SignedInAgain(IUsageProvider provider, ProviderState state) =>
        state.Login is { } previous && provider.LoginIdentity() is { } current && current != previous;

    /// <summary>Asks the rate-limited agents again when the first wait ends, instead of at the next 3-minute tick.</summary>
    private void ScheduleRetry()
    {
        retryTimer?.Stop();
        var now = DateTimeOffset.Now;
        var waits = states.Values
            .Where(state => state.IsRateLimited && state.Backoff.IsWaiting(now))
            .Select(state => state.Backoff.Until!.Value)
            .ToList();
        if (waits.Count == 0) return;
        if (retryTimer is null)
        {
            var retry = new DispatcherTimer();
            retry.Tick += async (_, _) =>
            {
                retry.Stop();
                await RefreshAsync(state => state.IsRateLimited);
            };
            retryTimer = retry;
        }
        retryTimer.Interval = waits.Min() - now + TimeSpan.FromSeconds(1);
        retryTimer.Start();
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
                var cachedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                states[provider.Id] = new ProviderState { Snapshot = snapshot, LastUpdated = cachedAt, Fetched = snapshot, FetchedAt = cachedAt };
            }
            catch (Exception)
            {
                // Ignore an unreadable cache.
            }
        }
    }
}
