import AppKit
import SwiftUI
import UsageCore

/// Headless modes for debugging and for checking the UI without clicking
/// the menu bar:
///
///     AIUsageBar --dump [--raw]
///     AIUsageBar --snapshot out.png [--fixture claude.json] [--codex-fixture codex.json] [--copilot-fixture copilot.json] [--cursor-fixture cursor.json] [--gemini-fixture gemini.json] [--grok-fixture grok.json] [--dark] [--lang en|pt] [--tab claude|codex|copilot|cursor|gemini|grokBot|agents] [--first-run] [--update-available 9.9.9] [--tab-limit claude=weekly_all] [--layout tabs|list]
///     AIUsageBar --install-update [--no-relaunch]
///
/// `--raw` also prints each service's usage response (no tokens).
/// `--snapshot` also writes `out-menubar.png` with the status item image.
/// `--install-update` runs the in-app update from the command line: if GitHub
/// has a newer release, it is installed over this app bundle once the command exits.
struct CLICommand {
    enum Mode {
        case dump
        case snapshot(output: String)
        case installUpdate(relaunch: Bool)
    }

    let mode: Mode
    let fixturePath: String?
    let grokFixturePath: String?
    let copilotFixturePath: String?
    let codexFixturePath: String?
    let cursorFixturePath: String?
    let geminiFixturePath: String?
    let tab: String?
    let firstRun: Bool
    let dark: Bool
    let raw: Bool
    let language: AppLanguage?
    let updateAvailable: AppVersion?
    /// `provider=key`: what one agent's tab shows (a `TabLimit` key).
    let tabLimit: (ProviderID, String)?
    let layout: PopupLayout?

    init?(arguments: [String]) {
        func value(after flag: String) -> String? {
            guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
            return arguments[index + 1]
        }
        if arguments.contains("--dump") {
            mode = .dump
        } else if let output = value(after: "--snapshot") {
            mode = .snapshot(output: output)
        } else if arguments.contains("--install-update") {
            mode = .installUpdate(relaunch: !arguments.contains("--no-relaunch"))
        } else {
            return nil
        }
        fixturePath = value(after: "--fixture")
        grokFixturePath = value(after: "--grok-fixture")
        copilotFixturePath = value(after: "--copilot-fixture")
        codexFixturePath = value(after: "--codex-fixture")
        cursorFixturePath = value(after: "--cursor-fixture")
        geminiFixturePath = value(after: "--gemini-fixture")
        tab = value(after: "--tab")
        firstRun = arguments.contains("--first-run")
        dark = arguments.contains("--dark")
        raw = arguments.contains("--raw")
        language = value(after: "--lang").flatMap(AppLanguage.init(rawValue:))
        updateAvailable = value(after: "--update-available").flatMap(AppVersion.init)
        layout = value(after: "--layout").flatMap(PopupLayout.init(rawValue:))
        tabLimit = value(after: "--tab-limit").flatMap { pair in
            let parts = pair.split(separator: "=", maxSplits: 1).map(String.init)
            guard parts.count == 2, let id = ProviderID(rawValue: parts[0]) else { return nil }
            return (id, parts[1])
        }
    }

