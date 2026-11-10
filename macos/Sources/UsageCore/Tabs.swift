import Foundation

/// What one tab shows next to the agent's name.
public struct TabSummary: Equatable, Sendable {
    public var text: String
    public var severity: Severity
    public var isWarning: Bool

    /// The chosen limit's percentage (see `TabLimit`), by default the fullest one so an
    /// agent about to run out stands out without opening its tab; "!" when it failed and
    /// there is nothing to show.
    public static func make(snapshot: ProviderSnapshot?, hasError: Bool, limit: String = TabLimit.highest) -> TabSummary {
        if let snapshot, let shown = TabLimit.item(in: snapshot.items, for: limit) {
            return TabSummary(text: UsageFormat.percent(shown.percent), severity: shown.severity, isWarning: hasError)
        }
        return hasError
            ? TabSummary(text: "!", severity: .warning, isWarning: true)
            : TabSummary(text: "–", severity: .normal, isWarning: false)
    }
}

/// Which limit an agent's tab shows: the fullest one (the default) or one the user picked,
/// saved as a key built from the limit's kind and scope ("weekly_scoped:Fable"), which
/// stays the same between refreshes.
public enum TabLimit {
    public static let highest = "highest"

    public static func key(for item: LimitItem) -> String {
        item.scopeName.map { "\(item.kind):\($0)" } ?? item.kind
    }

    /// The picked limit; the fullest one when the pick is "highest" or the limit is gone.
    public static func item(in items: [LimitItem], for key: String) -> LimitItem? {
        if key != highest, let picked = items.first(where: { self.key(for: $0) == key }) { return picked }
        return items.max { $0.percent < $1.percent }
    }

    /// The choices for one agent: the fullest limit first, then each of its limits in
    /// display order. A saved pick missing from the current data stays listed so it
    /// still shows its checkmark.
    public static func options(items: [LimitItem], saved: String) -> [String] {
        var keys = [highest]
        for item in items where !keys.contains(key(for: item)) { keys.append(key(for: item)) }
        if !keys.contains(saved) { keys.append(saved) }
        return keys
    }

    public static func title(for key: String) -> String {
        if key == highest { return tr("Maior uso", "Highest usage") }
        let parts = key.split(separator: ":", maxSplits: 1).map(String.init)
        guard let kind = parts.first else { return key }
        return UsageResponse.title(kind: kind, scopeName: parts.count > 1 ? parts[1] : nil)
    }
}

/// How the popover lays out the agents.
public enum PopupLayout: String, CaseIterable, Identifiable, Sendable {
    /// One tab per agent, once two or more are connected.
    case tabs
    /// Every agent one below the other on a single screen, as before tabs.
    case list

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .tabs: tr("Uma aba por agente", "One tab per agent")
        case .list: tr("Todas as IAs juntas", "All AIs together")
        }
    }

    public static let `default` = PopupLayout.tabs
}

/// Which tab the popover shows, kept free of UI so it can be tested.
public enum PopupTabs {
    /// The setup / onboarding tab.
    public static let agentsKey = "agents"

    /// One agent needs no strip: the popover looks as it always did.
    public static func showStrip(connectedCount: Int) -> Bool { connectedCount >= 2 }

    /// The tab to show. First run and "nothing connected" open the Agents tab;
    /// a saved agent that is gone falls back to the first connected one.
    public static func resolve(requested: String?, connected: [ProviderID], onboardingDone: Bool) -> String {
        if connected.isEmpty || !onboardingDone { return agentsKey }
        if requested == agentsKey { return agentsKey }
        if let requested, connected.contains(where: { $0.rawValue == requested }) { return requested }
        return connected[0].rawValue
    }

    /// Next / previous tab key, wrapping, for arrow keys.
    public static func step(keys: [String], current: String, direction: Int) -> String {
        guard !keys.isEmpty else { return current }
        let index = keys.firstIndex(of: current) ?? 0
        return keys[((index + direction) % keys.count + keys.count) % keys.count]
    }
}
