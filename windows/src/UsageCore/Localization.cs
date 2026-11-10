using System.Globalization;

namespace AIUsageBar.Core;

/// <summary>The language the whole app is shown in.</summary>
public enum AppLanguage { Portuguese, English }

public static class Localization
{
    private static int current = (int)SystemDefault();

    /// <summary>
    /// Read by every piece of text the app produces. Usage is fetched off the
    /// UI thread, hence the volatile access.
    /// </summary>
    public static AppLanguage Current
    {
        get => (AppLanguage)Volatile.Read(ref current);
        set => Volatile.Write(ref current, (int)value);
    }

    /// <summary>Portuguese for a Windows set to Portuguese, English otherwise.</summary>
    public static AppLanguage SystemDefault() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? AppLanguage.Portuguese : AppLanguage.English;

    /// <summary>The text for the current language: <c>T("Semanal", "Weekly")</c>.</summary>
    public static string T(string portuguese, string english) =>
        Current == AppLanguage.Portuguese ? portuguese : english;

    public static string Code(this AppLanguage language) => language == AppLanguage.Portuguese ? "pt" : "en";

    public static AppLanguage? LanguageFromCode(string? code) => code switch
    {
        "pt" => AppLanguage.Portuguese,
        "en" => AppLanguage.English,
        _ => null,
    };

    /// <summary>Each language named in itself, so it can be found whatever is selected.</summary>
    public static string NativeName(this AppLanguage language) =>
        language == AppLanguage.Portuguese ? "Português" : "English";

    public static CultureInfo Culture(this AppLanguage language) =>
        CultureInfo.GetCultureInfo(language == AppLanguage.Portuguese ? "pt-BR" : "en-US");
}
