using System.Windows;
using AIUsageBar.Core;
using Microsoft.Win32;
using Localization = AIUsageBar.Core.Localization;

namespace AIUsageBar;

public partial class App : System.Windows.Application
{
    private Mutex? singleInstance;
    private UsageStore? store;
    private TrayController? tray;
    private Updater? updater;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (CliCommand.TryParse(e.Args) is { } command)
        {
            var exitCode = await command.RunAsync();
            Shutdown(exitCode);
            return;
        }

        // Debug: the real tray and taskbar widget on the sample fixtures, with no logins read and no network.
        if (e.Args.Contains("--widget-demo"))
        {
            RunWidgetDemo(e.Args);
            return;
        }

        singleInstance = new Mutex(initiallyOwned: true, "AIUsageBar.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        Localization.Current = settings.LanguageValue;
        Theme.Apply();

        store = new UsageStore(settings, [new ClaudeProvider(), new CodexProvider(), new CopilotProvider(), new GeminiProvider(), new GrokBotProvider(new DpapiUnprotector())]);
        updater = new Updater(Updater.RunningVersion);
        tray = new TrayController(store, updater);
        store.Start();
        updater.Start();

        // Light/dark switches, accent color changes, DPI changes.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void RunWidgetDemo(string[] args)
    {
        if (Localization.LanguageFromCode(args.SkipWhile(a => a != "--lang").Skip(1).FirstOrDefault()) is { } language) Localization.Current = language;
        Theme.Apply(forceDark: args.Contains("--dark"));
        var demo = new AppSettings
        {
            LanguageValue = Localization.Current,
            OnboardingDismissed = true,
            ShowTaskbarWidget = true,
            TaskbarPlacement = args.Contains("--left-edge") ? TaskbarPlacement.LeftEdge : TaskbarPlacement.LeftOfTray,
            TaskbarStyle = args.Contains("--ring-only") ? TaskbarStyle.RingOnly : TaskbarStyle.RingAndPercent,
        };
        store = new UsageStore(demo, CliCommand.FixtureSnapshots(args));
        tray = new TrayController(store, new Updater(current: null));
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.InvokeAsync(() =>
        {
            Theme.Apply();
            tray?.Refresh();
        });

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => tray?.Refresh());

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && store is not null)
        {
            Dispatcher.InvokeAsync(() =>
            {
                _ = store.RefreshAsync(force: true);
                updater?.CheckIfStale();
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        tray?.Dispose();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
