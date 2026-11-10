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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (CliCommand.TryParse(e.Args) is { } command)
        {
            var exitCode = await command.RunAsync();
            Shutdown(exitCode);
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

        store = new UsageStore(settings, [new ClaudeProvider(), new CopilotProvider(), new GrokBotProvider(new DpapiUnprotector())]);
        tray = new TrayController(store);
        store.Start();

        // Light/dark switches, accent color changes, DPI changes.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
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
            Dispatcher.InvokeAsync(() => store.RefreshAsync(force: true));
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
