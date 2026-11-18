import CryptoKit
import Foundation

/// The Claude Code login (OAuth) this app piggybacks on. Read-only: the app
/// never writes, refreshes or stores the token anywhere.
public struct ClaudeCredentials: Equatable, Sendable, CustomStringConvertible {
    public var accessToken: String
    public var expiresAt: Date?
    public var subscriptionType: String?
    public var rateLimitTier: String?
    /// The account this login belongs to. Claude Code keeps it next to the
    /// token, and it survives a token renewal, so it is what tells one
    /// account's usage from another's.
    public var organizationUuid: String?

    public init(
        accessToken: String,
        expiresAt: Date?,
        subscriptionType: String?,
        rateLimitTier: String?,
        organizationUuid: String? = nil
    ) {
        self.accessToken = accessToken
        self.expiresAt = expiresAt
        self.subscriptionType = subscriptionType
        self.rateLimitTier = rateLimitTier
        self.organizationUuid = organizationUuid
    }

    public func isExpired(now: Date) -> Bool {
        guard let expiresAt else { return false }
        return expiresAt < now
    }

    public var isExpired: Bool { isExpired(now: Date()) }

    /// "Max 5x", "Max 20x", "Pro", "Team", ...
    ///
    /// `subscriptionType` names the plan; `rateLimitTier` only tells the Max
    /// tiers apart, and is consulted for those alone. A Team account carries
    /// the tier `default_claude_max_5x`, so reading the tier first labelled it
    /// "Max 5x".
    public var planName: String? {
        let plan = (subscriptionType ?? "").lowercased()
        if plan.isEmpty || plan == "max" {
            let tier = (rateLimitTier ?? "").lowercased()
            if tier.contains("max_20x") { return "Max 20x" }
            if tier.contains("max_5x") { return "Max 5x" }
        }
        guard !plan.isEmpty else { return nil }
        return plan.prefix(1).uppercased() + plan.dropFirst()
    }

    /// Which account these numbers belong to. Unchanged by a token renewal,
    /// so cached usage survives one and is only dropped on a real sign-in
    /// change.
    public var account: String? { organizationUuid }

    /// The login itself, account plus the life of the current token. It moves
    /// whenever Claude Code signs in again or renews, which is the moment a
    /// wait earned by the previous login stops applying. Carries no part of
    /// the token.
    public var loginIdentity: String {
        let expiry = expiresAt.map { String(Int($0.timeIntervalSince1970)) } ?? "none"
        return "\(organizationUuid ?? "none")|\(expiry)"
    }

    /// Never prints the token.
    public var description: String {
        "ClaudeCredentials(plan: \(planName ?? "?"), expiresAt: \(expiresAt.map(String.init(describing:)) ?? "?"), token: <redacted>)"
    }

    /// Parses the JSON Claude Code stores (`{"claudeAiOauth": {...}}`).
    public static func parse(_ data: Data) throws -> ClaudeCredentials {
        guard
            let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
            let oauth = root["claudeAiOauth"] as? [String: Any],
            let token = oauth["accessToken"] as? String, !token.isEmpty
        else { throw CredentialsError.malformed }

        let expiresAt = (oauth["expiresAt"] as? NSNumber).map { Date(timeIntervalSince1970: $0.doubleValue / 1000) }
        return ClaudeCredentials(
            accessToken: token,
            expiresAt: expiresAt,
            subscriptionType: oauth["subscriptionType"] as? String,
            rateLimitTier: oauth["rateLimitTier"] as? String,
            organizationUuid: root["organizationUuid"] as? String
        )
    }
}

public enum CredentialsError: Error, Equatable {
    case notFound
    case malformed
}

public enum CredentialsStore {
    /// Keychain item (generic password) Claude Code writes on macOS. A custom
    /// config directory adds a suffix to it; see `service(environment:)`.
    public static let keychainService = "Claude Code-credentials"

    /// The account Claude Code falls back to when `$USER` is unusable.
    static let fallbackAccount = "claude-code-user"

    /// Characters Claude Code accepts in the account name.
    private static let accountCharacters = CharacterSet(
        charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-"
    )

    /// Blocking (spawns `/usr/bin/security`): call it off the main thread.
    public static func load(
        environment: [String: String] = ProcessInfo.processInfo.environment,
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        now: Date = Date()
    ) throws -> ClaudeCredentials {
        let sources: [() -> Data?] = [
            { readKeychain(environment: environment) },
            { readLegacyKeychain(environment: environment) },
            { readFile(environment: environment, home: home) },
        ]
        return try select(from: sources, now: now)
    }

    /// The first login that is still valid. An expired one is only returned
    /// when no source holds a valid one, so a leftover Keychain item from an
    /// older Claude Code cannot shadow the live login: sending its dead token
    /// on every refresh earns a 429 that never clears.
    static func select(from sources: [() -> Data?], now: Date) throws -> ClaudeCredentials {
        var sawData = false
        var expired: ClaudeCredentials?
        for source in sources {
            guard let data = source() else { continue }
            sawData = true
            guard let credentials = parse(data) else { continue }
            if credentials.isExpired(now: now) {
                if expired == nil { expired = credentials }
                continue
            }
            return credentials
        }
        if let expired { return expired }
        throw sawData ? CredentialsError.malformed : CredentialsError.notFound
    }

    /// Plain JSON, or the hex `security -w` prints for a secret it considers
    /// non-printable.
    static func parse(_ data: Data) -> ClaudeCredentials? {
        if let credentials = try? ClaudeCredentials.parse(data) { return credentials }
        guard let decoded = hexDecoded(data) else { return nil }
        return try? ClaudeCredentials.parse(decoded)
    }

