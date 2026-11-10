using SkiaSharp;

namespace AIUsageBar.Core.Rendering;

/// <summary>
/// The small icon beside an agent's name on its tab: the same glyph the tray
/// uses (cut out of the installed app's icon), drawn in one color so it follows
/// the theme. With no app to borrow from (Copilot lives in editors) it is the
/// agent's initial in a thin circle. No brand artwork ships with the app.
/// </summary>
public static class TabIconRenderer
{
    public static SKBitmap Render(ProviderId provider, Glyph? glyph, SKColor color, int size)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var bounds = new SKRect(0, 0, size, size);
        if (glyph is not null)
        {
            // Same fit as the tray: the artwork's farthest point touches the circle.
            TrayIconRenderer.DrawGlyph(canvas, glyph, bounds, color);
            return bitmap;
        }

        var stroke = Math.Max(1f, size * 0.07f);
        var ring = SKRect.Inflate(bounds, -stroke / 2, -stroke / 2);
        using (var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = color.WithAlpha(190) })
        {
            canvas.DrawOval(ring, outline);
        }
        TrayIconRenderer.DrawNumber(canvas, provider.Initial(), SKRect.Inflate(ring, -stroke * 1.6f, -stroke * 1.6f), color);
        return bitmap;
    }
}
