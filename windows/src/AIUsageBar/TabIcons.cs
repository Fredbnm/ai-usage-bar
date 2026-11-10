using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using SkiaSharp;

namespace AIUsageBar;

/// <summary>WPF images for the tab icons, tinted to the theme's text color.</summary>
internal static class TabIcons
{
    /// <summary>Pixels per side; shown at 16 device-independent pixels, so crisp up to 300% scaling.</summary>
    private const int Pixels = 48;
    private const double Dips = 16;

    private static readonly Dictionary<(ProviderId, Color), ImageSource> Cache = [];

    public static ImageSource Get(ProviderId provider, Color color)
    {
        if (Cache.TryGetValue((provider, color), out var cached)) return cached;

        using var rendered = TabIconRenderer.Render(provider, InstalledApps.Glyph(provider), new SKColor(color.R, color.G, color.B), Pixels);
        using var bgra = rendered.Copy(SKColorType.Bgra8888);
        var dpi = 96 * Pixels / Dips;
        var source = BitmapSource.Create(Pixels, Pixels, dpi, dpi, PixelFormats.Pbgra32, null, bgra.Bytes, Pixels * 4);
        source.Freeze();
        return Cache[(provider, color)] = source;
    }
}
