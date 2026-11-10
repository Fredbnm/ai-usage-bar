import XCTest
@testable import UsageCore

final class CodexGeminiTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        AppLanguage.current = .english
        directory = FileManager.default.temporaryDirectory.appendingPathComponent("agent-tests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    @discardableResult
    private func write(_ text: String, as name: String) throws -> URL {
        let url = directory.appendingPathComponent(name)
        try Data(text.utf8).write(to: url)
        return url
    }

    // MARK: Codex

    // Same shape as docs/sample-codex-usage.json.
    private let codexSample = Data("""
    {
      "plan_type": "plus",
      "rate_limit": {
        "limit_reached": false,
        "primary_window":   { "used_percent": 23, "limit_window_seconds": 18000,  "reset_at": 1795000000 },
        "secondary_window": { "used_percent": 48, "limit_window_seconds": 604800, "reset_after_seconds": 3600 }
      }
    }
    """.utf8)

    func testReadsTheCodexLogin() throws {
        let path = try write(#"{"OPENAI_API_KEY":null,"tokens":{"access_token":"eyJ.secret","refresh_token":"r","account_id":"acc-1"}}"#, as: "auth.json")
        let credentials = try XCTUnwrap(CodexCredentialsStore.load(from: path))
        XCTAssertEqual(credentials.accessToken, "eyJ.secret")
        XCTAssertEqual(credentials.accountId, "acc-1")
        XCTAssertFalse("\(credentials)".contains("secret"))

        // An API-key-only login has no ChatGPT plan; a missing or broken file is no login.
        XCTAssertNil(CodexCredentialsStore.load(from: try write(#"{"OPENAI_API_KEY":"sk-x"}"#, as: "key.json")))
        XCTAssertNil(CodexCredentialsStore.load(from: try write("{ nope", as: "broken.json")))
        XCTAssertNil(CodexCredentialsStore.load(from: directory.appendingPathComponent("missing.json")))
    }

    func testParsesTheSessionAndWeeklyWindows() throws {
        let now = Date(timeIntervalSince1970: 1_700_000_000)
        let snapshot = try XCTUnwrap(CodexUsage.snapshot(fromResponse: codexSample, now: now))
        XCTAssertEqual(snapshot.provider, .codex)
        XCTAssertEqual(snapshot.planName, "Plus")

        let session = snapshot.items[0]
        XCTAssertEqual(session.kind, "session")
        XCTAssertEqual(session.group, .session)
        XCTAssertEqual(session.percent, 23, accuracy: 0.001)
        XCTAssertEqual(session.resetsAt, Date(timeIntervalSince1970: 1_795_000_000))

        let weekly = snapshot.items[1]
        XCTAssertEqual(weekly.kind, "weekly")
        XCTAssertEqual(weekly.group, .weekly)
        XCTAssertEqual(weekly.resetsAt, now.addingTimeInterval(3600))   // from reset_after_seconds
        XCTAssertEqual(session.title, "Current session")
        XCTAssertEqual(weekly.title, "Weekly")
    }

    func testACodexWindowIsToldApartByItsLength() throws {
        // Only a weekly window (some plans): it must not be called the session.
        let onlyWeekly = Data(#"{"rate_limit":{"primary_window":{"used_percent":10,"limit_window_seconds":604800}}}"#.utf8)
        let snapshot = try XCTUnwrap(CodexUsage.snapshot(fromResponse: onlyWeekly))
        XCTAssertEqual(snapshot.items.count, 1)
        XCTAssertEqual(snapshot.items[0].kind, "weekly")

        // Without lengths: first = session, second = weekly. A reached limit shows as critical.
        let plain = try XCTUnwrap(CodexUsage.snapshot(fromResponse: Data(
            #"{"rate_limit":{"limit_reached":true,"primary_window":{"used_percent":100},"secondary_window":{"used_percent":60}}}"#.utf8
        )))
        XCTAssertEqual(plain.items.map(\.kind), ["session", "weekly"])
        XCTAssertEqual(plain.items[0].severity, .critical)
        XCTAssertEqual(plain.items[1].severity, .normal)
    }

    func testUnknownCodexShapesGiveNoSnapshot() {
        XCTAssertNil(CodexUsage.snapshot(fromResponse: Data("[]".utf8)))
        XCTAssertNil(CodexUsage.snapshot(fromResponse: Data(#"{"plan_type":"plus"}"#.utf8)))
        XCTAssertNil(CodexUsage.snapshot(fromResponse: Data("garbage".utf8)))
    }

    func testCodexWithoutALoginIsHiddenAndAnExpiredOneSaysSo() async throws {
        let missing = CodexProvider(authURL: directory.appendingPathComponent("none.json"))
        XCTAssertFalse(missing.isAvailable())
        XCTAssertEqual(missing.detect(), .needsLogin)
        do {
            _ = try await missing.fetch()
            XCTFail("expected notLoggedIn")
        } catch {
            XCTAssertEqual(error as? UsageError, .notLoggedIn)
        }

        // A JWT whose exp is in the past: no request is made (this test has no network).
        let exp = Int(Date().addingTimeInterval(-3600).timeIntervalSince1970)
        let payload = Data("{\"exp\":\(exp)}".utf8).base64EncodedString()
            .replacingOccurrences(of: "=", with: "")
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
        let old = try write("{\"tokens\":{\"access_token\":\"h.\(payload).s\"}}", as: "old.json")
        let expired = CodexProvider(authURL: old)
        XCTAssertTrue(expired.isAvailable())
        do {
            _ = try await expired.fetch()
            XCTFail("expected an expired login")
        } catch {
            XCTAssertEqual(error as? UsageError, .unauthorized(expired: true))
        }
        XCTAssertTrue(UsageError.notLoggedIn.message(for: .codex).contains("codex login"))
    }

    func testCodexCacheRoundTrips() throws {
        let cache = try ProviderCache(plan: "Plus", raw: codexSample).encoded()
        XCTAssertEqual(try XCTUnwrap(CodexProvider().snapshot(fromCache: cache)).items.count, 2)
        XCTAssertNil(CodexProvider().snapshot(fromCache: Data([1, 2, 3])))
    }

    // MARK: Gemini

    // Same shape as docs/sample-gemini-usage.json, with two buckets for one model.
    private let geminiSample = Data("""
    {"buckets":[
      {"modelId":"gemini-2.5-flash","tokenType":"REQUESTS","remainingFraction":0.91,"resetTime":"2026-11-11T08:00:00Z"},
      {"modelId":"gemini-2.5-pro","tokenType":"REQUESTS","remainingFraction":0.62,"resetTime":"2026-11-11T08:00:00Z"},
      {"modelId":"gemini-2.5-pro","tokenType":"INPUT_TOKENS","remainingFraction":0.80,"resetTime":"2026-11-11T08:00:00Z"},
      {"modelId":"gemini-2.5-flash-lite","remainingFraction":1.0}
    ]}
    """.utf8)

    func testReadsTheGeminiLogin() throws {
        let path = try write(#"{"access_token":"ya29.secret","refresh_token":"1//r","expiry_date":4102444800000,"id_token":"x"}"#, as: "oauth_creds.json")
        let credentials = try XCTUnwrap(GeminiCredentialsStore.load(from: path))
        XCTAssertEqual(credentials.accessToken, "ya29.secret")
        XCTAssertFalse(credentials.isExpired)
        XCTAssertFalse("\(credentials)".contains("secret"))

        let old = try XCTUnwrap(GeminiCredentialsStore.load(from: try write(#"{"access_token":"t","expiry_date":1000}"#, as: "old.json")))
        XCTAssertTrue(old.isExpired)
        XCTAssertNil(GeminiCredentialsStore.load(from: try write("{}", as: "empty.json")))
        XCTAssertNil(GeminiCredentialsStore.load(from: directory.appendingPathComponent("missing.json")))
    }

    func testParsesGeminiQuotaPerModelProFirst() throws {
        let snapshot = try XCTUnwrap(GeminiUsage.snapshot(fromQuota: geminiSample, plan: "Free"))
        XCTAssertEqual(snapshot.provider, .gemini)
        XCTAssertEqual(snapshot.planName, "Free")
        XCTAssertEqual(snapshot.items.count, 3)   // two pro buckets collapse into one

        let pro = snapshot.items[0]
        XCTAssertEqual(pro.scopeName, "2.5 Pro")
        XCTAssertEqual(pro.percent, 38, accuracy: 0.001)   // the fullest bucket, 1 - 0.62
        XCTAssertEqual(pro.group, .weekly)                 // what the menu bar ring follows
        XCTAssertEqual(pro.title, "Daily · 2.5 Pro")
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(identifier: "UTC")!
        XCTAssertEqual(calendar.dateComponents([.year, .month, .day, .hour], from: try XCTUnwrap(pro.resetsAt)),
                       DateComponents(year: 2026, month: 11, day: 11, hour: 8))
        XCTAssertEqual(snapshot.items.map(\.scopeName), ["2.5 Pro", "2.5 Flash", "2.5 Flash Lite"])
        XCTAssertTrue(snapshot.items.dropFirst().allSatisfy { $0.group == .other })
    }

    func testFindsTheGeminiProjectAndPlan() {
        func data(_ text: String) -> Data { Data(text.utf8) }
        XCTAssertEqual(GeminiUsage.project(from: data(#"{"cloudaicompanionProject":"my-project-1","currentTier":{"id":"free-tier"}}"#)), "my-project-1")
        XCTAssertEqual(GeminiUsage.project(from: data(#"{"cloudaicompanionProject":{"id":"obj-project"}}"#)), "obj-project")
        XCTAssertNil(GeminiUsage.project(from: data("{}")))
        XCTAssertEqual(GeminiUsage.planName(from: data(#"{"currentTier":{"id":"free-tier"}}"#)), "Free")
        XCTAssertEqual(GeminiUsage.planName(from: data(#"{"currentTier":{"id":"standard-tier","name":"Gemini Code Assist"}}"#)), "Standard")
        XCTAssertNil(GeminiUsage.planName(from: data("{}")))
    }

    func testGeminiWithoutALoginIsHiddenAndAnExpiredOneSaysSo() async throws {
        let missing = GeminiProvider(credentialsURL: directory.appendingPathComponent("none.json"))
        XCTAssertFalse(missing.isAvailable())
        XCTAssertEqual(missing.detect(), .needsLogin)
        do {
            _ = try await missing.fetch()
            XCTFail("expected notLoggedIn")
        } catch {
            XCTAssertEqual(error as? UsageError, .notLoggedIn)
        }

        let expired = GeminiProvider(credentialsURL: try write(#"{"access_token":"t","expiry_date":1000}"#, as: "old.json"))
        do {
            _ = try await expired.fetch()
            XCTFail("expected an expired login")
        } catch {
            XCTAssertEqual(error as? UsageError, .unauthorized(expired: true))
        }
        XCTAssertTrue(UsageError.unauthorized(expired: true).message(for: .gemini).contains("Gemini CLI"))
        XCTAssertTrue(UsageError.notLoggedIn.message(for: .gemini).contains("Gemini CLI"))
    }

    func testUnknownGeminiShapesGiveNoSnapshot() throws {
        XCTAssertNil(GeminiUsage.snapshot(fromQuota: Data("{}".utf8), plan: nil))
        XCTAssertNil(GeminiUsage.snapshot(fromQuota: Data(#"{"buckets":[{"modelId":"x"}]}"#.utf8), plan: nil))
        XCTAssertNil(GeminiUsage.snapshot(fromQuota: Data("garbage".utf8), plan: nil))
        let cache = try ProviderCache(plan: "Free", raw: geminiSample).encoded()
        XCTAssertEqual(GeminiProvider().snapshot(fromCache: cache)?.planName, "Free")
    }

    // MARK: Menu bar, titles, initials

    func testCodexAndGeminiMenuBarOptions() {
        XCTAssertEqual(ProviderID.codex.menuBarDisplayOptions, [.session, .weekly, .both, .hidden])
        XCTAssertEqual(ProviderID.codex.defaultMenuBarDisplay, .both)
        XCTAssertEqual(ProviderID.gemini.menuBarDisplayOptions, [.weekly, .hidden])
        XCTAssertEqual(ProviderID.gemini.defaultMenuBarDisplay, .weekly)
        XCTAssertEqual(MenuBarDisplay.weekly.label(for: .gemini), "Daily")
        XCTAssertEqual(ProviderID.allCases, [.claude, .codex, .copilot, .gemini, .grokBot])
    }

    func testEveryAgentHasADistinctInitial() {
        let initials = ProviderID.allCases.map(\.initial)
        XCTAssertEqual(Set(initials).count, initials.count)
        XCTAssertEqual(ProviderID.codex.initial, "Cx")
        XCTAssertEqual(ProviderID.gemini.initial, "Ge")
    }

    // MARK: Icons

    @discardableResult
    private func makeExtension(_ name: String, packageIcon: String?, file: String?) throws -> URL {
        let folder = directory.appendingPathComponent("ext/\(name)")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("resources"), withIntermediateDirectories: true)
        let package = packageIcon.map { #"{"icon":"\#($0)"}"# } ?? "{}"
        try Data(package.utf8).write(to: folder.appendingPathComponent("package.json"))
        if let file { try Data("png".utf8).write(to: folder.appendingPathComponent(file)) }
        return folder
    }

    func testFindsEachAgentsIconInItsExtension() throws {
        try makeExtension("openai.chatgpt-25.9.1-darwin-arm64", packageIcon: "resources/blossom.dark.png", file: "resources/blossom.dark.png")
        let newer = try makeExtension("openai.chatgpt-26.1021.22027-darwin-arm64", packageIcon: "resources/blossom.dark.png", file: "resources/blossom.dark.png")
        try makeExtension("openai.codex-audio-26.1021.22027", packageIcon: "resources/x.png", file: "resources/x.png")   // not the Codex agent
        let gemini = try makeExtension("google.geminicodeassist-2.1.0-universal", packageIcon: "resources/gemini.png", file: "resources/gemini.png")
        let roots = [directory.appendingPathComponent("ext")]

        XCTAssertEqual(ExtensionIcon.findCodex(in: roots)?.standardizedFileURL.path,
                       newer.appendingPathComponent("resources/blossom.dark.png").standardizedFileURL.path)
        XCTAssertEqual(ExtensionIcon.findGemini(in: roots)?.standardizedFileURL.path,
                       gemini.appendingPathComponent("resources/gemini.png").standardizedFileURL.path)
        XCTAssertNil(ExtensionIcon.findCopilot(in: roots))
        XCTAssertNil(ExtensionIcon.findCodex(in: [directory.appendingPathComponent("missing")]))

        // "26.1021.22027" is the version, not "darwin-arm64".
        XCTAssertEqual(ExtensionIcon.version(of: "openai.chatgpt-26.1021.22027-darwin-arm64"), [26, 1021, 22027])
        XCTAssertEqual(ExtensionIcon.version(of: "github.copilot-chat-0.33.2025103102"), [0, 33, 2025103102])
    }

    func testAnIconOutsideTheExtensionFolderIsIgnored() throws {
        try makeExtension("google.geminicodeassist-1.0.0", packageIcon: "../../escape.png", file: nil)
        try Data("png".utf8).write(to: directory.appendingPathComponent("escape.png"))
        XCTAssertNil(ExtensionIcon.findGemini(in: [directory.appendingPathComponent("ext")]))
    }
}
