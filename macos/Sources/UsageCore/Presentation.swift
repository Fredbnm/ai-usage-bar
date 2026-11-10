import Foundation

public enum Severity: Equatable, Sendable {
    case normal, warning, critical

    /// Uses the API's own severity when it is one we know, otherwise falls
    /// back to thresholds on the percentage.
    public init(api: String?, percent: Double) {
        switch api?.lowercased() {
        case "normal", "ok", "low":
            self = .normal
        case "warning", "warn", "medium", "elevated":
            self = .warning
        case let value? where ["crit", "exceed", "high", "block", "danger", "error", "max"].contains(where: { value.contains($0) }):
            self = .critical
        default:
            self = percent >= 90 ? .critical : percent >= 75 ? .warning : .normal
        }
    }
}

/// A limit ready to be displayed.
public struct LimitItem: Identifiable, Equatable, Sendable {
    public enum Group: Equatable, Sendable { case session, weekly, other }

    public var id: String
    public var kind: String
    /// Model or surface a scoped limit applies to ("Fable").
    public var scopeName: String?
    public var percent: Double
    public var severity: Severity
    public var resetsAt: Date?
    public var group: Group

    public init(
        id: String, kind: String, scopeName: String? = nil, percent: Double,
        severity: Severity, resetsAt: Date?, group: Group
    ) {
        self.id = id
        self.kind = kind
        self.scopeName = scopeName
        self.percent = percent
        self.severity = severity
        self.resetsAt = resetsAt
        self.group = group
    }

    /// Built when shown, so it follows the language setting.
    public var title: String {
        UsageResponse.title(kind: kind, scopeName: scopeName)
    }
}

public struct BreakdownItem: Identifiable, Equatable, Sendable {
    /// The API key ("claude_code", "chat", …).
    public var id: String
    public var displayName: String?
    public var percent: Double

    public var name: String {
        switch id {
        case "chat": "Chat"
        case "other": tr("Outros", "Other")
        default: displayName ?? id.replacingOccurrences(of: "_", with: " ").capitalized
        }
    }
}

extension UsageResponse {
    /// Limits in display order. Prefers the `limits` array; falls back to
    /// the older per-window fields if the array is missing.
    public var items: [LimitItem] {
        if let limits, !limits.isEmpty {
            return limits.enumerated().map { index, limit in
                LimitItem(
                    id: "\(index)-\(limit.kind)-\(limit.scope?.displayName ?? "")",
                    kind: limit.kind,
                    scopeName: limit.scope?.displayName,
                    percent: limit.percent,
                    severity: Severity(api: limit.severity, percent: limit.percent),
                    resetsAt: limit.resetsAt,
                    group: Self.group(kind: limit.kind, group: limit.group)
                )
            }
        }

        let windows: [(id: String, kind: String, scope: String?, window: UsageWindow?, group: LimitItem.Group)] = [
            ("session", "session", nil, fiveHour, .session),
            ("weekly_all", "weekly_all", nil, sevenDay, .weekly),
            ("weekly_opus", "weekly_scoped", "Opus", sevenDayOpus, .weekly),
            ("weekly_sonnet", "weekly_scoped", "Sonnet", sevenDaySonnet, .weekly),
        ]
        return windows.compactMap { entry in
            guard let percent = entry.window?.utilization else { return nil }
            return LimitItem(
                id: entry.id, kind: entry.kind, scopeName: entry.scope, percent: percent,
                severity: Severity(api: nil, percent: percent),
                resetsAt: entry.window?.resetsAt, group: entry.group
            )
        }
    }

    public var breakdownItems: [BreakdownItem] {
        (sevenDayBreakdown?.rows ?? [])
            .filter { $0.percent > 0 }
            .sorted { $0.percent > $1.percent }
            .map { BreakdownItem(id: $0.key, displayName: $0.displayName, percent: $0.percent) }
    }

    static func title(kind: String, scopeName: String?) -> String {
        switch kind {
        case "session":
            return tr("Sessão atual", "Current session")
        case "weekly_all":
            return tr("Semanal · todos os modelos", "Weekly · all models")
        case "weekly":
            return tr("Semanal", "Weekly")
        case "weekly_scoped":
            return tr("Semanal", "Weekly") + " · " + (scopeName ?? tr("limite específico", "specific limit"))
        default:
            let base = kind.replacingOccurrences(of: "_", with: " ").capitalized
            return scopeName.map { "\(base) · \($0)" } ?? base
        }
    }

    static func group(kind: String, group: String?) -> LimitItem.Group {
        if kind == "session" || group == "session" { return .session }
        if group == "weekly" || kind.hasPrefix("weekly") { return .weekly }
        return .other
    }
}
