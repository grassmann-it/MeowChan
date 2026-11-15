using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>Work package C of the dead-affordance audit: the status bar's store warning opens the setting it names, and the new texts exist in all 12 languages.</summary>
[Collection("Localization")]
public sealed class DeadAffordanceListsDialogsShellTests : IDisposable
{
    private readonly string _language = Texts.Language;
    public DeadAffordanceListsDialogsShellTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Fact]
    public void TheStoreWarningOpensTheSettingOfTheLimitThatIsNearlyReached()
    {
        StoreLimitUsage.Item entries = new(PrivateStoreLimitKind.FavoriteEntries, 90, 100);
        StoreLimitUsage.Item file = new(PrivateStoreLimitKind.FavoriteFile, 90, 100);
        Assert.Equal("FavoriteEntryLimit", MainWindow.StoreWarningSettingField([entries]));
        Assert.Equal("FavoriteFileLimitMiB", MainWindow.StoreWarningSettingField([file, entries]));
        // The fullest warning is the one the status bar shows, so it is the one that is opened.
        Assert.Equal("FavoriteEntryLimit", MainWindow.StoreWarningSettingField([entries, file]));
        Assert.Equal("FavoriteEntryLimit", MainWindow.StoreWarningSettingField([]));
    }

    [Fact]
    public void BothSettingsTheWarningOpensExistOnTheSettingsPageUnderThatName() => BooruComponentDispatcher.Run(() =>
    {
        SettingsEditor editor = new();
        try
        {
            Assert.True(editor.HasSetting("FavoriteEntryLimit"));
            Assert.True(editor.HasSetting("FavoriteFileLimitMiB"));
        }
        finally { (editor as IDisposable)?.Dispose(); }
    });

    [Theory]
    [InlineData("priority.rowRestore")]
    [InlineData("plugins.copyPluginId")]
    [InlineData("plugins.pluginIdCopied")]
    public void TheNewTextsAreTranslatedInEveryLanguage(string key)
    {
        string english = Texts.T(key);
        Assert.NotEqual(key, english);
        foreach (string code in new[] { "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(code);
            string text = Texts.T(key);
            Assert.NotEqual(key, text);
            Assert.NotEqual(english, text);
        }
    }
}
