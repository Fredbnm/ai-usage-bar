using System.Text.Json.Nodes;

namespace AIUsageBar.Core;

/// <summary>
/// GitHub Copilot has no app of its own, but GitHub's editor extension ships
/// its official icon (<c>"icon"</c> in the extension's package.json). The meter
/// reads it from the installed extension at runtime, like it does with the
/// Claude and Grok Bot apps: no brand artwork ships with this repository.
/// </summary>
public static class CopilotIcon
{
    /// <summary>Editors that install VS Code extensions under the user profile.</summary>
    public static IReadOnlyList<string> DefaultExtensionRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [.. new[] { ".vscode", ".vscode-insiders", ".cursor" }.Select(d => Path.Combine(home, d, "extensions"))];
    }

    /// <summary>
    /// The icon file of the newest <c>github.copilot-chat-*</c> (then
    /// <c>github.copilot-*</c>) extension found, or null.
    /// </summary>
    public static string? Find(IEnumerable<string>? extensionRoots = null)
    {
        var candidates = new List<(string Directory, Version Version, int Rank)>();
        foreach (var root in extensionRoots ?? DefaultExtensionRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in SafeDirectories(root))
            {
                var name = Path.GetFileName(directory);
                var rank = name.StartsWith("github.copilot-chat-", StringComparison.OrdinalIgnoreCase) ? 0
                    : name.StartsWith("github.copilot-", StringComparison.OrdinalIgnoreCase) ? 1
                    : -1;
                if (rank < 0) continue;
                candidates.Add((directory, VersionOf(name), rank));
            }
        }

        foreach (var (directory, _, _) in candidates.OrderBy(c => c.Rank).ThenByDescending(c => c.Version))
        {
            if (IconFile(directory) is { } icon) return icon;
        }
        return null;
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception) { return []; }
    }

    /// <summary>"github.copilot-chat-0.33.2025103102" → 0.33.2025103102 (the number after the last dash).</summary>
    internal static Version VersionOf(string directoryName)
    {
        var text = directoryName[(directoryName.LastIndexOf('-') + 1)..];
        var parts = text.Split('.').Select(p => long.TryParse(p, out var n) ? (int)Math.Min(n, int.MaxValue) : 0).ToArray();
        return new Version(parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2));
    }

    private static string? IconFile(string extensionDirectory)
    {
        try
        {
            var relative = Json.Str(Json.Parse(File.ReadAllBytes(Path.Combine(extensionDirectory, "package.json"))), "icon");
            if (relative is { Length: > 0 } &&
                Path.GetFullPath(Path.Combine(extensionDirectory, relative)) is var path &&
                path.StartsWith(Path.GetFullPath(extensionDirectory), StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path))
            {
                return path;
            }
        }
        catch (Exception)
        {
            // No readable package.json: fall through to the usual name.
        }
        var usual = Path.Combine(extensionDirectory, "assets", "copilot.png");
        return File.Exists(usual) ? usual : null;
    }
}
