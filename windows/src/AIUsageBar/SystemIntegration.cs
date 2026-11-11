using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using Microsoft.Win32;
using SkiaSharp;

namespace AIUsageBar;

/// <summary>DPAPI for the current user: what Electron uses to protect its key on Windows.</summary>
internal sealed class DpapiUnprotector : ISecretUnprotector
{
    public byte[] Unprotect(byte[] protectedData) =>
        ProtectedData.Unprotect(protectedData, optionalEntropy: null, DataProtectionScope.CurrentUser);
}

/// <summary>"Start with Windows" via the per-user Run key (no admin rights needed).</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AIUsageBar";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Finds the installed Claude, Cursor and Grok Bot apps, for their icons and to open them.</summary>
internal static class InstalledApps
{
    private static readonly Dictionary<ProviderId, Glyph?> GlyphCache = [];

    private static readonly string[] UninstallKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    /// <summary>The names the app's uninstall entry may carry (Cursor's per-user setup adds "(User)").</summary>
    private static string[] AppNames(ProviderId provider) => provider switch
    {
        ProviderId.Claude => ["Claude"],
        ProviderId.Cursor => ["Cursor", "Cursor (User)"],
        _ => ["Grok Bot"],
    };

    /// <summary>Usual install folders, when the uninstall entry does not say.</summary>
    private static IEnumerable<string> KnownPaths(ProviderId provider)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return provider switch
        {
            ProviderId.Claude => [Path.Combine(local, "AnthropicClaude", "claude.exe"), Path.Combine(local, "Programs", "Claude", "Claude.exe")],
            ProviderId.Cursor =>
            [
                Path.Combine(local, "Programs", "cursor", "Cursor.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cursor", "Cursor.exe"),
            ],
            _ => [Path.Combine(local, "Programs", "Grok Bot", "Grok Bot.exe"), Path.Combine(local, "Programs", "grok-bot", "Grok Bot.exe")],
        };
    }

    /// <summary>The app's executable (or icon file), from the uninstall entries or known folders.</summary>
    public static string? IconSource(ProviderId provider)
    {
        // These live inside editors: their official icons come from the editor extension.
        switch (provider)
        {
            case ProviderId.Copilot: return ExtensionIcon.FindCopilot();
            case ProviderId.Codex: return ExtensionIcon.FindCodex();
            case ProviderId.Gemini: return ExtensionIcon.FindGemini();
        }
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var path in UninstallKeys)
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(name);
                    if (entry?.GetValue("DisplayName") is not string displayName ||
                        !AppNames(provider).Contains(displayName, StringComparer.OrdinalIgnoreCase)) continue;
                    if (entry.GetValue("DisplayIcon") is string icon)
                    {
                        var file = icon.Split(',')[0].Trim().Trim('"');
                        if (File.Exists(file)) return file;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable registry branches are skipped.
            }
        }
        return KnownPaths(provider).FirstOrDefault(File.Exists) ?? StorePackageLogo(StorePackageNames(provider));
    }

    /// <summary>Package names (before the first "_") the app may have as a Microsoft Store / MSIX install.</summary>
    private static string[] StorePackageNames(ProviderId provider) => provider switch
    {
        ProviderId.Claude => ["Claude"],
        ProviderId.GrokBot => ["GrokBot", "Grok Bot"],
        _ => [],
    };

    /// <summary>
    /// Store apps do not register an uninstall entry. The package registry
    /// (readable without admin rights) names the install folder, and its
    /// AppxManifest.xml names the logo; the biggest scale of it is used.
    /// </summary>
    private static string? StorePackageLogo(string[] names)
    {
        if (names.Length == 0) return null;
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages is null) return null;
            foreach (var key in packages.GetSubKeyNames())
            {
                if (!names.Any(name => key.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase))) continue;
                using var package = packages.OpenSubKey(key);
                if (package?.GetValue("PackageRootFolder") is not string root || !Directory.Exists(root)) continue;

                var manifest = System.Xml.Linq.XDocument.Load(Path.Combine(root, "AppxManifest.xml"));
                var logo = manifest.Descendants().FirstOrDefault(e => e.Name.LocalName == "VisualElements")
                    ?.Attributes().FirstOrDefault(a => a.Name.LocalName == "Square150x150Logo")?.Value;
                if (logo is null) continue;

                var path = Path.Combine(root, logo.Replace('/', Path.DirectorySeparatorChar));
                var directory = Path.GetDirectoryName(path)!;
                var name = Path.GetFileNameWithoutExtension(path);
                foreach (var candidate in new[] { $"{name}.scale-400.png", $"{name}.scale-200.png", Path.GetFileName(path) })
                {
                    if (File.Exists(Path.Combine(directory, candidate))) return Path.Combine(directory, candidate);
                }
            }
        }
        catch (Exception)
        {
            // Not a Store install, or unreadable: the initial is shown instead.
        }
        return null;
    }

    /// <summary>The white artwork of the app's own icon, as on macOS. Nothing is bundled.</summary>
    public static Glyph? Glyph(ProviderId provider)
    {
        if (GlyphCache.TryGetValue(provider, out var cached)) return cached;
        Glyph? glyph = null;
        try
        {
            var source = IconSource(provider);
            if (source is not null && source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                // Copilot's icon is dark artwork on a light square, Gemini's a colored shape; Codex's and the
                // Store apps' logos are white on a tile.
                using var png = SKBitmap.Decode(source);
                if (png is not null)
                {
                    glyph = GlyphExtractor.Artwork(png, provider switch
                    {
                        ProviderId.Copilot => ArtworkKind.Dark,
                        ProviderId.Gemini => ArtworkKind.Silhouette,
                        _ => ArtworkKind.White,
                    });
                }
            }
            else if (source is not null)
            {
                using var icon = source.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
                    ? new System.Drawing.Icon(source, 256, 256)
                    : System.Drawing.Icon.ExtractIcon(source, 0, 256);
                if (icon is not null)
                {
                    using var bitmap = icon.ToBitmap();
                    using var stream = new MemoryStream();
                    bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    using var decoded = SKBitmap.Decode(stream.ToArray());
                    glyph = GlyphExtractor.WhiteArtwork(decoded);
                }
            }
        }
        catch (Exception)
        {
            // Fall back to the service's initial.
        }
        return GlyphCache[provider] = glyph;
    }

    public static void Open(ProviderId provider)
    {
        try
        {
            if (provider.Descriptor().HelpUrl is { } url)
            {
                Shell(url.AbsoluteUri);
            }
            else if (IconSource(ProviderId.GrokBot) is { } exe && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            else
            {
                Shell("grokbot://");
            }
        }
        catch (Exception)
        {
            // Nothing to open.
        }
    }

    public static void Shell(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}

/// <summary>Turns a Skia bitmap into a tray icon, owning the native handle.</summary>
internal sealed class TrayIconImage : IDisposable
{
    private readonly IntPtr handle;

    public TrayIconImage(SKBitmap bitmap)
    {
        using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        using var gdi = new System.Drawing.Bitmap(stream);
        handle = gdi.GetHicon();
        Icon = System.Drawing.Icon.FromHandle(handle);
    }

    public System.Drawing.Icon Icon { get; }

    public void Dispose()
    {
        Icon.Dispose();
        DestroyIcon(handle);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
