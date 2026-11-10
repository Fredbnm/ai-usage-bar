namespace AIUsageBar.Core;

/// <summary>GitHub Copilot's official icon, from GitHub's editor extension. See <see cref="ExtensionIcon"/>.</summary>
public static class CopilotIcon
{
    public static IReadOnlyList<string> DefaultExtensionRoots() => ExtensionIcon.DefaultExtensionRoots();

    public static string? Find(IEnumerable<string>? extensionRoots = null) => ExtensionIcon.FindCopilot(extensionRoots);

    internal static Version VersionOf(string directoryName) => ExtensionIcon.VersionOf(directoryName);
}
