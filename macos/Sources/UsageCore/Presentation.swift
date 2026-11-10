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
    public var title: String
    public var percent: Double
    public var severity: Severity
    public var resetsAt: Date?
    public var group: Group
}

public struct BreakdownItem: Identifiable, Equatable, Sendable {
    public var id: String
    public var name: String
    public var percent: Double
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
                    title: Self.title(kind: limit.kind, scopeName: limit.scope?.displayName),
                    percent: limit.percent,
                    severity: Severity(api: limit.severity, percent: limit.percent),
                    resetsAt: limit.resetsAt,
                    group: Self.group(kind: limit.kind, group: limit.group)
                )
            }
        }

        let windows: [(String, String, UsageWindow?, LimitItem.Group)] = [
            ("session", "Sessão atual", fiveHour, .session),
            ("weekly_all", "Semanal · todos os modelos", sevenDay, .weekly),
            ("weekly_opus", "Semanal · Opus", sevenDayOpus, .weekly),
            ("weekly_sonnet", "Semanal · Sonnet", sevenDaySonnet, .weekly),
        ]
        return windows.compactMap { kind, title, window, group in
            guard let percent = window?.utilization else { return nil }
            return LimitItem(
                id: kind, kind: kind, title: title, percent: percent,
                severity: Severity(api: nil, percent: percent),
                resetsAt: window?.resetsAt, group: group
            )
        }
    }

    public var breakdownItems: [BreakdownItem] {
        (sevenDayBreakdown?.rows ?? [])
            .filter { $0.percent > 0 }
            .sorted { $0.percent > $1.percent }
            .map { BreakdownItem(id: $0.key, name: Self.breakdownName(key: $0.key, displayName: $0.displayName), percent: $0.percent) }
    }

    static func title(kind: String, scopeName: String?) -> String {
        switch kind {
        case "session":
            return "Sessão atual"
        case "weekly_all":
            return "Semanal · todos os modelos"
        case "weekly_scoped":
            return "Semanal · \(scopeName ?? "limite específico")"
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

    static func breakdownName(key: String, displayName: String?) -> String {
        switch key {
        case "chat": return "Chat"
        case "other": return "Outros"
        default: return displayName ?? key.replacingOccurrences(of: "_", with: " ").capitalized
        }
    }
}
