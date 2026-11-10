import Foundation
import os

/// The language the whole app is shown in.
public enum AppLanguage: String, CaseIterable, Identifiable, Sendable {
    case portuguese = "pt"
    case english = "en"

    public var id: String { rawValue }

    /// Each language named in itself, so it can be found whatever is selected.
    public var nativeName: String {
        switch self {
        case .portuguese: "Português"
        case .english: "English"
        }
    }

    public var locale: Locale {
        switch self {
        case .portuguese: Locale(identifier: "pt_BR")
        case .english: Locale(identifier: "en_US")
        }
    }

    /// Portuguese for a Mac set to Portuguese, English otherwise.
    public static var systemDefault: AppLanguage {
        let preferred = Locale.preferredLanguages.first?.lowercased() ?? ""
        return preferred.hasPrefix("pt") ? .portuguese : .english
    }

    private static let storage = OSAllocatedUnfairLock(initialState: AppLanguage.systemDefault)

    /// Read by every piece of text the app produces; snapshots are fetched
    /// off the main thread, hence the lock.
    public static var current: AppLanguage {
        get { storage.withLock { $0 } }
        set { storage.withLock { $0 = newValue } }
    }
}

/// The text for the current language: `tr("Semanal", "Weekly")`.
public func tr(_ portuguese: String, _ english: String) -> String {
    AppLanguage.current == .portuguese ? portuguese : english
}
