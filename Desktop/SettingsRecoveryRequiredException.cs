using System;
using System.Globalization;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The profile's settings cannot be used as they are (damaged, newer format, backup only, or missing next to other data).
/// The host refuses to start so that no file is touched; the startup window explains the situation and the way out
/// in the user's language instead of showing the technical message below.
/// </summary>
internal sealed class SettingsRecoveryRequiredException : InvalidOperationException
{
    internal const string SettingsFileName = "settings.json";
    internal const string BackupFileName = SettingsFileName + ".bak";

    internal ProfileOrigin Origin { get; }
    internal string ProfileDirectory { get; }
    /// <summary>The language of a readable backup; null when no settings were read, so the caller picks one.</summary>
    internal string? SavedLanguage { get; }

    private SettingsRecoveryRequiredException(ProfileOrigin origin, string profileDirectory, string? savedLanguage)
        : base($"Settings recovery requires an explicit recovery decision ({origin}); existing files were preserved.")
    {
        Origin = origin;
        ProfileDirectory = profileDirectory;
        SavedLanguage = savedLanguage;
    }

    internal static SettingsRecoveryRequiredException For(Config config, string profileDirectory) =>
        new(config.Origin, profileDirectory, config.LoadProblem == SettingsLoadProblem.RecoveredFromBackup ? config.Language : null);

    /// <summary>The text of the startup window in the current language: what happened, that nothing was changed, what to do.</summary>
    internal string LocalizedMessage()
    {
        string key = Origin switch
        {
            ProfileOrigin.BackupRecovery => "settings.startup.backup",
            ProfileOrigin.Unsupported => "settings.startup.unsupported",
            ProfileOrigin.ExistingMissingSettings => "settings.startup.missing",
            _ => "settings.startup.damaged"
        };
        return Texts.T(key, SettingsFileName, BackupFileName, ProfileDirectory);
    }

    /// <summary>The language of the startup window: a readable backup's saved language, else the operating system's.</summary>
    internal static string StartupLanguage(string? savedLanguage, CultureInfo operatingSystem)
    {
        CultureInfo culture = operatingSystem;
        if (savedLanguage is not null)
        {
            if (!Texts.IsValidLanguageCode(savedLanguage)) return "en";
            try { culture = CultureInfo.GetCultureInfo(savedLanguage); }
            catch (CultureNotFoundException) { return "en"; }
        }

        var languages = Texts.Languages;
        for (CultureInfo current = culture; current.Name.Length != 0; current = current.Parent)
        {
            LanguageInfo? language = languages.FirstOrDefault(info => string.Equals(info.Code, current.Name, StringComparison.OrdinalIgnoreCase));
            if (language is not null) return language.Code;
        }
        return "en";
    }
}
