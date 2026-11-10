import XCTest
@testable import UsageCore

final class AppUpdateTests: XCTestCase {
    private func release(tag: String = "v0.8.0", assets: String? = nil) -> Data {
        let assetList = assets ?? """
        [
          {"name": "AIUsageBar-macOS.dmg", "size": 1606117,
           "browser_download_url": "https://github.com/matheusvelloso/ai-usage-bar/releases/download/\(tag)/AIUsageBar-macOS.dmg"},
          {"name": "AIUsageBar-Windows-Setup.exe", "size": 75831738,
           "browser_download_url": "https://github.com/matheusvelloso/ai-usage-bar/releases/download/\(tag)/AIUsageBar-Windows-Setup.exe"}
        ]
        """
        return Data("""
        {"tag_name": "\(tag)", "name": "AI Usage Bar", "draft": false, "prerelease": false,
         "html_url": "https://github.com/matheusvelloso/ai-usage-bar/releases/tag/\(tag)",
         "assets": \(assetList)}
        """.utf8)
    }

    func testVersionsCompareNumberByNumber() throws {
        let v = { (text: String) in try XCTUnwrap(AppVersion(text)) }
        XCTAssertLessThan(try v("0.9.0"), try v("0.10.0"))
        XCTAssertLessThan(try v("0.7.0"), try v("v0.7.1"))
        XCTAssertLessThan(try v("0.7.9"), try v("1.0"))
        XCTAssertEqual(try v("0.7"), try v("0.7.0"))
        XCTAssertEqual(try v("v1.2.3-beta+abc"), try v("1.2.3"))
        XCTAssertFalse(try v("0.7.0") < v("0.7.0"))
        XCTAssertEqual(try v("V2.0.1").description, "2.0.1")
    }

    func testRejectsTextThatIsNotAVersion() {
        for text in ["", "v", "latest", "1..2", "1.2.x", "-1.0", "1.2.3.4.5"] {
            XCTAssertNil(AppVersion(text), text)
        }
    }

    func testParsesTheLatestRelease() throws {
        let parsed = try AppUpdates.parse(release())
        XCTAssertEqual(parsed.version, AppVersion("0.8.0"))
        XCTAssertEqual(parsed.page.absoluteString, "https://github.com/matheusvelloso/ai-usage-bar/releases/tag/v0.8.0")
        let dmg = try XCTUnwrap(parsed.asset(named: AppUpdates.macAsset))
        XCTAssertEqual(dmg.size, 1_606_117)
        XCTAssertEqual(dmg.url.absoluteString, "https://github.com/matheusvelloso/ai-usage-bar/releases/download/v0.8.0/AIUsageBar-macOS.dmg")
    }

    func testOffersOnlyANewerReleaseWithAMacInstaller() throws {
        let parsed = try AppUpdates.parse(release())
        XCTAssertNotNil(AppUpdates.update(from: parsed, current: try XCTUnwrap(AppVersion("0.7.0")), asset: AppUpdates.macAsset))
        XCTAssertNil(AppUpdates.update(from: parsed, current: try XCTUnwrap(AppVersion("0.8.0")), asset: AppUpdates.macAsset))
        XCTAssertNil(AppUpdates.update(from: parsed, current: try XCTUnwrap(AppVersion("0.9.0")), asset: AppUpdates.macAsset))

        let windowsOnly = try AppUpdates.parse(release(assets: """
        [{"name": "AIUsageBar-Windows-Setup.exe", "size": 1,
          "browser_download_url": "https://github.com/matheusvelloso/ai-usage-bar/releases/download/v0.8.0/AIUsageBar-Windows-Setup.exe"}]
        """))
        XCTAssertNil(AppUpdates.update(from: windowsOnly, current: try XCTUnwrap(AppVersion("0.7.0")), asset: AppUpdates.macAsset))
    }

    func testIgnoresDownloadsOutsideThisRepository() throws {
        let parsed = try AppUpdates.parse(release(assets: """
        [
          {"name": "AIUsageBar-macOS.dmg", "size": 1, "browser_download_url": "https://example.com/AIUsageBar-macOS.dmg"},
          {"name": "AIUsageBar-macOS.dmg", "size": 1, "browser_download_url": "http://github.com/matheusvelloso/ai-usage-bar/releases/download/v0.8.0/AIUsageBar-macOS.dmg"},
          {"name": "AIUsageBar-macOS.dmg", "size": 1, "browser_download_url": "https://github.com/someone/fork/releases/download/v0.8.0/AIUsageBar-macOS.dmg"}
        ]
        """))
        XCTAssertTrue(parsed.assets.isEmpty)
    }

    func testRejectsDraftsPreReleasesAndOddTags() {
        XCTAssertThrowsError(try AppUpdates.parse(release().replacingDraft()))
        XCTAssertThrowsError(try AppUpdates.parse(Data(#"{"tag_name": "v0.8.0", "prerelease": true}"#.utf8)))
        XCTAssertThrowsError(try AppUpdates.parse(Data(#"{"tag_name": "nightly"}"#.utf8)))
        XCTAssertThrowsError(try AppUpdates.parse(Data("not json".utf8)))
    }
}

private extension Data {
    /// The same release marked as a draft.
    func replacingDraft() -> Data {
        Data(String(decoding: self, as: UTF8.self).replacingOccurrences(of: "\"draft\": false", with: "\"draft\": true").utf8)
    }
}
