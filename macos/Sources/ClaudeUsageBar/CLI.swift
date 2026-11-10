import AppKit
import SwiftUI
import UsageCore

/// Headless modes for debugging and for checking the UI without clicking
/// the menu bar:
///
///     ClaudeUsageBar --dump
///     ClaudeUsageBar --snapshot out.png [--fixture usage.json] [--dark]
///
/// `--snapshot` also writes `out-menubar.png` with the status item image.
struct CLICommand {
    enum Mode {
        case dump
        case snapshot(output: String)
    }

    let mode: Mode
    let fixturePath: String?
    let dark: Bool

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
        dark = arguments.contains("--dark")
    }

    @MainActor
    func run() async {
        let store: UsageStore
        if let fixturePath {
            guard let data = FileManager.default.contents(atPath: fixturePath),
                  let response = try? UsageResponse.decode(data)
            else {
                fail("Não consegui ler a fixture \(fixturePath)")
                return
            }
            store = UsageStore(preview: response, plan: "Max 5x")
        } else {
            store = UsageStore()
            await store.refresh(force: true)
        }

        switch mode {
        case .dump:
            print(store.dumpText)
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

        let icon = MenuBarIcon.image(percent: store.usage?.menuBarPercent(store.metric), showsAlert: store.error != nil)
        let iconPath = (path as NSString).deletingPathExtension + "-menubar.png"
        if let iconPNG = pngData(icon, scale: 4, background: dark ? .black : .white, tint: dark ? .white : .black) {
            write(iconPNG, to: iconPath)
        }
    }

    private func pngData(_ image: NSImage, scale: CGFloat = 1, background: NSColor? = nil, tint: NSColor? = nil) -> Data? {
        let pixelsWide = Int(image.size.width * scale)
        let pixelsHigh = Int(image.size.height * scale)
        guard pixelsWide > 0, pixelsHigh > 0,
              let rep = NSBitmapImageRep(
                bitmapDataPlanes: nil, pixelsWide: pixelsWide, pixelsHigh: pixelsHigh,
                bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
        else { return nil }
        rep.size = image.size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        let rect = NSRect(origin: .zero, size: image.size)
        image.draw(in: rect)
        if let tint {
            // Recolor only the glyph pixels (template images draw in black).
            tint.setFill()
            rect.fill(using: .sourceAtop)
        }
        if let background {
            background.setFill()
            rect.fill(using: .destinationOver)
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
