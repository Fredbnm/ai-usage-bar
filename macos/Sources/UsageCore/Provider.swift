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

/// The number shown next to the menu bar icon.
public enum MenuBarSelection: Hashable, Sendable {
    case highest
    case item(ProviderID, kind: String)

    public init(rawValue: String) {
        switch rawValue {
        // Values saved by version 0.1.
        case "session": self = .item(.claude, kind: "session")
        case "weekly": self = .item(.claude, kind: "weekly_all")
        default:
            let parts = rawValue.split(separator: ":", maxSplits: 1).map(String.init)
            if parts.count == 2, let provider = ProviderID(rawValue: parts[0]) {
                self = .item(provider, kind: parts[1])
            } else {
                self = .highest
            }
        }
    }

    public var rawValue: String {
        switch self {
        case .highest: "highest"
        case .item(let provider, let kind): "\(provider.rawValue):\(kind)"
        }
    }

    /// Falls back to the highest value when the chosen limit is missing.
    public func percent(in snapshots: [ProviderSnapshot]) -> Double? {
        let highest = snapshots.flatMap(\.items).map(\.percent).max()
        switch self {
        case .highest:
            return highest
        case .item(let provider, let kind):
            return snapshots.first { $0.provider == provider }?.items.first { $0.kind == kind }?.percent ?? highest
        }
    }

    public static func options(for snapshots: [ProviderSnapshot]) -> [(id: String, label: String)] {
        var options = [(id: MenuBarSelection.highest.rawValue, label: "O mais alto entre todos")]
        for snapshot in snapshots {
            for item in snapshot.items {
                let selection = MenuBarSelection.item(snapshot.provider, kind: item.kind)
                options.append((id: selection.rawValue, label: "\(snapshot.provider.displayName) · \(item.title)"))
            }
        }
        return options
    }
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
