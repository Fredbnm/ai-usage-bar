import AppKit
import UsageCore

/// Draws the status item: a small pie gauge followed by the percentage.
/// Rendered as a template image so macOS tints it for light/dark menu bars.
enum MenuBarIcon {
    static func image(percent: Double?, showsAlert: Bool) -> NSImage {
        let text: String
        if let percent {
            text = UsageFormat.percent(percent)
        } else {
            text = showsAlert ? "!" : "–"
        }
        let font = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium)
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.black]
        let textSize = (text as NSString).size(withAttributes: attributes)

        let gauge: CGFloat = 14
        let gap: CGFloat = 4
        let height: CGFloat = 18
        let size = NSSize(width: ceil(gauge + gap + textSize.width), height: height)

        let image = NSImage(size: size, flipped: false) { _ in
            let ringRect = NSRect(x: 0, y: (height - gauge) / 2, width: gauge, height: gauge)
                .insetBy(dx: 0.75, dy: 0.75)
            let ring = NSBezierPath(ovalIn: ringRect)
            ring.lineWidth = 1.5
            NSColor.black.setStroke()
            ring.stroke()

            if let percent, percent > 0 {
                let fraction = min(max(percent / 100, 0), 1)
                let inner = ringRect.insetBy(dx: 2.25, dy: 2.25)
                let center = NSPoint(x: inner.midX, y: inner.midY)
                let pie = NSBezierPath()
                pie.move(to: center)
                pie.appendArc(
                    withCenter: center, radius: inner.width / 2,
                    startAngle: 90, endAngle: 90 - 360 * fraction, clockwise: true
                )
                pie.close()
                NSColor.black.setFill()
                pie.fill()
            }

            let origin = NSPoint(x: gauge + gap, y: (height - textSize.height) / 2)
            (text as NSString).draw(at: origin, withAttributes: attributes)
            return true
        }
        image.isTemplate = true
        return image
    }
}
