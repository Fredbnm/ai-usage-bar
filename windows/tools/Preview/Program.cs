// Renders every tray icon combination to a PNG, on any OS, from fixtures and
// app icon PNGs:
//
//   dotnet run --project tools/Preview -- out.png \
//     --claude-fixture ../docs/sample-usage.json --grok-fixture ../docs/sample-grok-usage.json \
//     --claude-icon claude.png --grok-icon grok.png [--lang en|pt]
using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using SkiaSharp;

if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
{
    Console.Error.WriteLine("usage: Preview out.png [--claude-fixture f] [--grok-fixture f] [--claude-icon png] [--grok-icon png] [--lang en|pt]");
    return 1;
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

Localization.Current = Localization.LanguageFromCode(Option("--lang")) ?? AppLanguage.English;

var snapshots = new List<ProviderSnapshot>();
if (Option("--claude-fixture") is { } claudeFixture &&
    ClaudeProvider.SnapshotFromResponse(File.ReadAllBytes(claudeFixture), "Max 5x") is { } claude)
{
    snapshots.Add(claude);
}
if (Option("--grok-fixture") is { } grokFixture && GrokUsage.Snapshot(File.ReadAllBytes(grokFixture)) is { } grok)
{
    snapshots.Add(grok);
}

var glyphs = new Dictionary<ProviderId, Glyph>();
foreach (var (provider, flag) in new[] { (ProviderId.Claude, "--claude-icon"), (ProviderId.GrokBot, "--grok-icon") })
{
    if (Option(flag) is not { } path) continue;
    using var icon = SKBitmap.Decode(path);
    if (GlyphExtractor.WhiteArtwork(icon) is { } glyph)
    {
        glyphs[provider] = glyph;
        Console.WriteLine($"{provider}: centre ({glyph.CenterX:F1}, {glyph.CenterY:F1}), radius {glyph.Radius:F1}");
    }
    else
    {
        Console.WriteLine($"{provider}: no white artwork found");
    }
}

foreach (var snapshot in snapshots)
{
    var segment = TraySegment.Make(snapshot.Provider, TrayOptions.Default(snapshot.Provider), snapshot, null);
    Console.WriteLine($"{snapshot.Provider} tooltip:\n  {segment?.Tooltip.Replace("\n", "\n  ")}");
}

File.WriteAllBytes(args[0], TrayPreview.RenderPng(snapshots, glyphs));
Console.WriteLine($"Saved {args[0]}");
return 0;
