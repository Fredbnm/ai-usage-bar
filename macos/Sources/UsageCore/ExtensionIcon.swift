import Foundation

/// Several agents have no app of their own but live in an editor extension
/// that ships their official icon (`"icon"` in the extension's package.json):
/// GitHub Copilot, the Codex extension, Gemini Code Assist. The meter reads it
/// from the installed extension at runtime, like it does with the Claude and
/// Grok Bot apps: no brand artwork ships with this repository.
public enum ExtensionIcon {
    /// Editors that install VS Code extensions under the home folder.
    public static var defaultExtensionRoots: [URL] {
        let home = FileManager.default.homeDirectoryForCurrentUser
        return [".vscode", ".vscode-insiders", ".cursor"].map { home.appendingPathComponent("\($0)/extensions") }
    }

    /// The icon file of the newest installed extension whose folder starts with
    /// one of `idPrefixes` (earlier prefixes win), or nil. `usualIcons` are
    /// tried when package.json names no icon.
    public static func find(idPrefixes: [String], in roots: [URL]? = nil, usualIcons: [String] = []) -> URL? {
        var candidates: [(directory: URL, version: [Int], rank: Int)] = []
        for root in roots ?? defaultExtensionRoots {
            let names = (try? FileManager.default.contentsOfDirectory(atPath: root.path)) ?? []
            for name in names {
                let lower = name.lowercased()
                guard let rank = idPrefixes.firstIndex(where: { lower.hasPrefix($0.lowercased()) }) else { continue }
                candidates.append((root.appendingPathComponent(name), version(of: name), rank))
            }
        }
        let ordered = candidates.sorted {
            $0.rank != $1.rank ? $0.rank < $1.rank : compare($1.version, $0.version)   // newest first
        }
        for candidate in ordered {
            if let icon = iconFile(in: candidate.directory, usualIcons: usualIcons) { return icon }
        }
        return nil
    }

    /// "github.copilot-chat-0.33.2025103102" → [0, 33, 2025103102];
    /// "openai.chatgpt-26.1021.22027-darwin-arm64" → [26, 1021, 22027]
    /// (the first dotted number after a dash).
    static func version(of directoryName: String) -> [Int] {
        guard let range = directoryName.range(of: #"(?<=-)\d+(?:\.\d+)+"#, options: .regularExpression) else { return [0, 0, 0] }
        var parts = directoryName[range].split(separator: ".").map { Int($0) ?? 0 }
        while parts.count < 3 { parts.append(0) }
        return parts
    }

    /// Whether `lhs` sorts before `rhs`, comparing numbers (missing ones are 0).
    private static func compare(_ lhs: [Int], _ rhs: [Int]) -> Bool {
        for index in 0..<max(lhs.count, rhs.count) {
            let a = index < lhs.count ? lhs[index] : 0
            let b = index < rhs.count ? rhs[index] : 0
            if a != b { return a < b }
        }
        return false
    }

    private static func iconFile(in directory: URL, usualIcons: [String]) -> URL? {
        if let data = try? Data(contentsOf: directory.appendingPathComponent("package.json")),
           let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
           let relative = object["icon"] as? String, !relative.isEmpty,
           let named = inside(directory, relative) {
            return named
        }
        for name in usualIcons {
            if let usual = inside(directory, name) { return usual }
        }
        return nil
    }

    /// The file, only if it exists and never outside the extension's own folder.
    private static func inside(_ directory: URL, _ relative: String) -> URL? {
        let url = directory.appendingPathComponent(relative).standardizedFileURL
        guard url.path.hasPrefix(directory.standardizedFileURL.path + "/"),
              FileManager.default.fileExists(atPath: url.path)
        else { return nil }
        return url
    }

    public static func findCopilot(in roots: [URL]? = nil) -> URL? {
        find(idPrefixes: ["github.copilot-chat-", "github.copilot-"], in: roots, usualIcons: ["assets/copilot.png"])
    }

    public static func findCodex(in roots: [URL]? = nil) -> URL? {
        find(idPrefixes: ["openai.chatgpt-"], in: roots)
    }

    public static func findGemini(in roots: [URL]? = nil) -> URL? {
        find(idPrefixes: ["google.geminicodeassist-", "google.gemini-"], in: roots)
    }
}

/// Kept for callers of the Copilot-only API.
public enum CopilotIcon {
    public static var defaultExtensionRoots: [URL] { ExtensionIcon.defaultExtensionRoots }

    /// The icon file of the newest `github.copilot-chat-*` (then `github.copilot-*`)
    /// extension found, or nil.
    public static func find(in roots: [URL]? = nil) -> URL? { ExtensionIcon.findCopilot(in: roots) }

    static func version(of directoryName: String) -> [Int] { ExtensionIcon.version(of: directoryName) }
}
