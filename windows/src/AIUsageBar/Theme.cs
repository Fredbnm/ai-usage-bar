using System.Windows.Media;
using Microsoft.Win32;

namespace AIUsageBar;

/// <summary>Follows Windows' light/dark settings and accent color.</summary>
internal static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Apps (the popup) use the dark theme.</summary>
    public static bool AppsAreDark => ReadDword(PersonalizeKey, "AppsUseLightTheme") == 0;

    /// <summary>The taskbar (tray icons) uses the dark theme; Windows 10 default.</summary>
    public static bool TaskbarIsDark => ReadDword(PersonalizeKey, "SystemUsesLightTheme") != 1;

    public static void Apply(bool? forceDark = null)
    {
        var dark = forceDark ?? AppsAreDark;
        var resources = System.Windows.Application.Current.Resources;
        void Set(string key, uint argb) => resources[key] = Freeze(new SolidColorBrush(Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)));

        if (dark)
        {
            Set("PopupBackground", 0xFF2B2B2B);
            Set("PopupBorder", 0xFF3D3D3D);
            Set("PrimaryText", 0xFFFFFFFF);
            Set("SecondaryText", 0xFFA6A6A6);
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
            Set("PrimaryText", 0xFF1A1A1A);
            Set("SecondaryText", 0xFF5F5F5F);
            Set("TrackBrush", 0x1A000000);
            Set("Divider", 0xFFE5E5E5);
            Set("BadgeBackground", 0x0F000000);
            Set("HoverBackground", 0x0F000000);
            Set("NeutralBarBrush", 0xFF9A9A9A);
        }
        Set("ErrorBackground", 0x26FF8C00);
        Set("WarningBrush", 0xFFFF8C00);
        Set("CriticalBrush", 0xFFE81123);
        var accent = AccentColor(dark);
        resources["AccentBrush"] = Freeze(new SolidColorBrush(accent));
        resources["UpdateBackground"] = Freeze(new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B)));
    }

    /// <summary>Windows' accent color (DWM stores it as 0xAABBGGRR).</summary>
    private static Color AccentColor(bool dark)
    {
        if (ReadDword(@"Software\Microsoft\Windows\DWM", "AccentColor") is { } abgr)
        {
            return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        }
        return dark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0);
    }

    private static int? ReadDword(string path, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static T Freeze<T>(T freezable) where T : System.Windows.Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