    /// Whether any source holds a login at all, valid or not: a missing login
    /// and an expired one get different messages, so detection reports both as
    /// connected and lets the fetch tell them apart.
    public static func hasLogin(
        environment: [String: String] = ProcessInfo.processInfo.environment,
        home: URL = FileManager.default.homeDirectoryForCurrentUser
    ) -> Bool {
        readKeychain(environment: environment) != nil
            || readLegacyKeychain(environment: environment) != nil
            || readFile(environment: environment, home: home) != nil
    }

    /// The account Claude Code writes the Keychain item under: `$USER`, or
    /// `claude-code-user` when it is missing or holds characters Claude Code
    /// rejects.
    public static func account(environment: [String: String] = ProcessInfo.processInfo.environment) -> String {
        let user = environment["USER"] ?? NSUserName()
        guard !user.isEmpty, user.unicodeScalars.allSatisfy(accountCharacters.contains) else {
            return fallbackAccount
        }
        return user
    }

    /// The Keychain service Claude Code writes the item under. A custom config
    /// directory appends the first eight hex digits of its SHA-256, so a user
    /// who sets `CLAUDE_CONFIG_DIR` keeps a separate item.
    public static func service(environment: [String: String] = ProcessInfo.processInfo.environment) -> String {
        guard let directory = secureStorageDirectory(environment: environment) else { return keychainService }
        let normalized = directory.precomposedStringWithCanonicalMapping
        let digest = SHA256.hash(data: Data(normalized.utf8))
        let hex = digest.map { String(format: "%02x", $0) }.joined().prefix(8)
        return "\(keychainService)-\(hex)"
    }

    /// The config directory whose hash suffixes the Keychain service, or nil
    /// when Claude Code uses the unsuffixed name.
    /// `CLAUDE_SECURESTORAGE_CONFIG_DIR` wins when it is set at all; an empty
    /// value means "no suffix", the same as leaving it unset.
    private static func secureStorageDirectory(environment: [String: String]) -> String? {
        if let storage = environment["CLAUDE_SECURESTORAGE_CONFIG_DIR"] {
            return storage.isEmpty ? nil : storage
        }
        guard let config = environment["CLAUDE_CONFIG_DIR"], !config.isEmpty else { return nil }
        return config
    }

    /// Goes through the `security` tool rather than the Security framework
    /// so the Keychain item's existing access rules apply and macOS does not
    /// ask again every time this app is rebuilt.
    ///
    /// Asks for the account as well as the service: Claude Code looks its item
    /// up by both, and a Mac can carry items from older versions that used a
    /// different account. Without `-a`, `security` answers with whichever it
    /// finds first, which may be a login that expired months ago.
    static func readKeychain(environment: [String: String] = ProcessInfo.processInfo.environment) -> Data? {
        readKeychain(account: account(environment: environment), service: service(environment: environment))
    }

    /// The pre-2.x lookup, by service alone, for a Mac whose only item was
    /// written under an account this version no longer derives. Tried after
    /// the account-scoped one, and `select(from:now:)` keeps an expired result
    /// from winning over a valid one.
    static func readLegacyKeychain(environment: [String: String] = ProcessInfo.processInfo.environment) -> Data? {
        readKeychain(account: nil, service: service(environment: environment))
    }

    static func readKeychain(account: String?, service: String) -> Data? {
        var arguments = ["find-generic-password"]
        if let account { arguments += ["-a", account] }
        arguments += ["-s", service, "-w"]

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        process.arguments = arguments
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return nil }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0, !data.isEmpty else { return nil }
        return data
    }

    /// `~/.claude/.credentials.json`, used by Claude Code where there is no
    /// Keychain. `CLAUDE_SECURESTORAGE_CONFIG_DIR` overrides it, then
    /// `CLAUDE_CONFIG_DIR`; an empty value means the default.
    static func readFile(
        environment: [String: String] = ProcessInfo.processInfo.environment,
        home: URL = FileManager.default.homeDirectoryForCurrentUser
    ) -> Data? {
        try? Data(contentsOf: configDirectory(environment: environment, home: home)
            .appendingPathComponent(".credentials.json"))
    }

    static func configDirectory(
        environment: [String: String],
        home: URL = FileManager.default.homeDirectoryForCurrentUser
    ) -> URL {
        let fallback = home.appendingPathComponent(".claude")
        if let storage = environment["CLAUDE_SECURESTORAGE_CONFIG_DIR"] {
            return storage.isEmpty ? fallback : URL(fileURLWithPath: storage)
        }
        guard let config = environment["CLAUDE_CONFIG_DIR"], !config.isEmpty else { return fallback }
        return URL(fileURLWithPath: config)
    }

    /// `security -w` prints the secret as hex when it contains bytes it
    /// considers non-printable.
    static func hexDecoded(_ data: Data) -> Data? {
        guard let text = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
              text.count.isMultiple(of: 2), text.allSatisfy(\.isHexDigit)
        else { return nil }
        var bytes = [UInt8]()
        bytes.reserveCapacity(text.count / 2)
        var index = text.startIndex
        while index < text.endIndex {
            let next = text.index(index, offsetBy: 2)
            guard let byte = UInt8(text[index..<next], radix: 16) else { return nil }
            bytes.append(byte)
            index = next
        }
        return Data(bytes)
    }
}
