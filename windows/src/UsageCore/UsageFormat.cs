using static AIUsageBar.Core.Localization;

namespace AIUsageBar.Core;

/// <summary>Percentages, durations and reset times in the current language.</summary>
public static class UsageFormat
{
    /// <summary>"42%"; "&lt;1%" for usage above zero that would round to 0.</summary>
    public static string Percent(double value) =>
        value is > 0 and < 0.5 ? "<1%" : $"{(int)Math.Round(value, MidpointRounding.AwayFromZero)}%";

    /// <summary>"1 h 16 min", "45 min", "2 d 4 h" (the same in both languages).</summary>
    public static string Duration(TimeSpan interval)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(interval.TotalMinutes));
        var days = minutes / 1440;
        var hours = minutes % 1440 / 60;
        var mins = minutes % 60;
        if (days > 0) return hours > 0 ? $"{days} d {hours} h" : $"{days} d";
        if (hours > 0) return mins > 0 ? $"{hours} h {mins} min" : $"{hours} h";
        return $"{mins} min";
    }

    /// <summary>
    /// Less than a day away: "Reinicia em 1 h 16 min · 13:50" / "Resets in
    /// 1 h 16 min · 1:50 PM". Further: "Reinicia qui. às 00:00 · em 1 d 11 h"
    /// / "Resets Thu at 12:00 AM · in 1 d 11 h". A week or more away (a monthly
    /// cycle): "Reinicia dia 08/12 às 00:00 · em 27 d 11 h" / "Resets Dec 8 at …".
    /// </summary>
    public static string Reset(DateTimeOffset date, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        var current = now ?? DateTimeOffset.Now;
        zone ??= TimeZoneInfo.Local;
        // Limits that reset together come back as 02:59:59.55 and 03:00:00.55;
        // rounding to the minute keeps them from looking like different days.
        var rounded = new DateTimeOffset(
            (long)Math.Round(date.UtcTicks / (double)TimeSpan.TicksPerMinute) * TimeSpan.TicksPerMinute, TimeSpan.Zero);
        var interval = rounded - current;
        if (interval <= TimeSpan.Zero) return T("Reiniciando…", "Resetting…");

        var local = TimeZoneInfo.ConvertTime(rounded, zone);
        var time = Format(local, T("HH:mm", "h:mm tt"));
        var remaining = Duration(interval);
        if (interval < TimeSpan.FromDays(1))
        {
            return T($"Reinicia em {remaining} · {time}", $"Resets in {remaining} · {time}");
        }
        var tomorrow = TimeZoneInfo.ConvertTime(current, zone).Date.AddDays(1);
        var day = local.Date == tomorrow ? T("amanhã", "tomorrow")
            // The weekday alone would not say which week.
            : interval >= TimeSpan.FromDays(7) ? T($"dia {Format(local, "dd/MM")}", Format(local, "MMM d"))
            : Format(local, "ddd");
        return T($"Reinicia {day} às {time} · em {remaining}", $"Resets {day} at {time} · in {remaining}");
    }

    public static string Updated(DateTimeOffset date, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        var elapsed = (now ?? DateTimeOffset.Now) - date;
        if (elapsed < TimeSpan.FromMinutes(1)) return T("Atualizado agora", "Updated just now");
        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = (int)elapsed.TotalMinutes;
            return T($"Atualizado há {minutes} min", $"Updated {minutes} min ago");
        }
        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = (int)elapsed.TotalHours;
            return T($"Atualizado há {hours} h", $"Updated {hours} h ago");
        }
        var when = Format(TimeZoneInfo.ConvertTime(date, zone ?? TimeZoneInfo.Local), T("dd/MM HH:mm", "MMM d, h:mm tt"));
        return T($"Atualizado em {when}", $"Updated {when}");
    }

    /// <summary>"US$ 1.234,50" / "$1,234.50": an amount in US cents (Cursor bills in dollars).</summary>
    public static string Dollars(long cents)
    {
        var value = (cents / 100m).ToString("N2", Current.Culture());
        return T($"US$ {value}", $"${value}");
    }

    /// <summary>"12/11" / "Nov 12".</summary>
    public static string ShortDate(DateTimeOffset date, TimeZoneInfo? zone = null) =>
        Format(TimeZoneInfo.ConvertTime(date, zone ?? TimeZoneInfo.Local), T("dd/MM", "MMM d"));

    private static string Format(DateTimeOffset date, string pattern) =>
        date.ToString(pattern, Current.Culture());
}
