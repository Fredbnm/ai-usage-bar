import XCTest
@testable import UsageCore

final class GrokBotTests: XCTestCase {
    // Generated with:
    //   key = PBKDF2-SHA1("test-password", "saltysalt", 1003, 16 bytes)
    //   "v10" + openssl enc -aes-128-cbc -K <key> -iv 2020…20 <<< "hello grok bot"
    private let password = Data("test-password".utf8)
    private let blobBase64 = "djEwKuXxtj0WL0ulbfO9IIBuqw=="

    func testDerivesElectronKey() {
        let key = ElectronSafeStorage.deriveKey(password: password)
        XCTAssertEqual(key.map { String(format: "%02x", $0) }.joined(), "c0ffe4c25f07f62bfc6ab011d9efa54e")
    }

    func testDecryptsElectronSafeStorage() throws {
        let key = ElectronSafeStorage.deriveKey(password: password)
        let blob = try XCTUnwrap(Data(base64Encoded: blobBase64))
        XCTAssertEqual(try ElectronSafeStorage.decryptString(blob, key: key), "hello grok bot")
    }

    func testRejectsWrongKeyOrVersion() throws {
        let blob = try XCTUnwrap(Data(base64Encoded: blobBase64))
        let wrongKey = ElectronSafeStorage.deriveKey(password: Data("nope".utf8))
        XCTAssertThrowsError(try ElectronSafeStorage.decryptString(blob, key: wrongKey))
        XCTAssertThrowsError(try ElectronSafeStorage.decrypt(Data("v11abc".utf8), key: wrongKey))
    }

    func testFindsActiveAccountToken() throws {
        let accounts = #"{"active":"aaa","accounts":{"bbb":{"cursor-access-token":"T3RoZXI="},"aaa":{"cursor-access-token":"\#(blobBase64)","cursor-refresh-token":"x"}}}"#
        let file = try JSONSerialization.data(withJSONObject: ["version": 1, "cursor-accounts": accounts])
        let stored = try GrokBotSecrets.activeAccessToken(in: file)
        XCTAssertEqual(stored, .encrypted(try XCTUnwrap(Data(base64Encoded: blobBase64))))
    }

    func testReadsLegacyScopedAndPlaintextTokens() throws {
        let scope = String(repeating: "ab", count: 32)
        let scoped = try JSONSerialization.data(withJSONObject: ["cursor-access-token": "scoped:v1:\(scope):\(blobBase64)"])
        XCTAssertEqual(try GrokBotSecrets.activeAccessToken(in: scoped), .encrypted(try XCTUnwrap(Data(base64Encoded: blobBase64))))

        let plain = try JSONSerialization.data(withJSONObject: ["cursor-access-token": "plaintext:v1:" + Data("tok".utf8).base64EncodedString()])
        XCTAssertEqual(try GrokBotSecrets.activeAccessToken(in: plain), .plaintext("tok"))

        let empty = try JSONSerialization.data(withJSONObject: ["version": 1])
        XCTAssertThrowsError(try GrokBotSecrets.activeAccessToken(in: empty)) { error in
            XCTAssertEqual(error as? UsageError, .notLoggedIn)
        }
    }

    func testReadsJWTExpiration() {
        // {"alg":"none"}.{"exp":4102444800}.
        let token = "eyJhbGciOiJub25lIn0.eyJleHAiOjQxMDI0NDQ4MDB9."
        XCTAssertEqual(JWT.expiration(of: token), Date(timeIntervalSince1970: 4_102_444_800))
        XCTAssertNil(JWT.expiration(of: "not-a-jwt"))
    }

