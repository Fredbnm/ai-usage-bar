using SkiaSharp;

namespace AIUsageBar.Core.Rendering;

/// <summary>
/// Draws one service's tray icon: a ring that fills with usage around either
/// the service's glyph or the percentage. Monochrome in the taskbar's color
/// (white on a dark taskbar, near-black on a light one); only the ring turns
/// orange or red when a limit is close and alert colors are on.
/// </summary>
public static class TrayIconRenderer
{
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
                Color = RingColor(segment.Severity, foreground, colorAlerts),
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
            DrawNumber(canvas, provider.DisplayName()[..1], inner, foreground);
        }
        return bitmap;
    }

    private static SKColor RingColor(Severity severity, SKColor foreground, bool colorAlerts) =>
        !colorAlerts ? foreground : severity switch
        {
            Severity.Warning => Orange,
            Severity.Critical => Red,
            _ => foreground,
        };

    /// <summary>Centres the glyph on its visual centre, its farthest point touching the inner circle.</summary>
    private static void DrawGlyph(SKCanvas canvas, Glyph glyph, SKRect inner, SKColor color)
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

    /// <summary>The largest bold text that fits inside the ring.</summary>
    private static void DrawNumber(SKCanvas canvas, string text, SKRect inner, SKColor color)
    {
        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, inner.Height) { Edging = SKFontEdging.Antialias, Subpixel = true };
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
