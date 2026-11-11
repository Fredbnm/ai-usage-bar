// Draws the background of the .dmg window: what to do, an arrow from the app
// to Applications, and how to open the app the first time.
//
//   swift scripts/make-dmg-background.swift out.png out@2x.png
//
// scripts/make-dmg-background.sh turns both into Support/dmg-background.tiff.
// The layout matches scripts/dmg-settings.py: the app icon centred at
// (170, 200) and Applications at (470, 200). Finder's window size includes the
// title bar, so the window is 428 points tall and the picture runs to 430: the
// visible part is about 400 points, whatever the title bar's height.
//
// Finder draws the icon labels in dark text over any background picture, also
// in Dark Mode, so the background stays light.
import AppKit

let width: CGFloat = 640
let height: CGFloat = 430
let appCenter = CGPoint(x: 170, y: 200)
let applicationsCenter = CGPoint(x: 470, y: 200)

/// Same green as the ring in the app icon, a little darker to read on white.
let accent = NSColor(calibratedRed: 0.11, green: 0.66, blue: 0.53, alpha: 1)
let ink = NSColor(calibratedRed: 0.12, green: 0.14, blue: 0.19, alpha: 1)
let muted = NSColor(calibratedRed: 0.38, green: 0.41, blue: 0.47, alpha: 1)

/// Window coordinates (origin at the top left, like Finder's icon positions) to AppKit's.
func point(_ x: CGFloat, _ y: CGFloat) -> NSPoint { NSPoint(x: x, y: height - y) }

func drawCentered(_ text: String, font: NSFont, color: NSColor, top: CGFloat) {
    let string = NSAttributedString(string: text, attributes: [.font: font, .foregroundColor: color])
    let size = string.size()
    string.draw(at: point((width - size.width) / 2, top + size.height))
}

func render(scale: CGFloat, to path: String) throws {
    guard let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: Int(width * scale), pixelsHigh: Int(height * scale),
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    ) else { throw CocoaError(.fileWriteUnknown) }
    rep.size = NSSize(width: width, height: height)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

    // A soft light gradient.
    NSGradient(
        starting: NSColor(calibratedRed: 0.99, green: 0.99, blue: 1.0, alpha: 1),
        ending: NSColor(calibratedRed: 0.93, green: 0.95, blue: 0.96, alpha: 1)
    )!.draw(in: NSRect(x: 0, y: 0, width: width, height: height), angle: -90)

    // A faint halo behind each icon.
    for center in [appCenter, applicationsCenter] {
        NSGradient(starting: accent.withAlphaComponent(0.13), ending: accent.withAlphaComponent(0))!
            .draw(fromCenter: point(center.x, center.y), radius: 0, toCenter: point(center.x, center.y), radius: 92, options: [])
    }

    drawCentered("Install AI Usage Bar", font: .systemFont(ofSize: 22, weight: .bold), color: ink, top: 38)
    drawCentered("Drag the app onto the Applications folder.", font: .systemFont(ofSize: 14), color: muted, top: 72)

    // A dashed arc from the app to Applications, ending in an arrowhead.
    let start = point(appCenter.x + 78, appCenter.y - 4)
    let end = point(applicationsCenter.x - 80, applicationsCenter.y - 4)
    let control1 = point(appCenter.x + 115, appCenter.y - 46)
    let control2 = point(applicationsCenter.x - 115, applicationsCenter.y - 46)
    let arc = NSBezierPath()
    arc.move(to: start)
    arc.curve(to: end, controlPoint1: control1, controlPoint2: control2)
    arc.lineWidth = 4
    arc.lineCapStyle = .round
    arc.setLineDash([9, 9], count: 2, phase: 0)
    accent.setStroke()
    arc.stroke()

    // The arrowhead points along the curve's last stretch.
    let angle = atan2(end.y - control2.y, end.x - control2.x)
    let length: CGFloat = 16
    let spread: CGFloat = .pi / 6
    let head = NSBezierPath()
    head.move(to: NSPoint(x: end.x - length * cos(angle - spread), y: end.y - length * sin(angle - spread)))
    head.line(to: end)
    head.line(to: NSPoint(x: end.x - length * cos(angle + spread), y: end.y - length * sin(angle + spread)))
    head.lineWidth = 4
    head.lineCapStyle = .round
    head.lineJoinStyle = .round
    head.stroke()

    drawCentered(
        "First time: open Applications, right-click AI Usage Bar and choose Open.",
        font: .systemFont(ofSize: 12), color: muted, top: 340
    )

    NSGraphicsContext.restoreGraphicsState()
    guard let png = rep.representation(using: .png, properties: [:]) else { throw CocoaError(.fileWriteUnknown) }
    try png.write(to: URL(fileURLWithPath: path))
}

let arguments = CommandLine.arguments
guard arguments.count == 3 else {
    FileHandle.standardError.write(Data("usage: make-dmg-background.swift out.png out@2x.png\n".utf8))
    exit(2)
}
try render(scale: 1, to: arguments[1])
try render(scale: 2, to: arguments[2])
