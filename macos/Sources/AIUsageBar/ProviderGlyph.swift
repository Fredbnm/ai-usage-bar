import AppKit
import UsageCore

/// A monochrome glyph cut out of an app icon, with the point it should be
/// centred on and how far its artwork reaches from there.
struct Glyph {
    /// Black artwork on transparent, `side × side` points.
    let image: NSImage
    /// Visual centre in the image's drawing coordinates (origin bottom-left).
    let center: NSPoint
    /// Half the size the artwork should span inside the ring.
    let radius: CGFloat
}

/// A monochrome glyph per service, taken from the installed app's own icon
/// (the star on Claude's orange tile, the little character on Grok Bot's
/// black tile), so it can be drawn in the menu bar's color like any other
/// menu bar icon. Nothing is bundled with this app.
@MainActor
enum ProviderGlyph {
    private static var cache: [ProviderID: Glyph] = [:]
    private static var missing: Set<ProviderID> = []

    /// Where the icon of an agent that lives in an editor extension is, and how its artwork is read:
    /// Copilot's is dark on a light square, Codex's white on a tile, Gemini's a colored shape.
    private static func extensionIcon(for provider: ProviderID) -> (URL, GlyphExtractor.ArtworkKind)? {
        switch provider {
        case .copilot: return ExtensionIcon.findCopilot().map { ($0, GlyphExtractor.ArtworkKind.dark) }
        case .codex: return ExtensionIcon.findCodex().map { ($0, GlyphExtractor.ArtworkKind.white) }
        case .gemini: return ExtensionIcon.findGemini().map { ($0, GlyphExtractor.ArtworkKind.silhouette) }
        case .claude, .grokBot: return nil
        }
    }

    static func glyph(for provider: ProviderID) -> Glyph? {
        if let cached = cache[provider] { return cached }
        if missing.contains(provider) { return nil }
        let glyph: Glyph?
        if let (url, kind) = extensionIcon(for: provider) {
            // Copilot, Codex and Gemini have no app of their own: their official icon comes from the editor extension.
            glyph = NSImage(contentsOf: url).flatMap { GlyphExtractor.artwork(of: $0, kind: kind) }
        } else if let bundleIdentifier = provider.appBundleIdentifier,
                  let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleIdentifier) {
            glyph = GlyphExtractor.whiteArtwork(of: NSWorkspace.shared.icon(forFile: url.path))
        } else {
            glyph = nil
        }
        guard let glyph else {
            missing.insert(provider)
            return nil
        }
        cache[provider] = glyph
        return glyph
    }
}

/// The small icon beside an agent's name on its tab: the same glyph the menu
/// bar uses, as a template image so SwiftUI tints it with the text color. With
/// no app to borrow from (Copilot) it is the agent's initial in a thin circle.
@MainActor
enum TabIcon {
    private static var cache: [ProviderID: NSImage] = [:]

    static func image(for provider: ProviderID, side: CGFloat = 16) -> NSImage {
        if let cached = cache[provider] { return cached }
        let glyph = ProviderGlyph.glyph(for: provider)
        let image = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            if glyph == nil {
                let stroke: CGFloat = 1
                let ring = NSBezierPath(ovalIn: rect.insetBy(dx: stroke / 2, dy: stroke / 2))
                ring.lineWidth = stroke
                NSColor.black.withAlphaComponent(0.75).setStroke()
                ring.stroke()
            }
            MenuBarIcon.drawGlyph(glyph, provider: provider, in: rect.insetBy(dx: 1, dy: 1), color: .black, letterSize: side * 0.5)
            return true
        }
        image.isTemplate = true
        // The Glyph is read once and the image does not change with it, but a
        // missing app can appear later: only cache when there is artwork.
        if glyph != nil { cache[provider] = image }
        return image
    }
}

enum GlyphExtractor {
    /// Keeps the white artwork of an icon as an alpha mask:
    /// 1. "whiteness" = darkest RGB channel, so orange and black tiles drop out;
    /// 2. only the largest connected shape survives, which removes the thin
    ///    white rim highlight macOS 26 icons have along the tile edge;
    /// 3. the centre is found by mean shift towards the densest area — the
    ///    hub of a star, the middle of a blob — rather than the bounding box,
    ///    which irregular rays would skew.
    /// `dark`: the symbol is dark on a light or transparent square (Copilot's icon),
    /// so "darkness" is measured instead of "whiteness".
    static func whiteArtwork(of icon: NSImage, side: Int = 256, dark: Bool = false) -> Glyph? {
        artwork(of: icon, kind: dark ? .dark : .white, side: side)
    }

    /// What the artwork of an icon looks like, which decides how it is told
    /// apart from its background.
    enum ArtworkKind {
        /// White on a colored tile (Claude, Grok Bot, Codex).
        case white
        /// Dark on a light or transparent square (GitHub Copilot).
        case dark
        /// Colored on a transparent or plain background (Gemini).
        case silhouette
    }

    static func artwork(of icon: NSImage, kind: ArtworkKind, side: Int = 256) -> Glyph? {
        guard let source = bitmap(side: side), let input = source.bitmapData else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: source)
        icon.draw(in: NSRect(x: 0, y: 0, width: side, height: side))
        NSGraphicsContext.restoreGraphicsState()

