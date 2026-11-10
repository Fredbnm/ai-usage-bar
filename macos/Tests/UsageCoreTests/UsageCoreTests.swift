import XCTest
@testable import UsageCore

final class UsageCoreTests: XCTestCase {
    /// Same shape as the real response, with made-up numbers, an unknown
    /// field and a broken `limits` entry that must be skipped.
    private let sample = """
    {
      "five_hour": {"utilization": 42.0, "resets_at": "2026-11-10T16:50:00.554353+00:00"},
      "seven_day": {"utilization": 30.0, "resets_at": "2026-11-12T03:00:00.554376+00:00"},
      "seven_day_opus": null,
      "some_new_field": {"weird": [1, 2, 3]},
      "limits": [
        {"kind": "session", "group": "session", "percent": 42, "severity": "normal",
         "resets_at": "2026-11-10T16:50:00.554353+00:00", "scope": null, "is_active": true},
        {"kind": "weekly_all", "group": "weekly", "percent": 30, "severity": "normal",
         "resets_at": "2026-11-12T03:00:00.554376+00:00", "scope": null, "is_active": false},
        {"kind": "weekly_scoped", "group": "weekly", "percent": 91, "severity": "critical",
         "resets_at": "2026-11-12T02:59:59.554553+00:00",
         "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": "unexpected-string"},
         "is_active": false},
        {"kind": "broken_entry"}
      ],
      "seven_day_breakdown": {
        "window_started_at": "2026-11-05T03:00:00.554376+00:00",
        "rows": [
          {"key": "claude_code", "display_name": "Claude Code", "percent": 70},
          {"key": "chat", "display_name": "Chats", "percent": 30},
          {"key": "other", "display_name": "Other", "percent": 0}
        ]
      }
    }
    """

    private func decodeSample() throws -> UsageResponse {
        try UsageResponse.decode(Data(sample.utf8))
    }

    func testDecodesLimitsArray() throws {
        let items = try decodeSample().items
        XCTAssertEqual(items.map(\.title), ["Sessão atual", "Semanal · todos os modelos", "Semanal · Fable"])
        XCTAssertEqual(items.map(\.percent), [42, 30, 91])
        XCTAssertEqual(items.map(\.group), [.session, .weekly, .weekly])
        XCTAssertEqual(items[2].severity, .critical)
        XCTAssertNotNil(items[0].resetsAt)
    }

    func testMenuBarMetrics() throws {
        let response = try decodeSample()
        XCTAssertEqual(response.menuBarPercent(.session), 42)
        XCTAssertEqual(response.menuBarPercent(.weekly), 30)
        XCTAssertEqual(response.menuBarPercent(.highest), 91)
    }

    func testBreakdownSkipsZeroAndTranslates() throws {
        let rows = try decodeSample().breakdownItems
        XCTAssertEqual(rows.map(\.name), ["Claude Code", "Chat"])
        XCTAssertEqual(rows.map(\.percent), [70, 30])
    }

    func testFallsBackToLegacyWindows() throws {
        let json = """
        {"five_hour": {"utilization": 10, "resets_at": null},
         "seven_day": {"utilization": 20, "resets_at": "2026-11-12T03:00:00+00:00"},
         "seven_day_opus": {"utilization": 5, "resets_at": null}}
        """
        let items = try UsageResponse.decode(Data(json.utf8)).items
        XCTAssertEqual(items.map(\.title), ["Sessão atual", "Semanal · todos os modelos", "Semanal · Opus"])
        XCTAssertEqual(items.map(\.percent), [10, 20, 5])
    }

    func testUnknownKindGetsReadableTitle() {
        XCTAssertEqual(UsageResponse.title(kind: "monthly_all", scopeName: nil), "Monthly All")
        XCTAssertEqual(UsageResponse.title(kind: "weekly_scoped", scopeName: nil), "Semanal · limite específico")
    }

    func testParsesMicrosecondDates() throws {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        let expected = calendar.date(from: DateComponents(year: 2026, month: 11, day: 10, hour: 16, minute: 50))!
        let parsed = try XCTUnwrap(APIDate.parse("2026-11-10T16:50:00.554353+00:00"))
        XCTAssertEqual(parsed.timeIntervalSince(expected), 0.554353, accuracy: 0.001)
        XCTAssertNotNil(APIDate.parse("2026-11-10T16:50:00Z"))
        XCTAssertNil(APIDate.parse("not a date"))
    }

