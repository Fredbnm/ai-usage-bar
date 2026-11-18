import Foundation
import UsageCore

@MainActor
final class UsageStore: ObservableObject {
    struct ProviderState {
        /// What is shown: what the agent returned, plus for Claude any newer numbers from Claude Code.
        var snapshot: ProviderSnapshot?
        var lastUpdated: Date?
        /// What the agent itself returned last, and when.
        var fetched: ProviderSnapshot?
        var fetchedAt: Date?
        var error: UsageError?
        var isLoading = false
        /// The last request, successful or not.
        var lastAttempt: Date?
        /// The login the last request used, when the agent can name one, so a
        /// sign-in change can lift a wait the previous login earned.
        var login: String?
        var backoff = RateLimitBackoff()
        /// Claude, with the Claude Code switch off: a rate limit with nothing to show suggests turning it on.
        var suggestsClaudeCode = false
        /// What the service's public status page says; nil until it was read, or when it could not be read for a while.
        var serviceStatus: ServiceStatus?
        /// The last time the status page answered, and the last time it was asked.
        var statusCheckedAt: Date?
        var statusAttempt: Date?
        var isCheckingStatus = false

        /// Waiting out a "too many requests": a notice, not a warning.
        var isRateLimited: Bool { error?.isRateLimit ?? false }
        /// An error worth flagging in the menu bar and on the tab.
        var hasWarning: Bool { error != nil && !isRateLimited }
        /// The status page reports a problem (maintenance alone does not count).
        var hasOutage: Bool { serviceStatus?.isProblem ?? false }

        func message(for id: ProviderID, now: Date = Date()) -> String? {
            guard let message = error?.message(for: id, retryAt: backoff.until, now: now) else { return nil }
            guard suggestsClaudeCode, isRateLimited, snapshot == nil else { return message }
            return message + " " + ClaudeCodeBridge.hint
        }
    }

    @Published private(set) var states: [ProviderID: ProviderState] = [:]
    /// Services shown in the popover, in display order.
    @Published private(set) var visibleProviders: [ProviderID] = []
    @Published private(set) var menuBarDisplays: [ProviderID: MenuBarDisplay] = [:]
    /// The limit each agent's tab shows (`TabLimit` keys); missing means the fullest one.
    @Published private(set) var tabLimits: [ProviderID: String] = [:]
    /// The first-run "Agents" tab was closed with Done.
    @Published private(set) var onboardingDismissed: Bool
    /// Agents the user switched off in the Agents tab (raw values).
    @Published private(set) var hiddenProviders: Set<String>
    /// Agents the user switched on in the Agents tab, shown even before they return any usage (raw values).
    @Published private(set) var shownProviders: Set<String>
    /// The tab asked for; `currentTab` resolves it against what is connected.
    @Published private var requestedTab: String?
    /// Tabs (one agent at a time) or every agent on one screen.
    @Published var popupLayout: PopupLayout {
        didSet { if !isPreview { defaults.set(popupLayout.rawValue, forKey: Keys.popupLayout) } }
    }
    @Published var menuBarStyle: MenuBarStyle {
        didSet { defaults.set(menuBarStyle.rawValue, forKey: Keys.menuBarStyle) }
    }
    /// Orange/red ring when a limit is close; off keeps it in the menu bar color.
    @Published var colorAlerts: Bool {
        didSet { defaults.set(colorAlerts, forKey: Keys.colorAlerts) }
    }
    @Published var language: AppLanguage {
        didSet {
            AppLanguage.current = language
            defaults.set(language.rawValue, forKey: Keys.language)
        }
    }
    /// Claude Code's status line runs this app and hands it the Claude numbers.
    @Published private(set) var readsClaudeCode = false

