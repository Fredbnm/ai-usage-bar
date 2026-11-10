import AppKit
import UsageCore

/// Looks for a newer release now and then, and installs it in place on request:
/// download the .dmg, check it holds this same app at the announced version,
/// swap the bundle once the app has quit, and reopen it.
@MainActor
final class Updater: ObservableObject {
    enum Phase: Equatable {
        case idle
        case checking
        case upToDate
        case downloading
        case installing
        case failed(String)
    }

    /// A newer release with a Mac installer, if there is one.
    @Published private(set) var available: AppRelease?
    @Published private(set) var phase: Phase = .idle

    /// The running version; nil outside an app bundle (`swift run`), which never updates.
    let current: AppVersion?
    private let isPreview: Bool
    private var isChecking = false
    private var lastCheck: Date?
    private var timer: Timer?
    private var clearTask: Task<Void, Never>?

    init(current: AppVersion? = Bundle.main.shortVersion) {
        self.current = current
        isPreview = false
    }

    /// Shows a made-up update without any network: used by `--snapshot --update-available`.
    init(preview version: AppVersion, current: AppVersion?) {
        self.current = current
        isPreview = true
        available = AppRelease(version: version, page: AppUpdates.releasesPage, assets: [])
    }

    var currentText: String { current?.description ?? "dev" }

    var showsBanner: Bool { available != nil || phase != .idle }

    /// Whether the new version can replace this copy: an .app in a folder this user can write to.
    /// Otherwise the button opens the download page instead.
    var canInstallInPlace: Bool {
        if isPreview { return true }
        let bundle = Bundle.main.bundleURL
        let files = FileManager.default
        return bundle.pathExtension == "app"
            && files.isWritableFile(atPath: bundle.path)
            && files.isWritableFile(atPath: bundle.deletingLastPathComponent().path)
    }

    // MARK: Checking

    func start() {
        guard current != nil else { return }
        Task {
            // Let the usage requests go first.
            try? await Task.sleep(nanoseconds: 10_000_000_000)
            await check()
        }
        let timer = Timer(timeInterval: AppUpdates.checkInterval, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.check() }
        }
        timer.tolerance = 600
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// When the popover opens or the Mac wakes, since the timer does not fire during sleep.
    func checkIfStale() {
        guard let lastCheck, Date().timeIntervalSince(lastCheck) >= AppUpdates.checkInterval else { return }
        Task { await check() }
    }

    /// `manual` is the "Check for updates" item: it also says when there is nothing new or the check failed.
    func check(manual: Bool = false) async {
        guard let current else {
            if manual {
                flash(.failed(tr(
                    "Esta cópia foi compilada localmente e não se atualiza sozinha.",
                    "This copy was built locally and does not update itself."
                )))
            }
            return
        }
        guard !isPreview, !isChecking, phase != .downloading, phase != .installing else { return }
        isChecking = true
        defer { isChecking = false }
        if manual { phase = .checking }
        do {
            let release = try await AppUpdates.fetchLatest()
            lastCheck = Date()
            available = AppUpdates.update(from: release, current: current, asset: AppUpdates.macAsset)
            if manual {
                if available == nil { flash(.upToDate) } else { phase = .idle }
            } else if case .failed = phase {
                phase = .idle
            }
        } catch {
            if manual {
                flash(.failed(tr(
                    "Não consegui procurar atualizações agora. Tente de novo mais tarde.",
                    "Couldn't check for updates right now. Try again later."
                )))
            }
        }
    }

    /// A passing message that clears itself after a few seconds.
    private func flash(_ phase: Phase) {
        self.phase = phase
        clearTask?.cancel()
        clearTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 6_000_000_000)
            guard !Task.isCancelled, let self, self.phase == phase else { return }
            self.phase = .idle
        }
    }

    // MARK: Installing

    func install() {
        guard let release = available, !isPreview, phase != .downloading, phase != .installing else { return }
        guard canInstallInPlace,
              let asset = release.asset(named: AppUpdates.macAsset),
              let bundleID = Bundle.main.bundleIdentifier
        else {
            openReleasePage()
            return
        }
        clearTask?.cancel()
        phase = .downloading
        let target = Bundle.main.bundleURL
        Task {
            do {
                let staged = try await UpdateInstaller.stage(asset, version: release.version, bundleID: bundleID)
                phase = .installing
                try UpdateInstaller.replace(target, with: staged, after: ProcessInfo.processInfo.processIdentifier, relaunch: true)
                // Let "Installing…" draw before the app goes away.
                try? await Task.sleep(nanoseconds: 400_000_000)
                NSApp.terminate(nil)
            } catch {
                phase = .failed((error as? UpdateInstaller.Failure)?.message ?? UpdateInstaller.Failure.install.message)
            }
        }
    }

    func openReleasePage() {
        NSWorkspace.shared.open(available?.page ?? AppUpdates.releasesPage)
    }
}

