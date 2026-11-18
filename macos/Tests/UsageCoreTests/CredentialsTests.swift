import XCTest
@testable import UsageCore

/// How the Claude Code login is looked up. The app has to ask for the same
/// Keychain item Claude Code wrote, and must not settle on a login that
/// expired months ago.
final class CredentialsTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    override func setUp() {
        super.setUp()
        AppLanguage.current = .portuguese
    }

    private func login(expiresAt: Date?, token: String = "t") -> Data {
        var oauth: [String: Any] = ["accessToken": token, "subscriptionType": "max"]
        if let expiresAt { oauth["expiresAt"] = expiresAt.timeIntervalSince1970 * 1000 }
        return try! JSONSerialization.data(withJSONObject: ["claudeAiOauth": oauth])
    }

    // MARK: plan name

    private func credentials(plan: String?, tier: String?) -> ClaudeCredentials {
        ClaudeCredentials(accessToken: "t", expiresAt: nil, subscriptionType: plan, rateLimitTier: tier)
    }

    func testTheSubscriptionNamesThePlan() {
        XCTAssertEqual(
            credentials(plan: "team", tier: "default_claude_max_5x").planName,
            "Team",
            "a Team account carries a max_5x tier; the tier must not win"
        )
        XCTAssertEqual(credentials(plan: "enterprise", tier: "default_claude_max_5x").planName, "Enterprise")
        XCTAssertEqual(credentials(plan: "pro", tier: nil).planName, "Pro")
    }

    func testTheTierSeparatesTheMaxPlans() {
        XCTAssertEqual(credentials(plan: "max", tier: "default_claude_max_5x").planName, "Max 5x")
        XCTAssertEqual(credentials(plan: "max", tier: "default_claude_max_20x").planName, "Max 20x")
        XCTAssertEqual(credentials(plan: "max", tier: nil).planName, "Max", "no tier still names the plan")
    }

    func testTheTierIsTheFallbackWithoutASubscription() {
        XCTAssertEqual(credentials(plan: nil, tier: "default_claude_max_20x").planName, "Max 20x")
        XCTAssertEqual(credentials(plan: "", tier: "default_claude_max_5x").planName, "Max 5x")
        XCTAssertNil(credentials(plan: nil, tier: nil).planName)
    }

    // MARK: account and login identity

    func testTheAccountComesFromTheOrganization() throws {
        let json = """
        {"organizationUuid": "org-1", "claudeAiOauth": {"accessToken": "a",
          "expiresAt": 4102444800000, "subscriptionType": "team", "rateLimitTier": "default_claude_max_5x"}}
        """
        let parsed = try ClaudeCredentials.parse(Data(json.utf8))
        XCTAssertEqual(parsed.organizationUuid, "org-1")
        XCTAssertEqual(parsed.account, "org-1")
        XCTAssertEqual(parsed.planName, "Team")
    }

    func testTheLoginIdentityMovesWithTheTokenButTheAccountDoesNot() {
        let first = ClaudeCredentials(
            accessToken: "a", expiresAt: Date(timeIntervalSince1970: 100),
            subscriptionType: "max", rateLimitTier: nil, organizationUuid: "org-1")
        let renewed = ClaudeCredentials(
            accessToken: "b", expiresAt: Date(timeIntervalSince1970: 200),
            subscriptionType: "max", rateLimitTier: nil, organizationUuid: "org-1")
        let other = ClaudeCredentials(
            accessToken: "c", expiresAt: Date(timeIntervalSince1970: 100),
            subscriptionType: "max", rateLimitTier: nil, organizationUuid: "org-2")

        XCTAssertNotEqual(first.loginIdentity, renewed.loginIdentity, "a renewal is a new login to retry on")
        XCTAssertEqual(first.account, renewed.account, "but the cached numbers are still this account's")
        XCTAssertNotEqual(first.account, other.account)
        XCTAssertFalse(first.loginIdentity.contains("a"), "carries no part of the token")
    }

    // MARK: cached numbers belong to an account

    private var cachedUsage: Data {
        let raw = Data(#"{"five_hour": {"utilization": 42.0, "resets_at": "2026-11-10T16:50:00.554353+00:00"}}"#.utf8)
        return try! ProviderCache(plan: "Team", raw: raw, account: "org-1").encoded()
    }

    func testCachedNumbersFromAnotherAccountAreDropped() {
        XCTAssertNil(
            ClaudeProvider.snapshot(fromCache: cachedUsage, account: "org-2"),
            "after signing in elsewhere, the previous account's percentages are not yours"
        )
    }

    func testCachedNumbersAreKeptForTheSameAccount() {
        XCTAssertEqual(ClaudeProvider.snapshot(fromCache: cachedUsage, account: "org-1")?.planName, "Team")
    }

    func testCachedNumbersAreKeptWhenTheAccountCannotBeRead() {
        XCTAssertNotNil(
            ClaudeProvider.snapshot(fromCache: cachedUsage, account: nil),
            "an unreadable Keychain is no reason to blank the popover"
        )
    }

    func testACacheWrittenBeforeAccountsWereRecordedIsKept() throws {
        let raw = Data(#"{"five_hour": {"utilization": 42.0, "resets_at": "2026-11-10T16:50:00.554353+00:00"}}"#.utf8)
        let legacy = try ProviderCache(plan: "Max 5x", raw: raw, account: nil).encoded()
        XCTAssertNotNil(ClaudeProvider.snapshot(fromCache: legacy, account: "org-1"))
    }

    // MARK: account

    func testAccountIsTheUserName() {
        XCTAssertEqual(CredentialsStore.account(environment: ["USER": "anderson"]), "anderson")
        XCTAssertEqual(CredentialsStore.account(environment: ["USER": "ana.b_c-1"]), "ana.b_c-1")
    }

    func testAccountFallsBackWhenTheUserNameIsUnusable() {
        let fallback = CredentialsStore.fallbackAccount
        XCTAssertEqual(CredentialsStore.account(environment: ["USER": ""]), fallback)
        XCTAssertEqual(CredentialsStore.account(environment: ["USER": "andré"]), fallback, "Claude Code rejects non-ASCII")
        XCTAssertEqual(CredentialsStore.account(environment: ["USER": "two words"]), fallback)
    }

    // MARK: service

    func testServiceHasNoSuffixByDefault() {
        XCTAssertEqual(CredentialsStore.service(environment: [:]), "Claude Code-credentials")
        XCTAssertEqual(CredentialsStore.service(environment: ["CLAUDE_CONFIG_DIR": ""]), "Claude Code-credentials")
    }

    func testCustomConfigDirectoryAddsItsHash() {
        XCTAssertEqual(
            CredentialsStore.service(environment: ["CLAUDE_CONFIG_DIR": "/Users/someone/.config/claude"]),
            "Claude Code-credentials-f6ab3f41",
            "the first eight hex digits of the SHA-256 of the directory"
        )
    }

    func testSecureStorageDirectoryWins() {
        let environment = [
            "CLAUDE_CONFIG_DIR": "/Users/someone/.config/claude",
            "CLAUDE_SECURESTORAGE_CONFIG_DIR": "/tmp/cfg",
        ]
        XCTAssertEqual(CredentialsStore.service(environment: environment), "Claude Code-credentials-519e587f")
        XCTAssertEqual(
            CredentialsStore.service(environment: ["CLAUDE_CONFIG_DIR": "/tmp/cfg", "CLAUDE_SECURESTORAGE_CONFIG_DIR": ""]),
            "Claude Code-credentials",
            "set but empty means the default name, as in Claude Code"
        )
    }

    // MARK: config directory

    func testConfigDirectory() {
        let home = URL(fileURLWithPath: "/Users/someone")
        XCTAssertEqual(
            CredentialsStore.configDirectory(environment: [:], home: home).path,
            "/Users/someone/.claude"
        )
        XCTAssertEqual(
            CredentialsStore.configDirectory(environment: ["CLAUDE_CONFIG_DIR": "/tmp/cfg"], home: home).path,
            "/tmp/cfg"
        )
        XCTAssertEqual(
            CredentialsStore.configDirectory(
                environment: ["CLAUDE_CONFIG_DIR": "/tmp/cfg", "CLAUDE_SECURESTORAGE_CONFIG_DIR": "/tmp/secure"],
                home: home
            ).path,
            "/tmp/secure"
        )
        XCTAssertEqual(
            CredentialsStore.configDirectory(environment: ["CLAUDE_SECURESTORAGE_CONFIG_DIR": ""], home: home).path,
            "/Users/someone/.claude"
        )
    }

    // MARK: choosing between sources

    func testAValidLoginWinsOverAnExpiredOne() throws {
        let stale = login(expiresAt: now.addingTimeInterval(-86_400 * 180), token: "stale")
        let live = login(expiresAt: now.addingTimeInterval(3600), token: "live")
        let chosen = try CredentialsStore.select(from: [{ stale }, { live }], now: now)
        XCTAssertEqual(chosen.accessToken, "live", "a leftover item must not shadow the live login")
    }

    func testAnExpiredLoginIsReturnedWhenNothingElseIsValid() throws {
        let stale = login(expiresAt: now.addingTimeInterval(-60), token: "stale")
        let chosen = try CredentialsStore.select(from: [{ stale }, { nil }], now: now)
        XCTAssertEqual(chosen.accessToken, "stale", "so the user still gets the \"login expired\" notice")
        XCTAssertTrue(chosen.isExpired(now: now))
    }

    func testALoginWithoutAnExpiryCounts() throws {
        let chosen = try CredentialsStore.select(from: [{ self.login(expiresAt: nil) }], now: now)
        XCTAssertFalse(chosen.isExpired(now: now))
    }

    func testHexEncodedSourceIsParsed() throws {
        let hex = login(expiresAt: now.addingTimeInterval(3600), token: "live")
            .map { String(format: "%02x", $0) }.joined()
        let chosen = try CredentialsStore.select(from: [{ Data(hex.utf8) }], now: now)
        XCTAssertEqual(chosen.accessToken, "live")
    }

    func testNoSourceAtAllIsNotFound() {
        XCTAssertThrowsError(try CredentialsStore.select(from: [{ nil }, { nil }], now: now)) { error in
            XCTAssertEqual(error as? CredentialsError, .notFound)
        }
    }

    func testUnreadableDataIsMalformed() {
        XCTAssertThrowsError(try CredentialsStore.select(from: [{ Data("not json".utf8) }], now: now)) { error in
            XCTAssertEqual(error as? CredentialsError, .malformed)
        }
    }

    func testAMalformedSourceDoesNotHideALaterValidOne() throws {
        let live = login(expiresAt: now.addingTimeInterval(3600), token: "live")
        let chosen = try CredentialsStore.select(from: [{ Data("not json".utf8) }, { live }], now: now)
        XCTAssertEqual(chosen.accessToken, "live")
    }
}
