import Foundation

/// Percentages, durations and reset times in the current `AppLanguage`.
public enum UsageFormat {
    /// "42%"; "<1%" for usage that is above zero but would round to 0.
    public static func percent(_ value: Double) -> String {
        if value > 0, value < 0.5 { return "<1%" }
        return "\(Int(value.rounded()))%"
    }

    /// "1 h 16 min", "45 min", "2 d 4 h" (the same in both languages).
    public static func duration(_ interval: TimeInterval) -> String {
        let minutes = max(1, Int((interval / 60).rounded(.up)))
        let days = minutes / 1440
        let hours = (minutes % 1440) / 60
        let mins = minutes % 60
        if days > 0 { return hours > 0 ? "\(days) d \(hours) h" : "\(days) d" }
        if hours > 0 { return mins > 0 ? "\(hours) h \(mins) min" : "\(hours) h" }
        return "\(mins) min"
    }

    /// Less than a day away: "Reinicia em 1 h 16 min · 13:50" /
    /// "Resets in 1 h 16 min · 1:50 PM". Further: "Reinicia qui. às 00:00 ·
    /// em 1 d 11 h" / "Resets Thu at 12:00 AM · in 1 d 11 h". A week or more
    /// away (a monthly cycle): "Reinicia dia 08/12 às 00:00 · em 27 d 11 h" /
    /// "Resets Dec 8 at 12:00 AM · in 27 d 11 h".
    public static func reset(_ date: Date, now: Date = Date(), calendar: Calendar = .current) -> String {
        // Limits that reset together come back as 02:59:59.55 and 03:00:00.55;
        // rounding to the minute keeps them from looking like different days.
        let date = Date(timeIntervalSinceReferenceDate: (date.timeIntervalSinceReferenceDate / 60).rounded() * 60)
        let interval = date.timeIntervalSince(now)
        guard interval > 0 else { return tr("Reiniciando…", "Resetting…") }
        let time = string(date, format: tr("HH:mm", "h:mm a"), calendar: calendar)
        let remaining = duration(interval)
        if interval < 24 * 3600 {
            return tr("Reinicia em \(remaining) · \(time)", "Resets in \(remaining) · \(time)")
        }
        let day: String
        if calendar.isDate(date, inSameDayAs: now.addingTimeInterval(86_400)) {
            day = tr("amanhã", "tomorrow")
        } else if interval >= 7 * 86_400 {
            // The weekday alone would not say which week.
            day = tr("dia \(string(date, format: "dd/MM", calendar: calendar))", string(date, format: "MMM d", calendar: calendar))
        } else {
            day = string(date, format: "EEE", calendar: calendar)
        }
        return tr("Reinicia \(day) às \(time) · em \(remaining)", "Resets \(day) at \(time) · in \(remaining)")
    }

    public static func updated(_ date: Date, now: Date = Date(), calendar: Calendar = .current) -> String {
        let elapsed = now.timeIntervalSince(date)
        if elapsed < 60 { return tr("Atualizado agora", "Updated just now") }
        if elapsed < 3600 {
            let minutes = Int(elapsed / 60)
            return tr("Atualizado há \(minutes) min", "Updated \(minutes) min ago")
        }
        if elapsed < 86_400 {
            let hours = Int(elapsed / 3600)
            return tr("Atualizado há \(hours) h", "Updated \(hours) h ago")
        }
        let when = string(date, format: tr("dd/MM HH:mm", "MMM d, h:mm a"), calendar: calendar)
        return tr("Atualizado em \(when)", "Updated \(when)")
    }

    /// "US$ 1.234,50" / "$1,234.50": an amount in US cents (Cursor bills in dollars).
    public static func dollars(_ cents: Int) -> String {
        let formatter = NumberFormatter()
        formatter.locale = AppLanguage.current.locale
        formatter.numberStyle = .decimal
        formatter.minimumFractionDigits = 2
        formatter.maximumFractionDigits = 2
        let amount = NSDecimalNumber(value: cents).dividing(by: 100)
        let value = formatter.string(from: amount) ?? "\(amount)"
        return tr("US$ \(value)", "$\(value)")
    }

    /// "12/11" / "Nov 12".
    public static func shortDate(_ date: Date, calendar: Calendar = .current) -> String {
        string(date, format: tr("dd/MM", "MMM d"), calendar: calendar)
    }

    private static func string(_ date: Date, format: String, calendar: Calendar) -> String {
        let formatter = DateFormatter()
        formatter.locale = AppLanguage.current.locale
        formatter.calendar = calendar
        formatter.timeZone = calendar.timeZone
        formatter.dateFormat = format
        return formatter.string(from: date)
    }
}
