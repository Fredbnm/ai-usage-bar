import Foundation

/// Raw response of `GET https://api.anthropic.com/api/oauth/usage`.
///
/// This is not a public API: fields appear, disappear and change shape
/// without notice. Everything is optional and decoded leniently so that one
/// unexpected field never breaks the whole response.
public struct UsageResponse: Decodable, Equatable, Sendable {
    public var fiveHour: UsageWindow?
    public var sevenDay: UsageWindow?
    public var sevenDayOpus: UsageWindow?
    public var sevenDaySonnet: UsageWindow?
    public var limits: [UsageLimit]?
    public var sevenDayBreakdown: UsageBreakdown?
    public var extraUsage: ExtraUsage?

    enum CodingKeys: String, CodingKey {
        case fiveHour, sevenDay, sevenDayOpus, sevenDaySonnet, limits, sevenDayBreakdown, extraUsage
    }

    public init(
        fiveHour: UsageWindow? = nil,
        sevenDay: UsageWindow? = nil,
        limits: [UsageLimit]? = nil,
        sevenDayBreakdown: UsageBreakdown? = nil
    ) {
        self.fiveHour = fiveHour
        self.sevenDay = sevenDay
        self.limits = limits
        self.sevenDayBreakdown = sevenDayBreakdown
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        fiveHour = try? c.decodeIfPresent(UsageWindow.self, forKey: .fiveHour)
        sevenDay = try? c.decodeIfPresent(UsageWindow.self, forKey: .sevenDay)
        sevenDayOpus = try? c.decodeIfPresent(UsageWindow.self, forKey: .sevenDayOpus)
        sevenDaySonnet = try? c.decodeIfPresent(UsageWindow.self, forKey: .sevenDaySonnet)
        limits = (try? c.decodeIfPresent(LossyArray<UsageLimit>.self, forKey: .limits))?.elements
        sevenDayBreakdown = try? c.decodeIfPresent(UsageBreakdown.self, forKey: .sevenDayBreakdown)
        extraUsage = try? c.decodeIfPresent(ExtraUsage.self, forKey: .extraUsage)
    }

    public static func decode(_ data: Data) throws -> UsageResponse {
        try makeDecoder().decode(UsageResponse.self, from: data)
    }

    public static func makeDecoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let string = try container.decode(String.self)
            guard let date = APIDate.parse(string) else {
                throw DecodingError.dataCorruptedError(in: container, debugDescription: "Invalid date: \(string)")
            }
            return date
        }
        return decoder
    }
}

/// Legacy per-window shape (`five_hour`, `seven_day`, `seven_day_opus`, ...).
public struct UsageWindow: Decodable, Equatable, Sendable {
    public var utilization: Double?
    public var resetsAt: Date?

    enum CodingKeys: String, CodingKey { case utilization, resetsAt }

    public init(utilization: Double?, resetsAt: Date?) {
        self.utilization = utilization
        self.resetsAt = resetsAt
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        utilization = try? c.decodeIfPresent(Double.self, forKey: .utilization)
        resetsAt = try? c.decodeIfPresent(Date.self, forKey: .resetsAt)
    }
}

/// One entry of the `limits` array: the same list claude.ai shows in
/// Settings → Usage (session, weekly, weekly per model, ...).
public struct UsageLimit: Decodable, Equatable, Sendable {
    public var kind: String
    public var group: String?
    public var percent: Double
    public var severity: String?
    public var resetsAt: Date?
    public var scope: LimitScope?
    public var isActive: Bool?

    enum CodingKeys: String, CodingKey { case kind, group, percent, severity, resetsAt, scope, isActive }

    public init(
        kind: String, group: String? = nil, percent: Double, severity: String? = nil,
        resetsAt: Date? = nil, scope: LimitScope? = nil, isActive: Bool? = nil
    ) {
        self.kind = kind
        self.group = group
        self.percent = percent
        self.severity = severity
        self.resetsAt = resetsAt
        self.scope = scope
        self.isActive = isActive
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        kind = try c.decode(String.self, forKey: .kind)
        percent = try c.decode(Double.self, forKey: .percent)
        group = try? c.decodeIfPresent(String.self, forKey: .group)
        severity = try? c.decodeIfPresent(String.self, forKey: .severity)
        resetsAt = try? c.decodeIfPresent(Date.self, forKey: .resetsAt)
        scope = try? c.decodeIfPresent(LimitScope.self, forKey: .scope)
        isActive = try? c.decodeIfPresent(Bool.self, forKey: .isActive)
    }
}

public struct LimitScope: Decodable, Equatable, Sendable {
    public var model: NamedEntity?
    public var surface: NamedEntity?

    enum CodingKeys: String, CodingKey { case model, surface }

    public init(model: NamedEntity? = nil, surface: NamedEntity? = nil) {
        self.model = model
        self.surface = surface
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        model = try? c.decodeIfPresent(NamedEntity.self, forKey: .model)
        surface = try? c.decodeIfPresent(NamedEntity.self, forKey: .surface)
    }

    public var displayName: String? {
        model?.displayName ?? surface?.displayName
    }
}

public struct NamedEntity: Decodable, Equatable, Sendable {
    public var id: String?
    public var displayName: String?

    enum CodingKeys: String, CodingKey { case id, displayName }

    public init(id: String? = nil, displayName: String?) {
        self.id = id
        self.displayName = displayName
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try? c.decodeIfPresent(String.self, forKey: .id)
        displayName = try? c.decodeIfPresent(String.self, forKey: .displayName)
    }
}

/// Share of the weekly usage per product (Claude Code, Chat, Cowork, ...).
/// Percentages add up to ~100 and are *not* percentages of the limit.
public struct UsageBreakdown: Decodable, Equatable, Sendable {
    public var windowStartedAt: Date?
    public var rows: [BreakdownRow]

    enum CodingKeys: String, CodingKey { case windowStartedAt, rows }

    public init(windowStartedAt: Date? = nil, rows: [BreakdownRow]) {
        self.windowStartedAt = windowStartedAt
        self.rows = rows
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        windowStartedAt = try? c.decodeIfPresent(Date.self, forKey: .windowStartedAt)
        rows = (try? c.decodeIfPresent(LossyArray<BreakdownRow>.self, forKey: .rows))?.elements ?? []
    }
}

public struct BreakdownRow: Decodable, Equatable, Sendable {
    public var key: String
    public var displayName: String?
    public var percent: Double

    public init(key: String, displayName: String?, percent: Double) {
        self.key = key
        self.displayName = displayName
        self.percent = percent
    }
}

public struct ExtraUsage: Decodable, Equatable, Sendable {
    public var isEnabled: Bool?
    public var utilization: Double?
}

/// Decodes an array, silently dropping the elements that fail to decode.
struct LossyArray<Element: Decodable>: Decodable {
    var elements: [Element]

    private struct Skip: Decodable {
        init(from decoder: Decoder) throws {}
    }

    init(from decoder: Decoder) throws {
        var container = try decoder.unkeyedContainer()
        var elements: [Element] = []
        while !container.isAtEnd {
            if let element = try? container.decode(Element.self) {
                elements.append(element)
            } else {
                _ = try? container.decode(Skip.self)
            }
        }
        self.elements = elements
    }
}
