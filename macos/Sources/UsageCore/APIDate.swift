import Foundation

/// Parses the ISO-8601 timestamps returned by the usage endpoint, e.g.
/// `2026-11-10T16:50:00.554353+00:00`. The microsecond precision is not
/// reliably accepted by `ISO8601DateFormatter`, so the fraction is handled
/// by hand as a fallback.
public enum APIDate {
    public static func parse(_ string: String) -> Date? {
        let withFraction = ISO8601DateFormatter()
        withFraction.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = withFraction.date(from: string) { return date }

        let plain = ISO8601DateFormatter()
        plain.formatOptions = [.withInternetDateTime]
        if let date = plain.date(from: string) { return date }

        guard let range = string.range(of: #"\.\d+"#, options: .regularExpression) else { return nil }
        let fraction = Double("0" + String(string[range])) ?? 0
        var trimmed = string
        trimmed.removeSubrange(range)
        return plain.date(from: trimmed)?.addingTimeInterval(fraction)
    }
}
