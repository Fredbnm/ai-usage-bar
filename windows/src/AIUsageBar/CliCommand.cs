using System.Runtime.InteropServices;
using AIUsageBar.Core;
using AIUsageBar.Core.Rendering;

namespace AIUsageBar;

/// <summary>
/// Headless modes for debugging and CI:
/// <code>
/// AIUsageBar.exe --dump [--raw]
/// AIUsageBar.exe --snapshot out.png [--fixture claude.json] [--codex-fixture codex.json] [--copilot-fixture copilot.json] [--cursor-fixture cursor.json] [--gemini-fixture gemini.json] [--grok-fixture grok.json] [--dark] [--lang en|pt] [--tab claude|copilot|cursor|grokBot|agents] [--first-run] [--update-available 9.9.9] [--tab-limit claude=weekly_all] [--layout tabs|list]
/// </code>
/// AIUsageBar.exe --install-update [--no-relaunch] [--as-version 0.0.1]
/// <c>--snapshot</c> also writes <c>out-tray.png</c> with every tray icon combination.
/// <c>--install-update</c> runs the in-app update from the command line: if GitHub has a newer release, it is
/// installed over this copy once the command exits. <c>--as-version</c> pretends to be an older version (CI uses it).
/// The app is a GUI program, so run it with <c>Start-Process -Wait</c> (or from cmd) to see the output.
/// </summary>
internal sealed class CliCommand
{
    private readonly string[] args;

    private CliCommand(string[] args) => this.args = args;

    public static CliCommand? TryParse(string[] args) =>
        args.Contains("--dump") || args.Contains("--snapshot") || args.Contains("--install-update") ? new CliCommand(args) : null;

    private string? Option(string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    public async Task<int> RunAsync()
    {
        AttachConsole(AttachParentProcess);
        try
        {
            if (Localization.LanguageFromCode(Option("--lang")) is { } language) Localization.Current = language;
            if (args.Contains("--install-update")) return await InstallUpdateAsync();
            return args.Contains("--dump") ? await DumpAsync() : Snapshot();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private async Task<int> DumpAsync()
    {
        IUsageProvider[] providers = [new ClaudeProvider(), new CodexProvider(), new CopilotProvider(), new CursorProvider(), new GeminiProvider(), new GrokBotProvider(new DpapiUnprotector())];
        foreach (var provider in providers.Where(p => p.IsAvailable()))
        {
            Console.WriteLine($"== {provider.Id.DisplayName()}");
            try
            {
                var result = await provider.FetchAsync();
                if (result.Snapshot.PlanName is { } plan) Console.WriteLine($"Plan: {plan}");
                foreach (var item in result.Snapshot.Items)
                {
                    var reset = item.ResetsAt is { } at ? " — " + UsageFormat.Reset(at) : "";
                    Console.WriteLine($"• {item.Title}: {UsageFormat.Percent(item.Percent)} [{item.Severity}]{reset}");
                }
                foreach (var note in result.Snapshot.Notes) Console.WriteLine($"i {note.Text}");
                if (args.Contains("--raw")) Console.WriteLine(System.Text.Encoding.UTF8.GetString(result.Cache));
            }
            catch (UsageException error)
            {
                Console.WriteLine($"! {error.MessageFor(provider.Id)}");
            }
        }
        return 0;
    }

    /// <summary>The snapshots of the <c>--*fixture</c> options, in display order.</summary>
    internal static List<ProviderSnapshot> FixtureSnapshots(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        var snapshots = new List<ProviderSnapshot>();
        if (Option("--fixture") is { } claude &&
            ClaudeProvider.SnapshotFromResponse(File.ReadAllBytes(claude), "Max 5x") is { } claudeSnapshot)
        {
            snapshots.Add(claudeSnapshot);
        }
        if (Option("--codex-fixture") is { } codex && CodexUsage.Snapshot(File.ReadAllBytes(codex)) is { } codexSnapshot)
        {
            snapshots.Add(codexSnapshot);
        }
        if (Option("--copilot-fixture") is { } copilot && CopilotUsage.Snapshot(File.ReadAllBytes(copilot)) is { } copilotSnapshot)
        {
            snapshots.Add(copilotSnapshot);
        }
        if (Option("--cursor-fixture") is { } cursor && CursorUsage.Snapshot(File.ReadAllBytes(cursor), "Pro") is { } cursorSnapshot)
        {
            snapshots.Add(cursorSnapshot);
        }
        if (Option("--gemini-fixture") is { } gemini && GeminiUsage.Snapshot(File.ReadAllBytes(gemini), "Free") is { } geminiSnapshot)
        {
            snapshots.Add(geminiSnapshot);
        }
        if (Option("--grok-fixture") is { } grok && GrokUsage.Snapshot(File.ReadAllBytes(grok)) is { } grokSnapshot)
        {
            snapshots.Add(grokSnapshot);
        }
        return snapshots;
    }

    private int Snapshot()
    {
        var output = Option("--snapshot") ?? throw new ArgumentException("--snapshot needs a file name");
        var snapshots = FixtureSnapshots(args);

        var settings = new AppSettings { LanguageValue = Localization.Current, OnboardingDismissed = !args.Contains("--first-run") };
        var store = new UsageStore(settings, snapshots);
        if (Option("--tab") is { } tab) store.PreviewTab(tab);
        if (Enum.TryParse<PopupLayout>(Option("--layout"), ignoreCase: true, out var layout)) settings.Layout = layout;
        if (Option("--tab-limit")?.Split('=', 2) is [var agent, var limit] && PopupTabs.ProviderOf(agent) is { } limited)
        {
            settings.TabLimits[limited.Key()] = limit;
        }
        var updater = AppVersion.Parse(Option("--update-available")) is { } update
            ? Updater.Preview(update, Updater.RunningVersion)
            : new Updater(current: null);
        var window = new PopupWindow(store, updater);
        window.RenderToPng(output);
        window.Close();
        Console.WriteLine($"Saved {output}");

        var glyphs = new Dictionary<ProviderId, Glyph>();
        foreach (var snapshot in snapshots)
        {
            if (InstalledApps.Glyph(snapshot.Provider) is { } glyph) glyphs[snapshot.Provider] = glyph;
        }
        var trayPath = Path.ChangeExtension(output, null) + "-tray.png";
        File.WriteAllBytes(trayPath, TrayPreview.RenderPng(snapshots, glyphs));
        Console.WriteLine($"Saved {trayPath}");
        return 0;
    }

    private async Task<int> InstallUpdateAsync()
    {
        var updater = new Updater(AppVersion.Parse(Option("--as-version")) ?? Updater.RunningVersion)
        {
            ApiToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
        };
        await updater.CheckAsync(manual: true);
        if (updater.Available is not { } release)
        {
            if (updater.Phase == UpdatePhase.Failed)
            {
                Console.Error.WriteLine(updater.Failure);
                return 1;
            }
            Console.WriteLine($"Running {updater.CurrentText}: nothing to install.");
            return 0;
        }
        Console.WriteLine($"Running {updater.CurrentText} ({updater.InstallMode}); installing {release.Version}.");
        if (!await updater.DownloadAndSwapAsync(relaunch: !args.Contains("--no-relaunch")))
        {
            Console.Error.WriteLine(updater.Failure);
            return 1;
        }
        Console.WriteLine($"Installing {release.Version} as soon as this command exits.");
        return 0;
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
