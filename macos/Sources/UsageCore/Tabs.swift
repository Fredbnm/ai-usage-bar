import Foundation

/// What one tab shows next to the agent's name.
public struct TabSummary: Equatable, Sendable {
    public var text: String
    public var severity: Severity
    public var isWarning: Bool

    /// The fullest limit's percentage, so an agent about to run out stands out
    /// without opening its tab; "!" when it failed and there is nothing to show.
    public static func make(snapshot: ProviderSnapshot?, hasError: Bool) -> TabSummary {
        if let snapshot, let fullest = snapshot.items.max(by: { $0.percent < $1.percent }) {
            return TabSummary(text: UsageFormat.percent(fullest.percent), severity: fullest.severity, isWarning: hasError)
        }
        return hasError
            ? TabSummary(text: "!", severity: .warning, isWarning: true)
            : TabSummary(text: "–", severity: .normal, isWarning: false)
    }
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