    static let refreshInterval: TimeInterval = 180
    /// Opening the popover, and every `openTick` while it stays open, asks again the agents asked longer ago than this.
    static let staleAfter: TimeInterval = 60
    static let openTick: TimeInterval = 15
    /// How often the numbers Claude Code saved are looked at.
    static let claudeCodeTick: TimeInterval = 15
    /// How often the services' status pages are read; opening the popover reads the ones older than `statusStaleAfter`.
    static let statusInterval: TimeInterval = 300
    static let statusStaleAfter: TimeInterval = 120
    /// The refresh button reads them again unless they were just read.
    static let statusForcedAfter: TimeInterval = 15
    /// A status page that has not answered for this long no longer counts: its last word may be out of date.
    static let statusExpiresAfter: TimeInterval = 1800

    private let providers: [any UsageProvider]
    private let defaults: UserDefaults
    /// `--snapshot` changes settings to draw them; those changes are not saved.
    private var isPreview = false
    private var timer: Timer?
    /// Runs while the popover is open.
    private var openTimer: Timer?
    /// Asks a rate-limited agent again as soon as its wait ends.
    private var retryTimer: Timer?
    /// Claude Code's status line and the numbers it saved; nil in previews and most tests.
    private let claudeCode: ClaudeCodeBridge?
    private var claudeCodeReading: ClaudeCodeReading?
    private var claudeCodeDate: Date?
    private var claudeCodeTimer: Timer?
    /// Reads the status pages; nil in previews and tests.
    private let statusClient: (any ServiceStatusFetching)?
    private var statusTimer: Timer?
    /// What Claude Code's status line runs.
    var executablePath: String? = Bundle.main.executablePath
    /// The current time; tests move it forward.
    var clock: () -> Date = { Date() }

    private enum Keys {
        /// Settings from versions 0.1 and 0.2 that no longer apply.
        static let legacyKeys = ["cachedResponse", "cachedAt", "planName", "menuBarMetric", "menuBarSelection"]
        static func cache(_ id: ProviderID) -> String { "cache.\(id.rawValue)" }
        static func cachedAt(_ id: ProviderID) -> String { "cachedAt.\(id.rawValue)" }
        static func menuBar(_ id: ProviderID) -> String { "menuBar.\(id.rawValue)" }
        static func tab(_ id: ProviderID) -> String { "tab.\(id.rawValue)" }
        static let menuBarStyle = "menuBar.style"
        static let popupLayout = "popup.layout"
        static let colorAlerts = "menuBar.colorAlerts"
        static let language = "language"
        static let onboardingDismissed = "onboardingDismissed"
        static let selectedProvider = "selectedProvider"
        static let hiddenProviders = "hiddenProviders"
        static let shownProviders = "shownProviders"
    }

