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

    static let refreshInterval: TimeInterval = 180
    static let rateLimitBackoff: TimeInterval = 600

    private let providers: [any UsageProvider]
    private let defaults: UserDefaults
    private var timer: Timer?

    private enum Keys {
        /// Settings from versions 0.1 and 0.2 that no longer apply.
        static let legacyKeys = ["cachedResponse", "cachedAt", "planName", "menuBarMetric", "menuBarSelection"]
        static func cache(_ id: ProviderID) -> String { "cache.\(id.rawValue)" }
        static func cachedAt(_ id: ProviderID) -> String { "cachedAt.\(id.rawValue)" }
        static func menuBar(_ id: ProviderID) -> String { "menuBar.\(id.rawValue)" }
    }

    init(providers: [any UsageProvider] = [ClaudeProvider(), GrokBotProvider()], defaults: UserDefaults = .standard) {
        self.providers = providers
        self.defaults = defaults
        Keys.legacyKeys.forEach(defaults.removeObject(forKey:))
        for id in ProviderID.allCases {
            let saved = defaults.string(forKey: Keys.menuBar(id)).flatMap(MenuBarDisplay.init(rawValue:))
            menuBarDisplays[id] = saved.flatMap { id.menuBarDisplayOptions.contains($0) ? $0 : nil } ?? id.defaultMenuBarDisplay
        }
        updateVisibleProviders()
        loadCache()
    }

    /// Fixed data, no network: used by `--snapshot --fixture`.
    init(preview snapshots: [ProviderSnapshot], updated: Date = Date()) {
        providers = []
        defaults = .standard
        visibleProviders = snapshots.map(\.provider)
        for id in ProviderID.allCases { menuBarDisplays[id] = id.defaultMenuBarDisplay }
        for snapshot in snapshots {
            states[snapshot.provider] = ProviderState(snapshot: snapshot, lastUpdated: updated)
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

    /// Overrides the settings without saving them (for previews).
    func previewMenuBarDisplays(_ displays: [ProviderID: MenuBarDisplay]) {
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
        return lines.isEmpty ? "Uso de IA" : lines.joined(separator: "\n")
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

    func refresh(force: Bool = false) async {
        updateVisibleProviders()
        let now = Date()
        let targets = providers.filter { provider in
            let state = state(for: provider.id)
            guard visibleProviders.contains(provider.id), !state.isLoading else { return false }
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

    private func updateVisibleProviders() {
        let visible = providers.filter { $0.isAvailable() }.map(\.id)
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
            if let error = state.error { lines.append("Aviso: \(error.message(for: id))") }
            for item in state.snapshot?.items ?? [] {
                let reset = item.resetsAt.map { " — \(UsageFormat.reset($0))" } ?? ""
                lines.append("• \(item.title): \(UsageFormat.percent(item.percent)) [\(item.severity)]\(reset)")
            }
            for note in state.snapshot?.notes ?? [] { lines.append("ℹ︎ \(note)") }
            let breakdown = (state.snapshot?.breakdown ?? []).map { "\($0.name) \(UsageFormat.percent($0.percent))" }
            if !breakdown.isEmpty { lines.append("Divisão da semana: " + breakdown.joined(separator: ", ")) }
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
