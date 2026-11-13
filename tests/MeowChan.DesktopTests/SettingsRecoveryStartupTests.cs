using System.Globalization;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Tests.PersistenceCorruption;

namespace MeowChan.DesktopTests;

/// <summary>
/// What the user sees when the profile's settings cannot be used: the host refuses to start (nothing is written) and the startup
/// window gets a localized text that says what happened, that nothing was changed and what to do. Before PF-R1 the window showed
/// the English technical sentence with the enum name.
/// </summary>
[Collection("Localization")]
public sealed class SettingsRecoveryStartupTests : IDisposable
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };
    private readonly string _previous = Texts.Language;
    public SettingsRecoveryStartupTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    private static void SaveTwoGenerations(string directory)
    {
        string downloads = Path.Combine(directory, "downloads");
        new Config { DownloadPath = downloads, AutomaticUpdateChecks = false, Language = "de" }.SaveConfig(directory);
        new Config { DownloadPath = downloads, AutomaticUpdateChecks = false, Language = "de", BooruCatalogLimitMiB = 17 }.SaveConfig(directory);
    }

    // The profile lease's own marker files are not profile data.
    private static string[] Snapshot(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetFileName(path).StartsWith(".meowchan-profile", StringComparison.Ordinal)
            && !Path.GetFileName(path).StartsWith(".ochan-profile", StringComparison.Ordinal))
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(directory, path) + ":" + Convert.ToHexString(File.ReadAllBytes(path))).ToArray();

    // The four states in which a profile is refused, each built from real files.
    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { nameof(ProfileOrigin.BackupRecovery), "settings.startup.backup" },
        new object[] { nameof(ProfileOrigin.Unsupported), "settings.startup.unsupported" },
        new object[] { nameof(ProfileOrigin.Damaged), "settings.startup.damaged" },
        new object[] { nameof(ProfileOrigin.ExistingMissingSettings), "settings.startup.missing" }
    };

    private static void Prepare(ProfileOrigin origin, string directory)
    {
        string primary = Path.Combine(directory, "settings.json");
        switch (origin)
        {
            case ProfileOrigin.BackupRecovery:
                SaveTwoGenerations(directory);
                File.WriteAllBytes(primary, Damage.Make("truncated-half", File.ReadAllBytes(primary), "SchemaVersion"));
                break;
            case ProfileOrigin.Unsupported:
                File.WriteAllText(primary, "{\"SchemaVersion\": 99}");
                break;
            case ProfileOrigin.Damaged:
                File.WriteAllText(primary, "{");
                break;
            default:
                File.WriteAllText(Path.Combine(directory, "session.json"), "retained watch intent");
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void AProfileWithUnusableSettingsStopsTheHostWithoutTouchingAnyFile(string originName, string key)
    {
        ProfileOrigin origin = Enum.Parse<ProfileOrigin>(originName);
        using TempDirectory temp = new();
        Prepare(origin, temp.Path);
        string[] before = Snapshot(temp.Path);

        SettingsRecoveryRequiredException stop = Assert.Throws<SettingsRecoveryRequiredException>(
            () => new DesktopSessionHost(temp.Path, Array.Empty<IImageBoard>()));

        Assert.Equal(origin, stop.Origin);
        Assert.Equal(Path.GetFullPath(temp.Path), Path.GetFullPath(stop.ProfileDirectory));
        Assert.Equal(origin == ProfileOrigin.BackupRecovery ? "de" : null, stop.SavedLanguage);
        Assert.Equal(before, Snapshot(temp.Path));
        // The profile lease is released again: a second try is refused for the same reason, not because it is locked.
        Assert.Throws<SettingsRecoveryRequiredException>(() => new DesktopSessionHost(temp.Path, Array.Empty<IImageBoard>()));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheStartupTextIsLocalizedNamesTheFilesAndTheWayOutAndNeverTheEnumName(string originName, string key)
    {
        ProfileOrigin origin = Enum.Parse<ProfileOrigin>(originName);
        using TempDirectory temp = new();
        Prepare(origin, temp.Path);
        SettingsRecoveryRequiredException stop = Assert.Throws<SettingsRecoveryRequiredException>(
            () => new DesktopSessionHost(temp.Path, Array.Empty<IImageBoard>()));

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            string shown = stop.LocalizedMessage();
            Assert.Equal(Texts.T(key, "settings.json", "settings.json.bak", stop.ProfileDirectory), shown);
            Assert.DoesNotContain(key, shown, StringComparison.Ordinal);
            Assert.DoesNotContain(origin.ToString(), shown, StringComparison.Ordinal);
            Assert.DoesNotContain("explicit recovery decision", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("{", shown, StringComparison.Ordinal);
            Assert.Contains("settings.json", shown, StringComparison.Ordinal);
            Assert.Contains(stop.ProfileDirectory, shown, StringComparison.Ordinal);
            Assert.True(seen.Add(shown), $"{language} shows the text of another language.");
        }
        // The technical message stays English and is for the log only.
        Assert.Contains(origin.ToString(), stop.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EachSituationHasItsOwnWayOut()
    {
        string[] texts = Cases().Select(row => Texts.T((string)row[1], "settings.json", "settings.json.bak", "/profile")).ToArray();
        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("copy settings.json.bak over settings.json", texts[0], StringComparison.Ordinal);
        Assert.Contains("newer", texts[1], StringComparison.Ordinal);
        Assert.All(texts, text => Assert.Contains("Nothing was changed or deleted", text, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("de", "en-US", "de")]
    [InlineData("fr-FR", "en-US", "fr")]
    [InlineData(null, "ru-RU", "ru")]
    [InlineData(null, "es-MX", "es")]
    [InlineData(null, "ja-JP", "en")]
    [InlineData("xx-unknown", "de-DE", "en")]
    [InlineData(null, "en-GB", "en")]
    public void TheStartupLanguageIsTheBackupsOrTheOperatingSystemsWithEnglishAsTheFallback(string? saved, string osCulture, string expected) =>
        Assert.Equal(expected, SettingsRecoveryRequiredException.StartupLanguage(saved, CultureInfo.GetCultureInfo(osCulture)));
}
