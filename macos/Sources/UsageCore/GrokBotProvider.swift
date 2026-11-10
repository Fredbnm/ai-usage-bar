import Foundation

/// `aiserver.v1.DashboardService/GetSandUsageStatus`: what Grok Bot's own
/// Settings → Usage screen shows. Connect/proto3 JSON, so field names are
/// camelCase, default values are omitted and int64 values come as strings.
public struct GrokUsageStatus: Decodable, Equatable, Sendable {
    public var currentPeriodStart: Date?
    public var nextResetTimestampUtc: Date?
    /// Weekly usage, 0–100.
    public var usagePercent: Double?
    public var availableBankedResetCount: Int?
    public var hasAvailableUsage: Bool?
    public var usesPooledEnterpriseAllowance: Bool?
    public var grokPlanLabel: String?
    public var cursorPlanName: String?
    public var sandTrialExpiresAt: Date?
    public var onDemandEnabled: Bool?

    enum CodingKeys: String, CodingKey {
        case currentPeriodStart, nextResetTimestampUtc, usagePercent, availableBankedResetCount,
             hasAvailableUsage, usesPooledEnterpriseAllowance, grokPlanLabel, cursorPlanName,
             sandTrialExpiresAt, onDemandSettings
    }

    private struct OnDemandSettings: Decodable {
        var enabled: Bool?
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        currentPeriodStart = c.lenientDate(.currentPeriodStart)
        nextResetTimestampUtc = c.lenientDate(.nextResetTimestampUtc)
        usagePercent = c.lenientDouble(.usagePercent)
        availableBankedResetCount = c.lenientDouble(.availableBankedResetCount).map { Int($0) }
        hasAvailableUsage = try? c.decodeIfPresent(Bool.self, forKey: .hasAvailableUsage)
        usesPooledEnterpriseAllowance = try? c.decodeIfPresent(Bool.self, forKey: .usesPooledEnterpriseAllowance)
        grokPlanLabel = try? c.decodeIfPresent(String.self, forKey: .grokPlanLabel)
        cursorPlanName = try? c.decodeIfPresent(String.self, forKey: .cursorPlanName)
        sandTrialExpiresAt = c.lenientDate(.sandTrialExpiresAt)
        onDemandEnabled = (try? c.decodeIfPresent(OnDemandSettings.self, forKey: .onDemandSettings))?.enabled
    }

    public static func decode(_ data: Data) throws -> GrokUsageStatus {
        let decoder = JSONDecoder()
        // camelCase keys pass through unchanged; snake_case ones get converted.
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        return try decoder.decode(GrokUsageStatus.self, from: data)
    }

    /// `grokPlanLabel` is often the generic "Grok Bot Plan"; in that case the
    /// Cursor plan name ("Premium", "Pro"…) says more.
    public var planName: String? {
        var label = grokPlanLabel?.trimmingCharacters(in: .whitespaces) ?? ""
        for affix in ["Grok Bot", "Plan"] {
            if label.lowercased().hasPrefix(affix.lowercased()) { label = String(label.dropFirst(affix.count)) }
            if label.lowercased().hasSuffix(affix.lowercased()) { label = String(label.dropLast(affix.count)) }
            label = label.trimmingCharacters(in: .whitespaces)
        }
        if !label.isEmpty, let full = grokPlanLabel?.trimmingCharacters(in: .whitespaces) { return full }
        let cursor = cursorPlanName?.trimmingCharacters(in: .whitespaces) ?? ""
        return cursor.isEmpty ? nil : cursor.prefix(1).uppercased() + cursor.dropFirst()
    }
}

public struct GrokBotClient: Sendable {
    public static let baseURL = URL(string: "https://api2.cursor.sh")!
    public static let usageStatusMethod = "aiserver.v1.DashboardService/GetSandUsageStatus"

    public init() {}

    public func fetchUsageStatus(accessToken: String) async throws -> Data {
        var request = URLRequest(
            url: Self.baseURL.appendingPathComponent(Self.usageStatusMethod),
            cachePolicy: .reloadIgnoringLocalCacheData,
            timeoutInterval: 20
        )
        request.httpMethod = "POST"
        request.httpBody = Data("{}".utf8)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("1", forHTTPHeaderField: "Connect-Protocol-Version")
        // Grok Bot's privacy flag; we never send content, only read usage.
        request.setValue("true", forHTTPHeaderField: "x-ghost-mode")
        return try await HTTP.send(request)
    }
}

