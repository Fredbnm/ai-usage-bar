import XCTest
@testable import UsageCore

final class UsageCoreTests: XCTestCase {
    override func setUp() {
        super.setUp()
        AppLanguage.current = .portuguese
    }

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

    func testMenuBarSegments() throws {
        let claude = try XCTUnwrap(ClaudeProvider.snapshot(fromResponse: Data(sample.utf8), plan: "Max 5x"))

        // Both: session on top (drives the ring), weekly below.
        let both = try XCTUnwrap(MenuBarSegment.make(provider: .claude, display: .both, snapshot: claude, hasError: false))
        XCTAssertEqual(both.lines, ["42%", "30%"])
        XCTAssertEqual(both.ringPercent, 42)

        let session = try XCTUnwrap(MenuBarSegment.make(provider: .claude, display: .session, snapshot: claude, hasError: false))
        XCTAssertEqual(session.lines, ["42%"])
        XCTAssertEqual(session.ringPercent, 42)

        // Weekly means "all models", not the per-model Fable limit (91%).
        let weekly = try XCTUnwrap(MenuBarSegment.make(provider: .claude, display: .weekly, snapshot: claude, hasError: false))
        XCTAssertEqual(weekly.lines, ["30%"])
        XCTAssertEqual(weekly.ringPercent, 30)
        XCTAssertEqual(weekly.ringSeverity, .normal)

        XCTAssertNil(MenuBarSegment.make(provider: .claude, display: .hidden, snapshot: claude, hasError: false))
    }

    func testMenuBarSegmentWithoutData() throws {
        let loading = try XCTUnwrap(MenuBarSegment.make(provider: .grokBot, display: .weekly, snapshot: nil, hasError: false))
        XCTAssertEqual(loading.lines, ["–"])
        XCTAssertNil(loading.ringPercent)
        let failed = try XCTUnwrap(MenuBarSegment.make(provider: .grokBot, display: .weekly, snapshot: nil, hasError: true))
        XCTAssertEqual(failed.lines, ["!"])
    }

    func testMenuBarSegmentFallsBackWhenLimitMissing() throws {
        // A service with only a weekly limit asked to show "both" shows just that.
        let grok = ProviderSnapshot(provider: .grokBot, planName: nil, items: [
            LimitItem(id: "g", kind: "weekly", percent: 95, severity: .critical, resetsAt: nil, group: .weekly),
        ])
        let segment = try XCTUnwrap(MenuBarSegment.make(provider: .grokBot, display: .both, snapshot: grok, hasError: false))
        XCTAssertEqual(segment.lines, ["95%"])
        XCTAssertEqual(segment.ringSeverity, .critical)
        let sessionOnly = try XCTUnwrap(MenuBarSegment.make(provider: .grokBot, display: .session, snapshot: grok, hasError: false))
        XCTAssertEqual(sessionOnly.lines, ["95%"])
    }

    func testMenuBarStyles() {
        XCTAssertEqual(MenuBarStyle.default, .icon)
        XCTAssertEqual(MenuBarStyle.allCases, [.icon, .percent])
        XCTAssertEqual(MenuBarStyle(rawValue: "percent"), .percent)
        XCTAssertEqual(MenuBarStyle.percent.label, "Só o percentual")
    }

    func testMenuBarOptionsPerService() {
        XCTAssertEqual(ProviderID.claude.menuBarDisplayOptions, [.session, .weekly, .both, .hidden])
        XCTAssertEqual(ProviderID.grokBot.menuBarDisplayOptions, [.weekly, .hidden])
        XCTAssertEqual(MenuBarDisplay.weekly.label(for: .claude), "Só a semanal")
        XCTAssertEqual(MenuBarDisplay.weekly.label(for: .grokBot), "Semanal")
        XCTAssertEqual(ProviderID.claude.defaultMenuBarDisplay, .both)
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
        let nextMonth = calendar.date(from: DateComponents(year: 2026, month: 12, day: 8, hour: 0, minute: 0))!
        XCTAssertEqual(
            UsageFormat.reset(nextMonth, now: now, calendar: calendar),
            "Reinicia dia 08/12 às 00:00 · em 27 d 11 h"
        )
        XCTAssertEqual(UsageFormat.reset(now.addingTimeInterval(-1), now: now, calendar: calendar), "Reiniciando…")

        // 23:59:59.55 must read the same as the 00:00 reset next to it.
        let almostMidnight = thursday.addingTimeInterval(-0.45)
        XCTAssertEqual(
            UsageFormat.reset(almostMidnight, now: now, calendar: calendar),
            "Reinicia qui. às 00:00 · em 1 d 11 h"
        )
    }

    func testTitlesFollowTheLanguage() throws {
        let response = try decodeSample()
        AppLanguage.current = .english
        XCTAssertEqual(response.items.map(\.title), ["Current session", "Weekly · all models", "Weekly · Fable"])
        XCTAssertEqual(response.breakdownItems.map(\.name), ["Claude Code", "Chat"])
        XCTAssertEqual(MenuBarDisplay.both.label(for: .claude), "Session + weekly")
        XCTAssertEqual(MenuBarStyle.percent.label, "Percentage only")
        AppLanguage.current = .portuguese
        XCTAssertEqual(response.items.first?.title, "Sessão atual")
    }

    func testEnglishResetAndUpdatedText() {
        AppLanguage.current = .english
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "America/Sao_Paulo")!
        let now = calendar.date(from: DateComponents(year: 2026, month: 11, day: 10, hour: 12, minute: 34))!

        XCTAssertEqual(UsageFormat.reset(now.addingTimeInterval(76 * 60), now: now, calendar: calendar), "Resets in 1 h 16 min · 1:50 PM")
        let thursday = calendar.date(from: DateComponents(year: 2026, month: 11, day: 12, hour: 0, minute: 0))!
        XCTAssertEqual(UsageFormat.reset(thursday, now: now, calendar: calendar), "Resets Thu at 12:00 AM · in 1 d 11 h")
        let tomorrow = calendar.date(from: DateComponents(year: 2026, month: 11, day: 11, hour: 18, minute: 0))!
        XCTAssertEqual(UsageFormat.reset(tomorrow, now: now, calendar: calendar), "Resets tomorrow at 6:00 PM · in 1 d 5 h")
        let nextMonth = calendar.date(from: DateComponents(year: 2026, month: 12, day: 8, hour: 0, minute: 0))!
        XCTAssertEqual(UsageFormat.reset(nextMonth, now: now, calendar: calendar), "Resets Dec 8 at 12:00 AM · in 27 d 11 h")
        XCTAssertEqual(UsageFormat.updated(now.addingTimeInterval(-10), now: now, calendar: calendar), "Updated just now")
        XCTAssertEqual(UsageFormat.updated(now.addingTimeInterval(-600), now: now, calendar: calendar), "Updated 10 min ago")
        XCTAssertEqual(UsageFormat.shortDate(thursday, calendar: calendar), "Nov 12")
        XCTAssertEqual(UsageError.network("x").message(for: .claude), "No connection. Trying again soon.")
    }
}