    @MainActor
    func run() async {
        if case .installUpdate(let relaunch) = mode {
            return await installUpdate(relaunch: relaunch)
        }
        let store: UsageStore
        if fixturePath != nil || grokFixturePath != nil || copilotFixturePath != nil || codexFixturePath != nil
            || cursorFixturePath != nil || geminiFixturePath != nil {
            var snapshots: [ProviderSnapshot] = []
            if let fixturePath {
                guard let data = FileManager.default.contents(atPath: fixturePath),
                      let snapshot = ClaudeProvider.snapshot(fromResponse: data, plan: "Max 5x")
                else { return fail("Could not read fixture \(fixturePath)") }
                snapshots.append(snapshot)
            }
            if let codexFixturePath {
                guard let data = FileManager.default.contents(atPath: codexFixturePath),
                      let snapshot = CodexUsage.snapshot(fromResponse: data)
                else { return fail("Could not read fixture \(codexFixturePath)") }
                snapshots.append(snapshot)
            }
            if let copilotFixturePath {
                guard let data = FileManager.default.contents(atPath: copilotFixturePath),
                      let snapshot = CopilotUsage.snapshot(fromResponse: data)
                else { return fail("Could not read fixture \(copilotFixturePath)") }
                snapshots.append(snapshot)
            }
            if let cursorFixturePath {
                guard let data = FileManager.default.contents(atPath: cursorFixturePath),
                      let snapshot = CursorUsage.snapshot(fromResponse: data, plan: "Pro")
                else { return fail("Could not read fixture \(cursorFixturePath)") }
                snapshots.append(snapshot)
            }
            if let geminiFixturePath {
                guard let data = FileManager.default.contents(atPath: geminiFixturePath),
                      let snapshot = GeminiUsage.snapshot(fromQuota: data, plan: "Free")
                else { return fail("Could not read fixture \(geminiFixturePath)") }
                snapshots.append(snapshot)
            }
            if let grokFixturePath {
                guard let data = FileManager.default.contents(atPath: grokFixturePath),
                      let snapshot = GrokBotProvider.snapshot(fromResponse: data)
                else { return fail("Could not read fixture \(grokFixturePath)") }
                snapshots.append(snapshot)
            }
            store = UsageStore(preview: snapshots)
        } else {
            store = UsageStore()
            await store.refresh(force: true)
        }

        if let language { AppLanguage.current = language }
        if let tab { store.previewTab(tab) }
        if firstRun { store.previewFirstRun() }
        if let (id, key) = tabLimit { store.previewTabLimit(key, for: id) }
        if let layout { store.popupLayout = layout }

        switch mode {
        case .dump:
            print(store.dumpText)
            if raw {
                for id in store.visibleProviders {
                    print("\n== Resposta bruta · \(id.displayName)")
                    print(store.rawResponse(for: id) ?? "(sem resposta salva)")
                }
            }
        case .snapshot(let output):
            await snapshot(store: store, to: output)
        case .installUpdate:
            break
        }
    }

    @MainActor
    private func installUpdate(relaunch: Bool) async {
        guard let current = Bundle.main.shortVersion, let bundleID = Bundle.main.bundleIdentifier else {
            return fail("Run this from inside the app bundle (AI Usage Bar.app/Contents/MacOS/AIUsageBar).")
        }
        do {
            let release = try await AppUpdates.fetchLatest(token: ProcessInfo.processInfo.environment["GITHUB_TOKEN"])
            print("Running \(current); latest release is \(release.version).")
            guard let update = AppUpdates.update(from: release, current: current, asset: AppUpdates.macAsset),
                  let asset = update.asset(named: AppUpdates.macAsset)
            else { return print("Nothing to install.") }
            let staged = try await UpdateInstaller.stage(asset, version: update.version, bundleID: bundleID)
            try UpdateInstaller.replace(Bundle.main.bundleURL, with: staged, after: getpid(), relaunch: relaunch)
            print("Installing \(update.version) into \(Bundle.main.bundlePath) as soon as this command exits.")
        } catch let failure as UpdateInstaller.Failure {
            fail(failure.message)
        } catch {
            fail("Update failed: \(error)")
        }
    }

    /// Renders the real view hierarchy in an off-screen window, so AppKit
    /// controls (buttons, menus, links) are drawn too.
    @MainActor
    private func snapshot(store: UsageStore, to path: String) async {
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let current = Bundle.main.shortVersion ?? AppVersion("0.7.0")
        let updater = updateAvailable.map { Updater(preview: $0, current: current) } ?? Updater(current: nil)
        let host = NSHostingView(rootView: PopoverView(store: store, updater: updater).background(Color(nsColor: .windowBackgroundColor)))
        host.appearance = appearance
        host.frame = NSRect(origin: .zero, size: host.fittingSize)

        let window = NSWindow(contentRect: host.frame, styleMask: .borderless, backing: .buffered, defer: false)
        window.appearance = appearance
        window.contentView = host
        window.setFrameOrigin(NSPoint(x: -10_000, y: -10_000))
        window.orderFrontRegardless()
        host.layoutSubtreeIfNeeded()
        try? await Task.sleep(nanoseconds: 400_000_000)

        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else {
            fail("Could not render the popover")
            return
        }
        host.cacheDisplay(in: host.bounds, to: rep)
        window.orderOut(nil)
        guard let png = rep.representation(using: .png, properties: [:]) else {
            fail("Could not encode the PNG")
            return
        }
        write(png, to: path)

        let menuBarPath = (path as NSString).deletingPathExtension + "-menubar.png"
        if let menuBarPNG = menuBarVariants(store: store) {
            write(menuBarPNG, to: menuBarPath)
        }
    }

