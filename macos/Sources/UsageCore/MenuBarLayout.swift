import Foundation

/// How each service's block is drawn in the menu bar.
public enum MenuBarStyle: String, CaseIterable, Identifiable, Sendable {
    /// The service's glyph inside a ring that fills with the usage.
    case icon
    /// A small pie gauge and the numbers only (the original look).
    case percent

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .icon: tr("Ícone da IA com anel", "AI icon with ring")
        case .percent: tr("Só o percentual", "Percentage only")
        }
    }

    public static let `default` = MenuBarStyle.icon
}

/// What one service shows in the menu bar.
public enum MenuBarDisplay: String, CaseIterable, Identifiable, Sendable {
    case session
    case weekly
    /// Session on top, weekly below; the ring follows the session.
    case both
    case hidden

    public var id: String { rawValue }

    public func label(for provider: ProviderID) -> String {
        switch self {
        case .session: tr("Só a sessão atual", "Current session only")
        case .weekly:
            if provider == .copilot || provider == .cursor { tr("Mensal", "Monthly") }
            else if provider == .gemini { tr("Diário", "Daily") }
            else { provider.menuBarDisplayOptions.contains(.session) ? tr("Só a semanal", "Weekly only") : tr("Semanal", "Weekly") }
        case .both: tr("Sessão atual + semanal", "Session + weekly")
        case .hidden: tr("Não mostrar", "Hide")
        }
    }
}

extension ProviderID {
    public var menuBarDisplayOptions: [MenuBarDisplay] {
        switch self {
        case .claude, .codex: [.session, .weekly, .both, .hidden]
        case .copilot, .cursor, .gemini, .grokBot: [.weekly, .hidden]
        }
    }

    public var defaultMenuBarDisplay: MenuBarDisplay {
        switch self {
        case .claude, .codex: .both
        case .copilot, .cursor, .gemini, .grokBot: .weekly
        }
    }
}

/// One service's block in the menu bar: a ring around the service icon
/// plus one or two lines of percentages.
public struct MenuBarSegment: Equatable, Sendable {
    public var provider: ProviderID
    /// Fills the ring; nil while there is no data.
    public var ringPercent: Double?
    public var ringSeverity: Severity
    /// One or two lines; the first one is what the ring shows.
    public var lines: [String]

    public init(provider: ProviderID, ringPercent: Double?, ringSeverity: Severity, lines: [String]) {
        self.provider = provider
        self.ringPercent = ringPercent
        self.ringSeverity = ringSeverity
        self.lines = lines
    }

    public static func make(
        provider: ProviderID,
        display: MenuBarDisplay,
        snapshot: ProviderSnapshot?,
        hasError: Bool
    ) -> MenuBarSegment? {
        if display == .hidden { return nil }
        guard let items = snapshot?.items, !items.isEmpty else {
            return MenuBarSegment(provider: provider, ringPercent: nil, ringSeverity: .normal, lines: [hasError ? "!" : "–"])
        }

        let session = items.first { $0.group == .session }
        let weekly = items.first { $0.kind == "weekly_all" } ?? items.first { $0.group == .weekly }
        let highest = items.max { $0.percent < $1.percent }

        var shown: [LimitItem]
        switch display {
        case .session: shown = [session ?? weekly].compactMap { $0 }
        case .weekly: shown = [weekly ?? session].compactMap { $0 }
        case .both: shown = [session, weekly].compactMap { $0 }
        case .hidden: return nil
        }
        if shown.isEmpty { shown = [highest].compactMap { $0 } }
        guard let ring = shown.first else { return nil }

        return MenuBarSegment(
            provider: provider,
            ringPercent: ring.percent,
            ringSeverity: ring.severity,
            lines: shown.map { UsageFormat.percent($0.percent) }
        )
    }
}
