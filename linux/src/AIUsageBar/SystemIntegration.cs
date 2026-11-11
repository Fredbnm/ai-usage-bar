using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace AIUsageBar;

/// <summary>
/// The path that starts this copy again: the AppImage itself when running from one
/// (the executable inside it lives in a temporary mount), otherwise the executable.
/// </summary>
internal static class Launcher
{
    /// <summary>The portable build: one self-contained file, whose assemblies have no path of their own.</summary>
#pragma warning disable IL3000 // An empty location is exactly what tells a single-file app apart.
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(Launcher).Assembly.Location);
#pragma warning restore IL3000

    public static string? AppImage =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path && File.Exists(path) ? path : null;

    /// <summary>Null when started as <c>dotnet AIUsageBar.dll</c> (a build from source): there is no single file to start.</summary>
    public static string? Path => AppImage
        ?? (Environment.ProcessPath is { } exe && System.IO.Path.GetFileNameWithoutExtension(exe) != "dotnet" ? exe : null);
}

/// <summary>"Start at login" via an XDG autostart entry (<c>~/.config/autostart</c>), which every desktop reads.</summary>
internal static class Autostart
{
    private static string EntryPath =>
        System.IO.Path.Combine(Xdg.ConfigHome, "autostart", "ai-usage-bar.desktop");

    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (Launcher.Path is not { } path || !File.Exists(EntryPath)) return false;
                var lines = File.ReadAllLines(EntryPath);
                return lines.Contains("Exec=" + DesktopFile.Exec(path)) && !lines.Any(l => l.Trim() is "Hidden=true" or "X-GNOME-Autostart-enabled=false");
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(EntryPath);
                return;
            }
            if (Launcher.Path is not { } path) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(EntryPath)!);
            File.WriteAllText(EntryPath, DesktopFile.Content(path, autostart: true));
        }
        catch (Exception)
        {
            // Nothing to do: the menu shows the real state next time.
        }
    }
}

internal static class DesktopFile
{
    /// <summary>Quoted for the Exec key of a .desktop file.</summary>
    public static string Exec(string path) =>
        "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";

    public static string Content(string path, bool autostart) => $"""
        [Desktop Entry]
        Type=Application
        Name=AI Usage Bar
        Comment=Claude, Codex, Copilot, Cursor, Gemini and Grok Bot usage limits in the tray
        Comment[pt_BR]=Limites de uso do Claude, Codex, Copilot, Cursor, Gemini e Grok Bot na bandeja
        Exec={Exec(path)}
        Icon=ai-usage-bar
        Terminal=false
        Categories=Utility;
        Keywords=Claude;Codex;Copilot;Cursor;Gemini;Grok;AI;usage;limits;
        StartupNotify=false
        {(autostart ? "X-GNOME-Autostart-enabled=true\n" : "")}
        """;
}

/// <summary>
/// An AppImage or the portable executable has no entry in the app menu, which is where the popup is opened
/// from on a desktop without a tray (and where "Start at login" takes its icon). The app adds its own
/// entry and icon under <c>~/.local/share</c>, and keeps it pointing at wherever the file was moved.
/// Packages that install their own entry (and builds from source) are left alone.
/// </summary>
internal static class MenuEntry
{
    private static string EntryPath => System.IO.Path.Combine(Xdg.DataHome, "applications", "ai-usage-bar.desktop");

    private static string IconPath => System.IO.Path.Combine(Xdg.DataHome, "icons", "hicolor", "256x256", "apps", "ai-usage-bar.png");

    public static void Ensure()
    {
        try
        {
            var portable = Launcher.IsSingleFile;
            if (Launcher.Path is not { } path || (Launcher.AppImage is null && !portable)) return;
            if (!File.Exists(IconPath))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(IconPath)!);
                using var icon = Avalonia.Platform.AssetLoader.Open(new Uri("avares://AIUsageBar/Assets/AppIcon.png"));
                using var file = File.Create(IconPath);
                icon.CopyTo(file);
            }
            var content = DesktopFile.Content(path, autostart: false);
            if (File.Exists(EntryPath) && File.ReadAllText(EntryPath) == content) return;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(EntryPath)!);
            File.WriteAllText(EntryPath, content);
        }
        catch (Exception)
        {
            // The app works without a menu entry.
        }
    }
}

