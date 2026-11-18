import Foundation

public struct ClaudeProvider: UsageProvider {
    public let id = ProviderID.claude

    public init() {}

    /// Always shown: Claude is the reason this app exists, so a missing
    /// login is reported instead of hidden.
    public func isAvailable() -> Bool { true }

    /// Keychain (`security`) or `~/.claude/.credentials.json`; spawns a process.
    public func detect() -> SetupStatus {
        CredentialsStore.hasLogin() ? .connected : .needsLogin
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
        let cache = try ProviderCache(
            plan: credentials.planName,
            raw: data,
            account: credentials.account
        ).encoded()
        return ProviderFetchResult(snapshot: snapshot, cache: cache)
    }

    /// Reads the login again (spawns a process), so numbers cached under a
    /// different account are dropped instead of being shown as yours. Signing
    /// in to another account otherwise left the previous account's percentages
    /// on screen for as long as the new one failed to load.
    public func snapshot(fromCache data: Data) -> ProviderSnapshot? {
        Self.snapshot(fromCache: data, account: currentAccount())
    }

    /// `account`: the login on this Mac now, or nil when it could not be read.
    public static func snapshot(fromCache data: Data, account: String?) -> ProviderSnapshot? {
        guard let cache = ProviderCache.decode(data) else { return nil }
        // Only when the mismatch is certain: a Keychain that cannot be read
        // right now is no reason to blank numbers that are probably still ours.
        if let cached = cache.account, let account, cached != account { return nil }
        return Self.snapshot(fromResponse: cache.raw, plan: cache.plan)
    }

    public func loginIdentity() -> String? {
        try? CredentialsStore.load().loginIdentity
    }

    private func currentAccount() -> String? {
        try? CredentialsStore.load().account
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