    init(
        providers: [any UsageProvider] = [ClaudeProvider(), CodexProvider(), CopilotProvider(), CursorProvider(), GeminiProvider(), GrokBotProvider()],
        defaults: UserDefaults = .standard,
        claudeCode: ClaudeCodeBridge? = nil,
        statusClient: (any ServiceStatusFetching)? = nil
    ) {
        self.providers = providers
        self.defaults = defaults
        self.claudeCode = claudeCode
        self.statusClient = statusClient
        Self.migrateFromClaudeUsage(into: defaults)
        // Settings saved by an older version: this is not a first run.
        let hadSettings = defaults.object(forKey: Keys.language) != nil
            || defaults.object(forKey: Keys.cache(.claude)) != nil
            || defaults.object(forKey: Keys.menuBar(.claude)) != nil
        onboardingDismissed = defaults.object(forKey: Keys.onboardingDismissed) as? Bool ?? hadSettings
        hiddenProviders = Set(defaults.stringArray(forKey: Keys.hiddenProviders) ?? [])
        shownProviders = Set(defaults.stringArray(forKey: Keys.shownProviders) ?? [])
        requestedTab = defaults.string(forKey: Keys.selectedProvider)
        menuBarStyle = defaults.string(forKey: Keys.menuBarStyle).flatMap(MenuBarStyle.init(rawValue:)) ?? .default
        popupLayout = defaults.string(forKey: Keys.popupLayout).flatMap(PopupLayout.init(rawValue:)) ?? .default
        colorAlerts = defaults.object(forKey: Keys.colorAlerts) as? Bool ?? true
        if let saved = defaults.string(forKey: Keys.language).flatMap(AppLanguage.init(rawValue:)) {
            language = saved
        } else {
            // Versions before 0.5 were Portuguese-only: an existing install
            // keeps Portuguese; a new one follows the system language.
            let isUpgrade = defaults.object(forKey: Keys.cache(.claude)) != nil
                || defaults.object(forKey: Keys.menuBar(.claude)) != nil
            language = isUpgrade ? .portuguese : .systemDefault
            defaults.set(language.rawValue, forKey: Keys.language)
        }
        AppLanguage.current = language
        Keys.legacyKeys.forEach(defaults.removeObject(forKey:))
        for id in ProviderID.allCases {
            let saved = defaults.string(forKey: Keys.menuBar(id)).flatMap(MenuBarDisplay.init(rawValue:))
            menuBarDisplays[id] = saved.flatMap { id.menuBarDisplayOptions.contains($0) ? $0 : nil } ?? id.defaultMenuBarDisplay
            tabLimits[id] = defaults.string(forKey: Keys.tab(id))
        }
        updateVisibleProviders()
        loadCache()
        readsClaudeCode = claudeCode?.isInstalled() ?? false
        loadClaudeCode()
    }

    /// Fixed data, no network: used by `--snapshot --fixture`.
    init(preview snapshots: [ProviderSnapshot], updated: Date = Date()) {
        providers = []
        defaults = .standard
        claudeCode = nil
        statusClient = nil
        onboardingDismissed = true
        hiddenProviders = []
        shownProviders = []
        requestedTab = nil
        menuBarStyle = .default
        popupLayout = .default
        colorAlerts = true
        language = AppLanguage.current
        isPreview = true
        visibleProviders = snapshots.map(\.provider)
        for id in ProviderID.allCases { menuBarDisplays[id] = id.defaultMenuBarDisplay }
        for snapshot in snapshots {
            states[snapshot.provider] = ProviderState(snapshot: snapshot, lastUpdated: updated)
        }
    }

    /// Up to 0.5 the app was "Claude Usage" (bundle id local.claude-usage-bar):
    /// carry its settings and cache over once, on the first launch under the new id.
    private static func migrateFromClaudeUsage(into defaults: UserDefaults) {
        let marker = "migratedFromClaudeUsage"
        guard !defaults.bool(forKey: marker) else { return }
        defaults.set(true, forKey: marker)
        guard defaults.object(forKey: Keys.language) == nil,
              let legacy = UserDefaults(suiteName: "local.claude-usage-bar")
        else { return }
        let prefixes = ["menuBar", "cache.", "cachedAt.", "language"]
        for (key, value) in legacy.dictionaryRepresentation() where prefixes.contains(where: key.hasPrefix) {
            defaults.set(value, forKey: key)
        }
    }

    // MARK: Tabs and onboarding

    struct AgentRow: Identifiable {
        var id: ProviderID
        var status: SetupStatus
        /// In the popover and the menu bar.
        var isShown: Bool
        /// Its usage loaded at least once on this computer.
        var hasData: Bool
        var plan: String?
    }

    /// The tab the popover shows: a provider's raw value or `PopupTabs.agentsKey`.
    var currentTab: String {
        PopupTabs.resolve(requested: requestedTab, connected: visibleProviders, onboardingDone: onboardingDismissed)
    }

    var showTabStrip: Bool { popupLayout == .tabs && PopupTabs.showStrip(connectedCount: visibleProviders.count) }

    /// "All AIs together": every agent on one screen, unless the Agents tab was asked for.
    var showsAllProviders: Bool { popupLayout == .list && currentTab != PopupTabs.agentsKey }