        // 1. Coverage per pixel (row 0 = top).
        var coverage = [Double](repeating: 0, count: side * side)
        let corner = [Int(input[0]), Int(input[1]), Int(input[2]), Int(input[3])]
        for y in 0..<side {
            for x in 0..<side {
                let i = y * source.bytesPerRow + x * 4
                let alpha = Double(input[i + 3]) / 255
                guard alpha > 0 else { continue }
                if kind == .silhouette {
                    // Transparent corner: the alpha is the shape. Otherwise how
                    // far a pixel is from the corner's color.
                    if corner[3] < 25 {
                        coverage[y * side + x] = alpha
                    } else {
                        var distance = 0
                        for channel in 0..<3 { distance = max(distance, abs(Int(input[i + channel]) - corner[channel])) }
                        coverage[y * side + x] = smoothstep(40, 140, Double(distance)) * alpha
                    }
                } else if kind == .dark {
                    let lightest = Double(max(input[i], input[i + 1], input[i + 2])) / alpha
                    coverage[y * side + x] = (1 - smoothstep(70, 160, lightest)) * alpha
                } else {
                    let darkest = Double(min(input[i], input[i + 1], input[i + 2])) / alpha
                    coverage[y * side + x] = smoothstep(170, 235, darkest) * alpha
                }
            }
        }

        // 2. Largest 8-connected component of visibly covered pixels.
        guard let component = largestComponent(coverage, side: side, threshold: 0.05),
              component.count > side * side / 100
        else { return nil }

        guard let mask = bitmap(side: side), let output = mask.bitmapData else { return nil }
        for index in component {
            let o = (index / side) * mask.bytesPerRow + (index % side) * 4
            output[o + 3] = UInt8((coverage[index] * 255).rounded())  // black, premultiplied
        }

        // 3. Centre and reach, in pixel coordinates (row 0 = top).
        var cx = 0.0, cy = 0.0, total = 0.0
        for index in component {
            let w = coverage[index]
            cx += Double(index % side) * w
            cy += Double(index / side) * w
            total += w
        }
        cx /= total
        cy /= total
        var reach = 0.0
        for index in component where coverage[index] > 0.25 {
            reach = max(reach, hypot(Double(index % side) - cx, Double(index / side) - cy))
        }
        let window = reach * 0.3
        for _ in 0..<12 {
            var sx = 0.0, sy = 0.0, sw = 0.0
            for index in component {
                let px = Double(index % side), py = Double(index / side)
                guard hypot(px - cx, py - cy) <= window else { continue }
                let w = coverage[index]
                sx += px * w; sy += py * w; sw += w
            }
            guard sw > 0 else { break }
            let (nx, ny) = (sx / sw, sy / sw)
            let moved = hypot(nx - cx, ny - cy)
            (cx, cy) = (nx, ny)
            if moved < 0.1 { break }
        }
        var radius = 0.0
        for index in component where coverage[index] > 0.25 {
            radius = max(radius, hypot(Double(index % side) + 0.5 - (cx + 0.5), Double(index / side) + 0.5 - (cy + 0.5)) + 0.5)
        }

        // How much of that disc the artwork covers: a star is mostly empty
        // space, a blob-shaped character (Grok Bot) is mostly filled. Mean
        // shift has no dense hub to find in a filled shape and drifts to a
        // corner, so filled shapes are centred on their bounding box instead
        // and sized by its longer side.
        let area = component.reduce(0.0) { $0 + coverage[$1] }
        let fill = area / (Double.pi * radius * radius)
        if fill > 0.45 {
            var minX = side, minY = side, maxX = -1, maxY = -1
            for index in component where coverage[index] > 0.25 {
                let x = index % side, y = index / side
                minX = min(minX, x); maxX = max(maxX, x)
                minY = min(minY, y); maxY = max(maxY, y)
            }
            cx = Double(minX + maxX) / 2
            cy = Double(minY + maxY) / 2
            radius = Double(max(maxX - minX, maxY - minY) + 1) / 2
        }

        let image = NSImage(size: NSSize(width: side, height: side))
        image.addRepresentation(mask)
        image.isTemplate = true
        return Glyph(
            image: image,
            center: NSPoint(x: cx + 0.5, y: Double(side) - (cy + 0.5)),
            radius: radius
        )
    }

    private static func largestComponent(_ coverage: [Double], side: Int, threshold: Double) -> [Int]? {
        var visited = [Bool](repeating: false, count: side * side)
        var best: [Int] = []
        var stack: [Int] = []
        for start in 0..<(side * side) where !visited[start] && coverage[start] > threshold {
            var component: [Int] = []
            visited[start] = true
            stack.append(start)
            while let index = stack.popLast() {
                component.append(index)
                let x = index % side, y = index / side
                for dy in -1...1 {
                    for dx in -1...1 where dx != 0 || dy != 0 {
                        let nx = x + dx, ny = y + dy
                        guard nx >= 0, ny >= 0, nx < side, ny < side else { continue }
                        let neighbor = ny * side + nx
                        if !visited[neighbor] && coverage[neighbor] > threshold {
                            visited[neighbor] = true
                            stack.append(neighbor)
                        }
                    }
                }
            }
            if component.count > best.count { best = component }
        }
        return best.isEmpty ? nil : best
    }

    private static func bitmap(side: Int) -> NSBitmapImageRep? {
        NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: side * 4, bitsPerPixel: 32
        )
    }

    private static func smoothstep(_ edge0: Double, _ edge1: Double, _ value: Double) -> Double {
        let t = min(max((value - edge0) / (edge1 - edge0), 0), 1)
        return t * t * (3 - 2 * t)
    }
}
