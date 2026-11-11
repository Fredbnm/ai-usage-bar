using System.Diagnostics;
using AIUsageBar.Core;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar;

public partial class App : Application
{
    private UsageStore? store;
    private TrayController? tray;
    private Updater? updater;

    /// <summary>Set by <see cref="Program"/>: null in the demo, which runs beside a real copy.</summary>
    internal static SingleInstance? Instance { get; set; }

    /// <summary>Shown in the popup when nothing on the desktop displays tray icons.</summary>
    internal static string NoTrayNotice => T(
        "Esta área de trabalho não está mostrando ícones na bandeja. No GNOME, instale a extensão “AppIndicator and KStatusNotifierItem Support”. Até lá, abra o AI Usage Bar pelo menu de aplicativos para ver este painel.",
        "This desktop is not showing tray icons. On GNOME, install the “AppIndicator and KStatusNotifierItem Support” extension. Until then, open AI Usage Bar from the app menu to see this panel.");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        // Headless snapshots (CliCommand) set up their own window.
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;

        var args = desktop.Args ?? [];
        var demo = args.Contains("--demo");
        if (demo && Localization.LanguageFromCode(CliCommand.Option(args, "--lang")) is { } language) Localization.Current = language;

        AppSettings settings;
        if (demo)
        {
            settings = CliCommand.FixtureSettings(args);
            store = new UsageStore(settings, CliCommand.FixtureSnapshots(args));
            updater = AppVersion.Parse(CliCommand.Option(args, "--update-available")) is { } update
                ? Updater.Preview(update, Updater.RunningVersion)
                : new Updater(current: null);
        }
        else
        {
            settings = AppSettings.Load();
            Localization.Current = settings.LanguageValue;
            store = new UsageStore(settings, UsageStore.AllProviders());
            updater = new Updater(Updater.RunningVersion);
        }
        Theme.Apply(forceDark: args.Contains("--dark") ? true : null);

        tray = new TrayController(store, updater);
        if (!demo)
        {
            store.Start();
            updater.Start();
            store.Resumed += updater.CheckIfStale;
            MenuEntry.Ensure();
        }

        // Light/dark switches and accent color changes.
        if (PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                Theme.Apply(forceDark: args.Contains("--dark") ? true : null);
                tray.Refresh();
            });
        }

        // Started again (from the app menu, say): show the popup.
        if (Instance is { } instance) instance.Activated += () => tray.Popup.ShowNearPointer();

        // The first run opens the Agents tab, as on the other systems; a desktop without a tray gets the popup
        // with a hint, since there is nothing else to click.
        DispatcherTimer.RunOnce(() =>
        {
            var noTray = !TrayHost.IsAvailable();
            if (noTray) tray.Popup.Notice = NoTrayNotice;
            if (noTray || !settings.OnboardingDismissed || args.Contains("--open")) tray.Popup.ShowNearPointer();
        }, TimeSpan.FromSeconds(demo ? 0.5 : 3));

        desktop.Exit += (_, _) => tray.Dispose();
    }
}

/// <summary>Whether something on the desktop shows StatusNotifierItems (a tray).</summary>
internal static class TrayHost
{
    /// <summary>
    /// Asks D-Bus whether the StatusNotifierWatcher is there. True when that cannot be known
    /// (no <c>dbus-send</c>), so the hint is only shown when the tray is surely missing.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            var start = new ProcessStartInfo("dbus-send")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
            {
                "--session", "--print-reply", "--dest=org.freedesktop.DBus", "/org/freedesktop/DBus",
                "org.freedesktop.DBus.NameHasOwner", "string:org.kde.StatusNotifierWatcher",
            })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start);
            if (process is null) return true;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000)) return true;
            return process.ExitCode != 0 || !output.Result.Contains("boolean false");
        }
        catch (Exception)
        {
            return true;
        }
    }
}
