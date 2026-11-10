import Foundation
import UsageCore

@MainActor
final class UsageStore: ObservableObject {
    @Published private(set) var usage: UsageResponse?
    @Published private(set) var lastUpdated: Date?
    @Published private(set) var error: UsageError?
    @Published private(set) var isLoading = false
    @Published private(set) var planName: String?
    @Published var metric: MenuBarMetric {
        didSet { defaults.set(metric.rawValue, forKey: Keys.metric) }
    }

    static let refreshInterval: TimeInterval = 180
    static let rateLimitBackoff: TimeInterval = 600

    private let client = UsageClient()
    private let defaults: UserDefaults
    private var timer: Timer?
    private var backoffUntil: Date?

    private enum Keys {
        static let cachedResponse = "cachedResponse"
        static let cachedAt = "cachedAt"
        static let plan = "planName"
        static let metric = "menuBarMetric"
    }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        metric = MenuBarMetric(rawValue: defaults.string(forKey: Keys.metric) ?? "") ?? .session
        loadCache()
    }

    /// Fixed data, no network: used by `--snapshot --fixture`.
    init(preview usage: UsageResponse, plan: String?, updated: Date = Date(), error: UsageError? = nil) {
        defaults = .standard
        metric = .session
        self.usage = usage
        self.planName = plan
        self.lastUpdated = updated
        self.error = error
    }

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
        if error == nil, let lastUpdated, Date().timeIntervalSince(lastUpdated) < 60 { return }
        await refresh(force: true)
    }

    func refresh(force: Bool = false) async {
        if isLoading { return }
        if !force, let backoffUntil, backoffUntil > Date() { return }
        isLoading = true
        defer { isLoading = false }

        let credentials: ClaudeCredentials
        do {
            credentials = try await Task.detached(priority: .utility) { try CredentialsStore.load() }.value
        } catch {
            self.error = .notLoggedIn
            return
        }
        if let plan = credentials.planName {
            planName = plan
            defaults.set(plan, forKey: Keys.plan)
        }

        do {
            let data = try await client.fetchUsage(accessToken: credentials.accessToken)
            let response = try UsageResponse.decode(data)
            guard !response.items.isEmpty else { throw UsageError.decoding }
            let now = Date()
            usage = response
            lastUpdated = now
            error = nil
            backoffUntil = nil
            defaults.set(data, forKey: Keys.cachedResponse)
            defaults.set(now, forKey: Keys.cachedAt)
        } catch UsageError.unauthorized {
            error = .unauthorized(expired: credentials.isExpired)
        } catch UsageError.rateLimited {
            backoffUntil = Date().addingTimeInterval(Self.rateLimitBackoff)
            error = .rateLimited
        } catch let usageError as UsageError {
            error = usageError
        } catch {
            self.error = .decoding
        }
    }

    var tooltip: String {
        guard let usage else { return error?.message ?? "Uso do Claude" }
        return usage.items
            .map { "\($0.title): \(UsageFormat.percent($0.percent))" }
            .joined(separator: "\n")
    }

    /// Plain-text summary for `--dump`.
    var dumpText: String {
        var lines: [String] = []
        lines.append("Plano: \(planName ?? "?")")
        if let lastUpdated { lines.append(UsageFormat.updated(lastUpdated)) }
        if let error { lines.append("Aviso: \(error.message)") }
        for item in usage?.items ?? [] {
            let reset = item.resetsAt.map { " — \(UsageFormat.reset($0))" } ?? ""
            lines.append("• \(item.title): \(UsageFormat.percent(item.percent)) [\(item.severity)]\(reset)")
        }
        let breakdown = (usage?.breakdownItems ?? []).map { "\($0.name) \(UsageFormat.percent($0.percent))" }
        if !breakdown.isEmpty { lines.append("Divisão da semana: " + breakdown.joined(separator: ", ")) }
        return lines.joined(separator: "\n")
    }

    private func loadCache() {
        planName = defaults.string(forKey: Keys.plan)
        guard let data = defaults.data(forKey: Keys.cachedResponse),
              let response = try? UsageResponse.decode(data)
        else { return }
        usage = response
        lastUpdated = defaults.object(forKey: Keys.cachedAt) as? Date
    }
}
