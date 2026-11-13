using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>PF-R3: a save refused because of its backup says, in every language, which file it is and what to do.</summary>
[Collection("Localization")]
public sealed class StoreBackupBlockedTextTests : IDisposable
{
    private readonly string _previous = Texts.Language;
    public StoreBackupBlockedTextTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    [Theory]
    [InlineData("NewerVersion", "reader-groups.json.bak", "newer")]
    [InlineData("Unreadable", "archive-searches.json.bak", "could not be read")]
    public void TheTextNamesTheFileNotThePathAndTheWayOutInEveryLanguage(string reasonName, string file, string englishFragment)
    {
        ProfileBackupBlockReason reason = Enum.Parse<ProfileBackupBlockReason>(reasonName);
        string directory = Path.Combine(Path.GetTempPath(), "profile-secret-folder");
        ProfileBackupBlockedException blocked = new(Path.Combine(directory, file), reason);
        Assert.Equal(file, blocked.FileName);

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            string shown = StoreBackupBlockedText.For(blocked);
            Assert.Contains(file, shown, StringComparison.Ordinal);
            Assert.DoesNotContain("profile-secret-folder", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("store.backupBlocked", shown, StringComparison.Ordinal);
            Assert.DoesNotContain("{", shown, StringComparison.Ordinal);
            Assert.True(seen.Add(shown), $"{language} shows the text of another language.");
            if (language == "en") Assert.Contains(englishFragment, shown, StringComparison.Ordinal);
        }
    }
}
