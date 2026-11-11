import Foundation
import SQLite3

/// The Cursor editor's login, from VS Code's global state database
/// (`state.vscdb`). Read only: the token is read for each request, kept in
/// memory and never written, refreshed or logged.
///
/// Recent versions keep the token encrypted with Electron `safeStorage`
/// (`cursor-safe-storage:v1:<base64>`), whose key is the Keychain item
/// "Cursor Safe Storage"; older ones store the JWT as is.
public struct CursorLogin: Equatable, Sendable, CustomStringConvertible {
    public static let accessTokenKey = "cursorAuth/accessToken"
    public static let membershipTypeKey = "cursorAuth/stripeMembershipType"
    public static let keychainService = "Cursor Safe Storage"
    public static let keychainAccount = "Cursor Key"
    static let encryptedPrefix = "cursor-safe-storage:v1:"

    /// As stored: the JWT, or its encrypted form (see `encryptedToken`).
    public var accessToken: String
    public var membershipType: String?

    public var planName: String? { CursorUsage.planName(membershipType) }

    /// The `safeStorage` blob ("v10…") when the token is stored encrypted.
    public var encryptedToken: Data? {
        guard accessToken.hasPrefix(Self.encryptedPrefix) else { return nil }
        return Data(base64Encoded: String(accessToken.dropFirst(Self.encryptedPrefix.count)))
    }

    /// Blocking (may show the macOS Keychain prompt): call off the main thread.
    static func readSafeStoragePassword() throws -> Data {
        try ElectronSafeStorage.readPassword(service: keychainService, account: keychainAccount)
    }

    /// Never prints the token.
    public var description: String { "CursorLogin(plan: \(planName ?? "?"), token: <redacted>)" }

    /// `~/Library/Application Support/Cursor/User/globalStorage/state.vscdb`.
    public static var defaultDatabase: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Cursor/User/globalStorage/state.vscdb")
    }

    /// Blocking (reads a database Cursor may be writing to): call off the main thread.
    public static func load(from database: URL = defaultDatabase) throws -> CursorLogin {
        guard FileManager.default.fileExists(atPath: database.path) else { throw UsageError.notLoggedIn }
        let values = try StateDatabase.items(at: database, keys: [accessTokenKey, membershipTypeKey])
        guard let token = clean(values[accessTokenKey]) else { throw UsageError.notLoggedIn }
        return CursorLogin(accessToken: token, membershipType: clean(values[membershipTypeKey]))
    }

    /// VS Code keeps strings as they are; a JSON-quoted one is accepted too.
    private static func clean(_ value: String?) -> String? {
        guard var text = value?.trimmingCharacters(in: .whitespacesAndNewlines) else { return nil }
        if text.count >= 2, text.hasPrefix("\""), text.hasSuffix("\"") { text = String(text.dropFirst().dropLast()) }
        return text.isEmpty ? nil : text
    }
}

/// Just enough SQLite (the system's libsqlite3) to read VS Code's key/value
/// table, `ItemTable`. The database is opened read-only and waits briefly
/// while Cursor writes.
enum StateDatabase {
    /// The values of `keys`; missing keys are left out.
    static func items(at url: URL, keys: [String]) throws -> [String: String] {
        var db: OpaquePointer?
        defer { sqlite3_close(db) }
        let opened = sqlite3_open_v2(url.path, &db, SQLITE_OPEN_READONLY, nil)
        guard opened == SQLITE_OK else { throw failure(opened) }
        sqlite3_busy_timeout(db, 2000)

        let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        var values: [String: String] = [:]
        for key in keys {
            var statement: OpaquePointer?
            defer { sqlite3_finalize(statement) }
            let prepared = sqlite3_prepare_v2(db, "SELECT value FROM ItemTable WHERE key = ?1", -1, &statement, nil)
            guard prepared == SQLITE_OK else { throw failure(prepared) }
            sqlite3_bind_text(statement, 1, key, -1, transient)
            let step = sqlite3_step(statement)
            if step == SQLITE_ROW {
                // TEXT or BLOB: both come back as their bytes. The pointer is read before the size.
                let blob = sqlite3_column_blob(statement, 0)
                let size = Int(sqlite3_column_bytes(statement, 0))
                values[key] = blob.map { String(decoding: UnsafeRawBufferPointer(start: $0, count: size), as: UTF8.self) } ?? ""
            } else if step != SQLITE_DONE {
                throw failure(step)
            }
        }
        return values
    }

