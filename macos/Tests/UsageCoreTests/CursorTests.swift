import XCTest
@testable import UsageCore

final class CursorTests: XCTestCase {
    override func setUp() {
        super.setUp()
        AppLanguage.current = .portuguese
    }

    /// Shape of a real legacy Pro+ answer (numbers changed): dates are epoch
    /// milliseconds as strings, money is cents.
    private let legacyMonthly = """
    {"billingCycleStart": "1791495353000", "billingCycleEnd": "1794173753000",
     "planUsage": {"totalSpend": 19058, "includedSpend": 7000, "bonusSpend": 12058, "limit": 7000,
                   "remainingBonus": false, "autoPercentUsed": 13.586666666666666, "apiPercentUsed": 100,
                   "totalPercentUsed": 15.525865580448064},
     "spendLimitUsage": {"limitType": "user"},
     "someFutureField": {"x": 1}}
    """

    private func parse(_ json: String, plan: String? = "Pro") throws -> ProviderSnapshot {
        try XCTUnwrap(CursorUsage.snapshot(fromResponse: Data(json.utf8), plan: plan))
    }

    func testLegacyPlanShowsTheMonthlyCycleAndBothPools() throws {
        let snapshot = try parse(legacyMonthly, plan: "Pro+")
        XCTAssertEqual(snapshot.provider, .cursor)
        XCTAssertEqual(snapshot.planName, "Pro+")
        XCTAssertEqual(snapshot.items.map(\.title), ["Mensal", "Mensal · Auto", "Mensal · API"])
        XCTAssertEqual(snapshot.items.map { UsageFormat.percent($0.percent) }, ["16%", "14%", "100%"])
        XCTAssertEqual(snapshot.items[2].severity, .critical)
        XCTAssertEqual(snapshot.items[0].group, .weekly)
        for item in snapshot.items {
            XCTAssertEqual(item.resetsAt, Date(timeIntervalSince1970: 1_794_173_753))
        }
        XCTAssertEqual(snapshot.notes, [.includedSpend(cents: 7000, limit: 7000, bonus: 12058)])
        XCTAssertEqual(snapshot.notes.map(\.text), ["US$ 70,00 usados de US$ 70,00 incluídos no plano + US$ 120,58 de bônus"])
    }

    func testTheWeeklyPoolOfTheNewPlansIsLabelledWeekly() throws {
        let snapshot = try parse("""
        {"billingCycleStart": "2026-10-19T00:00:00Z", "billingCycleEnd": "2026-10-26T00:00:00Z",
         "planUsage": {"totalPercentUsed": 7.09}}
        """)
        XCTAssertEqual(snapshot.items.map(\.title), ["Semanal"])
        XCTAssertEqual(snapshot.items.first?.percent, 7.09)
        XCTAssertEqual(snapshot.notes, [])
    }