/// <summary>
/// Where the agents' own icons come from on Linux. Nothing is bundled: Claude's star comes from the Claude
/// Code extension (or a Claude desktop package's icon), Copilot, Codex and Gemini from their editor
/// extensions, Cursor and Grok Bot from their installed icons. An agent without one shows its initial instead.
/// </summary>
internal static class InstalledApps
{
    private static readonly Dictionary<ProviderId, Glyph?> GlyphCache = [];

    /// <summary>Icon theme folders, user ones first.</summary>
    private static IEnumerable<string> IconRoots()
    {
        var dataHome = Xdg.DataHome;
        var dataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } dirs ? dirs : "/usr/local/share:/usr/share")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);
        foreach (var root in new[] { dataHome, System.IO.Path.Combine(dataHome, "flatpak", "exports", "share"), "/var/lib/flatpak/exports/share" }.Concat(dataDirs).Distinct())
        {
            yield return System.IO.Path.Combine(root, "icons", "hicolor");
        }
    }

    /// <summary>The largest PNG of a named icon in the hicolor theme, or in /usr/share/pixmaps.</summary>
    internal static string? ThemeIcon(IEnumerable<string> names, IEnumerable<string>? roots = null)
    {
        string[] sizes = ["1024x1024", "512x512", "256x256", "192x192", "128x128", "96x96", "64x64", "48x48"];
        var nameList = names.ToList();
        foreach (var root in roots ?? IconRoots())
        foreach (var size in sizes)
        foreach (var name in nameList)
        {
            var path = System.IO.Path.Combine(root, size, "apps", name + ".png");
            if (File.Exists(path)) return path;
        }
        return nameList.Select(name => $"/usr/share/pixmaps/{name}.png").FirstOrDefault(File.Exists);
    }

    public static string? IconSource(ProviderId provider) => provider switch
    {
        ProviderId.Claude => ExtensionIcon.FindClaudeCode() ?? ThemeIcon(["claude", "claude-desktop", "com.anthropic.claude"]),
        ProviderId.Copilot => ExtensionIcon.FindCopilot(),
        ProviderId.Codex => ExtensionIcon.FindCodex(),
        ProviderId.Gemini => ExtensionIcon.FindGemini(),
        ProviderId.Cursor => ThemeIcon(["co.anysphere.cursor", "cursor"]),
        ProviderId.GrokBot => ThemeIcon(["grok-bot", "grokbot", "Grok Bot", "grok"]),
        _ => null,
    };

    /// <summary>The white artwork of the agent's own icon, as on macOS and Windows.</summary>
    public static Glyph? Glyph(ProviderId provider)
    {
        if (GlyphCache.TryGetValue(provider, out var cached)) return cached;
        Glyph? glyph = null;
        try
        {
            if (IconSource(provider) is { } source && SKBitmap.Decode(source) is { } bitmap)
            {
                using (bitmap)
                {
                    // Copilot's icon is dark artwork on a light square, Gemini's a colored shape; Claude's,
                    // Codex's, Cursor's and Grok Bot's are light on a tile.
                    glyph = GlyphExtractor.Artwork(bitmap, provider switch
                    {
                        ProviderId.Copilot => ArtworkKind.Dark,
                        ProviderId.Gemini => ArtworkKind.Silhouette,
                        _ => ArtworkKind.White,
                    });
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
        if (provider.Descriptor().HelpUrl is { } url) Shell.Open(url.AbsoluteUri);
        else Shell.Open("grokbot://");
    }
}

internal static class Shell
{
    /// <summary>Opens a link or file with the desktop's default app (<c>xdg-open</c>).</summary>
    public static void Open(string target)
    {
        try
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(target);
            using var _ = Process.Start(start);
        }
        catch (Exception)
        {
            // No xdg-open: nothing to open with.
        }
    }
}

/// <summary>The keyring through <c>secret-tool</c> (libsecret-tools), for Grok Bot's "v11" values.</summary>
internal sealed class SecretToolKeyring : IKeyringReader
{
    public string? Password(string application)
    {
        try
        {
            var start = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "lookup", "application", application }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return null;
            }
            var password = output.Result.TrimEnd('\n');
            return process.ExitCode == 0 && password.Length > 0 ? password : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Theme-tinted images for the tab icons.</summary>
internal static class TabIcons
{
    /// <summary>Pixels per side; shown at 16 device-independent pixels, so crisp up to 300% scaling.</summary>
    private const int Pixels = 48;

    private static readonly Dictionary<(ProviderId, bool), Bitmap> Cache = [];

    public static Bitmap Get(ProviderId provider, bool dark)
    {
        if (Cache.TryGetValue((provider, dark), out var cached)) return cached;
        var color = dark ? SKColors.White : new SKColor(0x1A, 0x1A, 0x1A);
        using var rendered = TabIconRenderer.Render(provider, InstalledApps.Glyph(provider), color, Pixels);
        return Cache[(provider, dark)] = Images.ToBitmap(rendered);
    }
}

internal static class Images
{
    public static byte[] Png(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static Bitmap ToBitmap(SKBitmap bitmap)
    {
        using var stream = new MemoryStream(Png(bitmap));
        return new Bitmap(stream);
    }
}

/// <summary>
/// Where the mouse is, from X11 (Avalonia runs on X11, or XWayland in a Wayland session). Used to put the popup
/// next to the tray icon that was clicked. Null when it cannot be known: under Wayland the pointer over the
/// panel is not visible to X11 programs.
/// </summary>
internal static class X11Pointer
{
    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    private static IntPtr display;

    public static bool IsWayland =>
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland"
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public static PixelPoint? Position()
    {
        if (IsWayland || !OperatingSystem.IsLinux()) return null;
        try
        {
            if (display == IntPtr.Zero) display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return null;
            return XQueryPointer(display, XDefaultRootWindow(display), out _, out _, out var x, out var y, out _, out _, out _) != 0
                ? new PixelPoint(x, y)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// One copy per user: a lock file under <c>$XDG_RUNTIME_DIR</c>. Starting the app again (from the app menu, say)
/// asks the running copy to open its popup through a socket next to it, which is how the popup is reached on a
/// desktop without a tray.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly FileStream lockFile;
    private readonly Socket listener;
    private readonly string socketPath;

    private SingleInstance(FileStream lockFile, Socket listener, string socketPath)
    {
        this.lockFile = lockFile;
        this.listener = listener;
        this.socketPath = socketPath;
    }

    private static string Directory
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return string.IsNullOrEmpty(runtime) || !System.IO.Directory.Exists(runtime) ? AppSettings.Directory : runtime;
        }
    }

    /// <summary>Raised on the UI thread when another copy was started.</summary>
    public event Action? Activated;

    /// <summary>The instance, or null after asking the copy that already runs to show itself.</summary>
    public static SingleInstance? Acquire()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var lockPath = System.IO.Path.Combine(Directory, "ai-usage-bar.lock");
        var socketPath = System.IO.Path.Combine(Directory, "ai-usage-bar.sock");
        FileStream lockFile;
        try
        {
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Signal(socketPath);
            return null;
        }

        try { File.Delete(socketPath); } catch (Exception) { /* a stale socket from a crash */ }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(4);
        }
        catch (Exception)
        {
            // Still a single instance, only without "show yourself".
        }
        var instance = new SingleInstance(lockFile, listener, socketPath);
        _ = instance.AcceptAsync();
        return instance;
    }

    private static void Signal(string socketPath)
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(socketPath));
            client.Send("show\n"u8);
        }
        catch (Exception)
        {
            // The other copy is starting or stopping; nothing to show.
        }
    }

    private async Task AcceptAsync()
    {
        while (true)
        {
            try
            {
                using var client = await listener.AcceptAsync();
                Dispatcher.UIThread.Post(() => Activated?.Invoke());
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        listener.Dispose();
        try { File.Delete(socketPath); } catch (Exception) { /* already gone */ }
        lockFile.Dispose();
    }
}
