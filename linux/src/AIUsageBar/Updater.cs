using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using AIUsageBar.Core;
using Avalonia.Threading;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar;

internal enum UpdatePhase { Idle, Checking, UpToDate, Downloading, Installing, Failed }

/// <summary>
/// Looks for a newer release now and then and installs it on request. An AppImage replaces itself with
/// the new AppImage; the portable build (the .tar.gz) replaces its executable with the one from the new
/// archive. Linux lets a running program's file be replaced, so the swap happens right away; then the
/// app quits and a small detached shell reopens the new version once this process is gone.
/// </summary>
internal sealed class Updater
{
    private enum InstallKind { AppImage, Portable, Page }

    private readonly DispatcherTimer timer = new() { Interval = AppUpdates.CheckInterval };
    private readonly DispatcherTimer clearTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly bool isPreview;
    private DateTimeOffset? lastCheck;
    private bool isChecking;
    private bool isFlashing;
    private InstallKind? kind;

    public Updater(AppVersion? current)
    {
        Current = current;
        timer.Tick += async (_, _) => await CheckAsync();
        clearTimer.Tick += (_, _) =>
        {
            clearTimer.Stop();
            if (isFlashing) Set(UpdatePhase.Idle);
        };
    }

    /// <summary>Shows a made-up update without any network: used by <c>--snapshot --update-available</c>.</summary>
    public static Updater Preview(AppVersion version, AppVersion? current) =>
        new(current, preview: true) { Available = new AppRelease(version, AppUpdates.ReleasesPage, []) };

    private Updater(AppVersion? current, bool preview) : this(current) => isPreview = preview;

    public event Action? Changed;

    /// <summary>The running version (from the VERSION file the build stamps into the assembly).</summary>
    public static AppVersion? RunningVersion =>
        typeof(Updater).Assembly.GetName().Version is { } version ? AppVersion.From(version) : null;

    public AppVersion? Current { get; }

    /// <summary>A newer release with a file this copy can install, if there is one.</summary>
    public AppRelease? Available { get; private set; }

    public UpdatePhase Phase { get; private set; }

    public string? Failure { get; private set; }

    /// <summary>0–1 while downloading.</summary>
    public double Progress { get; private set; }

    public string CurrentText => Current?.ToString() ?? "dev";

    /// <summary>Only set by <c>--install-update</c> on CI; see <see cref="AppUpdates.FetchLatestAsync"/>.</summary>
    public string? ApiToken { get; init; }

    public bool ShowsBanner => Available is not null || Phase != UpdatePhase.Idle;

    /// <summary>Whether the button installs the update itself (otherwise it opens the download page).</summary>
    public bool CanInstallInPlace => isPreview || Kind != InstallKind.Page;

    /// <summary>"appimage", "portable" or "page": how this copy updates (for <c>--install-update</c>).</summary>
    internal string InstallMode => Kind.ToString().ToLowerInvariant();

    private InstallKind Kind => kind ??= FindKind();

    /// <summary>The release file this copy is replaced with.</summary>
    private string AssetName => Kind == InstallKind.Portable ? AppUpdates.LinuxArchiveAsset : AppUpdates.LinuxAppImageAsset;

    // MARK: Checking

    public void Start()
    {
        if (Current is null) return;
        timer.Start();
        // Let the usage requests go first.
        DispatcherTimer.RunOnce(() => _ = CheckAsync(), TimeSpan.FromSeconds(10));
    }

    /// <summary>When the popup opens or the computer wakes up.</summary>
    public void CheckIfStale()
    {
        if (lastCheck is { } last && DateTimeOffset.Now - last >= AppUpdates.CheckInterval) _ = CheckAsync();
    }

    /// <summary><paramref name="manual"/> is "Check for updates": it also says when there is nothing new or the check failed.</summary>
    public async Task CheckAsync(bool manual = false)
    {
        if (Current is not { } current)
        {
            if (manual) Flash(UpdatePhase.Failed, T("Esta cópia foi compilada localmente e não se atualiza sozinha.", "This copy was built locally and does not update itself."));
            return;
        }
        if (isPreview || isChecking || Phase is UpdatePhase.Downloading or UpdatePhase.Installing) return;
        isChecking = true;
        if (manual) Set(UpdatePhase.Checking);
        try
        {
            var release = await AppUpdates.FetchLatestAsync(ApiToken);
            lastCheck = DateTimeOffset.Now;
            Available = AppUpdates.Update(release, current, AssetName);
            if (manual && Available is null) Flash(UpdatePhase.UpToDate);
            else if (manual || Phase == UpdatePhase.Failed) Set(UpdatePhase.Idle);
            else Changed?.Invoke();
        }
        catch (Exception)
        {
            if (manual) Flash(UpdatePhase.Failed, T("Não consegui procurar atualizações agora. Tente de novo mais tarde.", "Couldn't check for updates right now. Try again later."));
        }
        finally
        {
            isChecking = false;
        }
    }

    // MARK: Installing

