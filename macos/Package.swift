// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "AIUsageBar",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "AIUsageBar", targets: ["AIUsageBar"]),
    ],
    targets: [
        // Data layer: credentials, HTTP client, response models, formatting.
        .target(name: "UsageCore"),
        // Menu bar app (AppKit status item + SwiftUI popover).
        .executableTarget(name: "AIUsageBar", dependencies: ["UsageCore"]),
        .testTarget(name: "UsageCoreTests", dependencies: ["UsageCore"]),
    ]
)
