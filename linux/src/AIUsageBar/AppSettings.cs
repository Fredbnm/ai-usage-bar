using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsageBar.Core;
using static AIUsageBar.Core.Localization;

namespace AIUsageBar;

/// <summary>
/// The color of the tray icons. Linux panels do not say how dark they are, so "Automatic" guesses:
/// KDE's panel follows the light/dark setting, GNOME's top bar and most other panels are dark.
/// </summary>
internal enum TrayIconColor { Automatic, White, Black }

/// <summary>User choices, kept in <c>~/.config/AIUsageBar/settings.json</c> (or under <c>$XDG_CONFIG_HOME</c>).</summary>
internal sealed class AppSettings
{
    public string Language { get; set; } = Localization.SystemDefault().Code();
    public TrayStyle Style { get; set; } = TrayStyle.Icon;
    public TrayIconColor IconColor { get; set; } = TrayIconColor.Automatic;
    public bool ColorAlerts { get; set; } = true;
    public Dictionary<string, TrayDisplay> Displays { get; set; } = [];

    /// <summary>The limit each agent's tab shows (<see cref="TabLimit"/> keys); missing means the fullest one.</summary>
    public Dictionary<string, string> TabLimits { get; set; } = [];

    /// <summary>The first-run "Agents" tab was closed with Done.</summary>
    public bool OnboardingDismissed { get; set; }

    /// <summary>Key of the agent tab shown last.</summary>
    public string? SelectedProvider { get; set; }

    /// <summary>Agents the user switched off in the Agents tab (keys).</summary>
    public List<string> HiddenProviders { get; set; } = [];

    /// <summary>Agents the user switched on in the Agents tab, shown even before they return any usage (keys).</summary>
    public List<string> ShownProviders { get; set; } = [];

    /// <summary>Tabs (one agent at a time) or every agent on one screen.</summary>
    public PopupLayout Layout { get; set; } = PopupLayout.Tabs;

    /// <summary>Snapshots and the demo never write the user's settings.</summary>
    [JsonIgnore]
    public bool IsTransient { get; init; }

    [JsonIgnore]
    public AppLanguage LanguageValue
    {
        get => Localization.LanguageFromCode(Language) ?? Localization.SystemDefault();
        set => Language = value.Code();
    }

    /// <summary><c>$XDG_CONFIG_HOME/AIUsageBar</c>, by default <c>~/.config/AIUsageBar</c>.</summary>
    public static string Directory => Path.Combine(Xdg.ConfigHome, "AIUsageBar");

    /// <summary><c>$XDG_CACHE_HOME/AIUsageBar</c>, by default <c>~/.cache/AIUsageBar</c>: the last usage responses.</summary>
    public static string CacheDirectory => Path.Combine(Xdg.Home("XDG_CACHE_HOME", ".cache"), "AIUsageBar");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public TrayDisplay DisplayFor(ProviderId provider) =>
        Displays.TryGetValue(provider.Key(), out var display) && TrayOptions.For(provider).Contains(display)
            ? display
            : TrayOptions.Default(provider);

    public string TabLimitFor(ProviderId provider) =>
        TabLimits.TryGetValue(provider.Key(), out var key) && !string.IsNullOrEmpty(key) ? key : TabLimit.Highest;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // A broken settings file falls back to defaults.
        }
        var fresh = new AppSettings();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        if (IsTransient) return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // Settings are a convenience; never crash over them.
        }
    }
}

/// <summary>
/// The XDG base directories. Not <c>Environment.GetFolderPath</c>: it returns an empty string when the
/// folder does not exist yet (a fresh account without <c>~/.config</c>), which would put files in the current folder.
/// </summary>
internal static class Xdg
{
    public static string UserHome =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>The folder named by <paramref name="variable"/> when it is an absolute path, else <c>~/<paramref name="fallback"/></c>.</summary>
    public static string Home(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path && Path.IsPathRooted(path)
            ? path
            : Path.Combine(UserHome, fallback);

    public static string ConfigHome => Home("XDG_CONFIG_HOME", ".config");

    public static string DataHome => Home("XDG_DATA_HOME", Path.Combine(".local", "share"));
}

internal static class TrayIconColorExtensions
{
    public static string Label(this TrayIconColor color) => color switch
    {
        TrayIconColor.White => T("Brancos (painel escuro)", "White (dark panel)"),
        TrayIconColor.Black => T("Pretos (painel claro)", "Black (light panel)"),
        _ => T("Automático", "Automatic"),
    };
}
