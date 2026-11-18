import XCTest
@testable import AIUsageBar
@testable import UsageCore

/// A provider that plays back a list of answers and counts how often it is asked.
private final class ScriptedProvider: UsageProvider, @unchecked Sendable {
    let id: ProviderID
    private let lock = NSLock()
    private var answers: [UsageError?]
    private var count = 0

    /// `nil` in `answers` is a successful update; the last answer repeats.
    init(id: ProviderID, answers: [UsageError?]) {
        self.id = id
        self.answers = answers
    }

    var calls: Int { lock.withLock { count } }

    /// Which login it would use now; changing it stands for signing in again.
    var login: String? {
        get { lock.withLock { currentLogin } }
        set { lock.withLock { currentLogin = newValue } }
    }
    private var currentLogin: String? = "login-1"

    func isAvailable() -> Bool { true }

    func loginIdentity() -> String? { login }

    func fetch() async throws -> ProviderFetchResult {
        let answer: UsageError? = lock.withLock {
            count += 1
            return answers.count > 1 ? answers.removeFirst() : answers.first ?? nil
        }
        if let answer { throw answer }
        let item = LimitItem(id: "session", kind: "session", percent: 40, severity: .normal, resetsAt: nil, group: .session)
        return ProviderFetchResult(snapshot: ProviderSnapshot(provider: id, planName: "Max", items: [item]), cache: Data())
    }

    func snapshot(fromCache data: Data) -> ProviderSnapshot? { nil }
}

@MainActor
final class RateLimitRefreshTests: XCTestCase {
    private var defaults: UserDefaults!
    private var suite: String!
    private var time = Date(timeIntervalSince1970: 1_800_000_000)

