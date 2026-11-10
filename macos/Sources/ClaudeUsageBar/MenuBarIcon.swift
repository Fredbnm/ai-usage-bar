import AppKit
import UsageCore

/// Draws the status item: for each service, its monochrome glyph inside a
/// ring that fills with the usage, followed by one or two percentages.
/// Blocks are separated by a thin vertical line.
///
/// Everything is drawn in the menu bar's own foreground color (white on a
/// dark menu bar, black on a light one), like template icons. The ring
/// alone turns orange or red when a limit is close, which a template image
/// could not do, so colors are resolved when AppKit draws the image in the
/// menu bar's appearance.
enum MenuBarIcon {
    private enum Metrics {
        /// The menu bar is 22 pt tall; leave a point above and below.
        static let ring: CGFloat = 19
        static let ringWidth: CGFloat = 1.6
        static let glyphGap: CGFloat = 1.1
        static let ringToText: CGFloat = 4
        static let separatorPadding: CGFloat = 7
        static let separatorHeight: CGFloat = 14
        static let singleFont = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        static let stackedFont = NSFont.monospacedDigitSystemFont(ofSize: 10, weight: .semibold)
        static let stackedGap: CGFloat = 2.5
    }

    private struct Layout {
        let segment: MenuBarSegment
        let glyph: Glyph?
        let font: NSFont
        let textWidth: CGFloat

        var width: CGFloat { Metrics.ring + Metrics.ringToText + textWidth }

        init(segment: MenuBarSegment, glyph: Glyph?) {
            self.segment = segment
            self.glyph = glyph
            font = segment.lines.count > 1 ? Metrics.stackedFont : Metrics.singleFont
            let attributes: [NSAttributedString.Key: Any] = [.font: font]
            textWidth = ceil(segment.lines.map { ($0 as NSString).size(withAttributes: attributes).width }.max() ?? 0)
        }
    }

    @MainActor
    static func image(segments: [MenuBarSegment], height: CGFloat = NSStatusBar.system.thickness) -> NSImage {
        guard !segments.isEmpty else { return emptyImage(height: height) }

        let layouts = segments.map { Layout(segment: $0, glyph: ProviderGlyph.glyph(for: $0.provider)) }
        let separatorWidth = Metrics.separatorPadding * 2 + 1
        let width = layouts.map(\.width).reduce(0, +) + separatorWidth * CGFloat(layouts.count - 1)

        let image = NSImage(size: NSSize(width: ceil(width), height: height), flipped: false) { _ in
            let foreground = foregroundColor()
            var x: CGFloat = 0
            for (index, layout) in layouts.enumerated() {
                if index > 0 {
                    drawSeparator(at: x + Metrics.separatorPadding, height: height, color: foreground)
                    x += separatorWidth
                }
                draw(layout, at: x, height: height, foreground: foreground)
                x += layout.width
            }
            return true
        }
        image.isTemplate = false
        return image
    }

    /// The color template icons get in the current menu bar.
    private static func foregroundColor() -> NSColor {
        let isDark = NSAppearance.currentDrawing().bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        return isDark ? .white : .black
    }

    // MARK: Drawing

    private static func draw(_ layout: Layout, at x: CGFloat, height: CGFloat, foreground: NSColor) {
        let segment = layout.segment
        let frame = NSRect(x: x, y: (height - Metrics.ring) / 2, width: Metrics.ring, height: Metrics.ring)
        let ringRect = frame.insetBy(dx: Metrics.ringWidth / 2, dy: Metrics.ringWidth / 2)

        let track = NSBezierPath(ovalIn: ringRect)
        track.lineWidth = Metrics.ringWidth
        foreground.withAlphaComponent(0.25).setStroke()
        track.stroke()

        if let percent = segment.ringPercent, percent > 0 {
            let fraction = min(percent / 100, 1)
            let arc = NSBezierPath()
            arc.appendArc(
                withCenter: NSPoint(x: ringRect.midX, y: ringRect.midY), radius: ringRect.width / 2,
                startAngle: 90, endAngle: 90 - 360 * fraction, clockwise: true
            )
            arc.lineWidth = Metrics.ringWidth
            arc.lineCapStyle = .round
            ringColor(for: segment.ringSeverity, foreground: foreground).setStroke()
            arc.stroke()
        }

        let inner = frame.insetBy(dx: Metrics.ringWidth + Metrics.glyphGap, dy: Metrics.ringWidth + Metrics.glyphGap)
        drawGlyph(layout.glyph, provider: segment.provider, in: inner, color: foreground)

        let textX = frame.maxX + Metrics.ringToText
        let font = layout.font
        if segment.lines.count > 1 {
            // Two rows of digits centred as a block, using cap height so the
            // numbers (no descenders) sit optically centred.
            let block = font.capHeight * 2 + Metrics.stackedGap
            let bottomBaseline = (height - block) / 2
            let topBaseline = bottomBaseline + font.capHeight + Metrics.stackedGap
            drawText(segment.lines[0], font: font, color: foreground, x: textX, baseline: topBaseline, width: layout.textWidth)
            drawText(segment.lines[1], font: font, color: foreground, x: textX, baseline: bottomBaseline, width: layout.textWidth)
        } else if let line = segment.lines.first {
            let baseline = (height - font.capHeight) / 2
            drawText(line, font: font, color: foreground, x: textX, baseline: baseline, width: layout.textWidth)
        }
    }