    /// Every menu bar combination, in a light and a dark menu bar.
    @MainActor
    private func menuBarVariants(store: UsageStore) -> Data? {
        let variants: [(String, MenuBarStyle, [ProviderID: MenuBarDisplay], Bool)] = [
            ("Claude session + weekly | Grok Bot", .icon, [.claude: .both, .grokBot: .weekly], true),
            ("Claude session only | Grok Bot", .icon, [.claude: .session, .grokBot: .weekly], true),
            ("Claude weekly only | Grok Bot", .icon, [.claude: .weekly, .grokBot: .weekly], true),
            ("Claude only, session + weekly", .icon, [.claude: .both, .grokBot: .hidden], true),
            ("Grok Bot only", .icon, [.claude: .hidden, .grokBot: .weekly], true),
            ("Percentage: session + weekly | Grok", .percent, [.claude: .both, .grokBot: .weekly], true),
            ("Percentage: session only | Grok", .percent, [.claude: .session, .grokBot: .weekly], true),
            ("Percentage: Claude only, session", .percent, [.claude: .session, .grokBot: .hidden], true),
            ("Icon, alert colors off", .icon, [.claude: .both, .grokBot: .weekly], false),
            ("Percentage, alert colors off", .percent, [.claude: .both, .grokBot: .weekly], false),
        ]
        let scale: CGFloat = 3
        let barHeight: CGFloat = 24
        let labelWidth: CGFloat = 210
        let columnWidth: CGFloat = 170
        let size = NSSize(width: labelWidth + columnWidth * 2, height: barHeight * CGFloat(variants.count))

        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: Int(size.width * scale), pixelsHigh: Int(size.height * scale),
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        else { return nil }
        rep.size = size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSColor.white.setFill()
        NSRect(origin: .zero, size: size).fill()

        for (row, variant) in variants.enumerated() {
            let y = size.height - barHeight * CGFloat(row + 1)
            (variant.0 as NSString).draw(
                at: NSPoint(x: 8, y: y + 6),
                withAttributes: [.font: NSFont.systemFont(ofSize: 11), .foregroundColor: NSColor.darkGray]
            )
            store.previewMenuBar(variant.2)
            for (column, appearanceName) in [NSAppearance.Name.aqua, .darkAqua].enumerated() {
                let cell = NSRect(x: labelWidth + columnWidth * CGFloat(column), y: y, width: columnWidth, height: barHeight)
                (column == 0 ? NSColor(white: 0.92, alpha: 1) : NSColor(white: 0.13, alpha: 1)).setFill()
                cell.fill()
                NSAppearance(named: appearanceName)?.performAsCurrentDrawingAppearance {
                    let image = MenuBarIcon.image(
                        segments: store.menuBarSegments, style: variant.1, colorAlerts: variant.3, height: barHeight
                    )
                    image.draw(in: NSRect(x: cell.minX + 10, y: cell.minY, width: image.size.width, height: image.size.height))
                }
            }
        }
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }

    private func write(_ data: Data, to path: String) {
        do {
            try data.write(to: URL(fileURLWithPath: path))
            print("Saved \(path)")
        } catch {
            fail("Could not save \(path): \(error.localizedDescription)")
        }
    }

    private func fail(_ message: String) {
        FileHandle.standardError.write(Data((message + "\n").utf8))
    }
}
