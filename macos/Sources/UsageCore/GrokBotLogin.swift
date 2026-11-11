import CommonCrypto
import Foundation
import Security

/// Reads the Grok Bot login. Grok Bot is an Electron app: it keeps its
/// tokens in `sand-secrets.json`, encrypted with Electron `safeStorage`,
/// whose key lives in the Keychain item "Grok Bot Safe Storage".
///
/// Read-only: the token is decrypted in memory for the request and never
/// written, refreshed or logged.
public enum GrokBotLogin {
    public static let keychainService = "Grok Bot Safe Storage"
    public static let keychainAccount = "Grok Bot Key"

    public static var dataDirectory: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Grok Bot")
    }

    public static var secretsFile: URL {
        dataDirectory.appendingPathComponent("sand-secrets.json")
    }

    /// Blocking (may show the macOS Keychain prompt): call off the main thread.
    static func readSafeStoragePassword() throws -> Data {
        try ElectronSafeStorage.readPassword(service: keychainService, account: keychainAccount)
    }
}

extension ElectronSafeStorage {
    /// The `safeStorage` password an Electron app keeps in the Keychain.
    /// Blocking (may show the macOS Keychain prompt): call off the main thread.
    static func readPassword(service: String, account: String) throws -> Data {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        switch status {
        case errSecSuccess:
            guard let data = item as? Data, !data.isEmpty else { throw UsageError.decoding }
            return data
        case errSecItemNotFound:
            throw UsageError.notLoggedIn
        default:
            // errSecUserCanceled, errSecAuthFailed, errSecInteractionNotAllowed…
            throw UsageError.keychainDenied
        }
    }
}

/// Caches the derived key for the app's lifetime so the Keychain is read
/// once per launch, not on every refresh.
public actor SafeStorageKeyCache {
    public static let shared = SafeStorageKeyCache()
    /// Cursor's key is a different Keychain item from Grok Bot's.
    public static let cursor = SafeStorageKeyCache()

    private var key: Data?

    public init() {}

    func key(loading load: @escaping @Sendable () throws -> Data) async throws -> Data {
        if let key { return key }
        let password = try await runBlocking(load)
        let derived = ElectronSafeStorage.deriveKey(password: password)
        key = derived
        return derived
    }

    func reset() { key = nil }
}

/// Locates the active account's access token inside `sand-secrets.json`.
public enum GrokBotSecrets {
    public enum StoredToken: Equatable, Sendable {
        case encrypted(Data)
        case plaintext(String)
    }

    static let accessTokenKey = "cursor-access-token"
    static let accountsKey = "cursor-accounts"
    static let scopedPrefix = "scoped:v1:"
    static let plaintextPrefix = "plaintext:v1:"

    /// Layout (Grok Bot 0.79):
    /// `{"version":1, "cursor-accounts":"{\"active\":\"<scope>\",\"accounts\":{\"<scope>\":{\"cursor-access-token\":\"<base64>\"}}}"}`
    /// Older files keep `cursor-access-token` at the top level, optionally
    /// as `scoped:v1:<64 hex>:<base64>`.
    public static func activeAccessToken(in fileData: Data) throws -> StoredToken {
        guard let root = try? JSONSerialization.jsonObject(with: fileData) as? [String: Any] else {
            throw UsageError.decoding
        }

        var value: String?
        if let accountsJSON = root[accountsKey] as? String,
           let accounts = try? JSONSerialization.jsonObject(with: Data(accountsJSON.utf8)) as? [String: Any],
           let active = accounts["active"] as? String,
           let all = accounts["accounts"] as? [String: Any],
           let entry = all[active] as? [String: Any] {
            value = entry[accessTokenKey] as? String
        }
        value = value ?? root[accessTokenKey] as? String
        guard let value, !value.isEmpty else { throw UsageError.notLoggedIn }

        if value.hasPrefix(plaintextPrefix) {
            guard let data = Data(base64Encoded: String(value.dropFirst(plaintextPrefix.count))),
                  let token = String(data: data, encoding: .utf8)
            else { throw UsageError.decoding }
            return .plaintext(token)
        }

        var base64 = value
        if value.hasPrefix(scopedPrefix) {
            let rest = value.dropFirst(scopedPrefix.count)
            guard let colon = rest.firstIndex(of: ":") else { throw UsageError.decoding }
            base64 = String(rest[rest.index(after: colon)...])
        }
        guard let blob = Data(base64Encoded: base64) else { throw UsageError.decoding }
        return .encrypted(blob)
    }
}

/// Chromium/Electron `OSCrypt` on macOS: "v10" + AES-128-CBC, key from
/// PBKDF2-SHA1(Keychain password, "saltysalt", 1003 rounds), IV of 16 spaces.
public enum ElectronSafeStorage {
    static let salt = Data("saltysalt".utf8)
    static let rounds: UInt32 = 1003
    static let iv = Data(repeating: 0x20, count: kCCBlockSizeAES128)
    static let versionPrefix = Data("v10".utf8)

    public static func deriveKey(password: Data) -> Data {
        var key = Data(count: kCCKeySizeAES128)
        let keyCount = key.count
        _ = key.withUnsafeMutableBytes { keyBytes in
            password.withUnsafeBytes { passwordBytes in
                salt.withUnsafeBytes { saltBytes in
                    CCKeyDerivationPBKDF(
                        CCPBKDFAlgorithm(kCCPBKDF2),
                        passwordBytes.baseAddress?.assumingMemoryBound(to: CChar.self), password.count,
                        saltBytes.baseAddress?.assumingMemoryBound(to: UInt8.self), salt.count,
                        CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA1), rounds,
                        keyBytes.baseAddress?.assumingMemoryBound(to: UInt8.self), keyCount
                    )
                }
            }
        }
        return key
    }

    public static func decrypt(_ blob: Data, key: Data) throws -> Data {
        guard blob.starts(with: versionPrefix) else { throw UsageError.decoding }
        let ciphertext = Data(blob.dropFirst(versionPrefix.count))
        var output = Data(count: ciphertext.count + kCCBlockSizeAES128)
        let outputCapacity = output.count
        var outputLength = 0
        let status = output.withUnsafeMutableBytes { outputBytes in
            ciphertext.withUnsafeBytes { inputBytes in
                key.withUnsafeBytes { keyBytes in
                    iv.withUnsafeBytes { ivBytes in
                        CCCrypt(
                            CCOperation(kCCDecrypt), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                            keyBytes.baseAddress, key.count, ivBytes.baseAddress,
                            inputBytes.baseAddress, ciphertext.count,
                            outputBytes.baseAddress, outputCapacity, &outputLength
                        )
                    }
                }
            }
        }
        guard status == kCCSuccess else { throw UsageError.decoding }
        return output.prefix(outputLength)
    }

    public static func decryptString(_ blob: Data, key: Data) throws -> String {
        guard let string = String(data: try decrypt(blob, key: key), encoding: .utf8) else {
            throw UsageError.decoding
        }
        return string
    }
}

enum JWT {
    /// The `exp` claim, if the token is a JWT. The signature is not checked:
    /// this is only used to pick a better error message.
    static func expiration(of token: String) -> Date? {
        let parts = token.split(separator: ".")
        guard parts.count >= 2 else { return nil }
        var base64 = String(parts[1])
            .replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        while base64.count % 4 != 0 { base64 += "=" }
        guard let data = Data(base64Encoded: base64),
              let payload = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let exp = payload["exp"] as? NSNumber
        else { return nil }
        return Date(timeIntervalSince1970: exp.doubleValue)
    }
}
