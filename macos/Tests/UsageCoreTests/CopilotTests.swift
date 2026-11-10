import XCTest
@testable import UsageCore

final class CopilotTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        AppLanguage.current = .english
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("copilot-tests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    // Same shape as docs/sample-copilot-usage.json.
    private let sample = Data("""
    {
      "copilot_plan": "individual_pro",
      "quota_reset_date": "2026-12-01",
      "quota_snapshots": {
        "premium_interactions": { "entitlement": 300, "remaining": 117, "percent_remaining": 39.0, "unlimited": false },
        "chat": { "unlimited": true, "percent_remaining": 100.0 },
        "completions": { "unlimited": true, "percent_remaining": 100.0 }
      }
    }
    """.utf8)

    private func write(_ text: String, as name: String) throws {
        try Data(text.utf8).write(to: directory.appendingPathComponent(name))
    }

    func testReadsTheTokenFromAppsJson() throws {
        try write(#"{"github.com:Iv1.abc":{"user":"octocat","oauth_token":"ghu_secret"}}"#, as: "apps.json")
        let found = CopilotCredentialsStore.find(directory: directory, environment: [:])
        XCTAssertEqual(found?.token, "ghu_secret")
        XCTAssertEqual(found?.source, "apps.json")
    }

    func testFallsBackToHostsJsonThenEnvironment() throws {
        try write(#"{"github.com":{"oauth_token":"ghu_hosts"}}"#, as: "hosts.json")
        XCTAssertEqual(CopilotCredentialsStore.find(directory: directory, environment: [:])?.token, "ghu_hosts")

        try FileManager.default.removeItem(at: directory.appendingPathComponent("hosts.json"))
        XCTAssertEqual(
            CopilotCredentialsStore.find(directory: directory, environment: ["GITHUB_TOKEN": "ghp_env"])?.token,
            "ghp_env"
        )
        XCTAssertNil(CopilotCredentialsStore.find(directory: directory, environment: [:]))
    }

    func testIgnoresEnterpriseHostsAndBrokenFiles() throws {
        XCTAssertNil(CopilotCredentialsStore.parseHosts(Data(#"{"ghe.example.com":{"oauth_token":"x"}}"#.utf8)))
        XCTAssertNil(CopilotCredentialsStore.parseHosts(Data("not json".utf8)))
        try write("{ broken", as: "apps.json")
        XCTAssertNil(CopilotCredentialsStore.find(directory: directory, environment: [:]))
    }

    func testNeverPrintsTheToken() {
        XCTAssertFalse("\(CopilotCredentials(token: "ghu_secret", source: "apps.json"))".contains("ghu_secret"))
    }

    func testParsesPremiumRequestsAndSkipsUnlimitedQuotas() throws {
        let snapshot = try XCTUnwrap(CopilotUsage.snapshot(fromResponse: sample))
        XCTAssertEqual(snapshot.provider, .copilot)
        XCTAssertEqual(snapshot.planName, "Pro")
        XCTAssertEqual(snapshot.items.count, 1)
        let item = snapshot.items[0]
        XCTAssertEqual(item.kind, "premium_requests")
        XCTAssertEqual(item.percent, 61, accuracy: 0.001)
        XCTAssertEqual(item.group, .weekly)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        XCTAssertEqual(calendar.dateComponents([.year, .month, .day], from: try XCTUnwrap(item.resetsAt)),
                       DateComponents(year: 2026, month: 12, day: 1))
    }

    func testUsesEntitlementWhenThePercentIsMissing() throws {
        let data = Data(#"{"quota_snapshots":{"premium_interactions":{"entitlement":300,"remaining":75}}}"#.utf8)
        XCTAssertEqual(try XCTUnwrap(CopilotUsage.snapshot(fromResponse: data)).items[0].percent, 75, accuracy: 0.001)
    }

    func testReadsTheFreePlanQuotas() throws {
        let data = Data("""
        {"copilot_plan":"free","monthly_quotas":{"chat":50,"completions":2000},
         "limited_user_quotas":{"chat":40,"completions":1500},"limited_user_reset_date":"2026-12-01"}
        """.utf8)
        let snapshot = try XCTUnwrap(CopilotUsage.snapshot(fromResponse: data))
        XCTAssertEqual(snapshot.planName, "Free")
        XCTAssertEqual(snapshot.items.first { $0.kind == "chat" }?.percent ?? -1, 20, accuracy: 0.001)
        XCTAssertEqual(snapshot.items.first { $0.kind == "completions" }?.percent ?? -1, 25, accuracy: 0.001)
    }

    func testNamesThePlans() {
        XCTAssertEqual(CopilotUsage.planName("individual_pro"), "Pro")
        XCTAssertEqual(CopilotUsage.planName("individual_pro_plus"), "Pro+")
        XCTAssertEqual(CopilotUsage.planName("business"), "Business")
        XCTAssertNil(CopilotUsage.planName(" "))
    }

    func testUnknownShapesGiveNoSnapshot() {
        XCTAssertNil(CopilotUsage.snapshot(fromResponse: Data("[]".utf8)))
        XCTAssertNil(CopilotUsage.snapshot(fromResponse: Data(#"{"copilot_plan":"business"}"#.utf8)))
        XCTAssertNil(CopilotUsage.snapshot(fromResponse: Data("garbage".utf8)))
    }

    func testCacheRoundTrips() throws {
        let provider = CopilotProvider(directory: directory, environment: [:], useGhCLI: false)
        let cache = try ProviderCache(plan: "Pro", raw: sample).encoded()
        XCTAssertEqual(try XCTUnwrap(provider.snapshot(fromCache: cache)).items[0].percent, 61, accuracy: 0.001)
        XCTAssertNil(provider.snapshot(fromCache: Data([1, 2, 3])))
    }

    func testWithoutALoginItIsHiddenAndFetchReportsNotLoggedIn() async {
        let provider = CopilotProvider(directory: directory, environment: [:], useGhCLI: false)
        XCTAssertFalse(provider.isAvailable())
        XCTAssertEqual(provider.detect(), .needsLogin)
        do {
            _ = try await provider.fetch()
            XCTFail("expected notLoggedIn")
        } catch {
            XCTAssertEqual(error as? UsageError, .notLoggedIn)
            XCTAssertTrue(UsageError.notLoggedIn.message(for: .copilot).contains("gh auth login"))
        }
    }

    func testDetectSeesALogin() throws {
        let provider = CopilotProvider(directory: directory, environment: [:], useGhCLI: false)
        try write(#"{"github.com":{"oauth_token":"ghu_x"}}"#, as: "hosts.json")
        XCTAssertEqual(provider.detect(), .connected)
    }

    func testGhLookupIsCached() {
        let cache = GhTokenCache()
        var calls = 0
        let load: () -> CopilotCredentials? = { calls += 1; return CopilotCredentials(token: "t", source: "gh") }
        let start = Date()
        _ = cache.lookup(now: start, load: load)
        _ = cache.lookup(now: start.addingTimeInterval(120), load: load)
        XCTAssertEqual(calls, 1)
        _ = cache.lookup(now: start.addingTimeInterval(700), load: load)
        XCTAssertEqual(calls, 2)
    }

    func testTabSummaryShowsTheFullestLimitOrAWarning() throws {
        let data = Data(#"{"quota_snapshots":{"premium_interactions":{"percent_remaining":5},"chat":{"percent_remaining":60}}}"#.utf8)
        let snapshot = try XCTUnwrap(CopilotUsage.snapshot(fromResponse: data))
        let summary = TabSummary.make(snapshot: snapshot, hasError: false)
        XCTAssertEqual(summary.text, "95%")
        XCTAssertEqual(summary.severity, .critical)
        XCTAssertFalse(summary.isWarning)

        XCTAssertEqual(TabSummary.make(snapshot: nil, hasError: true).text, "!")
        XCTAssertEqual(TabSummary.make(snapshot: nil, hasError: false).text, "–")
        XCTAssertTrue(TabSummary.make(snapshot: snapshot, hasError: true).isWarning)
    }

    func testResolvesTheTabToShow() {
        let connected: [ProviderID] = [.claude, .copilot]
        XCTAssertEqual(PopupTabs.resolve(requested: "claude", connected: connected, onboardingDone: false), PopupTabs.agentsKey)
        XCTAssertEqual(PopupTabs.resolve(requested: "claude", connected: [], onboardingDone: true), PopupTabs.agentsKey)
        XCTAssertEqual(PopupTabs.resolve(requested: "copilot", connected: connected, onboardingDone: true), "copilot")
        XCTAssertEqual(PopupTabs.resolve(requested: "grokBot", connected: connected, onboardingDone: true), "claude")
        XCTAssertEqual(PopupTabs.resolve(requested: nil, connected: connected, onboardingDone: true), "claude")
        XCTAssertEqual(PopupTabs.resolve(requested: PopupTabs.agentsKey, connected: connected, onboardingDone: true), PopupTabs.agentsKey)
    }

    func testTheStripNeedsTwoAgentsAndStepsWrap() {
        XCTAssertFalse(PopupTabs.showStrip(connectedCount: 1))
        XCTAssertTrue(PopupTabs.showStrip(connectedCount: 2))
        let keys = ["claude", "copilot", PopupTabs.agentsKey]
        XCTAssertEqual(PopupTabs.step(keys: keys, current: "claude", direction: 1), "copilot")
        XCTAssertEqual(PopupTabs.step(keys: keys, current: PopupTabs.agentsKey, direction: 1), "claude")
        XCTAssertEqual(PopupTabs.step(keys: keys, current: "claude", direction: -1), PopupTabs.agentsKey)
    }

    func testEveryProviderHasNamesAndOptions() {
        for id in ProviderID.allCases {
            XCTAssertFalse(id.displayName.isEmpty)
            XCTAssertFalse(id.shortName.isEmpty)
            XCTAssertFalse(id.setupHint.isEmpty)
            XCTAssertTrue(id.menuBarDisplayOptions.contains(id.defaultMenuBarDisplay))
        }
        XCTAssertEqual(MenuBarDisplay.weekly.label(for: .copilot), "Monthly")
    }
}
