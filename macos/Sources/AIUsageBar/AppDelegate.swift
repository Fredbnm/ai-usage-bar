import AppKit
import Combine
import SwiftUI
import UsageCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem?
    private var popover: NSPopover?
    private var store: UsageStore?
    private var updater: Updater?
    private var cancellables = Set<AnyCancellable>()
    private var appearanceObservation: NSKeyValueObservation?

    func applicationDidFinishLaunching(_ notification: Notification) {
        if let command = CLICommand(arguments: CommandLine.arguments) {
            Task {
                await command.run()
                exit(0)
            }
            return
        }

        let store = UsageStore()
        self.store = store
        let updater = Updater()
        self.updater = updater
        setUpStatusItem()
        setUpPopover(store: store, updater: updater)

        store.objectWillChange
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateStatusItem() }
            .store(in: &cancellables)

        NSWorkspace.shared.notificationCenter
            .publisher(for: NSWorkspace.didWakeNotification)
            .sink { [weak store, weak updater] _ in
                Task { await store?.refresh(force: true) }
                updater?.checkIfStale()
            }
            .store(in: &cancellables)

        updateStatusItem()
        store.start()
        updater.start()
    }

    private func setUpStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.target = self
        item.button?.action = #selector(togglePopover(_:))
        item.button?.imagePosition = .imageOnly
        statusItem = item
        // The icon is drawn in color, so redraw it when the menu bar flips
        // between light and dark (wallpaper, Dark Mode, full-screen apps).
        appearanceObservation = item.button?.observe(\.effectiveAppearance) { [weak self] _, _ in
            Task { @MainActor in self?.updateStatusItem() }
        }
    }

    private func setUpPopover(store: UsageStore, updater: Updater) {
        let popover = NSPopover()
        popover.behavior = .transient
        popover.animates = true
        let host = NSHostingController(rootView: PopoverView(store: store, updater: updater, onQuit: { NSApp.terminate(nil) }))
        host.sizingOptions = .preferredContentSize
        popover.contentViewController = host
        self.popover = popover
    }

    @objc private func togglePopover(_ sender: Any?) {
        guard let popover, let button = statusItem?.button else { return }
        if popover.isShown {
            popover.performClose(sender)
            return
        }
        if #available(macOS 14.0, *) {
            NSApp.activate()
        } else {
            NSApp.activate(ignoringOtherApps: true)
        }
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
        popover.contentViewController?.view.window?.makeKey()
        Task { await store?.refreshIfStale() }
        updater?.checkIfStale()
    }

    private func updateStatusItem() {
        guard let store, let button = statusItem?.button else { return }
        button.image = MenuBarIcon.image(
            segments: store.menuBarSegments,
            style: store.menuBarStyle,
            colorAlerts: store.colorAlerts
        )
        button.toolTip = store.tooltip
    }
}
