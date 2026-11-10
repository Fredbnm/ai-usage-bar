using System.Runtime.InteropServices;
using SkiaSharp;

namespace AIUsageBar.Core.Rendering;

/// <summary>
/// A monochrome glyph cut out of an app icon: white artwork on transparent,
/// with the point it should be centred on and how far it reaches from there
/// (pixel coordinates of <see cref="Mask"/>, origin top-left).
/// </summary>
public sealed record Glyph(SKBitmap Mask, float CenterX, float CenterY, float Radius);

/// <summary>
/// Port of the macOS extractor (macos/Sources/AIUsageBar/ProviderGlyph.swift):
/// 1. "whiteness" = darkest RGB channel, so orange and black tiles drop out;
/// 2. only the largest connected shape survives (drops rim highlights);
/// 3. sparse shapes (Claude's star) are centred on their hub by mean shift,
///    filled shapes (Grok Bot's character) on their bounding box.
/// </summary>
/// <summary>What the artwork of an icon looks like, which decides how it is told apart from its background.</summary>
public enum ArtworkKind
{
    /// <summary>White on a colored tile (Claude, Grok Bot, Codex).</summary>
    White,
    /// <summary>Dark on a light or transparent square (GitHub Copilot).</summary>
    Dark,
    /// <summary>Colored on a transparent or plain background (Gemini).</summary>
    Silhouette,
}

public static class GlyphExtractor
{
    /// <summary>
    /// Transparent corner: the alpha is the shape. Opaque corner: how far a pixel is from the corner's color.
    /// </summary>
    private static double SilhouetteCoverage(ReadOnlySpan<byte> pixels, int index, double alpha, byte[] corner)
    {
        if (corner[3] < 25) return alpha;
        if (alpha <= 0) return 0;
        var distance = 0;
        for (var channel = 0; channel < 3; channel++) distance = Math.Max(distance, Math.Abs(pixels[index * 4 + channel] - corner[channel]));
        return Smoothstep(40, 140, distance) * alpha;
    }

    /// <summary>
    /// The artwork of an icon whose symbol is <i>dark</i> on a light or transparent
    /// background, such as GitHub Copilot's own icon. Same pipeline, with "darkness"
    /// instead of "whiteness".
    /// </summary>
    public static Glyph? DarkArtwork(SKBitmap icon, int side = 256) => Artwork(icon, ArtworkKind.Dark, side);

    /// <summary>
    /// The shape of an icon that is neither white nor dark but colored (Gemini's sparkle): what stands out from the
    /// background, which is transparent or the color of the icon's corner.
    /// </summary>
    public static Glyph? Silhouette(SKBitmap icon, int side = 256) => Artwork(icon, ArtworkKind.Silhouette, side);

    public static Glyph? WhiteArtwork(SKBitmap icon, int side = 256, bool darkArtwork = false) =>
        Artwork(icon, darkArtwork ? ArtworkKind.Dark : ArtworkKind.White, side);

