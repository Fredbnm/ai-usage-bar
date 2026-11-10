import Foundation

public struct ClaudeProvider: UsageProvider {
    public let id = ProviderID.claude

    public init() {}

    /// Always shown: Claude is the reason this app exists, so a missing
    /// login is reported instead of hidden.
    public func isAvailable() -> Bool { true }

    /// Keychain (`security`) or `~/.claude/.credentials.json`; spawns a process.
    public func detect() -> SetupStatus {
        CredentialsStore.readKeychain() != nil || CredentialsStore.readFile() != nil ? .connected : .needsLogin
    }

    public func fetch() async throws -> ProviderFetchResult {
        let credentials: ClaudeCredentials
        do {
            credentials = try await runBlocking { try CredentialsStore.load() }
        } catch {
            throw UsageError.notLoggedIn
        }

        let data: Data
        do {
            data = try await UsageClient().fetchUsage(accessToken: credentials.accessToken)
        } catch UsageError.unauthorized {
            throw UsageError.unauthorized(expired: credentials.isExpired)
        }

        guard let snapshot = Self.snapshot(fromResponse: data, plan: credentials.planName) else {
            throw UsageError.decoding
        }
        let cache = try ProviderCache(plan: credentials.planName, raw: data).encoded()
        return ProviderFetchResult(snapshot: snapshot, cache: cache)
    }

    public func snapshot(fromCache data: Data) -> ProviderSnapshot? {
        guard let cache = ProviderCache.decode(data) else { return nil }
        return Self.snapshot(fromResponse: cache.raw, plan: cache.plan)
    }

    public static func snapshot(fromResponse data: Data, plan: String?) -> ProviderSnapshot? {
        guard let response = try? UsageResponse.decode(data), !response.items.isEmpty else { return nil }
        return ProviderSnapshot(
            provider: .claude,
            planName: plan,
            items: response.items,
            breakdown: response.breakdownItems
        )
    }
}
