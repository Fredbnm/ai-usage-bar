using SkiaSharp;

namespace AIUsageBar.Core.Rendering;

/// <summary>
/// A sheet with every tray combination at real Windows sizes (16, 20, 24 and
/// 32 px for 100–200% scaling), on a light and a dark taskbar, enlarged with
/// nearest-neighbour sampling so individual pixels can be judged.
/// </summary>
public static class TrayPreview
{
    public sealed record Variant(string Label, TrayStyle Style, IReadOnlyDictionary<ProviderId, TrayDisplay> Displays, bool ColorAlerts);

    public static readonly IReadOnlyList<Variant> DefaultVariants =
    [
        new("Icon: Claude session | Grok", TrayStyle.Icon, new Dictionary<ProviderId, TrayDisplay> { [ProviderId.Claude] = TrayDisplay.Session, [ProviderId.GrokBot] = TrayDisplay.Weekly }, true),
        new("Icon: Claude weekly | Grok", TrayStyle.Icon, new Dictionary<ProviderId, TrayDisplay> { [ProviderId.Claude] = TrayDisplay.Weekly, [ProviderId.GrokBot] = TrayDisplay.Weekly }, true),
        new("Number: Claude session | Grok", TrayStyle.Percent, new Dictionary<ProviderId, TrayDisplay> { [ProviderId.Claude] = TrayDisplay.Session, [ProviderId.GrokBot] = TrayDisplay.Weekly }, true),
        new("Icon, alert colors off", TrayStyle.Icon, new Dictionary<ProviderId, TrayDisplay> { [ProviderId.Claude] = TrayDisplay.Session, [ProviderId.GrokBot] = TrayDisplay.Weekly }, false),
        new("Number, alert colors off", TrayStyle.Percent, new Dictionary<ProviderId, TrayDisplay> { [ProviderId.Claude] = TrayDisplay.Session, [ProviderId.GrokBot] = TrayDisplay.Weekly }, false),
    ];

    private static readonly int[] Sizes = [16, 20, 24, 32];

    public static byte[] RenderPng(
        IReadOnlyList<ProviderSnapshot> snapshots,
        IReadOnlyDictionary<ProviderId, Glyph> glyphs,
        IReadOnlyList<Variant>? variants = null,
        int zoom = 4)
    {
        variants ??= DefaultVariants;
        const int labelWidth = 230, padding = 12, gapBetweenIcons = 6;
        var rowHeight = 32 * zoom + padding * 2;
        var iconsWidth = Sizes.Sum(size => (size * zoom + gapBetweenIcons) * snapshots.Count + padding);
        var width = labelWidth + iconsWidth * 2;
        var height = rowHeight * variants.Count;

        using var sheet = new SKBitmap(width, height);
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(SKColors.White);
        using var labelFont = new SKFont(SKTypeface.Default, 14);
        using var labelPaint = new SKPaint { IsAntialias = true, Color = new SKColor(0x44, 0x44, 0x44) };

        for (var row = 0; row < variants.Count; row++)
        {
            var variant = variants[row];
            var y = row * rowHeight;
            canvas.DrawText(variant.Label, 10, y + rowHeight / 2f + 5, SKTextAlign.Left, labelFont, labelPaint);

            for (var theme = 0; theme < 2; theme++)
            {
                var dark = theme == 1;
                var x = labelWidth + iconsWidth * theme;
                using (var background = new SKPaint { Color = dark ? new SKColor(0x20, 0x20, 0x20) : new SKColor(0xEE, 0xEE, 0xEE) })
                {
                    canvas.DrawRect(SKRect.Create(x, y, iconsWidth, rowHeight), background);
                }
                x += padding;
                foreach (var size in Sizes)
                {
                    foreach (var snapshot in snapshots)
                    {
                        var display = variant.Displays.GetValueOrDefault(snapshot.Provider, TrayOptions.Default(snapshot.Provider));
                        var segment = TraySegment.Make(snapshot.Provider, display, snapshot, null);
                        if (segment is null) continue;
                        using var icon = TrayIconRenderer.Render(segment, snapshot.Provider, variant.Style,
                            glyphs.GetValueOrDefault(snapshot.Provider), variant.ColorAlerts, dark, size);
                        using var image = SKImage.FromBitmap(icon);
                        var top = y + padding + (32 - size) * zoom / 2f;
                        canvas.DrawImage(image, SKRect.Create(x, top, size * zoom, size * zoom), new SKSamplingOptions(SKFilterMode.Nearest));
                        x += size * zoom + gapBetweenIcons;
                    }
                    x += padding;
                }
            }
        }

        using var encoded = SKImage.FromBitmap(sheet).Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
