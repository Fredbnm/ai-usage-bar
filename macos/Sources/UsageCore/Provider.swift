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

/// Everything the UI needs to draw one service.
public struct ProviderSnapshot: Equatable, Sendable {
    public var provider: ProviderID
    public var planName: String?
    public var items: [LimitItem]
    public var breakdown: [BreakdownItem]
    public var notes: [String]

    public init(provider: ProviderID, planName: String?, items: [LimitItem], breakdown: [BreakdownItem] = [], notes: [String] = []) {
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
