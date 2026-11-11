import XCTest
@testable import UsageCore

final class TabLimitTests: XCTestCase {
    override func setUp() {
        AppLanguage.current = .english
    }

    private let items = [
        LimitItem(id: "0-session", kind: "session", percent: 64, severity: .normal, resetsAt: nil, group: .session),
        LimitItem(id: "1-weekly_all", kind: "weekly_all", percent: 41, severity: .normal, resetsAt: nil, group: .weekly),
        LimitItem(id: "2-weekly_scoped-Fable", kind: "weekly_scoped", scopeName: "Fable", percent: 83, severity: .warning, resetsAt: nil, group: .weekly),
    ]

    private func snapshot(_ items: [LimitItem]) -> ProviderSnapshot {
        ProviderSnapshot(provider: .claude, planName: "Max 5x", items: items)
    }

    func testDefaultsToTheFullestLimit() {
        let summary = TabSummary.make(snapshot: snapshot(items), hasError: false)
        XCTAssertEqual(summary.text, "83%")
        XCTAssertEqual(summary.severity, .warning)
    }

    func testShowsThePickedLimit() {
        XCTAssertEqual(TabSummary.make(snapshot: snapshot(items), hasError: false, limit: "weekly_all").text, "41%")
        XCTAssertEqual(TabSummary.make(snapshot: snapshot(items), hasError: false, limit: "session").text, "64%")
        XCTAssertEqual(TabSummary.make(snapshot: snapshot(items), hasError: false, limit: "weekly_scoped:Fable").text, "83%")
    }

    func testFallsBackToTheFullestWhenThePickIsGone() {
        let summary = TabSummary.make(snapshot: snapshot(Array(items.prefix(2))), hasError: false, limit: "weekly_scoped:Fable")
        XCTAssertEqual(summary.text, "64%")
        XCTAssertEqual(TabSummary.make(snapshot: nil, hasError: false, limit: "session").text, "–")
        XCTAssertEqual(TabSummary.make(snapshot: nil, hasError: true, limit: "session").text, "!")
    }

    func testListsTheFullestFirstThenEachLimit() {
        XCTAssertEqual(
            TabLimit.options(items: items, saved: TabLimit.highest),
            ["highest", "session", "weekly_all", "weekly_scoped:Fable"]
        )
        // A pick that is not in the current data stays listed, so it keeps its checkmark.
        XCTAssertEqual(
            TabLimit.options(items: Array(items.prefix(1)), saved: "weekly_scoped:Opus"),
            ["highest", "session", "weekly_scoped:Opus"]
        )
    }

    func testTitlesFollowTheLimit() {
        XCTAssertEqual(TabLimit.title(for: "highest"), "Highest usage")
        XCTAssertEqual(TabLimit.title(for: "session"), "Current session")
        XCTAssertEqual(TabLimit.title(for: "weekly_all"), "Weekly · all models")
        XCTAssertEqual(TabLimit.title(for: "weekly_scoped:Fable"), "Weekly · Fable")
        XCTAssertEqual(TabLimit.title(for: "premium_requests"), "Premium requests · month")
        XCTAssertEqual(TabLimit.title(for: "daily_scoped:2.5 Pro"), "Daily · 2.5 Pro")
        AppLanguage.current = .portuguese
        XCTAssertEqual(TabLimit.title(for: "highest"), "Maior uso")
        AppLanguage.current = .english
    }
}