    var currentProvider: ProviderID? { ProviderID(rawValue: currentTab) }

    /// Keys of the tabs in order, the Agents tab last.
    var tabKeys: [String] { visibleProviders.map(\.rawValue) + [PopupTabs.agentsKey] }

    func selectTab(_ key: String) {
        requestedTab = key
        if key != PopupTabs.agentsKey { defaults.set(key, forKey: Keys.selectedProvider) }
    }

    /// Moves to the next/previous tab (arrow keys).
    func stepTab(_ direction: Int) {
        guard showTabStrip else { return }
        selectTab(PopupTabs.step(keys: tabKeys, current: currentTab, direction: direction))
    }

    /// Picks a tab without saving anything: used by `--snapshot --tab`.
    func previewTab(_ key: String) { requestedTab = key }

    /// "Set up agents…": opens the Agents tab, also with a single agent.
    func showAgents() { selectTab(PopupTabs.agentsKey) }

    func dismissOnboarding() {
        onboardingDismissed = true
        defaults.set(true, forKey: Keys.onboardingDismissed)
        requestedTab = visibleProviders.first?.rawValue
    }

    /// Previews the first-run state without saving it.
    func previewFirstRun() { onboardingDismissed = false }

    /// The Agents tab's "Show" box. Ticking it shows the agent even when its usage
    /// does not load (with the error), unticking hides it even when it works.
    func setProviderHidden(_ id: ProviderID, _ hidden: Bool) {
        if hidden {
            hiddenProviders.insert(id.rawValue)
            shownProviders.remove(id.rawValue)
        } else {
            hiddenProviders.remove(id.rawValue)
            shownProviders.insert(id.rawValue)
        }
        defaults.set(Array(hiddenProviders), forKey: Keys.hiddenProviders)
        defaults.set(Array(shownProviders), forKey: Keys.shownProviders)
        updateVisibleProviders()
        Task { await refresh() }
        Task { await refreshStatus() }
    }

    /// One row per supported agent, connected or not, for the Agents tab.
    var agentRows: [AgentRow] {
        ProviderID.allCases.map { id in
            let status = providers.first { $0.id == id }?.detect()
                ?? (state(for: id).snapshot == nil ? .needsLogin : .connected)
            let snapshot = state(for: id).snapshot
            return AgentRow(id: id, status: status, isShown: visibleProviders.contains(id), hasData: snapshot != nil, plan: snapshot?.planName)
        }
    }

    // MARK: Menu bar settings

    func menuBarDisplay(for id: ProviderID) -> MenuBarDisplay {
        menuBarDisplays[id] ?? id.defaultMenuBarDisplay
    }

    func setMenuBarDisplay(_ display: MenuBarDisplay, for id: ProviderID) {
        menuBarDisplays[id] = display
        defaults.set(display.rawValue, forKey: Keys.menuBar(id))
    }

    // MARK: Tab settings

    func tabLimit(for id: ProviderID) -> String {
        tabLimits[id] ?? TabLimit.highest
    }

    func setTabLimit(_ key: String, for id: ProviderID) {
        tabLimits[id] = key
        defaults.set(key, forKey: Keys.tab(id))
    }

    /// What the agent's tab can show, for its settings menu.
    func tabLimitOptions(for id: ProviderID) -> [String] {
        TabLimit.options(items: state(for: id).snapshot?.items ?? [], saved: tabLimit(for: id))
    }

    /// Overrides the tab settings without saving them (for `--snapshot --tab-limit`).
    func previewTabLimit(_ key: String, for id: ProviderID) {
        tabLimits[id] = key
    }

    /// Shows a status page problem without reading any page (for `--snapshot --service-status`).
    func previewServiceStatus(_ level: StatusLevel, for id: ProviderID) {
        let url = id.statusPage?.pageURL ?? URL(string: "https://status.claude.com")!
        states[id, default: ProviderState()].serviceStatus = ServiceStatus(level: level, incidents: ["Elevated error rates"], pageURL: url)
    }

