import Foundation

/// A GitHub token another tool already stored: the Copilot plugin of VS Code
/// or JetBrains, the `gh` CLI, or an environment variable. Read only: it is
/// never written, refreshed, cached or logged.
public struct CopilotCredentials: Equatable, Sendable, CustomStringConvertible {
    public var token: String
    public var source: String

    public init(token: String, source: String) {
        self.token = token
        self.source = source
    }

    /// Never prints the token.
    public var description: String { "CopilotCredentials(source: \(source), token: <redacted>)" }
}

public enum CopilotCredentialsStore {
    /// `~/.config/github-copilot` (the Copilot plugins use it on macOS too).
    public static var defaultDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".config/github-copilot")
    }

    static let files = ["apps.json", "hosts.json"]

    /// The token from the editor plugin files, then from the environment.
    /// `gh auth token` is tried last, by `CopilotProvider`, because it starts a process.
    public static func find(directory: URL? = nil, environment: [String: String]? = nil) -> CopilotCredentials? {
        for file in files {
            let url = (directory ?? defaultDirectory).appendingPathComponent(file)
            if let data = try? Data(contentsOf: url), let token = parseHosts(data) {
                return CopilotCredentials(token: token, source: file)
            }
        }
        let environment = environment ?? ProcessInfo.processInfo.environment
        for name in ["GH_TOKEN", "GITHUB_TOKEN"] {
            if let value = environment[name], !value.isEmpty {
                return CopilotCredentials(token: value, source: name)
            }
        }
        return nil
    }

    /// `apps.json`: `{"github.com:<client id>": {"user", "oauth_token"}}`;
    /// `hosts.json`: `{"github.com": {"user", "oauth_token"}}`.
    /// Only github.com entries count (not GitHub Enterprise hosts).
    public static func parseHosts(_ data: Data) -> String? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }
        for (key, value) in root {
            guard key == "github.com" || key.hasPrefix("github.com:"),
                  let entry = value as? [String: Any],
                  let token = (entry["oauth_token"] ?? entry["oauthToken"]) as? String,
                  !token.isEmpty
            else { continue }
            return token
        }
        return nil
    }

    /// The token `gh` holds for github.com. Blocking: spawns a process.
    /// A Finder-launched app has a minimal PATH, so Homebrew's folders are added.
    public static func fromGhCLI() -> CopilotCredentials? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
        process.arguments = ["gh", "auth", "token", "--hostname", "github.com"]
        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = ["/opt/homebrew/bin", "/usr/local/bin", environment["PATH"] ?? "/usr/bin:/bin"]
            .joined(separator: ":")
        process.environment = environment
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return nil }

        let killer = DispatchWorkItem { if process.isRunning { process.terminate() } }
        DispatchQueue.global().asyncAfter(deadline: .now() + 5, execute: killer)
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        killer.cancel()

        let token = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard process.terminationStatus == 0, !token.isEmpty else { return nil }
        return CopilotCredentials(token: token, source: "gh")
    }
}

/// `isAvailable()` runs on every refresh, so the `gh` process is started at
/// most once every few minutes.
final class GhTokenCache: @unchecked Sendable {
    static let shared = GhTokenCache()

    private let lock = NSLock()
    private var credentials: CopilotCredentials?
    private var checkedAt = Date.distantPast

    func lookup(now: Date = Date(), load: () -> CopilotCredentials? = CopilotCredentialsStore.fromGhCLI) -> CopilotCredentials? {
        lock.lock()
        defer { lock.unlock() }
        let age = now.timeIntervalSince(checkedAt)
        if age > 600 || (credentials == nil && age > 60) {
            credentials = load()
            checkedAt = now
        }
        return credentials
    }
}