    func testUnusedValuesLeftOutByProto3CountAsZero() throws {
        let snapshot = try parse(#"{"billingCycleStart": "1794096000000", "billingCycleEnd": "1796688000000", "planUsage": {"limit": 2000}}"#)
        XCTAssertEqual(snapshot.items.map(\.title), ["Mensal"])
        XCTAssertEqual(snapshot.items.first?.percent, 0)
        XCTAssertEqual(snapshot.notes.map(\.text), ["US$ 0,00 usados de US$ 20,00 incluídos no plano"])

        let oneLane = try parse(#"{"planUsage": {"apiPercentUsed": 40, "totalPercentUsed": 20}}"#)
        XCTAssertEqual(oneLane.items.map(\.title), ["Mensal", "Mensal · Auto", "Mensal · API"])
        XCTAssertEqual(oneLane.items.map(\.percent), [20, 0, 40])
    }

    func testWithoutTheHeadlineTheIncludedSpendGivesThePercentage() throws {
        let snapshot = try parse(#"{"plan_usage": {"included_spend": "500", "limit": "2000"}}"#)
        XCTAssertEqual(snapshot.items.first?.percent, 25)
    }

    func testOnDemandSpendingPrefersTheUsersOwnCap() throws {
        func notes(_ spend: String) throws -> [String] {
            try parse(#"{"planUsage": {}, "spendLimitUsage": \#(spend)}"#).notes.map(\.text)
        }
        XCTAssertEqual(try notes(#"{"individualUsed": 447, "individualLimit": 5000, "pooledLimit": 100000}"#), ["Sob demanda: US$ 4,47 de US$ 50,00"])
        XCTAssertEqual(try notes(#"{"pooledLimit": 500000, "pooledUsed": 123450, "limitType": "team"}"#), ["Sob demanda da equipe: US$ 1.234,50 de US$ 5.000,00"])
        XCTAssertEqual(try notes(#"{"totalSpend": 447}"#), ["Sob demanda: US$ 4,47"])
        XCTAssertEqual(try notes(#"{"limitType": "user"}"#), [])
    }

    func testAnswersWithNothingToShowAreNotASnapshot() {
        XCTAssertNil(CursorUsage.snapshot(fromResponse: Data("{}".utf8), plan: "Pro"))
        XCTAssertNil(CursorUsage.snapshot(fromResponse: Data("[]".utf8), plan: "Pro"))
        XCTAssertNil(CursorUsage.snapshot(fromResponse: Data("not json".utf8), plan: "Pro"))
    }

    func testNamesThePlanFromTheMembershipType() {
        XCTAssertEqual(CursorUsage.planName("pro"), "Pro")
        XCTAssertEqual(CursorUsage.planName("pro_plus"), "Pro+")
        XCTAssertEqual(CursorUsage.planName("ULTRA"), "Ultra")
        XCTAssertEqual(CursorUsage.planName("free"), "Hobby")
        XCTAssertEqual(CursorUsage.planName("free_trial"), "Pro Trial")
        XCTAssertEqual(CursorUsage.planName("enterprise"), "Enterprise")
        XCTAssertNil(CursorUsage.planName(" "))
        XCTAssertNil(CursorUsage.planName(nil))
    }

    func testTextsInEnglish() throws {
        AppLanguage.current = .english
        let snapshot = try parse(legacyMonthly)
        XCTAssertEqual(snapshot.items.map(\.title), ["Monthly", "Monthly · Auto", "Monthly · API"])
        XCTAssertEqual(snapshot.notes.map(\.text), ["$70.00 used of $70.00 included in the plan + $120.58 bonus"])
        XCTAssertEqual(SnapshotNote.onDemandSpend(cents: 447, limit: 5000).text, "On-demand: $4.47 of $50.00")
        XCTAssertEqual(MenuBarDisplay.weekly.label(for: .cursor), "Monthly")
        XCTAssertEqual(TabLimit.title(for: "monthly_scoped:API"), "Monthly · API")
        XCTAssertEqual(UsageError.notLoggedIn.message(for: .cursor), "No Cursor login found. Open Cursor and sign in.")
        XCTAssertEqual(UsageError.loginUnreadable.message(for: .cursor), "Could not read the Cursor login right now. Trying again soon.")
        XCTAssertEqual(
            UsageError.unauthorized(expired: true).message(for: .cursor),
            "The Cursor login expired. Open Cursor once to renew it and the meter picks up again."
        )
    }

    func testTheMenuBarAndTheTabFollowTheWholeCycle() throws {
        let snapshot = try parse(legacyMonthly)
        let segment = try XCTUnwrap(MenuBarSegment.make(provider: .cursor, display: .weekly, snapshot: snapshot, hasError: false))
        XCTAssertEqual(segment.lines, ["16%"])
        XCTAssertEqual(ProviderID.cursor.menuBarDisplayOptions, [.weekly, .hidden])
        XCTAssertEqual(ProviderID.cursor.defaultMenuBarDisplay, .weekly)
        XCTAssertEqual(TabSummary.make(snapshot: snapshot, hasError: false).text, "100%")
        XCTAssertEqual(TabSummary.make(snapshot: snapshot, hasError: false, limit: "monthly").text, "16%")
        XCTAssertEqual(TabLimit.options(items: snapshot.items, saved: TabLimit.highest), ["highest", "monthly", "monthly_scoped:Auto", "monthly_scoped:API"])
        XCTAssertEqual(ProviderID.cursor.initial, "Cu")
        XCTAssertEqual(ProviderID.cursor.displayName, "Cursor")
    }

    // MARK: - The login in the state database

    private var directory: URL!

    private func makeDatabase() throws -> URL {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent("state.vscdb")
        try StateDatabase.execute(at: url, sql: "CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);")
        return url
    }

    private func put(_ key: String, _ value: String, in url: URL) throws {
        let quote = { (text: String) in text.replacingOccurrences(of: "'", with: "''") }
        try StateDatabase.execute(at: url, sql: "INSERT INTO ItemTable VALUES ('\(quote(key))', '\(quote(value))');")
    }

    override func tearDown() {
        if let directory { try? FileManager.default.removeItem(at: directory) }
        super.tearDown()
    }

    func testReadsTheLoginFromTheStateDatabase() throws {
        let url = try makeDatabase()
        try put(CursorLogin.accessTokenKey, "header.payload.signature", in: url)
        try put(CursorLogin.membershipTypeKey, "pro", in: url)
        try put("cursorAuth/cachedEmail", "someone@example.com", in: url)

        let login = try CursorLogin.load(from: url)
        XCTAssertEqual(login.accessToken, "header.payload.signature")
        XCTAssertEqual(login.planName, "Pro")
        XCTAssertFalse(login.description.contains("header"))
        XCTAssertTrue(CursorProvider(database: url).isAvailable())
    }

    func testAcceptsAQuotedTokenAndNoMembership() throws {
        let url = try makeDatabase()
        try put(CursorLogin.accessTokenKey, "\"quoted.token.value\"", in: url)
        let login = try CursorLogin.load(from: url)
        XCTAssertEqual(login.accessToken, "quoted.token.value")
        XCTAssertNil(login.planName)
    }

    func testASignedOutCursorIsNotAvailable() throws {
        let url = try makeDatabase()
        try put("workbench.panel.width", "300", in: url)
        XCTAssertThrowsError(try CursorLogin.load(from: url)) { XCTAssertEqual($0 as? UsageError, .notLoggedIn) }
        XCTAssertFalse(CursorProvider(database: url).isAvailable())

        try put(CursorLogin.accessTokenKey, "", in: url)
        XCTAssertFalse(CursorProvider(database: url).isAvailable())

        let missing = directory.appendingPathComponent("missing/state.vscdb")
        XCTAssertFalse(CursorProvider(database: missing).isAvailable())
        XCTAssertThrowsError(try CursorLogin.load(from: missing)) { XCTAssertEqual($0 as? UsageError, .notLoggedIn) }
    }

    func testAFileThatIsNotTheStateDatabaseHasNoLogin() throws {
        _ = try makeDatabase()
        let text = directory.appendingPathComponent("text.vscdb")
        try Data("this is not a database, just some text that is long enough to have a header".utf8).write(to: text)
        XCTAssertThrowsError(try CursorLogin.load(from: text)) { XCTAssertEqual($0 as? UsageError, .notLoggedIn) }

        let other = directory.appendingPathComponent("other.vscdb")
        try StateDatabase.execute(at: other, sql: "CREATE TABLE other (x);")
        XCTAssertThrowsError(try CursorLogin.load(from: other)) { XCTAssertEqual($0 as? UsageError, .notLoggedIn) }
    }

    func testAnExpiredTokenIsReportedWithoutAskingTheServer() async throws {
        let url = try makeDatabase()
        // {"alg":"none"}.{"exp":1}.
        try put(CursorLogin.accessTokenKey, "eyJhbGciOiJub25lIn0.eyJleHAiOjF9.", in: url)
        do {
            _ = try await CursorProvider(database: url).fetch()
            XCTFail("expected an error")
        } catch {
            XCTAssertEqual(error as? UsageError, .unauthorized(expired: true))
        }
    }

    func testCacheKeepsThePlanFromTheLogin() throws {
        let cache = try ProviderCache(plan: "Pro", raw: Data(legacyMonthly.utf8)).encoded()
        let snapshot = try XCTUnwrap(CursorProvider().snapshot(fromCache: cache))
        XCTAssertEqual(snapshot.planName, "Pro")
        XCTAssertEqual(snapshot.items.count, 3)
    }
}