    /// Runs SQL on a database, creating it if needed; used by tests to build a state database.
    static func execute(at url: URL, sql: String) throws {
        var db: OpaquePointer?
        defer { sqlite3_close(db) }
        let opened = sqlite3_open_v2(url.path, &db, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, nil)
        guard opened == SQLITE_OK else { throw failure(opened) }
        let code = sqlite3_exec(db, sql, nil, nil, nil)
        guard code == SQLITE_OK else { throw failure(code) }
    }

    /// Busy or locked: Cursor is writing, try again later. Anything else (no
    /// such table, not a database) means there is no login to read.
    private static func failure(_ code: Int32) -> UsageError {
        let primary = code & 0xFF
        return primary == SQLITE_BUSY || primary == SQLITE_LOCKED ? .loginUnreadable : .notLoggedIn
    }
}

/// `aiserver.v1.DashboardService/GetCurrentPeriodUsage`: what Cursor's own
/// Settings → Usage screen shows for the current billing cycle. Connect/proto3
/// JSON: fields are camelCase, zero values are left out, money is in US cents
/// and int64 values (the dates) arrive as strings.
public enum CursorUsage {
    /// A cycle this short is the X Subscription's weekly pool, not a legacy monthly plan.
    static let weeklyCycle: TimeInterval = 10 * 86_400

    public static func snapshot(fromResponse data: Data, plan: String?) -> ProviderSnapshot? {
        guard let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return nil }
        let start = date(root["billingCycleStart"] ?? root["billing_cycle_start"])
        let end = date(root["billingCycleEnd"] ?? root["billing_cycle_end"])
        var cycle = "monthly"
        if let start, let end, end.timeIntervalSince(start) < weeklyCycle { cycle = "weekly" }

        var items: [LimitItem] = []
        var notes: [SnapshotNote] = []
        if let usage = (root["planUsage"] ?? root["plan_usage"]) as? [String: Any] {
            let limit = cents(usage, "limit")
            let included = cents(usage, "includedSpend", "included_spend")
            // The dashboard's headline. Missing means zero (proto3), unless an older answer only has the spend.
            let total = percent(usage, "totalPercentUsed", "total_percent_used")
                ?? (limit > 0 ? Double(included) * 100 / Double(limit) : 0)
            items.append(item(id: "cursor-\(cycle)", kind: cycle, scope: nil, percent: total, reset: end, group: .weekly))

            // Plans with two pools: Auto (Cursor's own models) and API (models billed at API prices).
            let auto = percent(usage, "autoPercentUsed", "auto_percent_used")
            let api = percent(usage, "apiPercentUsed", "api_percent_used")
            if auto != nil || api != nil {
                items.append(item(id: "cursor-\(cycle)-auto", kind: "\(cycle)_scoped", scope: "Auto", percent: auto ?? 0, reset: end, group: .other))
                items.append(item(id: "cursor-\(cycle)-api", kind: "\(cycle)_scoped", scope: "API", percent: api ?? 0, reset: end, group: .other))
            }

            if limit > 0 {
                notes.append(.includedSpend(cents: included, limit: limit, bonus: cents(usage, "bonusSpend", "bonus_spend")))
            }
        }
        if let onDemand = onDemand((root["spendLimitUsage"] ?? root["spend_limit_usage"]) as? [String: Any]) {
            notes.append(onDemand)
        }

