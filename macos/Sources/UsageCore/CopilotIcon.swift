import Foundation

/// GitHub Copilot has no app of its own, but GitHub's editor extension ships
/// its official icon (`"icon"` in the extension's package.json). The meter
/// reads it from the installed extension at runtime, like it does with the
/// Claude and Grok Bot apps: no brand artwork ships with this repository.
public enum CopilotIcon {
    /// Editors that install VS Code extensions under the home folder.
    public static var defaultExtensionRoots: [URL] {
        let home = FileManager.default.homeDirectoryForCurrentUser
        return [".vscode", ".vscode-insiders", ".cursor"].map { home.appendingPathComponent("\($0)/extensions") }
    }

    /// The icon file of the newest `github.copilot-chat-*` (then `github.copilot-*`)
    /// extension found, or nil.
    public static func find(in roots: [URL]? = nil) -> URL? {
        var candidates: [(directory: URL, version: [Int], rank: Int)] = []
        for root in roots ?? defaultExtensionRoots {
            let names = (try? FileManager.default.contentsOfDirectory(atPath: root.path)) ?? []
            for name in names {
                let lower = name.lowercased()
                let rank = lower.hasPrefix("github.copilot-chat-") ? 0 : lower.hasPrefix("github.copilot-") ? 1 : -1
                guard rank >= 0 else { continue }
                candidates.append((root.appendingPathComponent(name), version(of: name), rank))
            }
        }
        let ordered = candidates.sorted {
            $0.rank != $1.rank ? $0.rank < $1.rank : $1.version.lexicographicallyPrecedes($0.version)   // newest first
        }
        return ordered.lazy.compactMap { iconFile(in: $0.directory) }.first
    }

    /// "github.copilot-chat-0.33.2025103102" → [0, 33, 2025103102] (the number after the last dash).
    static func version(of directoryName: String) -> [Int] {
        let text = directoryName.split(separator: "-").last.map(String.init) ?? ""
        return text.split(separator: ".").map { Int($0) ?? 0 }
    }

    private static func iconFile(in directory: URL) -> URL? {
        if let data = try? Data(contentsOf: directory.appendingPathComponent("package.json")),
           let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
           let relative = object["icon"] as? String, !relative.isEmpty {
            let url = directory.appendingPathComponent(relative).standardizedFileURL
            // Never leave the extension folder.
            if url.path.hasPrefix(directory.standardizedFileURL.path + "/"), FileManager.default.fileExists(atPath: url.path) {
                return url
            }
        }
        let usual = directory.appendingPathComponent("assets/copilot.png")
        return FileManager.default.fileExists(atPath: usual.path) ? usual : nil
    }
}
