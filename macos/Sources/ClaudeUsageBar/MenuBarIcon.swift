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
        /// "Só o percentual" style: the pie gauge from version 0.1.
        static let pie: CGFloat = 14
        static let pieOutline: CGFloat = 1.5
        static let pieInset: CGFloat = 2.25
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
        let style: MenuBarStyle
        let glyph: Glyph?
        let font: NSFont
        let textWidth: CGFloat

        var gauge: CGFloat { style == .icon ? Metrics.ring : Metrics.pie }
        var width: CGFloat { gauge + Metrics.ringToText + textWidth }

        init(segment: MenuBarSegment, style: MenuBarStyle, glyph: Glyph?) {
            self.segment = segment
            self.style = style
            self.glyph = glyph
            font = segment.lines.count > 1 ? Metrics.stackedFont : Metrics.singleFont
            let attributes: [NSAttributedString.Key: Any] = [.font: font]
            textWidth = ceil(segment.lines.map { ($0 as NSString).size(withAttributes: attributes).width }.max() ?? 0)
        }
    }

    @MainActor
    static func image(
        segments: [MenuBarSegment],
        style: MenuBarStyle,
        colorAlerts: Bool = true,
        height: CGFloat = NSStatusBar.system.thickness
    ) -> NSImage {
        guard !segments.isEmpty else { return emptyImage(height: height) }

        let layouts = segments.map { segment in
            Layout(
                segment: segment,
                style: style,
                glyph: style == .icon ? ProviderGlyph.glyph(for: segment.provider) : nil
            )
        }
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
                draw(layout, at: x, height: height, foreground: foreground, colorAlerts: colorAlerts)
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

    private static func draw(_ layout: Layout, at x: CGFloat, height: CGFloat, foreground: NSColor, colorAlerts: Bool) {
        let segment = layout.segment
        let frame = NSRect(x: x, y: (height - layout.gauge) / 2, width: layout.gauge, height: layout.gauge)

        switch layout.style {
        case .icon:
            drawRing(segment, in: frame, foreground: foreground, colorAlerts: colorAlerts)
            let inner = frame.insetBy(dx: Metrics.ringWidth + Metrics.glyphGap, dy: Metrics.ringWidth + Metrics.glyphGap)
            drawGlyph(layout.glyph, provider: segment.provider, in: inner, color: foreground)
        case .percent:
            drawPie(segment, in: frame, foreground: foreground, colorAlerts: colorAlerts)
        }

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

    /// Icon style: a track plus an arc that fills clockwise from the top.
    private static func drawRing(_ segment: MenuBarSegment, in frame: NSRect, foreground: NSColor, colorAlerts: Bool) {
        let ringRect = frame.insetBy(dx: Metrics.ringWidth / 2, dy: Metrics.ringWidth / 2)

        let track = NSBezierPath(ovalIn: ringRect)
        track.lineWidth = Metrics.ringWidth
        foreground.withAlphaComponent(0.25).setStroke()
        track.stroke()

        guard let percent = segment.ringPercent, percent > 0 else { return }
        let fraction = min(percent / 100, 1)
        let arc = NSBezierPath()
        arc.appendArc(
            withCenter: NSPoint(x: ringRect.midX, y: ringRect.midY), radius: ringRect.width / 2,
            startAngle: 90, endAngle: 90 - 360 * fraction, clockwise: true
        )
        arc.lineWidth = Metrics.ringWidth
        arc.lineCapStyle = .round
        ringColor(for: segment.ringSeverity, foreground: foreground, colorAlerts: colorAlerts).setStroke()
        arc.stroke()
    }

    /// Percent style (version 0.1): an outlined circle with a pie wedge.
    private static func drawPie(_ segment: MenuBarSegment, in frame: NSRect, foreground: NSColor, colorAlerts: Bool) {
        let outlineRect = frame.insetBy(dx: Metrics.pieOutline / 2, dy: Metrics.pieOutline / 2)
        let outline = NSBezierPath(ovalIn: outlineRect)
        outline.lineWidth = Metrics.pieOutline
        foreground.setStroke()
        outline.stroke()

        guard let percent = segment.ringPercent, percent > 0 else { return }
        let fraction = min(percent / 100, 1)
        let inner = outlineRect.insetBy(dx: Metrics.pieInset, dy: Metrics.pieInset)
        let center = NSPoint(x: inner.midX, y: inner.midY)
        let pie = NSBezierPath()
        pie.move(to: center)
        pie.appendArc(withCenter: center, radius: inner.width / 2, startAngle: 90, endAngle: 90 - 360 * fraction, clockwise: true)
        pie.close()
        ringColor(for: segment.ringSeverity, foreground: foreground, colorAlerts: colorAlerts).setFill()
        pie.fill()
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

    private static func ringColor(for severity: Severity, foreground: NSColor, colorAlerts: Bool) -> NSColor {
        guard colorAlerts else { return foreground }
        switch severity {
        case .normal: return foreground
        case .warning: return .systemOrange
        case .critical: return .systemRed
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
