import Foundation

/// The ChatGPT login the Codex CLI and the Codex extension keep in
/// `~/.codex/auth.json`. Read only: the token is never written, refreshed,
/// cached or logged.
public struct CodexCredentials: Equatable, Sendable, CustomStringConvertible {
    public var accessToken: String
    public var accountId: String?

    public init(accessToken: String, accountId: String?) {
        self.accessToken = accessToken
        self.accountId = accountId
    }

    /// Never prints the token.
    public var description: String { "CodexCredentials(account: \(accountId ?? "?"), token: <redacted>)" }

    /// `{"tokens": {"access_token", "refresh_token", "id_token", "account_id"}}`.
    /// A login with only an API key (`OPENAI_API_KEY`) has no ChatGPT plan to
    /// measure: not a login here.
    public static func parse(_ data: Data) -> CodexCredentials? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let tokens = root["tokens"] as? [String: Any],
              let token = (tokens["access_token"] ?? tokens["accessToken"]) as? String,
              !token.isEmpty
        else { return nil }
        let account = (tokens["account_id"] ?? tokens["accountId"]) as? String
        return CodexCredentials(accessToken: token, accountId: account)
    }
}

public enum CodexCredentialsStore {
    /// `~/.codex/auth.json`, or `$CODEX_HOME/auth.json` when that is set.
    public static var defaultURL: URL {
        if let home = ProcessInfo.processInfo.environment["CODEX_HOME"], !home.isEmpty {
            return URL(fileURLWithPath: home).appendingPathComponent("auth.json")
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex/auth.json")
    }

    public static func load(from url: URL? = nil) -> CodexCredentials? {
        guard let data = try? Data(contentsOf: url ?? defaultURL) else { return nil }
        return CodexCredentials.parse(data)
    }
}

/// `GET chatgpt.com/backend-api/wham/usage`: what the Codex usage page shows.
/// Not documented by OpenAI; the field names come from third-party tools.
/// Every field is optional.
public enum CodexUsage {
    public static func snapshot(fromResponse data: Data, now: Date = Date()) -> ProviderSnapshot? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }

        let limits = (root["rate_limit"] ?? root["rateLimit"] ?? root["rate_limits"]) as? [String: Any]
        let limitReached = ((limits?["limit_reached"] ?? limits?["limitReached"]) as? Bool) == true
        let windows: [[String: Any]?] = [
            (limits?["primary_window"] ?? limits?["primaryWindow"] ?? limits?["primary"]) as? [String: Any],
            (limits?["secondary_window"] ?? limits?["secondaryWindow"] ?? limits?["secondary"]) as? [String: Any],
        ]
        var items: [LimitItem] = []
        for (index, window) in windows.enumerated() {
            if let item = item(window, index: index, limitReached: limitReached, now: now) { items.append(item) }
        }
        guard !items.isEmpty else { return nil }
        let plan = planName((root["plan_type"] ?? root["planType"]) as? String)
        return ProviderSnapshot(provider: .codex, planName: plan, items: items)
    }

    /// The first window is the 5-hour session and the second the week, told
    /// apart by `limit_window_seconds` when it is there, so a plan with only a
    /// weekly window is not mislabelled.
    private static func item(_ window: [String: Any]?, index: Int, limitReached: Bool, now: Date) -> LimitItem? {
        guard let window, let raw = number(window["used_percent"] ?? window["usedPercent"]), raw.isFinite else { return nil }
        let percent = min(max(raw, 0), 999)

        let isWeekly: Bool
        if let seconds = number(window["limit_window_seconds"] ?? window["limitWindowSeconds"]) {
            isWeekly = seconds >= 6 * 86_400
        } else {
            isWeekly = index == 1
        }
        var reset = date(window["reset_at"] ?? window["resetAt"])
        if reset == nil, let after = number(window["reset_after_seconds"] ?? window["resetAfterSeconds"]), after >= 0 {
            reset = now.addingTimeInterval(after)
        }
        let exhausted = limitReached && percent >= 99.5
        return LimitItem(
            id: isWeekly ? "codex-weekly" : "codex-session",
            kind: isWeekly ? "weekly" : "session",
            percent: percent,
            severity: exhausted ? .critical : Severity(api: nil, percent: percent),
            resetsAt: reset,
            group: isWeekly ? .weekly : .session
        )
    }

    private static func number(_ value: Any?) -> Double? {
        if let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() { return number.doubleValue }
        if let string = value as? String { return Double(string) }
        return nil
    }

    /// Epoch seconds, or an RFC 3339 string.
    private static func date(_ value: Any?) -> Date? {
        if let string = value as? String, Double(string) == nil { return APIDate.parse(string) }
        guard let seconds = number(value), seconds.isFinite, seconds > 0 else { return nil }
        return Date(timeIntervalSince1970: seconds)
    }

    /// `plus` → "Plus", `pro` → "Pro", `prolite` → "Prolite", `team` → "Team".
    static func planName(_ plan: String?) -> String? {
        guard let plan, !plan.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        return plan.replacingOccurrences(of: "_", with: " ").capitalized
    }
}

public struct CodexProvider: UsageProvider {
    public static let endpoint = URL(string: "https://chatgpt.com/backend-api/wham/usage")!

    public let id = ProviderID.codex
    private let authURL: URL?

    public init(authURL: URL? = nil) {
        self.authURL = authURL
    }

    public func isAvailable() -> Bool { CodexCredentialsStore.load(from: authURL) != nil }

    public func fetch() async throws -> ProviderFetchResult {
        let url = authURL
        let found = try await runBlocking { CodexCredentialsStore.load(from: url) }
        guard let login = found else { throw UsageError.notLoggedIn }
        // Codex renews its own token when it runs; this app never does.
        if let expires = JWT.expiration(of: login.accessToken), expires < Date() {
            throw UsageError.unauthorized(expired: true)
        }

        var request = URLRequest(url: Self.endpoint, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        request.setValue("Bearer \(login.accessToken)", forHTTPHeaderField: "Authorization")
        if let account = login.accountId, !account.isEmpty {
            request.setValue(account, forHTTPHeaderField: "ChatGPT-Account-Id")
        }
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("ai-usage-bar", forHTTPHeaderField: "User-Agent")
        let data = try await HTTP.send(request)

        guard let snapshot = CodexUsage.snapshot(fromResponse: data) else { throw UsageError.decoding }
        let cache = try ProviderCache(plan: snapshot.planName, raw: data).encoded()
        return ProviderFetchResult(snapshot: snapshot, cache: cache)
    }

    public func snapshot(fromCache data: Data) -> ProviderSnapshot? {
        ProviderCache.decode(data).flatMap { CodexUsage.snapshot(fromResponse: $0.raw) }
    }
}
