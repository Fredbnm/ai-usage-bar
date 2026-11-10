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
        return Cache[(provider, color)] = FromBitmap(rendered, Dips);
    }

    /// <summary>A frozen WPF image of a Skia bitmap that is shown <paramref name="dips"/> device-independent pixels wide.</summary>
    public static BitmapSource FromBitmap(SKBitmap bitmap, double dips)
    {
        using var bgra = bitmap.Copy(SKColorType.Bgra8888);
        var dpi = 96 * bitmap.Width / dips;
        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, dpi, dpi, PixelFormats.Pbgra32, null, bgra.Bytes, bitmap.Width * 4);
        source.Freeze();
        return source;
    }
}
