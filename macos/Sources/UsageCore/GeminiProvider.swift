import Foundation

/// The Google login of the Gemini CLI, in `~/.gemini/oauth_creds.json`. Read
/// only: the token is never written, refreshed, cached or logged.
public struct GeminiCredentials: Equatable, Sendable, CustomStringConvertible {
    public var accessToken: String
    public var expiresAt: Date?

    public init(accessToken: String, expiresAt: Date?) {
        self.accessToken = accessToken
        self.expiresAt = expiresAt
    }

    public var isExpired: Bool { expiresAt.map { $0 < Date() } ?? false }

    /// Never prints the token.
    public var description: String {
        "GeminiCredentials(expiresAt: \(expiresAt.map { "\($0)" } ?? "?"), token: <redacted>)"
    }

    /// `{"access_token", "refresh_token", "expiry_date" (ms since epoch), "id_token"}`.
    public static func parse(_ data: Data) -> GeminiCredentials? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let token = (root["access_token"] ?? root["accessToken"]) as? String,
              !token.isEmpty
        else { return nil }
        var expiry: Date?
        if let number = (root["expiry_date"] ?? root["expiryDate"]) as? NSNumber,
           CFGetTypeID(number) != CFBooleanGetTypeID(), number.doubleValue > 0 {
            expiry = Date(timeIntervalSince1970: number.doubleValue / 1000)
        }
        return GeminiCredentials(accessToken: token, expiresAt: expiry)
    }
}

public enum GeminiCredentialsStore {
    /// `~/.gemini/oauth_creds.json`. Only the Google sign-in leaves this file (not an API key).
    public static var defaultURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".gemini/oauth_creds.json")
    }

    public static func load(from url: URL? = nil) -> GeminiCredentials? {
        guard let data = try? Data(contentsOf: url ?? defaultURL) else { return nil }
        return GeminiCredentials.parse(data)
    }
}

/// The Code Assist backend the Gemini CLI talks to: `loadCodeAssist` names the
/// project and tier, `retrieveUserQuota` the remaining share of each model's
/// quota. Not documented by Google; the field names come from third-party
/// tools. Every field is optional.
public enum GeminiUsage {
    private static func object(_ data: Data) -> [String: Any]? {
        (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
    }

    /// `cloudaicompanionProject` is a string, or an object with an `id`.
    public static func project(from loadCodeAssistResponse: Data) -> String? {
        guard let root = object(loadCodeAssistResponse) else { return nil }
        if let text = root["cloudaicompanionProject"] as? String, !text.isEmpty { return text }
        if let project = root["cloudaicompanionProject"] as? [String: Any],
           let id = (project["id"] ?? project["name"]) as? String, !id.isEmpty {
            return id
        }
        return nil
    }

    /// `currentTier.id`: "free-tier" → "Free", "standard-tier" → "Standard", "legacy-tier" → "Legacy".
    public static func planName(from loadCodeAssistResponse: Data) -> String? {
        guard let root = object(loadCodeAssistResponse),
              let tier = (root["currentTier"] ?? root["current_tier"]) as? [String: Any]
        else { return nil }
        if let id = tier["id"] as? String, !id.isEmpty {
            return id
                .replacingOccurrences(of: "-tier", with: "", options: .caseInsensitive)
                .replacingOccurrences(of: "-", with: " ")
                .replacingOccurrences(of: "_", with: " ")
                .capitalized
        }
        if let label = tier["name"] as? String, !label.isEmpty { return label }
        return nil
    }

    /// One limit per model: used = `1 − remainingFraction`. The Pro model first
    /// (it is the one that runs out), at most five. The first is the one the
    /// menu bar ring follows.
    public static func snapshot(fromQuota data: Data, plan: String?) -> ProviderSnapshot? {
        guard let root = object(data), let buckets = root["buckets"] as? [Any] else { return nil }

        var byModel: [String: (used: Double, reset: Date?)] = [:]
        for case let bucket as [String: Any] in buckets {
            guard let model = (bucket["modelId"] ?? bucket["model_id"] ?? bucket["model"]) as? String, !model.isEmpty,
                  let remaining = number(bucket["remainingFraction"] ?? bucket["remaining_fraction"]), remaining.isFinite
            else { continue }
            let used = min(max((1 - remaining) * 100, 0), 100)
            let reset = ((bucket["resetTime"] ?? bucket["reset_time"]) as? String).flatMap { APIDate.parse($0) }
            // The same model can appear once per token type: the fullest one counts.
            if let known = byModel[model], known.used >= used { continue }
            byModel[model] = (used, reset)
        }

        let ordered = byModel.keys.sorted { (a: String, b: String) -> Bool in
            let (fa, fb) = (family(a), family(b))
            return fa != fb ? fa < fb : a < b
        }
        var items: [LimitItem] = []
        for (index, model) in ordered.prefix(5).enumerated() {
            guard let entry = byModel[model] else { continue }
            items.append(LimitItem(
                id: "gemini-\(model)", kind: "daily_scoped", scopeName: modelName(model),
                percent: entry.used, severity: Severity(api: nil, percent: entry.used),
                resetsAt: entry.reset, group: index == 0 ? .weekly : .other
            ))
        }
        return items.isEmpty ? nil : ProviderSnapshot(provider: .gemini, planName: plan, items: items)
    }

    private static func number(_ value: Any?) -> Double? {
        if let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() { return number.doubleValue }
        if let string = value as? String { return Double(string) }
        return nil
    }

    private static func family(_ model: String) -> Int {
        let name = model.lowercased()
        if name.contains("pro") { return 0 }
        if name.contains("lite") { return 2 }
        if name.contains("flash") { return 1 }
        return 3
    }

    /// "gemini-2.5-pro" → "2.5 Pro", "gemini-2.5-flash-lite" → "2.5 Flash Lite".
    static func modelName(_ model: String) -> String {
        var name = model
        if name.lowercased().hasPrefix("gemini-") { name = String(name.dropFirst("gemini-".count)) }
        return name
            .replacingOccurrences(of: "-", with: " ")
            .replacingOccurrences(of: "_", with: " ")
            .capitalized
    }
}

public struct GeminiProvider: UsageProvider {
    public static let loadCodeAssistEndpoint = URL(string: "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist")!
    public static let quotaEndpoint = URL(string: "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota")!

