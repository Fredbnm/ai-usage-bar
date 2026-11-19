using SkiaSharp;

namespace AIUsageBar.Core.Rendering;

/// <summary>
/// Draws one service's tray icon: a ring that fills with usage around either
/// the service's glyph or the percentage. Monochrome in the taskbar's color
/// (white on a dark taskbar, near-black on a light one); only the ring changes
/// color with the percentage when alert colors are on: green up to 20%, yellow-green
/// below 40%, yellow below 60%, orange below 90%, and red from 90%. A problem on the service's status page
/// adds a small badge on the ring's top right.
/// </summary>
public static class TrayIconRenderer
{
    public static readonly SKColor Green = new(0x2E, 0xBE, 0x4F);
    public static readonly SKColor YellowGreen = new(0xA8, 0xC4, 0x3C);
    public static readonly SKColor Yellow = new(0xF0, 0xC4, 0x00);
    public static readonly SKColor Orange = new(0xFF, 0x8C, 0x00);
    public static readonly SKColor Red = new(0xE8, 0x11, 0x23);

    public static SKBitmap Render(
        TraySegment? segment,
        ProviderId provider,
        TrayStyle style,
        Glyph? glyph,
        bool colorAlerts,
        bool darkTaskbar,
        int size)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);

        var foreground = darkTaskbar ? SKColors.White : new SKColor(0x1A, 0x1A, 0x1A);
        var stroke = Math.Max(1.6f, size * 0.11f);
        var ring = new SKRect(stroke / 2, stroke / 2, size - stroke / 2, size - stroke / 2);

        using (var track = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = stroke, Color = foreground.WithAlpha(70) })
        {
            canvas.DrawOval(ring, track);
        }
        if (segment?.RingPercent is > 0 and var percent)
        {
            using var arc = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = stroke,
                StrokeCap = SKStrokeCap.Round,
                Color = RingColor(percent, foreground, colorAlerts),
            };
            canvas.DrawArc(ring, -90, (float)(360 * Math.Min(percent / 100, 1)), false, arc);
        }

        var gap = Math.Max(0.8f, size * 0.05f);
        var inner = SKRect.Inflate(ring, -(stroke / 2 + gap), -(stroke / 2 + gap));
        if (style == TrayStyle.Percent)
        {
            DrawNumber(canvas, segment?.Number ?? "–", inner, foreground);
        }
        else if (glyph is not null)
        {
            DrawGlyph(canvas, glyph, inner, foreground);
        }
        else
        {
            DrawNumber(canvas, provider.Initial(), inner, foreground);
        }
        if (segment?.Alert is { } alert) DrawBadge(canvas, size, BadgeColor(alert, foreground, colorAlerts));
        return bitmap;
    }

    /// <summary>The badge is a color only with alert colors on; otherwise it is drawn like the rest of the icon.</summary>
    private static SKColor BadgeColor(Severity alert, SKColor foreground, bool colorAlerts) =>
        !colorAlerts ? foreground : alert == Severity.Critical ? Red : Orange;

    /// <summary>The smallest badge, in pixels, with room for a "!" that can be read.</summary>
    private const float MarkedBadge = 11;

    /// <summary>
    /// The status page reports a problem: a dot 40% of the ring's width on its top right, cut out of whatever
    /// it covers by a thin transparent gap so it stands apart on any taskbar. The "!" in it is cut out too, and
    /// left out where it would only be a smudge (16–24 px icons): the dot alone says it there.
    /// </summary>
    internal static void DrawBadge(SKCanvas canvas, int size, SKColor color)
    {
        var radius = size * 0.2f;
        var center = new SKPoint(size - radius, radius);
        // About 1 px once the panel shows the icon (Linux draws it at 48 px and lets the panel scale it).
        var gap = Math.Max(1f, size / 24f);

        using var clear = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Clear };
        canvas.DrawCircle(center, radius + gap, clear);
        using (var fill = new SKPaint { IsAntialias = true, Color = color })
        {
            canvas.DrawCircle(center, radius, fill);
        }
        if (radius * 2 < MarkedBadge) return;

        var width = radius * 0.32f;
        var stem = SKRect.Create(center.X - width / 2, center.Y - radius * 0.6f, width, radius * 0.78f);
        canvas.DrawRoundRect(stem, width / 2, width / 2, clear);
        canvas.DrawCircle(center.X, center.Y + radius * 0.48f, width * 0.6f, clear);
    }

    /// <summary>The progress stroke. Alert colors off keeps the taskbar color.</summary>
    public static SKColor RingColor(double percent, SKColor foreground, bool colorAlerts) =>
        !colorAlerts ? foreground : RingBands.For(percent) switch
        {
            RingBand.Green => Green,
            RingBand.YellowGreen => YellowGreen,
            RingBand.Yellow => Yellow,
            RingBand.Orange => Orange,
            _ => Red,
        };

    /// <summary>Centres the glyph on its visual centre, its farthest point touching the inner circle.</summary>
    internal static void DrawGlyph(SKCanvas canvas, Glyph glyph, SKRect inner, SKColor color)
    {
        var scale = inner.Width / 2 / glyph.Radius;
        var target = SKRect.Create(
            inner.MidX - glyph.CenterX * scale,
            inner.MidY - glyph.CenterY * scale,
            glyph.Mask.Width * scale,
            glyph.Mask.Height * scale);

        canvas.Save();
        var circle = SKRect.Inflate(inner, 0.5f, 0.5f);
        using (var clip = new SKRoundRect(circle, circle.Width / 2, circle.Height / 2))
        {
            canvas.ClipRoundRect(clip, antialias: true);
        }
        using var image = SKImage.FromBitmap(glyph.Mask);
        using var paint = new SKPaint { IsAntialias = true, ColorFilter = SKColorFilter.CreateBlendMode(color, SKBlendMode.SrcIn) };
        canvas.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Restore();
    }

    /// <summary>
    /// Segoe UI on Windows; on Linux, the first of the usual desktop fonts that is installed, in bold.
    /// Kept for the whole run (tray icons are redrawn on every refresh).
    /// </summary>
    private static readonly Lazy<SKTypeface> BoldTypeface = new(() =>
    {
        foreach (var family in new[] { "Segoe UI", "Ubuntu", "Cantarell", "Noto Sans", "DejaVu Sans", "Liberation Sans" })
        {
            if (SKFontManager.Default.MatchFamily(family, SKFontStyle.Bold) is { } typeface) return typeface;
        }
        return SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold) ?? SKTypeface.Default;
    });

    /// <summary>The largest bold text that fits inside the ring.</summary>
    internal static void DrawNumber(SKCanvas canvas, string text, SKRect inner, SKColor color)
    {
        using var font = new SKFont(BoldTypeface.Value, inner.Height) { Edging = SKFontEdging.Antialias, Subpixel = true };
        var maxWidth = inner.Width * (text.Length >= 3 ? 1.08f : 0.98f);
        for (var fontSize = inner.Height * 0.95f; fontSize > 4; fontSize -= 0.25f)
        {
            font.Size = fontSize;
            if (font.MeasureText(text) <= maxWidth && font.Metrics.CapHeight <= inner.Height * 0.62f) break;
        }
        using var paint = new SKPaint { IsAntialias = true, Color = color };
        var width = font.MeasureText(text);
        var baseline = inner.MidY + font.Metrics.CapHeight / 2;
        canvas.DrawText(text, inner.MidX - width / 2, baseline, SKTextAlign.Left, font, paint);
    }
}
