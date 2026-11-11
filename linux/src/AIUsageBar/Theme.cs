using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Styling;

namespace AIUsageBar;

/// <summary>
/// Follows the desktop's light/dark setting and accent color, which Avalonia reads from the
/// XDG desktop portal (GNOME, KDE and most others), and picks the tray icons' color.
/// </summary>
internal static class Theme
{
    public static bool IsDark { get; private set; }

    public static IBrush Accent { get; private set; } = Brushes.DodgerBlue;
    public static IBrush Warning { get; private set; } = Brushes.DarkOrange;
    public static IBrush Critical { get; private set; } = Brushes.Red;
    public static IBrush SecondaryText { get; private set; } = Brushes.Gray;
    public static IBrush PrimaryText { get; private set; } = Brushes.Black;
    public static IBrush UpdateBackground { get; private set; } = Brushes.Transparent;
    public static IBrush ErrorBackground { get; private set; } = Brushes.Transparent;

    /// <summary>The desktop session, from <c>XDG_CURRENT_DESKTOP</c> ("GNOME", "KDE", "ubuntu:GNOME"…).</summary>
    public static string Desktop => Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";

    public static bool IsKde => Desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase);

    public static bool IsGnome => Desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase)
        || Desktop.Contains("Unity", StringComparison.OrdinalIgnoreCase);

    /// <summary>White tray icons on a dark panel, near-black ones on a light panel.</summary>
    public static bool PanelIsDark(TrayIconColor color) => color switch
    {
        TrayIconColor.White => true,
        TrayIconColor.Black => false,
        // KDE's panel follows the color scheme; GNOME's top bar is dark in both styles, as are
        // the default panels of Cinnamon, Xfce (Xubuntu), Budgie and elementary.
        _ => !IsKde || IsDark,
    };

    public static void Apply(bool? forceDark = null)
    {
        var app = Application.Current!;
        var values = app.PlatformSettings?.GetColorValues();
        var dark = forceDark ?? values?.ThemeVariant == PlatformThemeVariant.Dark;
        IsDark = dark;
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

        var resources = app.Resources;
        IBrush Set(string key, uint argb)
        {
            var brush = new ImmutableSolidColorBrush(Color.FromUInt32(argb));
            resources[key] = brush;
            return brush;
        }

        if (dark)
        {
            Set("PopupBackground", 0xFF2B2B2B);
            Set("PopupBorder", 0xFF3D3D3D);
            PrimaryText = Set("PrimaryText", 0xFFFFFFFF);
            SecondaryText = Set("SecondaryText", 0xFFA6A6A6);
            Set("TrackBrush", 0x26FFFFFF);
            Set("Divider", 0xFF3A3A3A);
            Set("BadgeBackground", 0x1FFFFFFF);
            Set("HoverBackground", 0x14FFFFFF);
            Set("NeutralBarBrush", 0xFF8A8A8A);
        }
        else
        {
            Set("PopupBackground", 0xFFF9F9F9);
            Set("PopupBorder", 0xFFE0E0E0);
            PrimaryText = Set("PrimaryText", 0xFF1A1A1A);
            SecondaryText = Set("SecondaryText", 0xFF5F5F5F);
            Set("TrackBrush", 0x1A000000);
            Set("Divider", 0xFFE5E5E5);
            Set("BadgeBackground", 0x0F000000);
            Set("HoverBackground", 0x0F000000);
            Set("NeutralBarBrush", 0xFF9A9A9A);
        }
        ErrorBackground = Set("ErrorBackground", 0x26FF8C00);
        Warning = Set("WarningBrush", 0xFFFF8C00);
        Critical = Set("CriticalBrush", 0xFFE81123);

        var accent = AccentColor(values, dark);
        Accent = Set("AccentBrush", accent.ToUInt32());
        UpdateBackground = Set("UpdateBackground", (0x26u << 24) | (accent.ToUInt32() & 0xFFFFFF));
    }

    /// <summary>
    /// The desktop's accent color when the portal reports one, lightened on dark backgrounds;
    /// otherwise the GNOME blue.
    /// </summary>
    private static Color AccentColor(PlatformColorValues? values, bool dark)
    {
        var accent = values?.AccentColor1 is { A: > 0 } reported ? reported : Color.FromRgb(0x35, 0x84, 0xE4);
        if (!dark) return accent;
        static byte Lighten(byte channel) => (byte)(channel + (255 - channel) * 0.35);
        return Color.FromRgb(Lighten(accent.R), Lighten(accent.G), Lighten(accent.B));
    }
}