public struct GrokBotProvider: UsageProvider {
    public let id = ProviderID.grokBot
    public static let appURL = URL(fileURLWithPath: "/Applications/Grok Bot.app")

    private let keyCache: SafeStorageKeyCache

    public init(keyCache: SafeStorageKeyCache = .shared) {
        self.keyCache = keyCache
    }

    public func isAvailable() -> Bool {
        FileManager.default.fileExists(atPath: GrokBotLogin.secretsFile.path)
    }

    public func fetch() async throws -> ProviderFetchResult {
        let token = try await accessToken()
        let isExpired = JWT.expiration(of: token).map { $0 < Date() } ?? false
        if isExpired { throw UsageError.unauthorized(expired: true) }

        let data: Data
        do {
            data = try await GrokBotClient().fetchUsageStatus(accessToken: token)
        } catch UsageError.unauthorized {
            throw UsageError.unauthorized(expired: isExpired)
        }

        guard let snapshot = Self.snapshot(fromResponse: data) else { throw UsageError.decoding }
        let cache = try ProviderCache(plan: nil, raw: data).encoded()
        return ProviderFetchResult(snapshot: snapshot, cache: cache)
    }

    public func snapshot(fromCache data: Data) -> ProviderSnapshot? {
        ProviderCache.decode(data).flatMap { Self.snapshot(fromResponse: $0.raw) }
    }

    public static func snapshot(fromResponse data: Data, now: Date = Date()) -> ProviderSnapshot? {
        guard let status = try? GrokUsageStatus.decode(data) else { return nil }
        return snapshot(from: status, now: now)
    }

    static func snapshot(from status: GrokUsageStatus, now: Date) -> ProviderSnapshot? {
        var items: [LimitItem] = []
        if status.usesPooledEnterpriseAllowance != true, let raw = status.usagePercent, raw.isFinite {
            let percent = max(raw, 0)
            items.append(LimitItem(
                id: "grok-weekly",
                kind: "weekly",
                title: "Semanal",
                percent: percent,
                severity: status.hasAvailableUsage == false ? .critical : Severity(api: nil, percent: percent),
                resetsAt: status.nextResetTimestampUtc,
                group: .weekly
            ))
        }

        var notes: [String] = []
        if status.usesPooledEnterpriseAllowance == true {
            notes.append("Uso compartilhado da equipe, sem limite individual.")
        }
        if let count = status.availableBankedResetCount, count > 0 {
            notes.append(count == 1 ? "1 reset guardado disponível" : "\(count) resets guardados disponíveis")
        }
        if let trialEnd = status.sandTrialExpiresAt, trialEnd > now {
            notes.append("Teste grátis até \(UsageFormat.shortDate(trialEnd))")
        }
        if status.onDemandEnabled == true {
            notes.append("Uso sob demanda ligado")
        }

        guard !items.isEmpty || !notes.isEmpty else { return nil }
        return ProviderSnapshot(provider: .grokBot, planName: status.planName, items: items, notes: notes)
    }

    private func accessToken() async throws -> String {
        let fileData: Data
        do {
            fileData = try Data(contentsOf: GrokBotLogin.secretsFile)
        } catch {
            throw UsageError.notLoggedIn
        }

        switch try GrokBotSecrets.activeAccessToken(in: fileData) {
        case .plaintext(let token):
            return token
        case .encrypted(let blob):
            let key = try await keyCache.key(loading: GrokBotLogin.readSafeStoragePassword)
            do {
                return try ElectronSafeStorage.decryptString(blob, key: key)
            } catch {
                // The key may have been rotated: read it again next time.
                await keyCache.reset()
                throw error
            }
        }
    }
}

extension KeyedDecodingContainer {
    func lenientDouble(_ key: Key) -> Double? {
        if let value = try? decodeIfPresent(Double.self, forKey: key) { return value }
        if let string = try? decodeIfPresent(String.self, forKey: key) { return Double(string) }
        return nil
    }

    /// RFC 3339 string (protobuf Timestamp) or epoch number (s or ms).
    func lenientDate(_ key: Key) -> Date? {
        if let string = try? decodeIfPresent(String.self, forKey: key) {
            if let date = APIDate.parse(string) { return date }
            if let number = Double(string) { return Self.epochDate(number) }
            return nil
        }
        if let number = try? decodeIfPresent(Double.self, forKey: key) { return Self.epochDate(number) }
        return nil
    }

    private static func epochDate(_ value: Double) -> Date? {
        guard value > 0 else { return nil }
        return Date(timeIntervalSince1970: value > 100_000_000_000 ? value / 1000 : value)
    }
}