    func testBuildsSnapshotFromUsageStatus() throws {
        let json = """
        {"currentPeriodStart": "2026-11-06T03:00:00Z",
         "nextResetTimestampUtc": "2026-11-13T03:00:00.123456789Z",
         "usagePercent": 41.5,
         "availableBankedResetCount": "2",
         "hasAvailableUsage": true,
         "grokPlanLabel": "SuperGrok",
         "cursorPlanName": "pro",
         "onDemandSettings": {"visible": true, "enabled": true},
         "someFutureField": {"x": 1}}
        """
        let snapshot = try XCTUnwrap(GrokBotProvider.snapshot(fromResponse: Data(json.utf8)))
        XCTAssertEqual(snapshot.provider, .grokBot)
        XCTAssertEqual(snapshot.planName, "SuperGrok")
        XCTAssertEqual(snapshot.items.map(\.title), ["Semanal"])
        XCTAssertEqual(snapshot.items.first?.percent, 41.5)
        XCTAssertNotNil(snapshot.items.first?.resetsAt)
        XCTAssertEqual(snapshot.notes, ["2 resets guardados disponíveis", "Uso sob demanda ligado"])
    }

    func testGenericPlanLabelFallsBackToCursorPlan() throws {
        // Shape of the real response from Grok Bot 0.79 (trimmed).
        let json = """
        {"currentPeriodStart":"2026-11-07T04:47:36.822Z","nextResetTimestampUtc":"2026-11-14T04:47:36.822Z",
         "usagePercent":0.30335861321776814,"hasAvailableUsage":true,"hasNonZeroIncludedLimit":true,
         "onDemandSettings":{"visible":true,"eligible":true},
         "grokPlanLabel":"Grok Bot Plan","cursorPlanName":"Premium","isUnified":true,
         "billingBrand":"SAND_BILLING_BRAND_GROK","prepaidAvailableCents":"0"}
        """
        let snapshot = try XCTUnwrap(GrokBotProvider.snapshot(fromResponse: Data(json.utf8)))
        XCTAssertEqual(snapshot.planName, "Premium")
        XCTAssertEqual(snapshot.items.first.map { UsageFormat.percent($0.percent) }, "<1%")
        XCTAssertEqual(snapshot.notes, [])
    }

    func testSnakeCaseAndExhaustedUsage() throws {
        let json = #"{"usage_percent": 100, "has_available_usage": false, "next_reset_timestamp_utc": "2026-11-13T03:00:00Z", "cursor_plan_name": "Pro"}"#
        let snapshot = try XCTUnwrap(GrokBotProvider.snapshot(fromResponse: Data(json.utf8)))
        XCTAssertEqual(snapshot.items.first?.severity, .critical)
        XCTAssertEqual(snapshot.planName, "Pro")
    }

    func testPooledAllowanceHasNoPercent() throws {
        let json = #"{"usesPooledEnterpriseAllowance": true, "usagePercent": 12}"#
        let snapshot = try XCTUnwrap(GrokBotProvider.snapshot(fromResponse: Data(json.utf8)))
        XCTAssertTrue(snapshot.items.isEmpty)
        XCTAssertEqual(snapshot.notes, ["Uso compartilhado da equipe, sem limite individual."])
    }

    func testEmptyResponseIsNotASnapshot() {
        XCTAssertNil(GrokBotProvider.snapshot(fromResponse: Data("{}".utf8)))
    }

    func testCacheRoundTripKeepsPlanAndNoToken() throws {
        let raw = Data(#"{"usagePercent": 10}"#.utf8)
        let cache = try ProviderCache(plan: "X", raw: raw).encoded()
        XCTAssertEqual(ProviderCache.decode(cache)?.plan, "X")
        XCTAssertNotNil(GrokBotProvider().snapshot(fromCache: cache))
    }

    func testErrorMessagesNameTheRightApp() {
        XCTAssertTrue(UsageError.unauthorized(expired: true).message(for: .grokBot).contains("Grok Bot"))
        XCTAssertTrue(UsageError.unauthorized(expired: true).message(for: .claude).contains("Claude Code"))
        XCTAssertTrue(UsageError.keychainDenied.message(for: .grokBot).contains("Permitir sempre"))
    }
}