    /// Overrides the settings without saving them (for previews).
    func previewMenuBar(_ displays: [ProviderID: MenuBarDisplay]) {
        menuBarDisplays.merge(displays) { _, new in new }
    }

    /// The blocks to draw in the menu bar, in display order.
    var menuBarSegments: [MenuBarSegment] {
        visibleProviders.compactMap { id in
            let state = state(for: id)
            return MenuBarSegment.make(
                provider: id,
                display: menuBarDisplay(for: id),
                snapshot: state.snapshot,
                hasError: state.hasWarning,
                alert: state.hasOutage ? state.serviceStatus?.severity : nil
            )
        }
    }

    // MARK: Claude Code

    /// The ⚙ → Claude switch exists (not in previews).
    var canReadClaudeCode: Bool { claudeCode != nil }

    /// Puts the app in Claude Code's status line, or puts the user's status line back.
    func setReadsClaudeCode(_ on: Bool) throws {
        guard let claudeCode else { return }
        if on {
            guard let executablePath else { return }
            try claudeCode.install(executable: executablePath)
        } else {
            try claudeCode.uninstall()
        }
        readsClaudeCode = claudeCode.isInstalled()
        loadClaudeCode(force: true)
    }

    /// At launch: points Claude Code's status line at where the app is now, in case it moved.
    func updateClaudeCodeCommand() {
        guard readsClaudeCode, let executablePath else { return }
        claudeCode?.refresh(executable: executablePath)
    }

    /// Reloads the numbers Claude Code saved when they changed.
    func loadClaudeCode(force: Bool = false) {
        guard let claudeCode else { return }
        let date = claudeCode.readingDate()
        if force || date != claudeCodeDate {
            claudeCodeDate = date
            claudeCodeReading = claudeCode.loadReading()
        }
        showClaudeCode()
        updateVisibleProviders()
    }

    /// Claude's numbers: the endpoint's, with Claude Code's where they are newer.
    private func showClaudeCode() {
        guard claudeCode != nil else { return }
        var state = state(for: .claude)
        let combined = ClaudeCodeUsage.combine(fetched: state.fetched, fetchedAt: state.fetchedAt, reading: claudeCodeReading, now: clock())
        state.snapshot = combined.snapshot
        state.lastUpdated = combined.updated
        state.suggestsClaudeCode = !readsClaudeCode
        let old = states[.claude]
        if old?.snapshot != state.snapshot || old?.lastUpdated != state.lastUpdated || old?.suggestsClaudeCode != state.suggestsClaudeCode {
            states[.claude] = state
        }
    }

    // MARK: Derived state

    func state(for id: ProviderID) -> ProviderState {
        states[id] ?? ProviderState()
    }

    var snapshots: [ProviderSnapshot] {
        visibleProviders.compactMap { states[$0]?.snapshot }
    }

    var isLoading: Bool {
        visibleProviders.contains { state(for: $0).isLoading }
    }

    /// A visible agent has an error other than waiting out a rate limit.
    var hasError: Bool {
        visibleProviders.contains { state(for: $0).hasWarning }
    }

    /// A visible agent's service has a problem on its status page.
    var hasOutage: Bool {
        visibleProviders.contains { state(for: $0).hasOutage }
    }

    /// The oldest successful update, so a stale service is not hidden.
    var lastUpdated: Date? {
        visibleProviders.compactMap { states[$0]?.lastUpdated }.min()
    }

    var tooltip: String {
        let lines = visibleProviders.flatMap { id -> [String] in
            let state = state(for: id)
            let outage = state.hasOutage ? state.serviceStatus.map { ["⚠ " + $0.title(for: id)] } ?? [] : []
            guard let snapshot = state.snapshot else {
                return outage + (state.message(for: id, now: clock()).map { ["\(id.displayName): \($0)"] } ?? [])
            }
            return outage + snapshot.items.map { "\(id.displayName) · \($0.title): \(UsageFormat.percent($0.percent))" }
        }
        return lines.isEmpty ? tr("Uso de IA", "AI usage") : lines.joined(separator: "\n")
    }