    /// Centres the glyph on its visual centre, scaled so its farthest point
    /// touches the circle inside the ring, and paints it in `color`.
    private static func drawGlyph(_ glyph: Glyph?, provider: ProviderID, in rect: NSRect, color: NSColor) {
        guard let glyph, glyph.radius > 0 else {
            // No installed app to borrow artwork from: the service's initial.
            let letter = String(provider.displayName.prefix(1)) as NSString
            let attributes: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 8.5, weight: .bold),
                .foregroundColor: color,
            ]
            let size = letter.size(withAttributes: attributes)
            letter.draw(at: NSPoint(x: rect.midX - size.width / 2, y: rect.midY - size.height / 2), withAttributes: attributes)
            return
        }

        let scale = (rect.width / 2) / glyph.radius
        let size = NSSize(width: glyph.image.size.width * scale, height: glyph.image.size.height * scale)
        let target = NSRect(
            x: rect.midX - glyph.center.x * scale,
            y: rect.midY - glyph.center.y * scale,
            width: size.width, height: size.height
        )

        guard let context = NSGraphicsContext.current?.cgContext else { return }
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(ovalIn: rect.insetBy(dx: -0.5, dy: -0.5)).addClip()
        context.beginTransparencyLayer(in: rect, auxiliaryInfo: nil)
        glyph.image.draw(in: target, from: .zero, operation: .sourceOver, fraction: 1)
        color.setFill()
        rect.fill(using: .sourceIn)
        context.endTransparencyLayer()
        NSGraphicsContext.restoreGraphicsState()
    }

    private static func drawText(_ text: String, font: NSFont, color: NSColor, x: CGFloat, baseline: CGFloat, width: CGFloat) {
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
        let size = (text as NSString).size(withAttributes: attributes)
        // Right-align so stacked numbers line up on the "%".
        let origin = NSPoint(x: x + width - size.width, y: baseline + font.descender)
        (text as NSString).draw(at: origin, withAttributes: attributes)
    }

    private static func drawSeparator(at x: CGFloat, height: CGFloat, color: NSColor) {
        let rect = NSRect(x: x, y: (height - Metrics.separatorHeight) / 2, width: 1, height: Metrics.separatorHeight)
        color.withAlphaComponent(0.35).setFill()
        NSBezierPath(roundedRect: rect, xRadius: 0.5, yRadius: 0.5).fill()
    }

    private static func ringColor(for severity: Severity, foreground: NSColor) -> NSColor {
        switch severity {
        case .normal: foreground
        case .warning: .systemOrange
        case .critical: .systemRed
        }
    }

    /// Every service hidden: just an empty ring so the item stays clickable.
    private static func emptyImage(height: CGFloat) -> NSImage {
        let image = NSImage(size: NSSize(width: Metrics.ring, height: height), flipped: false) { _ in
            let rect = NSRect(x: 0, y: (height - Metrics.ring) / 2, width: Metrics.ring, height: Metrics.ring)
                .insetBy(dx: Metrics.ringWidth / 2, dy: Metrics.ringWidth / 2)
            let ring = NSBezierPath(ovalIn: rect)
            ring.lineWidth = Metrics.ringWidth
            NSColor.black.setStroke()
            ring.stroke()
            return true
        }
        image.isTemplate = true
        return image
    }
}

extension ProviderID {
    var appBundleIdentifier: String {
        switch self {
        case .claude: "com.anthropic.claudefordesktop"
        case .grokBot: "com.anysphere.sand"
        }
    }
}
