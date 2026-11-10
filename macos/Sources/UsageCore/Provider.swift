import Foundation

/// A service whose plan usage the app shows.
public enum ProviderID: String, CaseIterable, Codable, Sendable {
    case claude
    case grokBot

    public var displayName: String {
        switch self {
        case .claude: "Claude"
        case .grokBot: "Grok Bot"
        }
    }

    /// The app whose login is reused, as named in messages.
    public var loginSource: String {
        switch self {
        case .claude: "Claude Code"
        case .grokBot: "Grok Bot"
        }
    }
}

/// Extra facts shown under a service's limits.
public enum SnapshotNote: Hashable, Sendable {
    /// Team allowance, no individual limit.
    case pooledAllowance
    case bankedResets(Int)
    case trialUntil(Date)
    case onDemandEnabled

    public var text: String {
        switch self {
        case .pooledAllowance:
            return tr("Uso compartilhado da equipe, sem limite individual.", "Shared team allowance, no individual limit.")
        case .bankedResets(let count):
            return count == 1
                ? tr("1 reset guardado disponível", "1 banked reset available")
                : tr("\(count) resets guardados disponíveis", "\(count) banked resets available")
        case .trialUntil(let date):
            return tr("Teste grátis até \(UsageFormat.shortDate(date))", "Free trial until \(UsageFormat.shortDate(date))")
        case .onDemandEnabled:
            return tr("Uso sob demanda ligado", "On-demand usage on")
        }
    }
}

/// Everything the UI needs to draw one service.
public struct ProviderSnapshot: Equatable, Sendable {
    public var provider: ProviderID
    public var planName: String?
    public var items: [LimitItem]
    public var breakdown: [BreakdownItem]
    public var notes: [SnapshotNote]

    public init(provider: ProviderID, planName: String?, items: [LimitItem], breakdown: [BreakdownItem] = [], notes: [SnapshotNote] = []) {
        self.provider = provider
        self.planName = planName
        self.items = items
        self.breakdown = breakdown
        self.notes = notes
    }
}

public struct ProviderFetchResult: Sendable {
    public var snapshot: ProviderSnapshot
    /// Opaque payload to persist; turned back into a snapshot with
    /// `snapshot(fromCache:)` on the next launch. Never contains tokens.
    public var cache: Data
}

public protocol UsageProvider: Sendable {
    var id: ProviderID { get }
    /// Whether the service looks set up on this machine. Services that are
    /// not available are hidden instead of showing an error.
    func isAvailable() -> Bool
    func fetch() async throws -> ProviderFetchResult
    func snapshot(fromCache data: Data) -> ProviderSnapshot?
}

/// What gets persisted between launches: the plan name (which comes from
/// the login, not the usage response) and the raw usage response.
struct ProviderCache: Codable {
    var plan: String?
    var raw: Data

    func encoded() throws -> Data { try JSONEncoder().encode(self) }
    static func decode(_ data: Data) -> ProviderCache? { try? JSONDecoder().decode(ProviderCache.self, from: data) }
}

/// Runs blocking work (Keychain prompts, subprocesses) off the Swift
/// concurrency pool.
func runBlocking<T: Sendable>(_ work: @escaping @Sendable () throws -> T) async throws -> T {
    try await withCheckedThrowingContinuation { continuation in
        DispatchQueue.global(qos: .utility).async {
            continuation.resume(with: Result { try work() })
        }
    }
}