    // MARK: Refreshing

    func start() {
        Task { await refresh() }
        timer?.invalidate()
        let timer = Timer(timeInterval: Self.refreshInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refresh() }
        }
        timer.tolerance = 20
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer

        statusTimer?.invalidate()
        if statusClient != nil {
            Task { await refreshStatus() }
            let statusTimer = Timer(timeInterval: Self.statusInterval, repeats: true) { [weak self] _ in
                Task { @MainActor in await self?.refreshStatus() }
            }
            statusTimer.tolerance = 30
            RunLoop.main.add(statusTimer, forMode: .common)
            self.statusTimer = statusTimer
        }

        claudeCodeTimer?.invalidate()
        guard claudeCode != nil else { return }
        let claudeCodeTimer = Timer(timeInterval: Self.claudeCodeTick, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.loadClaudeCode() }
        }
        claudeCodeTimer.tolerance = 3
        RunLoop.main.add(claudeCodeTimer, forMode: .common)
        self.claudeCodeTimer = claudeCodeTimer
    }

    /// Refreshes what is stale now and every minute while the popover stays open.
    func popoverDidOpen() {
        loadClaudeCode()
        Task { await refreshIfStale() }
        Task { await refreshStatus() }
        openTimer?.invalidate()
        let timer = Timer(timeInterval: Self.openTick, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refreshIfStale() }
        }
        timer.tolerance = 2
        RunLoop.main.add(timer, forMode: .common)
        openTimer = timer
    }

    func popoverDidClose() {
        openTimer?.invalidate()
        openTimer = nil
    }

    /// The agents not asked in the last minute.
    func refreshIfStale() async {
        let now = clock()
        await refresh { state in
            state.lastAttempt.map { now.timeIntervalSince($0) >= Self.staleAfter } ?? true
        }
    }

    /// Every agent, except one waiting out a "too many requests": clicking refresh then
    /// would only make the server wait longer, so its last numbers stay until the wait ends.
    /// Also fetches agents that are signed in but not shown yet, so one appears as soon as its usage loads.
    func refresh() async {
        await refresh { _ in true }
    }

    /// The refresh button and waking from sleep: usage and the status pages together.
    func refreshAll() async {
        async let usage: Void = refresh()
        async let status: Void = refreshStatus(force: true)
        _ = await (usage, status)
    }

    /// Reads the status pages of the agents shown, except one read in the last two minutes
    /// (15 seconds with `force`). Separate from the usage requests and their rate-limit waits:
    /// the pages are public and say nothing about the user.
    func refreshStatus(force: Bool = false) async {
        guard let statusClient else { return }
        let now = clock()
        let staleAfter = force ? Self.statusForcedAfter : Self.statusStaleAfter
        let targets: [(ProviderID, StatusPage)] = visibleProviders.compactMap { id in
            guard let page = id.statusPage else { return nil }
            let state = state(for: id)
            if state.isCheckingStatus { return nil }
            if let last = state.statusAttempt, now.timeIntervalSince(last) < staleAfter { return nil }
            return (id, page)
        }
        guard !targets.isEmpty else { return }
        for (id, _) in targets {
            states[id, default: ProviderState()].isCheckingStatus = true
            states[id]?.statusAttempt = now
        }

        await withTaskGroup(of: (ProviderID, ServiceStatus?).self) { group in
            for (id, page) in targets {
                group.addTask { (id, try? await statusClient.fetch(page)) }
            }
            for await (id, status) in group {
                var state = state(for: id)
                state.isCheckingStatus = false
                if let status {
                    state.serviceStatus = status
                    state.statusCheckedAt = clock()
                } else if let checked = state.statusCheckedAt, clock().timeIntervalSince(checked) < Self.statusExpiresAfter {
                    // Keeps the last answer: the page may just be slow, or the network down.
                } else {
                    state.serviceStatus = nil
                }
                states[id] = state
            }
        }
    }

    private func refresh(where wanted: (ProviderState) -> Bool) async {
        updateVisibleProviders()
        let now = clock()
        let candidates = Set(candidateProviders.map(\.id))
        let targets = providers.filter { provider in
            let state = state(for: provider.id)
            guard candidates.contains(provider.id), !state.isLoading, wanted(state) else { return false }
            guard state.backoff.isWaiting(now: now) else { return true }
            // Signing in again is a fresh start: the wait belonged to the login
            // that earned it, not to this one. Only asked while an agent waits,
            // so the common path does not read the login.
            return signedInAgain(provider, state: state)
        }
        guard !targets.isEmpty else { return }
        for provider in targets {
            states[provider.id, default: ProviderState()].isLoading = true
            states[provider.id]?.lastAttempt = now
            // Got through on a new login: the previous one's wait is spent.
            if states[provider.id]?.backoff.isWaiting(now: now) == true {
                states[provider.id]?.backoff.reset()
            }
        }

        await withTaskGroup(of: (ProviderID, Result<ProviderFetchResult, Error>).self) { group in
            for provider in targets {
                group.addTask {
                    do { return (provider.id, .success(try await provider.fetch())) }
                    catch { return (provider.id, .failure(error)) }
                }
            }
            for await (id, result) in group {
                apply(result, to: id)
            }
        }
        scheduleRetry()
        updateVisibleProviders()
        // An agent that just showed up gets its status page read now, not at the next tick.
        if statusClient != nil, visibleProviders.contains(where: { $0.statusPage != nil && state(for: $0).statusAttempt == nil }) {
            Task { await refreshStatus() }
        }
    }

    private func apply(_ result: Result<ProviderFetchResult, Error>, to id: ProviderID) {
        var state = state(for: id)
        state.isLoading = false
        switch result {
        case .success(let fetched):
            let now = clock()
            state.snapshot = fetched.snapshot
            state.lastUpdated = now
            state.fetched = fetched.snapshot
            state.fetchedAt = now
            state.error = nil
            state.backoff.reset()
            defaults.set(fetched.cache, forKey: Keys.cache(id))
            defaults.set(now, forKey: Keys.cachedAt(id))
        case .failure(let error):
            let usageError = (error as? UsageError) ?? .decoding
            state.error = usageError
            switch usageError {
            case .rateLimited(let retryAfter):
                state.backoff.record(serverWait: retryAfter, now: clock())
            case .unauthorized, .notLoggedIn:
                // A login the server rejects will be rejected again in three
                // minutes. Asking anyway, on every tick and every popover, is
                // what turns one 401 into a 429 nothing can wait out. The wait
                // is lifted as soon as the login changes.
                state.backoff.record(serverWait: nil, now: clock())
            default:
                break
            }
            // Only the failing path needs to remember the login, and only to
            // notice it changing: reading it costs a process on Claude.
            if state.backoff.isWaiting(now: clock()) {
                state.login = providers.first { $0.id == id }?.loginIdentity()
            }
        }
        states[id] = state
        if id == .claude { showClaudeCode() }
    }

    /// Whether the agent's login is not the one that earned its current wait.
    private func signedInAgain(_ provider: UsageProvider, state: ProviderState) -> Bool {
        guard let previous = state.login, let current = provider.loginIdentity() else { return false }
        return current != previous
    }

    /// Asks the rate-limited agents again when the first wait ends, instead of at the next 3-minute tick.
    private func scheduleRetry() {
        retryTimer?.invalidate()
        retryTimer = nil
        let now = clock()
        guard let next = states.values.filter({ $0.isRateLimited && $0.backoff.isWaiting(now: now) }).compactMap(\.backoff.until).min()
        else { return }
        let timer = Timer(timeInterval: next.timeIntervalSince(now) + 1, repeats: false) { [weak self] _ in
            Task { @MainActor in await self?.refresh { $0.isRateLimited } }
        }
        timer.tolerance = 5
        RunLoop.main.add(timer, forMode: .common)
        retryTimer = timer
    }

    /// Signed in on this computer and not switched off by the user.
    private var candidateProviders: [any UsageProvider] {
        providers.filter { !hiddenProviders.contains($0.id.rawValue) && $0.isAvailable() }
    }

    /// A login alone is not enough to show an agent: the gh CLI, for one, can be signed in without a
    /// Copilot plan. An agent shows up once its usage has loaded on this computer (it stays after
    /// that, also when its login later expires), or when the user ticked Show for it.
    private func updateVisibleProviders() {
        let visible = candidateProviders
            .filter { states[$0.id]?.snapshot != nil || shownProviders.contains($0.id.rawValue) }
            .map(\.id)
        if visible != visibleProviders { visibleProviders = visible }
    }

    private func loadCache() {
        for provider in providers {
            guard let data = defaults.data(forKey: Keys.cache(provider.id)),
                  let snapshot = provider.snapshot(fromCache: data)
            else { continue }
            let cachedAt = defaults.object(forKey: Keys.cachedAt(provider.id)) as? Date
            states[provider.id] = ProviderState(snapshot: snapshot, lastUpdated: cachedAt, fetched: snapshot, fetchedAt: cachedAt)
        }
    }

    // MARK: Debug output

    /// Plain-text summary for `--dump`.
    var dumpText: String {
        var lines: [String] = []
        for id in visibleProviders {
            let state = state(for: id)
            lines.append("== \(id.displayName)\(state.snapshot?.planName.map { " (\($0))" } ?? "")")
            if let updated = state.lastUpdated { lines.append(UsageFormat.updated(updated)) }
            if let message = state.message(for: id, now: clock()) { lines.append(tr("Aviso: ", "Warning: ") + message) }
            if let status = state.serviceStatus, status.isNotice {
                let details = status.detailLines.joined(separator: "; ")
                lines.append("Status: " + status.title(for: id) + (details.isEmpty ? "" : " — " + details))
            }
            for item in state.snapshot?.items ?? [] {
                let reset = item.resetsAt.map { " — \(UsageFormat.reset($0))" } ?? ""
                lines.append("• \(item.title): \(UsageFormat.percent(item.percent)) [\(item.severity)]\(reset)")
            }
            for note in state.snapshot?.notes ?? [] { lines.append("ℹ︎ \(note)") }
            if id == .claude, let reading = claudeCodeReading?.current(now: clock()), !reading.isEmpty {
                let parts = [("5 h", reading.session), ("7 d", reading.weekly)].compactMap { name, window in
                    window.map { "\(name) \(UsageFormat.percent($0.percent))" }
                }
                let seen = [reading.session?.seenAt, reading.weekly?.seenAt].compactMap { $0 }.max()
                lines.append("ℹ︎ Claude Code (statusline): " + parts.joined(separator: " · ") + (seen.map { " — " + UsageFormat.updated($0, now: clock()) } ?? ""))
            }
            let breakdown = (state.snapshot?.breakdown ?? []).map { "\($0.name) \(UsageFormat.percent($0.percent))" }
            if !breakdown.isEmpty { lines.append(tr("Divisão da semana: ", "Weekly split: ") + breakdown.joined(separator: ", ")) }
        }
        return lines.joined(separator: "\n")
    }

    /// The cached usage response, for `--dump --raw`. Contains no tokens.
    func rawResponse(for id: ProviderID) -> String? {
        guard let data = defaults.data(forKey: Keys.cache(id)),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rawBase64 = object["raw"] as? String,
              let raw = Data(base64Encoded: rawBase64)
        else { return nil }
        return String(data: raw, encoding: .utf8)
    }
}