    override func setUp() async throws {
        suite = "RateLimitRefreshTests-\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suite)
        defaults.set("pt", forKey: "language")
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suite)
    }

    private func makeStore(_ providers: [ScriptedProvider], claudeCode: ClaudeCodeBridge? = nil) -> UsageStore {
        let store = UsageStore(providers: providers, defaults: defaults, claudeCode: claudeCode)
        store.clock = { [unowned self] in self.time }
        store.executablePath = "/Applications/AI Usage Bar.app/Contents/MacOS/AIUsageBar"
        return store
    }

    private var folder: URL!

    /// Claude Code's status line files, in a temporary folder.
    private func makeBridge() throws -> ClaudeCodeBridge {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("ClaudeCodeStore-\(UUID().uuidString)")
        addTeardownBlock { [folder] in try? FileManager.default.removeItem(at: folder!) }
        return ClaudeCodeBridge(directory: folder.appendingPathComponent("app"), settingsURL: folder.appendingPathComponent("claude/settings.json"))
    }

    /// What Claude Code hands its status line command, seen at `time`.
    private func record(_ bridge: ClaudeCodeBridge, session: Double, weekly: Double) {
        let limits: [String: Any] = [
            "five_hour": ["used_percentage": session, "resets_at": time.addingTimeInterval(3 * 3600).timeIntervalSince1970],
            "seven_day": ["used_percentage": weekly, "resets_at": time.addingTimeInterval(4 * 86_400).timeIntervalSince1970],
        ]
        bridge.record(statusLine: try! JSONSerialization.data(withJSONObject: ["rate_limits": limits]), now: time)
    }

    private func wait(_ seconds: TimeInterval) { time += seconds }

    private let tooMany = UsageError.rateLimited(retryAfter: nil)
    private let rejected = UsageError.unauthorized(expired: false)

    func testARejectedLoginWaitsInsteadOfAskingOnEveryTick() async {
        let claude = ScriptedProvider(id: .claude, answers: [rejected])
        let store = makeStore([claude])
        await store.refresh()

        let state = store.state(for: .claude)
        XCTAssertTrue(state.hasWarning, "a login the server refuses is an error, not a notice")
        XCTAssertEqual(state.backoff.until, time.addingTimeInterval(60))

        // The three-minute tick, the popover and the refresh button all hold off.
        wait(30)
        await store.refresh()
        await store.refreshIfStale()
        XCTAssertEqual(claude.calls, 1)

        // And the wait grows, instead of asking a refused login every three
        // minutes until the server answers 429 instead of 401.
        wait(31)
        await store.refresh()
        XCTAssertEqual(claude.calls, 2)
        XCTAssertEqual(store.state(for: .claude).backoff.until, time.addingTimeInterval(120))
    }

    func testSigningInAgainLiftsTheWait() async {
        let claude = ScriptedProvider(id: .claude, answers: [rejected, nil])
        let store = makeStore([claude])
        await store.refresh()
        XCTAssertEqual(claude.calls, 1)

        wait(10)
        await store.refresh()
        XCTAssertEqual(claude.calls, 1, "still the login the server refused")

        claude.login = "login-2"
        await store.refresh()
        XCTAssertEqual(claude.calls, 2, "a new login is asked at once, not after the wait")
        XCTAssertNil(store.state(for: .claude).error)
        XCTAssertEqual(store.state(for: .claude).backoff, RateLimitBackoff())
    }

    func testSigningInAgainLiftsARateLimitWaitToo() async {
        let claude = ScriptedProvider(id: .claude, answers: [tooMany, nil])
        let store = makeStore([claude])
        await store.refresh()
        wait(10)
        await store.refresh()
        XCTAssertEqual(claude.calls, 1)

        claude.login = "login-2"
        await store.refresh()
        XCTAssertEqual(claude.calls, 2, "the limit was earned by the previous login")
    }

    func testAnAgentThatCannotNameItsLoginStillWaits() async {
        let claude = ScriptedProvider(id: .claude, answers: [tooMany])
        claude.login = nil
        let store = makeStore([claude])
        await store.refresh()
        wait(10)
        await store.refresh()
        XCTAssertEqual(claude.calls, 1, "no login to compare means the wait stands")
    }

    func testKeepsTheNumbersAndWaitsAMinute() async {
        let claude = ScriptedProvider(id: .claude, answers: [nil, tooMany, nil])
        let store = makeStore([claude])
        await store.refresh()
        wait(180)
        await store.refresh()

        let state = store.state(for: .claude)
        XCTAssertTrue(state.isRateLimited)
        XCTAssertFalse(state.hasWarning, "a rate limit is a notice, not an error")
        XCTAssertFalse(store.hasError)
        XCTAssertEqual(state.snapshot?.items.first?.percent, 40, "the last numbers stay on screen")
        XCTAssertEqual(store.menuBarSegments.first?.lines, ["40%"])
        XCTAssertEqual(state.message(for: .claude, now: time), "Muitas atualizações seguidas. Aguarde um pouquinho: tento de novo em 1 min.")

        // Clicking refresh, opening the popover or waking the Mac during the wait does not ask the server.
        wait(30)
        await store.refresh()
        await store.refreshIfStale()
        XCTAssertEqual(claude.calls, 2)

        wait(31)
        await store.refresh()
        XCTAssertEqual(claude.calls, 3)
        XCTAssertNil(store.state(for: .claude).error)
    }

    func testRepeatedLimitsWaitLongerUntilAnUpdateWorks() async {
        let claude = ScriptedProvider(id: .claude, answers: [tooMany, tooMany, tooMany, nil, tooMany])
        let store = makeStore([claude])
        var waits: [TimeInterval] = []
        for _ in 0..<3 {
            await store.refresh()
            let until = store.state(for: .claude).backoff.until ?? time
            waits.append(until.timeIntervalSince(time))
            time = until
        }
        XCTAssertEqual(waits, [60, 120, 240])

        await store.refresh()
        XCTAssertEqual(store.state(for: .claude).backoff, RateLimitBackoff(), "a successful update starts over")
        wait(180)
        await store.refresh()
        XCTAssertEqual(store.state(for: .claude).backoff.until, time.addingTimeInterval(60))
    }

    func testRespectsRetryAfter() async {
        let claude = ScriptedProvider(id: .claude, answers: [.rateLimited(retryAfter: 300)])
        let store = makeStore([claude])
        await store.refresh()
        XCTAssertEqual(store.state(for: .claude).backoff.until, time.addingTimeInterval(300))
        XCTAssertEqual(store.state(for: .claude).message(for: .claude, now: time)?.suffix(9), "em 5 min.")
    }

    func testOtherAgentsKeepRefreshingDuringTheWait() async {
        let claude = ScriptedProvider(id: .claude, answers: [tooMany])
        let codex = ScriptedProvider(id: .codex, answers: [nil])
        let store = makeStore([claude, codex])
        await store.refresh()
        wait(10)
        await store.refresh()
        XCTAssertEqual(claude.calls, 1)
        XCTAssertEqual(codex.calls, 2)
    }

    func testOpeningThePopoverAsksOnlyAgentsNotAskedInTheLastMinute() async {
        let claude = ScriptedProvider(id: .claude, answers: [nil])
        let codex = ScriptedProvider(id: .codex, answers: [.network("offline")])
        let store = makeStore([claude, codex])
        await store.refresh()
        wait(30)
        await store.refreshIfStale()
        XCTAssertEqual([claude.calls, codex.calls], [1, 1], "an error alone no longer forces every agent")
        wait(30)
        await store.refreshIfStale()
        XCTAssertEqual([claude.calls, codex.calls], [2, 2])
    }

    // MARK: Claude Code's numbers

    func testShowsClaudeCodesNumbersWhenTheEndpointRefusesFromTheStart() async throws {
        let bridge = try makeBridge()
        let store = makeStore([ScriptedProvider(id: .claude, answers: [tooMany])], claudeCode: bridge)
        try store.setReadsClaudeCode(true)
        record(bridge, session: 4, weekly: 19)
        await store.refresh()
        store.loadClaudeCode()

        let state = store.state(for: .claude)
        XCTAssertTrue(state.isRateLimited)
        XCTAssertEqual(state.snapshot?.items.map(\.percent), [4, 19])
        XCTAssertEqual(state.snapshot?.notes, [.fromClaudeCode])
        XCTAssertEqual(state.lastUpdated, time)
        XCTAssertEqual(store.visibleProviders, [.claude], "Claude shows up with Claude Code's numbers alone")
        XCTAssertEqual(store.menuBarSegments.first?.lines, ["4%", "19%"])
        XCTAssertEqual(state.message(for: .claude, now: time), "Muitas atualizações seguidas. Aguarde um pouquinho: tento de novo em 1 min.")
    }

    func testSuggestsTheSwitchOnlyWhenThereIsNothingToShow() async throws {
        let bridge = try makeBridge()
        let store = makeStore([ScriptedProvider(id: .claude, answers: [tooMany])], claudeCode: bridge)
        await store.refresh()
        XCTAssertEqual(
            store.state(for: .claude).message(for: .claude, now: time),
            "Muitas atualizações seguidas. Aguarde um pouquinho: tento de novo em 1 min. " + ClaudeCodeBridge.hint
        )
        XCTAssertNil(makeStore([], claudeCode: nil).state(for: .claude).message(for: .claude), "no switch, no hint")

        try store.setReadsClaudeCode(true)
        XCTAssertTrue(store.readsClaudeCode)
        XCTAssertTrue(bridge.isInstalled())
        XCTAssertFalse(store.state(for: .claude).message(for: .claude, now: time)?.contains("⚙") ?? true)

        try store.setReadsClaudeCode(false)
        XCTAssertFalse(store.readsClaudeCode)
        XCTAssertFalse(bridge.isInstalled())
    }

    func testNewerClaudeCodeNumbersRaiseTheEndpoints() async throws {
        let bridge = try makeBridge()
        let store = makeStore([ScriptedProvider(id: .claude, answers: [nil])], claudeCode: bridge)
        await store.refresh()
        let fetchedAt = time
        XCTAssertEqual(store.state(for: .claude).snapshot?.items.first?.percent, 40)

        wait(60)
        record(bridge, session: 55, weekly: 19)
        store.loadClaudeCode(force: true)
        let state = store.state(for: .claude)
        XCTAssertEqual(state.snapshot?.items.first?.percent, 55)
        XCTAssertEqual(state.lastUpdated, time)
        XCTAssertEqual(state.fetchedAt, fetchedAt)
        XCTAssertEqual(state.fetched?.items.first?.percent, 40, "what the endpoint said is kept apart")
    }
}