/// The file work of an update. Runs off the main thread except for `replace`, which only starts a process.
enum UpdateInstaller {
    struct Failure: Error {
        let message: String

        static var download: Failure {
            Failure(message: tr(
                "Não consegui baixar a nova versão. Tente de novo ou baixe pela página.",
                "Couldn't download the new version. Try again, or download it from the page."
            ))
        }

        static var contents: Failure {
            Failure(message: tr(
                "O arquivo baixado não é o esperado, então nada foi instalado. Baixe pela página.",
                "The downloaded file is not what was expected, so nothing was installed. Download it from the page."
            ))
        }

        static var install: Failure {
            Failure(message: tr(
                "Não consegui instalar a nova versão. Baixe pela página.",
                "Couldn't install the new version. Download it from the page."
            ))
        }
    }

    /// Downloads the .dmg and copies the app inside it to a temporary folder,
    /// after checking it is this app (same bundle id) at the announced version
    /// and that its code signature is intact. Returns the copied app.
    static func stage(_ asset: AppRelease.Asset, version: AppVersion, bundleID: String) async throws -> URL {
        let files = FileManager.default
        let work = files.temporaryDirectory.appendingPathComponent("AIUsageBar-update-\(UUID().uuidString)", isDirectory: true)
        do {
            try files.createDirectory(at: work, withIntermediateDirectories: true)
            let (downloaded, response) = try await URLSession.shared.download(from: asset.url)
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw Failure.download }
            let dmg = work.appendingPathComponent("update.dmg")
            try files.moveItem(at: downloaded, to: dmg)
            if asset.size > 0, (try? dmg.resourceValues(forKeys: [.fileSizeKey]).fileSize) != asset.size {
                throw Failure.download
            }
            return try await Task.detached { try unpack(dmg, into: work, version: version, bundleID: bundleID) }.value
        } catch {
            try? files.removeItem(at: work)
            throw error as? Failure ?? Failure.download
        }
    }

    private static func unpack(_ dmg: URL, into work: URL, version: AppVersion, bundleID: String) throws -> URL {
        let mount = work.appendingPathComponent("mount", isDirectory: true)
        try FileManager.default.createDirectory(at: mount, withIntermediateDirectories: true)
        guard run("/usr/bin/hdiutil", ["attach", dmg.path, "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount.path, "-quiet"])
        else { throw Failure.contents }
        defer { run("/usr/bin/hdiutil", ["detach", mount.path, "-force", "-quiet"]) }

        let items = (try? FileManager.default.contentsOfDirectory(at: mount, includingPropertiesForKeys: nil)) ?? []
        guard let app = items.first(where: { $0.pathExtension == "app" }),
              let bundle = Bundle(url: app),
              bundle.bundleIdentifier == bundleID,
              (bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String).flatMap(AppVersion.init) == version
        else { throw Failure.contents }

        let staged = work.appendingPathComponent(app.lastPathComponent, isDirectory: true)
        guard run("/usr/bin/ditto", [app.path, staged.path]) else { throw Failure.install }
        guard run("/usr/bin/codesign", ["--verify", "--deep", staged.path]) else { throw Failure.contents }
        return staged
    }

    /// Starts a small script that waits for `pid` to exit, swaps the bundles
    /// (putting the old one back if the move fails), deletes the temporary
    /// folder and reopens the app.
    static func replace(_ target: URL, with staged: URL, after pid: Int32, relaunch: Bool) throws {
        let script = """
        pid="$1"; target="$2"; staged="$3"; relaunch="$4"; work="$5"
        while kill -0 "$pid" 2>/dev/null; do sleep 0.2; done
        backup="$target.previous"
        rm -rf "$backup"
        if mv "$target" "$backup"; then
          if mv "$staged" "$target"; then rm -rf "$backup"; else mv "$backup" "$target"; fi
        fi
        case "$work" in */AIUsageBar-update-*) rm -rf "$work" ;; esac
        if [ "$relaunch" = 1 ]; then /usr/bin/open "$target"; fi
        """
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        process.arguments = [
            "-c", script, "sh",
            String(pid), target.path, staged.path, relaunch ? "1" : "0", staged.deletingLastPathComponent().path,
        ]
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            throw Failure.install
        }
    }

    @discardableResult
    private static func run(_ tool: String, _ arguments: [String]) -> Bool {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return false
        }
        process.waitUntilExit()
        return process.terminationStatus == 0
    }
}

extension Bundle {
    /// CFBundleShortVersionString of an .app; nil for a bare executable.
    var shortVersion: AppVersion? {
        guard bundleURL.pathExtension == "app" else { return nil }
        return (object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String).flatMap(AppVersion.init)
    }
}
