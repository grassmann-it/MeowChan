using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

/// <summary>
/// Two limits had a minimum equal to their default and could only be raised: the favorite/rating file size (8 MiB) and
/// the own-tag count (1024). Both can now go lower, the field, the validation and the hint agree, and a lower limit
/// still deletes nothing: existing data stays readable, only growth is refused.
/// </summary>
[Collection("Localization")]
public sealed class MinimumBelowDefaultSettingsTests
{
    [Fact]
    public void TheMinimaAreBelowTheDefaultsEverywhereTheLimitIsDeclared()
    {
        Assert.Equal((8, 1, 128), (Config.FavoriteFileLimitMiBDefault, Config.FavoriteFileLimitMiBMinimum, Config.FavoriteFileLimitMiBMaximum));
        Assert.Equal((1024, 256, 20_000), (new Config().PrivateTagLimit, Config.PrivateTagMinimumLimit, Config.PrivateTagMaximumLimit));
        Assert.Equal(1024 * 1024, ProfileStoreLimits.MinimumFavoriteFileBytes);
        Assert.Equal(256, PrivateTagLimits.MinimumTagLimit);
        Config config = new() { FavoriteFileLimitMiB = 1, PrivateTagLimit = 256 };
        config.Validate();
        new Config().CopySettingsFieldsFrom(config);
        foreach ((int file, int tags) in new[] { (0, 256), (1, 255) })
        {
            Config rejected = new() { FavoriteFileLimitMiB = file, PrivateTagLimit = tags };
            Assert.Throws<ArgumentOutOfRangeException>(() => rejected.Validate());
        }
        ProfileStoreLimits store = new() { FavoriteFileBytes = ProfileStoreLimits.MinimumFavoriteFileBytes };
        Assert.Equal(1024 * 1024, store.FavoriteFileBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.FavoriteFileBytes = ProfileStoreLimits.MinimumFavoriteFileBytes - 1);
        PrivateTagLimits tagLimits = new() { TagLimit = PrivateTagLimits.MinimumTagLimit };
        Assert.Equal(256, tagLimits.TagLimit);
        Assert.Throws<ArgumentOutOfRangeException>(() => tagLimits.TagLimit = PrivateTagLimits.MinimumTagLimit - 1);
    }

    [Fact]
    public void ATagLimitBelowTheExistingCountKeepsEveryTagReadableAndOnlyRefusesNewOnes()
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, PrivateTagStore.FileName), "{\"Version\":1,\"Revision\":1,\"Tags\":["
            + string.Join(",", Enumerable.Range(0, 300).Select(index => "{\"Id\":\"" + new Guid(index + 1, 0, 0, new byte[8]).ToString("D")
                + "\",\"Name\":\"tag " + index.ToString(CultureInfo.InvariantCulture) + "\"}"))
            + "],\"Threads\":[],\"Media\":[]}");
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PrivateTagStore store = new(directory.Path, lease, new PrivateTagLimits { TagLimit = 256 });
        Assert.Equal(300, store.ReadSnapshot().Tags.Count);
        PrivateTagStoreException refused = Assert.Throws<PrivateTagStoreException>(() => store.Define("one more"));
        Assert.Equal(PrivateTagStoreError.TagLimitReached, refused.Error);
        Assert.Equal(300, store.ReadSnapshot().Tags.Count);
        // Renaming does not grow the vocabulary and stays allowed.
        Assert.True(store.Rename(store.ReadSnapshot().Tags[0].Id, "renamed").Changed);
    }

    [Fact]
    public void TheHintsNameTheNewMinimumInEveryLanguageAndTheMarkupUsesIt()
    {
        Texts.Initialize(); // With the bundled packs, whatever an earlier test of the collection left loaded.
        string previous = Texts.Language;
        try
        {
            foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
            {
                Texts.SetLanguage(language);
                Assert.Matches(@"\b1\b.*\b128\b", Texts.T("budgets.favoriteFileHint"));
                Assert.Matches(@"\b256\b.*\b20000\b", Texts.T("budgets.privateTagsHint"));
                Assert.DoesNotContain("1024 ", Texts.T("budgets.privateTagsHint"));
            }
        }
        finally { Texts.SetLanguage(previous); }
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Field(string name) => Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == name);
        Assert.Equal("256", (string?)Field("PrivateTagLimitInput").Attribute("Minimum"));
        Assert.Equal("1", (string?)Field("FavoriteFileLimitMiBInput").Attribute("Minimum"));
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
