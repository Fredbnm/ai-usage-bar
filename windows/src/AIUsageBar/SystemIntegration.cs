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

/// <summary>Finds the installed Claude and Grok Bot apps, for their icons and to open them.</summary>
internal static class InstalledApps
{
    private static readonly Dictionary<ProviderId, Glyph?> GlyphCache = [];

    private static readonly string[] UninstallKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    private static string AppName(ProviderId provider) => provider == ProviderId.Claude ? "Claude" : "Grok Bot";

    /// <summary>Usual install folders, when the uninstall entry does not say.</summary>
    private static IEnumerable<string> KnownPaths(ProviderId provider)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return provider == ProviderId.Claude
            ? [Path.Combine(local, "AnthropicClaude", "claude.exe"), Path.Combine(local, "Programs", "Claude", "Claude.exe")]
            : [Path.Combine(local, "Programs", "Grok Bot", "Grok Bot.exe"), Path.Combine(local, "Programs", "grok-bot", "Grok Bot.exe")];
    }

    /// <summary>The app's executable (or icon file), from the uninstall entries or known folders.</summary>
    public static string? IconSource(ProviderId provider)
    {
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
                        !displayName.Equals(AppName(provider), StringComparison.OrdinalIgnoreCase)) continue;
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
        return KnownPaths(provider).FirstOrDefault(File.Exists);
    }

    /// <summary>The white artwork of the app's own icon, as on macOS. Nothing is bundled.</summary>
    public static Glyph? Glyph(ProviderId provider)
    {
        if (GlyphCache.TryGetValue(provider, out var cached)) return cached;
        Glyph? glyph = null;
        try
        {
            if (IconSource(provider) is { } source)
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
            if (provider == ProviderId.Claude)
            {
                Shell("https://claude.ai/settings/usage");
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