        guard !items.isEmpty || !notes.isEmpty else { return nil }
        return ProviderSnapshot(provider: .cursor, planName: plan, items: items, notes: notes)
    }

    /// Spending past the plan (`spendLimitUsage`, cents): against the user's own
    /// cap first, then against the team's pool; spending with no cap shows the amount alone.
    private static func onDemand(_ spend: [String: Any]?) -> SnapshotNote? {
        guard let spend else { return nil }
        var used = cents(spend, "individualUsed", "individual_used")
        if used == 0 { used = cents(spend, "totalSpend", "total_spend") }
        let limit = cents(spend, "individualLimit", "individual_limit")
        if limit > 0 { return .onDemandSpend(cents: used, limit: limit) }
        let pool = cents(spend, "pooledLimit", "pooled_limit")
        if pool > 0 { return .teamOnDemandSpend(cents: cents(spend, "pooledUsed", "pooled_used"), limit: pool) }
        return used > 0 ? .onDemandSpend(cents: used, limit: nil) : nil
    }

    private static func item(id: String, kind: String, scope: String?, percent: Double, reset: Date?, group: LimitItem.Group) -> LimitItem {
        LimitItem(id: id, kind: kind, scopeName: scope, percent: percent,
                  severity: Severity(api: nil, percent: percent), resetsAt: reset, group: group)
    }

    private static func percent(_ object: [String: Any], _ keys: String...) -> Double? {
        guard let value = keys.lazy.compactMap({ number(object[$0]) }).first, value.isFinite else { return nil }
        return max(value, 0)
    }

    private static func cents(_ object: [String: Any], _ keys: String...) -> Int {
        guard let value = keys.lazy.compactMap({ number(object[$0]) }).first, value.isFinite, value > 0 else { return 0 }
        return Int(value.rounded())
    }

    private static func number(_ value: Any?) -> Double? {
        if let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() { return number.doubleValue }
        if let string = value as? String { return Double(string) }
        return nil
    }

    /// RFC 3339, or epoch seconds or milliseconds (as a number or a string).
    private static func date(_ value: Any?) -> Date? {
        if let string = value as? String, let date = APIDate.parse(string) { return date }
        guard let epoch = number(value), epoch > 0 else { return nil }
        return Date(timeIntervalSince1970: epoch > 100_000_000_000 ? epoch / 1000 : epoch)
    }

    /// `cursorAuth/stripeMembershipType`: `pro` → "Pro", `pro_plus` → "Pro+",
    /// `free` → "Hobby", `free_trial` → "Pro Trial".
    public static func planName(_ membership: String?) -> String? {
        let value = membership?.trimmingCharacters(in: .whitespaces).lowercased() ?? ""
        switch value {
        case "": return nil
        case "free": return "Hobby"
        case "free_trial": return "Pro Trial"
        default:
            return value
                .replacingOccurrences(of: "_plus", with: "+")
                .replacingOccurrences(of: "-plus", with: "+")
                .replacingOccurrences(of: "_", with: " ")
                .capitalized
        }
    }
}

public struct CursorClient: Sendable {
    public static let endpoint = URL(string: "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage")!

    public init() {}

    public func fetchCurrentPeriodUsage(accessToken: String) async throws -> Data {
        var request = URLRequest(url: Self.endpoint, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        request.httpMethod = "POST"
        request.httpBody = Data("{}".utf8)
        request.setValue("Bearer \(accessToken)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("ai-usage-bar", forHTTPHeaderField: "User-Agent")
        request.setValue("1", forHTTPHeaderField: "Connect-Protocol-Version")
        // Cursor's privacy flag; we never send content, only read usage.
        request.setValue("true", forHTTPHeaderField: "x-ghost-mode")
        return try await HTTP.send(request)
    }
}

public struct CursorProvider: UsageProvider {
    public let id = ProviderID.cursor
    /// Cursor's bundle identifier (it is built with ToDesktop).
    public static let bundleIdentifier = "com.todesktop.230313mzl4w4u92"

    private let database: URL
    private let keyCache: SafeStorageKeyCache

    public init(database: URL = CursorLogin.defaultDatabase, keyCache: SafeStorageKeyCache = .cursor) {
        self.database = database
        self.keyCache = keyCache
    }

    /// Cursor is installed and signed in. A database that is busy for a moment still counts.
    public func isAvailable() -> Bool {
        guard FileManager.default.fileExists(atPath: database.path) else { return false }
        do {
            _ = try CursorLogin.load(from: database)
            return true
        } catch {
            return (error as? UsageError) != .notLoggedIn
        }
    }

    public func fetch() async throws -> ProviderFetchResult {
        let database = database
        let login = try await runBlocking { try CursorLogin.load(from: database) }
        let token = try await accessToken(of: login)
        if let expires = JWT.expiration(of: token), expires < Date() {
            throw UsageError.unauthorized(expired: true)
        }

        let data = try await CursorClient().fetchCurrentPeriodUsage(accessToken: token)
        guard let snapshot = CursorUsage.snapshot(fromResponse: data, plan: login.planName) else { throw UsageError.decoding }
        let cache = try ProviderCache(plan: login.planName, raw: data).encoded()
        return ProviderFetchResult(snapshot: snapshot, cache: cache)
    }

    public func snapshot(fromCache data: Data) -> ProviderSnapshot? {
        ProviderCache.decode(data).flatMap { CursorUsage.snapshot(fromResponse: $0.raw, plan: $0.plan) }
    }

    private func accessToken(of login: CursorLogin) async throws -> String {
        guard login.accessToken.hasPrefix(CursorLogin.encryptedPrefix) else { return login.accessToken }
        guard let blob = login.encryptedToken else { throw UsageError.decoding }
        let key = try await keyCache.key(loading: { try CursorLogin.readSafeStoragePassword() })
        do {
            return try ElectronSafeStorage.decryptString(blob, key: key)
        } catch {
            // The key may have been rotated: read it again next time.
            await keyCache.reset()
            throw error
        }
    }
}