    /// <summary>The "Update" button: installs in place and quits so the new version can start, or opens the download page.</summary>
    public async Task InstallAsync()
    {
        if (Available is null || isPreview || Phase is UpdatePhase.Downloading or UpdatePhase.Installing) return;
        if (!CanInstallInPlace)
        {
            OpenReleasePage();
            return;
        }
        if (await DownloadAndSwapAsync(relaunch: true))
        {
            // Let "Installing…" draw before the app goes away.
            await Task.Delay(400);
            TrayController.Quit();
        }
    }

    /// <summary>
    /// Downloads the update and puts it in place of this copy. With <paramref name="relaunch"/>, the new version
    /// starts once this process has exited. Returns false (with <see cref="Failure"/> set) if anything went wrong;
    /// nothing was changed then.
    /// </summary>
    internal async Task<bool> DownloadAndSwapAsync(bool relaunch)
    {
        var target = Kind switch
        {
            InstallKind.AppImage => Launcher.AppImage,
            InstallKind.Portable => Environment.ProcessPath,
            _ => null,
        };
        var asset = Available?.Asset(AssetName);
        var failure = T("Não consegui baixar ou instalar a nova versão. Baixe pela página.", "Couldn't download or install the new version. Download it from the page.");
        if (asset is null || target is null)
        {
            Failure = failure;
            Set(UpdatePhase.Failed);
            return false;
        }

        Progress = 0;
        Set(UpdatePhase.Downloading);
        // Next to the old file, so the swap is a rename on the same file system.
        var downloaded = target + ".update";
        var archive = target + ".update.tar.gz";
        try
        {
            var shown = -1;
            var progress = new Progress<double>(value =>
            {
                Progress = value;
                var percent = (int)(value * 100);
                if (percent == shown) return;
                shown = percent;
                Changed?.Invoke();
            });
            if (Kind == InstallKind.Portable)
            {
                await AppUpdates.DownloadAsync(asset, archive, progress);
                await Task.Run(() => ExtractExecutable(archive, Path.GetFileName(target), downloaded));
                File.Delete(archive);
            }
            else
            {
                await AppUpdates.DownloadAsync(asset, downloaded, progress);
            }
            Set(UpdatePhase.Installing);
            File.SetUnixFileMode(downloaded, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.UserRead);
            File.Move(downloaded, target, overwrite: true);
            if (relaunch) RelaunchAfterExit(target);
            return true;
        }
        catch (Exception)
        {
            TryDelete(downloaded);
            TryDelete(archive);
            Failure = failure;
            Set(UpdatePhase.Failed);
            return false;
        }
    }

    /// <summary>Takes the executable named <paramref name="name"/> out of the release's .tar.gz.</summary>
    private static void ExtractExecutable(string archive, string name, string destination)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && Path.GetFileName(entry.Name) == name)
            {
                entry.ExtractToFile(destination, overwrite: true);
                return;
            }
        }
        throw new InvalidDataException($"{name} is not in the archive");
    }

    public void OpenReleasePage() => Shell.Open((Available?.Page ?? AppUpdates.ReleasesPage).ToString());

    /// <summary>A detached shell that waits for this process to exit (the single-instance lock goes with it) and starts the new version.</summary>
    private static void RelaunchAfterExit(string target)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in new[]
        {
            "-c",
            """
            i=0; while kill -0 "$1" 2>/dev/null && [ $i -lt 240 ]; do sleep 0.25; i=$((i+1)); done
            if command -v setsid >/dev/null 2>&1; then exec setsid "$2" >/dev/null 2>&1 </dev/null; else exec "$2" >/dev/null 2>&1 </dev/null; fi
            """,
            "ai-usage-bar-update",
            Environment.ProcessId.ToString(),
            target,
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var _ = Process.Start(start);
    }

    /// <summary>
    /// An AppImage in a folder this user can write to, the portable single-file executable likewise, or neither
    /// (a build from source, a package installed system-wide): then the download page opens.
    /// </summary>
    private static InstallKind FindKind()
    {
        if (Launcher.AppImage is { } appImage)
        {
            return CanWrite(Path.GetDirectoryName(appImage)) ? InstallKind.AppImage : InstallKind.Page;
        }
        // A single-file app has no assembly path; a build from source does.
        if (Environment.ProcessPath is not { } exe || !Launcher.IsSingleFile) return InstallKind.Page;
        return CanWrite(Path.GetDirectoryName(exe)) ? InstallKind.Portable : InstallKind.Page;
    }

    private static bool CanWrite(string? folder)
    {
        if (folder is null) return false;
        try
        {
            var probe = Path.Combine(folder, ".aiusagebar-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // A leftover next to the app is harmless and overwritten next time.
        }
    }

    private void Set(UpdatePhase phase)
    {
        Phase = phase;
        isFlashing = false;
        clearTimer.Stop();
        Changed?.Invoke();
    }

    /// <summary>A passing message that clears itself after a few seconds.</summary>
    private void Flash(UpdatePhase phase, string? failure = null)
    {
        Failure = failure;
        Set(phase);
        isFlashing = true;
        clearTimer.Start();
    }
}
