import AppKit
import SwiftUI
import UsageCore

/// Headless modes for debugging and for checking the UI without clicking
/// the menu bar:
///
///     ClaudeUsageBar --dump [--raw]
///     ClaudeUsageBar --snapshot out.png [--fixture claude.json] [--grok-fixture grok.json] [--dark]
///
/// `--raw` also prints each service's usage response (no tokens).
/// `--snapshot` also writes `out-menubar.png` with the status item image.
struct CLICommand {
    enum Mode {
        case dump
        case snapshot(output: String)
    }

    let mode: Mode
    let fixturePath: String?
    let grokFixturePath: String?
    let dark: Bool
    let raw: Bool

    init?(arguments: [String]) {
        func value(after flag: String) -> String? {
            guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
            return arguments[index + 1]
        }
        if arguments.contains("--dump") {
            mode = .dump
        } else if let output = value(after: "--snapshot") {
            mode = .snapshot(output: output)
        } else {
            return nil
        }
        fixturePath = value(after: "--fixture")
        grokFixturePath = value(after: "--grok-fixture")
        dark = arguments.contains("--dark")
        raw = arguments.contains("--raw")
    }

    @MainActor
    func run() async {
        let store: UsageStore
        if fixturePath != nil || grokFixturePath != nil {
            var snapshots: [ProviderSnapshot] = []
            if let fixturePath {
                guard let data = FileManager.default.contents(atPath: fixturePath),
                      let snapshot = ClaudeProvider.snapshot(fromResponse: data, plan: "Max 5x")
                else { return fail("Não consegui ler a fixture \(fixturePath)") }
                snapshots.append(snapshot)
            }
            if let grokFixturePath {
                guard let data = FileManager.default.contents(atPath: grokFixturePath),
                      let snapshot = GrokBotProvider.snapshot(fromResponse: data)
                else { return fail("Não consegui ler a fixture \(grokFixturePath)") }
                snapshots.append(snapshot)
            }
            store = UsageStore(preview: snapshots)
        } else {
            store = UsageStore()
            await store.refresh(force: true)
        }

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
        }
    }

    /// Renders the real view hierarchy in an off-screen window, so AppKit
    /// controls (buttons, menus, links) are drawn too.
    @MainActor
    private func snapshot(store: UsageStore, to path: String) async {
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        let host = NSHostingView(rootView: PopoverView(store: store).background(Color(nsColor: .windowBackgroundColor)))
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
            fail("Falha ao renderizar o pop-up")
            return
        }
        host.cacheDisplay(in: host.bounds, to: rep)
        window.orderOut(nil)
        guard let png = rep.representation(using: .png, properties: [:]) else {
            fail("Falha ao gerar o PNG")
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
        let variants: [(String, [ProviderID: MenuBarDisplay])] = [
            ("Claude sessão + semanal | Grok Bot", [.claude: .both, .grokBot: .weekly]),
            ("Claude só sessão | Grok Bot", [.claude: .session, .grokBot: .weekly]),
            ("Claude só semanal | Grok Bot", [.claude: .weekly, .grokBot: .weekly]),
            ("Só Claude, sessão + semanal", [.claude: .both, .grokBot: .hidden]),
            ("Só Grok Bot", [.claude: .hidden, .grokBot: .weekly]),
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
            store.previewMenuBarDisplays(variant.1)
            for (column, appearanceName) in [NSAppearance.Name.aqua, .darkAqua].enumerated() {
                let cell = NSRect(x: labelWidth + columnWidth * CGFloat(column), y: y, width: columnWidth, height: barHeight)
                (column == 0 ? NSColor(white: 0.92, alpha: 1) : NSColor(white: 0.13, alpha: 1)).setFill()
                cell.fill()
                NSAppearance(named: appearanceName)?.performAsCurrentDrawingAppearance {
                    let image = MenuBarIcon.image(segments: store.menuBarSegments, height: barHeight)
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
            print("Imagem salva em \(path)")
        } catch {
            fail("Não consegui salvar \(path): \(error.localizedDescription)")
        }
    }

    private func fail(_ message: String) {
        FileHandle.standardError.write(Data((message + "\n").utf8))
    }
}
