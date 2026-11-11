using Avalonia;
using Avalonia.Controls;

// Linux only: the analyzers then accept the Unix file APIs.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace AIUsageBar;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (CliCommand.TryParse(args) is { } command) return command.Run();

        // The demo runs beside a real copy; anything else is one copy per user.
        SingleInstance? instance = null;
        if (!args.Contains("--demo"))
        {
            instance = SingleInstance.Acquire();
            if (instance is null)
            {
                Console.WriteLine("AI Usage Bar is already running: opening its popup.");
                return 0;
            }
        }

        using (instance)
        {
            App.Instance = instance;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
    }

    /// <summary>Also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
