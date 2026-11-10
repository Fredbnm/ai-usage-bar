import XCTest
@testable import AIUsageBar
@testable import UsageCore

/// A provider with a fixed login state and a fixed answer.
private struct FakeProvider: UsageProvider {
    let id: ProviderID
    let signedIn: Bool
    let works: Bool

    func isAvailable() -> Bool { signedIn }

    func fetch() async throws -> ProviderFetchResult {
        guard works else { throw UsageError.http(404) }
        let item = LimitItem(id: "weekly", kind: "weekly", percent: 40, severity: .normal, resetsAt: nil, group: .weekly)
        return ProviderFetchResult(snapshot: ProviderSnapshot(provider: id, planName: "Pro", items: [item]), cache: Data())
    }

    func snapshot(fromCache data: Data) -> ProviderSnapshot? { nil }
}

@MainActor
final class AgentVisibilityTests: XCTestCase {
    private var defaults: UserDefaults!
    private var suite: String!

    override func setUp() async throws {
        suite = "AgentVisibilityTests-\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suite)
        defaults.set("en", forKey: "language")
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suite)
    }

    /// Claude works; Copilot is signed in (say, through the gh CLI) but its usage does not load; Codex has no login.
    private func makeStore() -> UsageStore {
        UsageStore(providers: [
            FakeProvider(id: .claude, signedIn: true, works: true),
            FakeProvider(id: .copilot, signedIn: true, works: false),
            FakeProvider(id: .codex, signedIn: false, works: true),
        ], defaults: defaults)
    }

    func testShowsOnlyAgentsWhoseUsageLoads() async {
        let store = makeStore()
        XCTAssertEqual(store.visibleProviders, [], "nothing is shown before the first answer")
        await store.refresh(force: true)
        XCTAssertEqual(store.visibleProviders, [.claude])
        XCTAssertFalse(store.hasError, "an agent that never worked does not raise a warning")
    }

    func testAgentsTabExplainsEachState() async {
        let store = makeStore()
        await store.refresh(force: true)
        let rows = Dictionary(uniqueKeysWithValues: store.agentRows.map { ($0.id, $0) })
        XCTAssertEqual(rows[.claude]?.status, .connected)
        XCTAssertEqual(rows[.claude]?.hasData, true)
        XCTAssertEqual(rows[.claude]?.isShown, true)
        XCTAssertEqual(rows[.copilot]?.status, .connected)
        XCTAssertEqual(rows[.copilot]?.hasData, false)
        XCTAssertEqual(rows[.copilot]?.isShown, false)
        XCTAssertEqual(rows[.codex]?.status, .needsLogin)
    }

    func testTickingShowKeepsAnAgentThatFails() async {
        let store = makeStore()
        await store.refresh(force: true)
        store.setProviderHidden(.copilot, false)
        XCTAssertEqual(store.visibleProviders, [.claude, .copilot])
        await store.refresh(force: true)
        XCTAssertTrue(store.hasError, "a shown agent that fails shows its warning")

        // The choice survives a restart.
        let restarted = makeStore()
        XCTAssertEqual(restarted.shownProviders, ["copilot"])

        store.setProviderHidden(.copilot, true)
        store.setProviderHidden(.claude, true)
        XCTAssertEqual(store.visibleProviders, [])
    }

    func testLayoutAndTabLimitAreSaved() {
        let store = makeStore()
        XCTAssertEqual(store.popupLayout, .tabs)
        XCTAssertEqual(store.tabLimit(for: .claude), TabLimit.highest)
        store.popupLayout = .list
        store.setTabLimit("weekly_all", for: .claude)

        let restarted = makeStore()
        XCTAssertEqual(restarted.popupLayout, .list)
        XCTAssertEqual(restarted.tabLimit(for: .claude), "weekly_all")
        XCTAssertFalse(restarted.showTabStrip)
    }
}
