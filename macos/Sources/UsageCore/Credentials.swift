import Foundation

/// The Claude Code login (OAuth) this app piggybacks on. Read-only: the app
/// never writes, refreshes or stores the token anywhere.
public struct ClaudeCredentials: Equatable, Sendable, CustomStringConvertible {
    public var accessToken: String
    public var expiresAt: Date?
    public var subscriptionType: String?
    public var rateLimitTier: String?

    public init(accessToken: String, expiresAt: Date?, subscriptionType: String?, rateLimitTier: String?) {
        self.accessToken = accessToken
        self.expiresAt = expiresAt
        self.subscriptionType = subscriptionType
        self.rateLimitTier = rateLimitTier
    }

    public var isExpired: Bool {
        guard let expiresAt else { return false }
        return expiresAt < Date()
    }

    /// "Max 5x", "Max 20x", "Pro", "Team", ...
    public var planName: String? {
        let tier = (rateLimitTier ?? "").lowercased()
        if tier.contains("max_20x") { return "Max 20x" }
        if tier.contains("max_5x") { return "Max 5x" }
        guard let plan = subscriptionType, !plan.isEmpty else { return nil }
        return plan.prefix(1).uppercased() + plan.dropFirst()
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
            rateLimitTier: oauth["rateLimitTier"] as? String
        )
    }
}

public enum CredentialsError: Error, Equatable {
    case notFound
    case malformed
}

public enum CredentialsStore {
    /// Keychain item (generic password) Claude Code writes on macOS.
    public static let keychainService = "Claude Code-credentials"

    /// Blocking (spawns `/usr/bin/security`): call it off the main thread.
    public static func load() throws -> ClaudeCredentials {
        var sawData = false
        for source in [readKeychain, readFile] {
            guard let data = source() else { continue }
            sawData = true
            if let credentials = try? ClaudeCredentials.parse(data) { return credentials }
            if let decoded = hexDecoded(data), let credentials = try? ClaudeCredentials.parse(decoded) {
                return credentials
            }
        }
        throw sawData ? CredentialsError.malformed : CredentialsError.notFound
    }

    /// Goes through the `security` tool rather than the Security framework
    /// so the Keychain item's existing access rules apply and macOS does not
    /// ask again every time this app is rebuilt.
    static func readKeychain() -> Data? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        process.arguments = ["find-generic-password", "-s", keychainService, "-w"]
        let output = Pipe()
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return nil }
        let data = output.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        guard process.terminationStatus == 0, !data.isEmpty else { return nil }
        return data
    }

    /// `~/.claude/.credentials.json` (or `$CLAUDE_CONFIG_DIR/.credentials.json`),
    /// used by Claude Code where there is no Keychain.
    static func readFile() -> Data? {
        let environment = ProcessInfo.processInfo.environment
        let base = environment["CLAUDE_CONFIG_DIR"].map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude")
        return try? Data(contentsOf: base.appendingPathComponent(".credentials.json"))
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
