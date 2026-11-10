import Foundation
import UsageCore

@MainActor
final class UsageStore: ObservableObject {
    struct ProviderState {
        var snapshot: ProviderSnapshot?
        var lastUpdated: Date?
        var error: UsageError?
        var isLoading = false
        var backoffUntil: Date?
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

    static let refreshInterval: TimeInterval = 180
    static let rateLimitBackoff: TimeInterval = 600

    private let providers: [any UsageProvider]
    private let defaults: UserDefaults
    /// `--snapshot` changes settings to draw them; those changes are not saved.
    private var isPreview = false
    private var timer: Timer?

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
        providers: [any UsageProvider] = [ClaudeProvider(), CodexProvider(), CopilotProvider(), GeminiProvider(), GrokBotProvider()],
        defaults: UserDefaults = .standard
    ) {
        self.providers = providers
        self.defaults = defaults
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
    }

    /// Fixed data, no network: used by `--snapshot --fixture`.
    init(preview snapshots: [ProviderSnapshot], updated: Date = Date()) {
        providers = []
        defaults = .standard
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
        Task { await refresh(force: true) }
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
                hasError: state.error != nil
            )
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

    var hasError: Bool {
        visibleProviders.contains { state(for: $0).error != nil }
    }

    /// The oldest successful update, so a stale service is not hidden.
    var lastUpdated: Date? {
        visibleProviders.compactMap { states[$0]?.lastUpdated }.min()
    }

    var tooltip: String {
        let lines = visibleProviders.flatMap { id -> [String] in
            let state = state(for: id)
            guard let snapshot = state.snapshot else {
                return state.error.map { ["\(id.displayName): \($0.message(for: id))"] } ?? []
            }
            return snapshot.items.map { "\(id.displayName) · \($0.title): \(UsageFormat.percent($0.percent))" }
        }
        return lines.isEmpty ? tr("Uso de IA", "AI usage") : lines.joined(separator: "\n")
    }

    // MARK: Refreshing

    func start() {
        Task { await refresh(force: true) }
        timer?.invalidate()
        let timer = Timer(timeInterval: Self.refreshInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.refresh() }
        }
        timer.tolerance = 20
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// Called when the popover opens.
    func refreshIfStale() async {
        let fresh = visibleProviders.allSatisfy { id in
            let state = state(for: id)
            guard state.error == nil, let updated = state.lastUpdated else { return false }
            return Date().timeIntervalSince(updated) < 60
        }
        if !fresh { await refresh(force: true) }
    }

    /// Also fetches agents that are signed in but not shown yet, so one appears as soon as its usage loads.
    func refresh(force: Bool = false) async {
        updateVisibleProviders()
        let now = Date()
        let candidates = Set(candidateProviders.map(\.id))
        let targets = providers.filter { provider in
            let state = state(for: provider.id)
            guard candidates.contains(provider.id), !state.isLoading else { return false }
            if !force, let backoff = state.backoffUntil, backoff > now { return false }
            return true
        }
        guard !targets.isEmpty else { return }
        for provider in targets { states[provider.id, default: ProviderState()].isLoading = true }

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
        updateVisibleProviders()
    }

    private func apply(_ result: Result<ProviderFetchResult, Error>, to id: ProviderID) {
        var state = state(for: id)
        state.isLoading = false
        switch result {
        case .success(let fetched):
            let now = Date()
            state.snapshot = fetched.snapshot
            state.lastUpdated = now
            state.error = nil
            state.backoffUntil = nil
            defaults.set(fetched.cache, forKey: Keys.cache(id))
            defaults.set(now, forKey: Keys.cachedAt(id))
        case .failure(let error):
            let usageError = (error as? UsageError) ?? .decoding
            state.error = usageError
            if usageError == .rateLimited {
                state.backoffUntil = Date().addingTimeInterval(Self.rateLimitBackoff)
            }
        }
        states[id] = state
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
            states[provider.id] = ProviderState(
                snapshot: snapshot,
                lastUpdated: defaults.object(forKey: Keys.cachedAt(provider.id)) as? Date
            )
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
            if let error = state.error { lines.append(tr("Aviso: ", "Warning: ") + error.message(for: id)) }
            for item in state.snapshot?.items ?? [] {
                let reset = item.resetsAt.map { " — \(UsageFormat.reset($0))" } ?? ""
                lines.append("• \(item.title): \(UsageFormat.percent(item.percent)) [\(item.severity)]\(reset)")
            }
            for note in state.snapshot?.notes ?? [] { lines.append("ℹ︎ \(note)") }
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
