using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using AIUsageBar.Core;
using Microsoft.Win32;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar;

internal enum UpdatePhase { Idle, Checking, UpToDate, Downloading, Installing, Failed }

/// <summary>
/// Looks for a newer release now and then and installs it on request. An installed
/// copy runs the new Setup silently; a portable copy swaps its own .exe. Either way
/// the app quits first and a small hidden PowerShell script reopens it afterwards.
/// </summary>
internal sealed class Updater
{
    /// <summary>The installer's AppId (see installer/AIUsageBar.iss), as Inno Setup names its uninstall key.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6B0E4C1D-8A2F-4E7B-9C35-1F7D2A9E4B60}_is1";

    private enum InstallKind { Installer, Portable, Page }

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

    /// <summary>The message of <see cref="UpdatePhase.Failed"/>.</summary>
    public string? Failure { get; private set; }

    /// <summary>0–1 while downloading.</summary>
    public double Progress { get; private set; }

    public string CurrentText => Current?.ToString() ?? "dev";

    /// <summary>Only set by <c>--install-update</c> on CI; see <see cref="AppUpdates.FetchLatestAsync"/>.</summary>
    public string? ApiToken { get; init; }

    public bool ShowsBanner => Available is not null || Phase != UpdatePhase.Idle;

    /// <summary>Whether the button installs the update itself (otherwise it opens the download page).</summary>
    public bool CanInstallInPlace => isPreview || Kind != InstallKind.Page;

    /// <summary>Where this copy lives does not change while it runs, so it is worked out once.</summary>
    private InstallKind Kind => kind ??= FindKind();

    // MARK: Checking

    public void Start()
    {
        if (Current is null) return;
        timer.Start();
        // Let the usage requests go first.
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(
            _ => CheckAsync(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>When the popup opens or the PC resumes, since the timer does not run during sleep.</summary>
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
            var asset = Kind == InstallKind.Portable ? AppUpdates.PortableAsset : AppUpdates.SetupAsset;
            Available = AppUpdates.Update(release, current, asset);
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

    /// <summary>The "Update" button: installs in place and quits so the swap can run, or opens the download page.</summary>
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
            System.Windows.Application.Current.Shutdown();
        }
    }

    /// <summary>"installer", "portable" or "page": how this copy updates (for <c>--install-update</c>).</summary>
    internal string InstallMode => Kind.ToString().ToLowerInvariant();

    /// <summary>
    /// Downloads the update and starts the script that swaps it in once this process exits.
    /// Returns false (with <see cref="Failure"/> set) if anything went wrong; nothing was changed then.
    /// </summary>
    internal async Task<bool> DownloadAndSwapAsync(bool relaunch)
    {
        var kind = Kind;
        var asset = kind switch
        {
            InstallKind.Installer => Available?.Asset(AppUpdates.SetupAsset),
            InstallKind.Portable => Available?.Asset(AppUpdates.PortableAsset),
            _ => null,
        };
        var failure = T("Não consegui baixar ou instalar a nova versão. Baixe pela página.", "Couldn't download or install the new version. Download it from the page.");
        if (asset is null || Environment.ProcessPath is not { } exe)
        {
            Failure = failure;
            Set(UpdatePhase.Failed);
            return false;
        }

        Progress = 0;
        Set(UpdatePhase.Downloading);
        var work = Path.Combine(Path.GetTempPath(), "AIUsageBar-update-" + Guid.NewGuid().ToString("N"));
        // The portable .exe is downloaded next to the old one, so the swap is a rename on the same drive.
        var downloaded = kind == InstallKind.Installer ? Path.Combine(work, asset.Name) : exe + ".update";
        try
        {
            Directory.CreateDirectory(work);
            var shown = -1;
            var progress = new Progress<double>(value =>
            {
                Progress = value;
                var percent = (int)(value * 100);
                if (percent == shown) return;
                shown = percent;
                Changed?.Invoke();
            });
            await AppUpdates.DownloadAsync(asset, downloaded, progress);
            Set(UpdatePhase.Installing);
            StartSwap(kind, downloaded, exe, work, relaunch);
            return true;
        }
        catch (Exception)
        {
            TryDelete(downloaded);
            TryDelete(work);
            Failure = failure;
            Set(UpdatePhase.Failed);
            return false;
        }
    }

    public void OpenReleasePage() =>
        Process.Start(new ProcessStartInfo((Available?.Page ?? AppUpdates.ReleasesPage).ToString()) { UseShellExecute = true });

    /// <summary>
    /// Waits for this process to exit, then runs the Setup silently (keeping the user's "Start with Windows"
    /// choice) or moves the new portable .exe over the old one, and reopens the app either way.
    /// </summary>
    private static void StartSwap(InstallKind kind, string downloaded, string exe, string work, bool relaunch)
    {
        static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
        var pid = Environment.ProcessId;
        var startup = StartupRegistration.IsEnabled ? "startup" : "!startup";
        var swap = kind == InstallKind.Installer
            ? $"Start-Process -FilePath {Quote(downloaded)} -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/MERGETASKS={startup}' -Wait"
            : $$"""
              foreach ($i in 1..40) {
                try { Move-Item -LiteralPath {{Quote(downloaded)}} -Destination {{Quote(exe)}} -Force -ErrorAction Stop; break }
                catch { Start-Sleep -Milliseconds 250 }
              }
              Remove-Item -LiteralPath {{Quote(downloaded)}} -Force -ErrorAction SilentlyContinue
              """;
        var script = $"""
            $ErrorActionPreference = 'SilentlyContinue'
            Wait-Process -Id {pid} -Timeout 60
            {swap}
            {(relaunch ? $"Start-Process -FilePath {Quote(exe)}" : "")}
            Remove-Item -LiteralPath {Quote(work)} -Recurse -Force
            """;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-EncodedCommand" })
        {
            start.ArgumentList.Add(argument);
        }
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        Process.Start(start);
    }

    /// <summary>
    /// Installed by Setup (the exe sits where the uninstall key says), portable (a single-file exe in a folder
    /// this user can write to), or neither (a build from source, or a read-only folder): then the page opens.
    /// </summary>
    private static InstallKind FindKind()
    {
        // A single-file app has no assembly path; a build from source does.
        if (Environment.ProcessPath is not { } exe || !string.IsNullOrEmpty(typeof(Updater).Assembly.Location)) return InstallKind.Page;
        var folder = Path.GetDirectoryName(exe);
        if (folder is null) return InstallKind.Page;
        using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
        {
            if (key?.GetValue("InstallLocation") is string installed
                && string.Equals(Path.TrimEndingDirectorySeparator(installed), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase))
            {
                return InstallKind.Installer;
            }
        }
        return CanWrite(folder) ? InstallKind.Portable : InstallKind.Page;
    }

    private static bool CanWrite(string folder)
    {
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
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // A leftover in %TEMP% is harmless.
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