    public let id = ProviderID.gemini
    private let credentialsURL: URL?

    public init(credentialsURL: URL? = nil) {
        self.credentialsURL = credentialsURL
    }

    public func isAvailable() -> Bool { GeminiCredentialsStore.load(from: credentialsURL) != nil }

    public func fetch() async throws -> ProviderFetchResult {
        let url = credentialsURL
        let found = try await runBlocking { GeminiCredentialsStore.load(from: url) }
        guard let login = found else { throw UsageError.notLoggedIn }
        // The Gemini CLI renews its own token when it runs; this app never does.
        if login.isExpired { throw UsageError.unauthorized(expired: true) }

        let loadBody = Data(#"{"metadata":{"ideType":"IDE_UNSPECIFIED","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}}"#.utf8)
        let account = try await post(Self.loadCodeAssistEndpoint, body: loadBody, token: login.accessToken)
        let project = GeminiUsage.project(from: account) ?? ProcessInfo.processInfo.environment["GOOGLE_CLOUD_PROJECT"]
        let plan = GeminiUsage.planName(from: account)

        var body = Data("{}".utf8)
        if let project, !project.isEmpty,
           let encoded = try? JSONSerialization.data(withJSONObject: ["project": project]) {
            body = encoded
        }
        let quota = try await post(Self.quotaEndpoint, body: body, token: login.accessToken)

        guard let snapshot = GeminiUsage.snapshot(fromQuota: quota, plan: plan) else { throw UsageError.decoding }
        let cache = try ProviderCache(plan: plan, raw: quota).encoded()
        return ProviderFetchResult(snapshot: snapshot, cache: cache)
    }

    public func snapshot(fromCache data: Data) -> ProviderSnapshot? {
        guard let cache = ProviderCache.decode(data) else { return nil }
        return GeminiUsage.snapshot(fromQuota: cache.raw, plan: cache.plan)
    }

    private func post(_ endpoint: URL, body: Data, token: String) async throws -> Data {
        var request = URLRequest(url: endpoint, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        request.httpMethod = "POST"
        request.httpBody = body
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("ai-usage-bar", forHTTPHeaderField: "User-Agent")
        return try await HTTP.send(request)
    }
}
