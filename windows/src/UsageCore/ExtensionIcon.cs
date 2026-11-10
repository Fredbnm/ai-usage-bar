using System.Text.RegularExpressions;

namespace AIUsageBar.Core;

/// <summary>
/// Several agents have no app of their own but live in an editor extension that ships their official
/// icon (<c>"icon"</c> in the extension's package.json): GitHub Copilot, the Codex extension, Gemini
/// Code Assist. The meter reads it from the installed extension at runtime, like it does with the Claude
/// and Grok Bot apps: no brand artwork ships with this repository.
/// </summary>
public static partial class ExtensionIcon
{
    /// <summary>Editors that install VS Code extensions under the user profile.</summary>
    public static IReadOnlyList<string> DefaultExtensionRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return [.. new[] { ".vscode", ".vscode-insiders", ".cursor" }.Select(d => Path.Combine(home, d, "extensions"))];
    }

    /// <summary>
    /// The icon file of the newest installed extension whose folder starts with one of
    /// <paramref name="idPrefixes"/> (earlier prefixes win), or null. <paramref name="usualIcons"/> are tried
    /// when package.json names no icon.
    /// </summary>
    public static string? Find(IReadOnlyList<string> idPrefixes, IEnumerable<string>? extensionRoots = null, IReadOnlyList<string>? usualIcons = null)
    {
        var candidates = new List<(string Directory, Version Version, int Rank)>();
        foreach (var root in extensionRoots ?? DefaultExtensionRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var directory in SafeDirectories(root))
            {
                var name = Path.GetFileName(directory);
                for (var rank = 0; rank < idPrefixes.Count; rank++)
                {
                    if (!name.StartsWith(idPrefixes[rank], StringComparison.OrdinalIgnoreCase)) continue;
                    candidates.Add((directory, VersionOf(name), rank));
                    break;
                }
            }
        }

        foreach (var (directory, _, _) in candidates.OrderBy(c => c.Rank).ThenByDescending(c => c.Version))
        {
            if (IconFile(directory, usualIcons ?? []) is { } icon) return icon;
        }
        return null;
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// "github.copilot-chat-0.33.2025103102" → 0.33.2025103102;
    /// "openai.chatgpt-26.1021.22027-win32-x64" → 26.1021.22027 (the first dotted number after the id).
    /// </summary>
    internal static Version VersionOf(string directoryName)
    {
        var match = DottedNumber().Match(directoryName);
        var parts = (match.Success ? match.Value : "0").Split('.')
            .Select(p => long.TryParse(p, out var n) ? (int)Math.Min(n, int.MaxValue) : 0).ToArray();
        return new Version(parts.ElementAtOrDefault(0), parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2));
    }

    [GeneratedRegex(@"(?<=-)\d+(?:\.\d+)+")]
    private static partial Regex DottedNumber();

    private static string? IconFile(string extensionDirectory, IReadOnlyList<string> usualIcons)
    {
        var root = Path.GetFullPath(extensionDirectory);
        try
        {
            var relative = Json.Str(Json.Parse(File.ReadAllBytes(Path.Combine(extensionDirectory, "package.json"))), "icon");
            if (relative is { Length: > 0 } && Inside(root, relative) is { } named) return named;
        }
        catch (Exception)
        {
            // No readable package.json: fall through to the usual names.
        }
        return usualIcons.Select(name => Inside(root, name)).FirstOrDefault(path => path is not null);
    }

    /// <summary>The file, only if it exists and never outside the extension's own folder.</summary>
    private static string? Inside(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    public static string? FindCopilot(IEnumerable<string>? roots = null) =>
        Find(["github.copilot-chat-", "github.copilot-"], roots, ["assets/copilot.png"]);

    public static string? FindCodex(IEnumerable<string>? roots = null) =>
        Find(["openai.chatgpt-"], roots);

    public static string? FindGemini(IEnumerable<string>? roots = null) =>
        Find(["google.geminicodeassist-", "google.gemini-"], roots);
}
