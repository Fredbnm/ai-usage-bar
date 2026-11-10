import SwiftUI

@main
struct AIUsageBarApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    // The UI lives in the status item managed by AppDelegate; SwiftUI just
    // needs a scene. LSUIElement in Info.plist keeps the app out of the Dock.
    var body: some Scene {
        Settings { EmptyView() }
    }
}