    func testSeverityFallback() {
        XCTAssertEqual(Severity(api: "normal", percent: 99), .normal)
        XCTAssertEqual(Severity(api: "warning", percent: 10), .warning)
        XCTAssertEqual(Severity(api: "exceeded", percent: 10), .critical)
        XCTAssertEqual(Severity(api: nil, percent: 50), .normal)
        XCTAssertEqual(Severity(api: "something_new", percent: 80), .warning)
        XCTAssertEqual(Severity(api: nil, percent: 95), .critical)
    }

    func testCredentialsParsing() throws {
        let json = """
        {"mcpOAuth": {}, "claudeAiOauth": {"accessToken": "secret-abc123", "refreshToken": "r",
          "expiresAt": 4102444800000, "subscriptionType": "max", "rateLimitTier": "default_claude_max_5x"}}
        """
        let credentials = try ClaudeCredentials.parse(Data(json.utf8))
        XCTAssertEqual(credentials.accessToken, "secret-abc123")
        XCTAssertEqual(credentials.planName, "Max 5x")
        XCTAssertFalse(credentials.isExpired)
        XCTAssertFalse(credentials.description.contains("secret-abc123"))

        let pro = ClaudeCredentials(accessToken: "t", expiresAt: Date(timeIntervalSince1970: 0), subscriptionType: "pro", rateLimitTier: nil)
        XCTAssertEqual(pro.planName, "Pro")
        XCTAssertTrue(pro.isExpired)

        XCTAssertThrowsError(try ClaudeCredentials.parse(Data("{}".utf8)))
    }

    func testHexDecoding() {
        let hex = Data("7b2261223a317d0a".utf8)  // {"a":1}\n
        XCTAssertEqual(CredentialsStore.hexDecoded(hex), Data("{\"a\":1}\n".utf8))
        XCTAssertNil(CredentialsStore.hexDecoded(Data("{\"a\":1}".utf8)))
    }

    func testDurations() {
        XCTAssertEqual(UsageFormat.duration(20), "1 min")
        XCTAssertEqual(UsageFormat.duration(45 * 60), "45 min")
        XCTAssertEqual(UsageFormat.duration(3600), "1 h")
        XCTAssertEqual(UsageFormat.duration(76 * 60), "1 h 16 min")
        XCTAssertEqual(UsageFormat.duration(25 * 3600), "1 d 1 h")
    }

    func testResetText() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Sao_Paulo")!
        // Tue 2026-11-10 12:34 in São Paulo (UTC-3).
        let now = calendar.date(from: DateComponents(year: 2026, month: 11, day: 10, hour: 12, minute: 34))!

        XCTAssertEqual(
            UsageFormat.reset(now.addingTimeInterval(76 * 60), now: now, calendar: calendar),
            "Reinicia em 1 h 16 min · 13:50"
        )
        let thursday = calendar.date(from: DateComponents(year: 2026, month: 11, day: 12, hour: 0, minute: 0))!
        XCTAssertEqual(
            UsageFormat.reset(thursday, now: now, calendar: calendar),
            "Reinicia qui. às 00:00 · em 1 d 11 h"
        )
        let tomorrow = calendar.date(from: DateComponents(year: 2026, month: 11, day: 11, hour: 18, minute: 0))!
        XCTAssertEqual(
            UsageFormat.reset(tomorrow, now: now, calendar: calendar),
            "Reinicia amanhã às 18:00 · em 1 d 5 h"
        )
        XCTAssertEqual(UsageFormat.reset(now.addingTimeInterval(-1), now: now, calendar: calendar), "Reiniciando…")

        // 23:59:59.55 must read the same as the 00:00 reset next to it.
        let almostMidnight = thursday.addingTimeInterval(-0.45)
        XCTAssertEqual(
            UsageFormat.reset(almostMidnight, now: now, calendar: calendar),
            "Reinicia qui. às 00:00 · em 1 d 11 h"
        )
    }
}