/// `GET api.github.com/copilot_internal/user`: what the Copilot settings page
/// shows. Not documented by GitHub; every field is optional.
public enum CopilotUsage {
    public static func snapshot(fromResponse data: Data) -> ProviderSnapshot? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }

        let reset = date(root["quota_reset_date"] ?? root["quotaResetDate"] ?? root["limited_user_reset_date"])
        var items: [LimitItem] = []

        let snapshots = (root["quota_snapshots"] ?? root["quotaSnapshots"]) as? [String: Any]
        for (kind, key) in [("premium_requests", "premium_interactions"), ("chat", "chat"), ("completions", "completions")] {
            guard let percent = percentUsed(snapshots?[key] as? [String: Any]) else { continue }
            items.append(item(kind: kind, percent: percent, reset: reset))
        }

        // Free plan: monthly_quotas (limit) and limited_user_quotas (left).
        if items.isEmpty {
            let limits = root["monthly_quotas"] as? [String: Any]
            let left = root["limited_user_quotas"] as? [String: Any]
            for kind in ["chat", "completions"] {
                guard let limit = number(limits?[kind]), limit > 0, let remaining = number(left?[kind]) else { continue }
                items.append(item(kind: kind, percent: (limit - remaining) / limit * 100, reset: reset))
            }
        }

        guard !items.isEmpty else { return nil }
        // The premium allowance first: it is the one that runs out.
        items.sort { ($0.kind == "premium_requests" ? 0 : 1) < ($1.kind == "premium_requests" ? 0 : 1) }
        let plan = planName((root["copilot_plan"] ?? root["copilotPlan"]) as? String)
        return ProviderSnapshot(provider: .copilot, planName: plan, items: items)
    }

    private static func item(kind: String, percent: Double, reset: Date?) -> LimitItem {
        let clamped = min(max(percent, 0), 999)
        return LimitItem(
            id: "copilot-\(kind)", kind: kind, percent: clamped,
            severity: Severity(api: nil, percent: clamped), resetsAt: reset,
            group: kind == "premium_requests" ? .weekly : .other
        )
    }

    /// Percent used of one quota, or nil for unlimited / unreadable ones.
    /// Prefers `percent_remaining`, then `entitlement` and `remaining`.
    private static func percentUsed(_ quota: [String: Any]?) -> Double? {
        guard let quota, (quota["unlimited"] as? Bool) != true else { return nil }
        if let left = number(quota["percent_remaining"] ?? quota["percentRemaining"]), left.isFinite { return 100 - left }
        if let entitlement = number(quota["entitlement"]), entitlement > 0, let remaining = number(quota["remaining"]) {
            return (entitlement - remaining) / entitlement * 100
        }
        return nil
    }

    private static func number(_ value: Any?) -> Double? {
        if let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() { return number.doubleValue }
        if let string = value as? String { return Double(string) }
        return nil
    }

    /// RFC 3339, or a bare `2026-12-01` (read as UTC).
    private static func date(_ value: Any?) -> Date? {
        guard let string = value as? String else { return nil }
        if let date = APIDate.parse(string) { return date }
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withFullDate]
        formatter.timeZone = TimeZone(identifier: "UTC")
        return formatter.date(from: string)
    }

    /// `individual_pro` → "Pro", `individual_pro_plus` → "Pro+", `business` → "Business".
    static func planName(_ plan: String?) -> String? {
        guard let plan, !plan.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        return plan
            .replacingOccurrences(of: "individual_", with: "", options: .caseInsensitive)
            .replacingOccurrences(of: "copilot_", with: "", options: .caseInsensitive)
            .replacingOccurrences(of: "_plus", with: "+", options: .caseInsensitive)
            .replacingOccurrences(of: "_", with: " ")
            .capitalized
    }
}

public struct CopilotProvider: UsageProvider {
    public static let endpoint = URL(string: "https://api.github.com/copilot_internal/user")!

    public let id = ProviderID.copilot
    private let directory: URL?
    private let environment: [String: String]?
    private let useGhCLI: Bool

    public init(directory: URL? = nil, environment: [String: String]? = nil, useGhCLI: Bool = true) {
        self.directory = directory
        self.environment = environment
        self.useGhCLI = useGhCLI
    }

    public func isAvailable() -> Bool { findCredentials() != nil }

    private func findCredentials() -> CopilotCredentials? {
        CopilotCredentialsStore.find(directory: directory, environment: environment)
            ?? (useGhCLI ? GhTokenCache.shared.lookup() : nil)
    }

    public func fetch() async throws -> ProviderFetchResult {
        let found = try await runBlocking { findCredentials() }
        guard let login = found else { throw UsageError.notLoggedIn }

        var request = URLRequest(url: Self.endpoint, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        request.setValue("token \(login.token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("ai-usage-bar", forHTTPHeaderField: "User-Agent")
        // The endpoint answers editor clients; identify as one.
        request.setValue("vscode/1.99.0", forHTTPHeaderField: "Editor-Version")
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        let data = try await HTTP.send(request)

        guard let snapshot = CopilotUsage.snapshot(fromResponse: data) else { throw UsageError.decoding }
        let cache = try ProviderCache(plan: snapshot.planName, raw: data).encoded()
        return ProviderFetchResult(snapshot: snapshot, cache: cache)
    }

    public func snapshot(fromCache data: Data) -> ProviderSnapshot? {
        ProviderCache.decode(data).flatMap { CopilotUsage.snapshot(fromResponse: $0.raw) }
    }
}
