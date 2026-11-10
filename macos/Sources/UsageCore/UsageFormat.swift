import Foundation

/// Portuguese (pt-BR) strings for percentages, durations and reset times.
public enum UsageFormat {
    public static let locale = Locale(identifier: "pt_BR")

    /// "42%"; "<1%" for usage that is above zero but would round to 0.
    public static func percent(_ value: Double) -> String {
        if value > 0, value < 0.5 { return "<1%" }
        return "\(Int(value.rounded()))%"
    }

    /// "1 h 16 min", "45 min", "2 d 4 h".
    public static func duration(_ interval: TimeInterval) -> String {
        let minutes = max(1, Int((interval / 60).rounded(.up)))
        let days = minutes / 1440
        let hours = (minutes % 1440) / 60
        let mins = minutes % 60
        if days > 0 { return hours > 0 ? "\(days) d \(hours) h" : "\(days) d" }
        if hours > 0 { return mins > 0 ? "\(hours) h \(mins) min" : "\(hours) h" }
        return "\(mins) min"
    }

    /// "Reinicia em 1 h 16 min · 13:50" for less than a day,
    /// "Reinicia qui. às 00:00 · em 1 d 11 h" beyond that.
    public static func reset(_ date: Date, now: Date = Date(), calendar: Calendar = .current) -> String {
        // Limits that reset together come back as 02:59:59.55 and 03:00:00.55;
        // rounding to the minute keeps them from looking like different days.
        let date = Date(timeIntervalSinceReferenceDate: (date.timeIntervalSinceReferenceDate / 60).rounded() * 60)
        let interval = date.timeIntervalSince(now)
        guard interval > 0 else { return "Reiniciando…" }
        let time = string(date, format: "HH:mm", calendar: calendar)
        if interval < 24 * 3600 {
            return "Reinicia em \(duration(interval)) · \(time)"
        }
        let day = calendar.isDate(date, inSameDayAs: now.addingTimeInterval(86_400))
            ? "amanhã"
            : string(date, format: "EEE", calendar: calendar)
        return "Reinicia \(day) às \(time) · em \(duration(interval))"
    }

    public static func updated(_ date: Date, now: Date = Date(), calendar: Calendar = .current) -> String {
        let elapsed = now.timeIntervalSince(date)
        if elapsed < 60 { return "Atualizado agora" }
        if elapsed < 3600 { return "Atualizado há \(Int(elapsed / 60)) min" }
        if elapsed < 86_400 { return "Atualizado há \(Int(elapsed / 3600)) h" }
        return "Atualizado em \(string(date, format: "dd/MM HH:mm", calendar: calendar))"
    }

    /// "12/11".
    public static func shortDate(_ date: Date, calendar: Calendar = .current) -> String {
        string(date, format: "dd/MM", calendar: calendar)
    }

    private static func string(_ date: Date, format: String, calendar: Calendar) -> String {
        let formatter = DateFormatter()
        formatter.locale = locale
        formatter.calendar = calendar
        formatter.timeZone = calendar.timeZone
        formatter.dateFormat = format
        return formatter.string(from: date)
    }
}