    public static Glyph? Artwork(SKBitmap icon, ArtworkKind kind, int side = 256)
    {
        var info = new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var source = new SKBitmap(info);
        using (var canvas = new SKCanvas(source))
        using (var image = SKImage.FromBitmap(icon))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawImage(image, new SKRect(0, 0, side, side), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        var pixels = source.GetPixelSpan();

        // 1. Coverage per pixel (row 0 = top).
        var coverage = new double[side * side];
        var corner = new[] { pixels[0], pixels[1], pixels[2], pixels[3] };
        for (var i = 0; i < side * side; i++)
        {
            var alpha = pixels[i * 4 + 3] / 255.0;
            if (alpha <= 0) continue;
            if (kind == ArtworkKind.Silhouette)
            {
                coverage[i] = SilhouetteCoverage(pixels, i, alpha, corner);
                continue;
            }
            if (kind == ArtworkKind.Dark)
            {
                var lightest = Math.Max(pixels[i * 4], Math.Max(pixels[i * 4 + 1], pixels[i * 4 + 2])) / alpha;
                coverage[i] = (1 - Smoothstep(70, 160, lightest)) * alpha;
                continue;
            }
            var darkest = Math.Min(pixels[i * 4], Math.Min(pixels[i * 4 + 1], pixels[i * 4 + 2])) / alpha;
            coverage[i] = Smoothstep(170, 235, darkest) * alpha;
        }

        // 2. Largest 8-connected component.
        var component = LargestComponent(coverage, side, 0.05);
        if (component is null || component.Count <= side * side / 100) return null;

        var mask = new byte[side * side * 4];
        foreach (var index in component)
        {
            var a = (byte)Math.Round(coverage[index] * 255);
            mask[index * 4] = a; mask[index * 4 + 1] = a; mask[index * 4 + 2] = a; mask[index * 4 + 3] = a; // white, premultiplied
        }

        // 3. Centre and reach.
        double cx = 0, cy = 0, total = 0;
        foreach (var index in component)
        {
            var w = coverage[index];
            cx += index % side * w; cy += index / side * w; total += w;
        }
        cx /= total; cy /= total;

        double reach = 0;
        foreach (var index in component.Where(i => coverage[i] > 0.25))
        {
            reach = Math.Max(reach, Hypot(index % side - cx, index / side - cy));
        }
        var window = reach * 0.3;
        for (var step = 0; step < 12; step++)
        {
            double sx = 0, sy = 0, sw = 0;
            foreach (var index in component)
            {
                double px = index % side, py = index / side;
                if (Hypot(px - cx, py - cy) > window) continue;
                var w = coverage[index];
                sx += px * w; sy += py * w; sw += w;
            }
            if (sw <= 0) break;
            var (nx, ny) = (sx / sw, sy / sw);
            var moved = Hypot(nx - cx, ny - cy);
            (cx, cy) = (nx, ny);
            if (moved < 0.1) break;
        }
        double radius = 0;
        foreach (var index in component.Where(i => coverage[i] > 0.25))
        {
            radius = Math.Max(radius, Hypot(index % side - cx, index / side - cy) + 0.5);
        }

        // Filled shapes have no dense hub; mean shift drifts to a corner.
        var area = component.Sum(i => coverage[i]);
        if (area / (Math.PI * radius * radius) > 0.45)
        {
            int minX = side, minY = side, maxX = -1, maxY = -1;
            foreach (var index in component.Where(i => coverage[i] > 0.25))
            {
                int x = index % side, y = index / side;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            cx = (minX + maxX) / 2.0;
            cy = (minY + maxY) / 2.0;
            radius = (Math.Max(maxX - minX, maxY - minY) + 1) / 2.0;
        }

        var bitmap = new SKBitmap(info);
        Marshal.Copy(mask, 0, bitmap.GetPixels(), mask.Length);
        bitmap.NotifyPixelsChanged();
        return new Glyph(bitmap, (float)(cx + 0.5), (float)(cy + 0.5), (float)radius);
    }

    private static List<int>? LargestComponent(double[] coverage, int side, double threshold)
    {
        var visited = new bool[side * side];
        List<int>? best = null;
        var stack = new Stack<int>();
        for (var start = 0; start < side * side; start++)
        {
            if (visited[start] || coverage[start] <= threshold) continue;
            var component = new List<int>();
            visited[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var index = stack.Pop();
                component.Add(index);
                int x = index % side, y = index / side;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= side || ny >= side) continue;
                    var neighbor = ny * side + nx;
                    if (visited[neighbor] || coverage[neighbor] <= threshold) continue;
                    visited[neighbor] = true;
                    stack.Push(neighbor);
                }
            }
            if (best is null || component.Count > best.Count) best = component;
        }
        return best;
    }

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    private static double Smoothstep(double edge0, double edge1, double value)
    {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
