using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsageBar.Core;

namespace AIUsageBar;

/// <summary>User choices, kept in <c>%APPDATA%\AIUsageBar\settings.json</c>.</summary>
internal sealed class AppSettings
{
    public string Language { get; set; } = Localization.SystemDefault().Code();
    public TrayStyle Style { get; set; } = TrayStyle.Icon;
    public bool ColorAlerts { get; set; } = true;
    public Dictionary<string, TrayDisplay> Displays { get; set; } = [];

    /// <summary>The first-run "Agents" tab was closed with Done.</summary>
    public bool OnboardingDismissed { get; set; }

    /// <summary>Key of the agent tab shown last.</summary>
    public string? SelectedProvider { get; set; }

    /// <summary>Agents the user switched off in the Agents tab (keys).</summary>
    public List<string> HiddenProviders { get; set; } = [];

    [JsonIgnore]
    public AppLanguage LanguageValue
    {
        get => Localization.LanguageFromCode(Language) ?? Localization.SystemDefault();
        set => Language = value.Code();
    }

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIUsageBar");

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

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(text, Options) ?? new AppSettings();
                // Saved by an older version: this is not a first run.
                if (!text.Contains(nameof(OnboardingDismissed), StringComparison.OrdinalIgnoreCase)) loaded.OnboardingDismissed = true;
                return loaded;
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
