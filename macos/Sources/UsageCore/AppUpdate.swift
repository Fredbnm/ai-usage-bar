import Foundation

/// A version like 0.7.0, compared number by number (0.10.0 is newer than 0.9.0).
public struct AppVersion: Comparable, CustomStringConvertible, Sendable {
    public let parts: [Int]

    /// Reads "0.7.0", "v0.7.0" or "0.7"; a suffix such as "-beta" or "+build" is ignored.
    public init?(_ text: String) {
        var core = Substring(text.trimmingCharacters(in: .whitespaces))
        if core.first == "v" || core.first == "V" { core = core.dropFirst() }
        if let end = core.firstIndex(where: { $0 == "-" || $0 == "+" }) { core = core[..<end] }
        let numbers = core.split(separator: ".", omittingEmptySubsequences: false).map { Int($0) }
        guard (1...4).contains(numbers.count), numbers.allSatisfy({ ($0 ?? -1) >= 0 }) else { return nil }
        parts = numbers.map { $0! }
    }

    public var description: String { parts.map(String.init).joined(separator: ".") }

    private func padded(to count: Int) -> [Int] { parts + Array(repeating: 0, count: max(0, count - parts.count)) }

    public static func == (lhs: AppVersion, rhs: AppVersion) -> Bool {
        let count = max(lhs.parts.count, rhs.parts.count)
        return lhs.padded(to: count) == rhs.padded(to: count)
    }

    public static func < (lhs: AppVersion, rhs: AppVersion) -> Bool {
        let count = max(lhs.parts.count, rhs.parts.count)
        return lhs.padded(to: count).lexicographicallyPrecedes(rhs.padded(to: count))
    }
}

/// A published release of this app on GitHub.
public struct AppRelease: Equatable, Sendable {
    public struct Asset: Equatable, Sendable {
        public let name: String
        public let url: URL
        public let size: Int

        public init(name: String, url: URL, size: Int) {
            self.name = name
            self.url = url
            self.size = size
        }
    }

    public let version: AppVersion
    public let page: URL
    public let assets: [Asset]

    public init(version: AppVersion, page: URL, assets: [Asset]) {
        self.version = version
        self.page = page
        self.assets = assets
    }

    public func asset(named name: String) -> Asset? { assets.first { $0.name == name } }
}

/// Looks for a newer version among the repository's GitHub releases. Only the
/// public releases API is called: nothing about the user or their usage is sent.
public enum AppUpdates {
    public static let repository = "matheusvelloso/ai-usage-bar"
    public static let latestEndpoint = URL(string: "https://api.github.com/repos/\(repository)/releases/latest")!
    public static let releasesPage = URL(string: "https://github.com/\(repository)/releases/latest")!
    /// Same fixed name the release workflow uploads.
    public static let macAsset = "AIUsageBar-macOS.dmg"
    /// How often the running app looks for a new release.
    public static let checkInterval: TimeInterval = 6 * 60 * 60

    private struct ReleaseResponse: Decodable {
        struct Asset: Decodable {
            let name: String
            let browser_download_url: String
            let size: Int?
        }
        let tag_name: String
        let html_url: String?
        let draft: Bool?
        let prerelease: Bool?
        let assets: [Asset]?
    }

    /// Reads the response of `releases/latest`. Drafts, pre-releases and
    /// download links outside this repository's releases are not offered.
    public static func parse(_ data: Data) throws -> AppRelease {
        guard let response = try? JSONDecoder().decode(ReleaseResponse.self, from: data),
              let version = AppVersion(response.tag_name),
              response.draft != true, response.prerelease != true
        else { throw UsageError.decoding }
        let page = response.html_url.flatMap(URL.init(string:)).flatMap(isReleasePage) ?? releasesPage
        let assets = (response.assets ?? []).compactMap { asset -> AppRelease.Asset? in
            guard let url = URL(string: asset.browser_download_url), isDownload(url) else { return nil }
            return AppRelease.Asset(name: asset.name, url: url, size: asset.size ?? 0)
        }
        return AppRelease(version: version, page: page, assets: assets)
    }

    /// The release to offer: newer than the running version and carrying the file this platform installs.
    public static func update(from release: AppRelease, current: AppVersion, asset: String) -> AppRelease? {
        release.version > current && release.asset(named: asset) != nil ? release : nil
    }

    /// `token` is only for CI (`--install-update` with `GITHUB_TOKEN` set), whose shared machines
    /// run out of GitHub's anonymous rate limit; the app itself always asks anonymously.
    public static func fetchLatest(token: String? = nil) async throws -> AppRelease {
        var request = URLRequest(url: latestEndpoint, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 20)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("ai-usage-bar", forHTTPHeaderField: "User-Agent")
        if let token, !token.isEmpty { request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        return try parse(try await HTTP.send(request))
    }

    private static func isDownload(_ url: URL) -> Bool {
        url.scheme == "https" && url.host == "github.com" && url.path.hasPrefix("/\(repository)/releases/download/")
    }

    private static func isReleasePage(_ url: URL) -> URL? {
        url.scheme == "https" && url.host == "github.com" && url.path.hasPrefix("/\(repository)/releases/") ? url : nil
    }
}
