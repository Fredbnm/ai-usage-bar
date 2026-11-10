import Foundation

/// A service whose plan usage the app shows.
public enum ProviderID: String, CaseIterable, Codable, Sendable {
    case claude
    case codex
    case copilot
    case gemini
    case grokBot

    public var displayName: String {
        switch self {
        case .claude: "Claude"
        case .codex: "Codex"
        case .copilot: "GitHub Copilot"
        case .gemini: "Gemini"
        case .grokBot: "Grok Bot"
        }
    }

    /// Label on the popover's tab.
    public var shortName: String {
        switch self {
        case .claude: "Claude"
        case .codex: "Codex"
        case .copilot: "Copilot"
        case .gemini: "Gemini"
        case .grokBot: "Grok"
        }
    }

    /// The app whose login is reused, as named in messages.
    public var loginSource: String {
        switch self {
        case .claude: "Claude Code"
        case .codex: "Codex"
        case .copilot: "GitHub Copilot"
        case .gemini: "Gemini CLI"
        case .grokBot: "Grok Bot"
        }
    }

    /// What the menu bar draws when there is no app icon to borrow. "Co", not
    /// "C": Copilot would otherwise look like Claude.
    public var initial: String {
        switch self {
        case .codex: "Cx"
        case .copilot: "Co"
        case .gemini: "Ge"
        default: String(displayName.prefix(1))
        }
    }

    /// How to connect this service, shown in the Agents tab when no login is found.
    public var setupHint: String {
        switch self {
        case .claude:
            tr("Entre com o comando claude no terminal ou pelo app do Claude.",
               "Sign in with the claude command in Terminal or in the Claude app.")
        case .codex:
            tr("Entre com o comando codex login no terminal ou na extensão Codex do VS Code.",
               "Sign in with codex login in a terminal, or in the Codex extension for VS Code.")
        case .gemini:
            tr("Rode gemini no terminal e escolha entrar com o Google.",
               "Run gemini in a terminal and choose to sign in with Google.")
        case .copilot:
            tr("Entre no GitHub Copilot no VS Code ou rode gh auth login.",
               "Sign in to GitHub Copilot in VS Code, or run gh auth login.")
        case .grokBot:
            tr("Instale o Grok Bot, abra-o e entre na sua conta.", "Install Grok Bot, open it and sign in.")
        }
    }
}

/// Whether a service's login was found on this Mac.
public enum SetupStatus: Equatable, Sendable {
    case connected, needsLogin
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
    /// For the Agents tab: whether the login is there. Defaults to
    /// `isAvailable()`; Claude overrides it because it is always shown.
    func detect() -> SetupStatus
    func fetch() async throws -> ProviderFetchResult
    func snapshot(fromCache data: Data) -> ProviderSnapshot?
}

extension UsageProvider {
    public func detect() -> SetupStatus { isAvailable() ? .connected : .needsLogin }
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
