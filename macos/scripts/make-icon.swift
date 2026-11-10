// Renders the 1024×1024 app icon PNG. Used by make-icon.sh.
// Usage: swift scripts/make-icon.swift out.png
import AppKit

let output = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "AppIcon-1024.png"
let side = 1024

guard let rep = NSBitmapImageRep(
    bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side,
    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)
else { fatalError("bitmap") }
rep.size = NSSize(width: side, height: side)

NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

let canvas = NSRect(x: 0, y: 0, width: side, height: side)
// macOS icon grid: 824 pt body centred on a 1024 canvas.
let body = canvas.insetBy(dx: 100, dy: 100)
let shape = NSBezierPath(roundedRect: body, xRadius: 185, yRadius: 185)
NSGradient(
    starting: NSColor(calibratedRed: 0.20, green: 0.22, blue: 0.28, alpha: 1),
    ending: NSColor(calibratedRed: 0.09, green: 0.10, blue: 0.13, alpha: 1)
)!.draw(in: shape, angle: -90)

let center = NSPoint(x: canvas.midX, y: canvas.midY)
let radius: CGFloat = 255
let lineWidth: CGFloat = 70

let track = NSBezierPath()
track.appendArc(withCenter: center, radius: radius, startAngle: 0, endAngle: 360)
track.lineWidth = lineWidth
NSColor(white: 1, alpha: 0.12).setStroke()
track.stroke()

let arc = NSBezierPath()
arc.appendArc(withCenter: center, radius: radius, startAngle: 90, endAngle: 90 - 360 * 0.68, clockwise: true)
arc.lineWidth = lineWidth
arc.lineCapStyle = .round
NSColor(calibratedRed: 0.27, green: 0.85, blue: 0.70, alpha: 1).setStroke()
arc.stroke()

let attributes: [NSAttributedString.Key: Any] = [
    .font: NSFont.systemFont(ofSize: 230, weight: .bold),
    .foregroundColor: NSColor.white,
]
let label = "%" as NSString
let size = label.size(withAttributes: attributes)
label.draw(at: NSPoint(x: center.x - size.width / 2, y: center.y - size.height / 2), withAttributes: attributes)

NSGraphicsContext.restoreGraphicsState()

guard let png = rep.representation(using: .png, properties: [:]) else { fatalError("png") }
try png.write(to: URL(fileURLWithPath: output))
