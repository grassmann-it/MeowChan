using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

/// <summary>
/// "Media index cache limit (MiB)" is a setting like the other storage limits: it has the former fixed value as its
/// default, a minimum and a maximum that match the cache's own range, refuses values outside it, survives copies and a
/// save/reload, has a labelled row with the range in its hint, and says in the user's language what it stopped.
/// The other limits of the page are covered by StoreLimitSettingsTests; this one is not in that table because its
/// constants are named after the cache.
/// </summary>
[Collection("Localization")]
public sealed class MediaIndexCacheLimitSettingTests
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };

    [Fact]
    public void TheDefaultIsTheFormerFixedValueAndTheRangeIsTheCachesOwn()
    {
        Assert.Equal(256, new Config().MediaIndexCacheLimitMiB);
        Assert.Equal(MediaLibraryIndexCache.DefaultLimitBytes, (long)new Config().MediaIndexCacheLimitMiB * 1024 * 1024);
        Assert.Equal(16, Config.MediaIndexCacheMinimumLimitMiB);
        Assert.Equal(1024, Config.MediaIndexCacheMaximumLimitMiB);
        // The maximum is the technical ceiling, so a saved index can never be larger than a limit the user may set.
        Assert.Equal(MediaLibraryIndexCache.MaximumBytes, (long)Config.MediaIndexCacheMaximumLimitMiB * 1024 * 1024);
        Assert.Equal(MediaLibraryIndexCache.MinimumLimitBytes, (long)Config.MediaIndexCacheMinimumLimitMiB * 1024 * 1024);
    }

    [Fact]
    public void TheMinimumAndMaximumPassAndEveryValueOutsideThemIsRefused()
    {
        foreach (int valid in new[] { Config.MediaIndexCacheMinimumLimitMiB, 256, Config.MediaIndexCacheMaximumLimitMiB })
            new Config { MediaIndexCacheLimitMiB = valid }.Validate();
        foreach (int invalid in new[] { Config.MediaIndexCacheMinimumLimitMiB - 1, Config.MediaIndexCacheMaximumLimitMiB + 1, 0, -1, int.MinValue, int.MaxValue })
        {
            Config candidate = new() { MediaIndexCacheLimitMiB = invalid };
            ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
            Assert.Equal(nameof(Config.MediaIndexCacheLimitMiB), refused.ParamName);
            Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(candidate));
        }
    }

    [Fact]
    public void TheValueSurvivesCloneCopiesAndSaveReload()
    {
        using TempDirectory directory = new();
        Config original = new() { MediaIndexCacheLimitMiB = Config.MediaIndexCacheMaximumLimitMiB };
        Config live = new();
        live.CopySettingsFieldsFrom(original.Clone());
        Config copy = new();
        copy.CopyFrom(live);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config config in new[] { original.Clone(), live, copy, loaded })
            Assert.Equal(Config.MediaIndexCacheMaximumLimitMiB, config.MediaIndexCacheLimitMiB);
    }

    [Fact]
    public void TheLabelAndTheHintNameTheRangeInEveryBuiltInLanguage()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                string label = Texts.T("budgets.mediaIndexCache"), hint = Texts.T("budgets.mediaIndexCacheHint");
                Assert.NotEqual("budgets.mediaIndexCache", label);
                Assert.NotEqual("budgets.mediaIndexCacheHint", hint);
                Assert.Contains(Config.MediaIndexCacheMinimumLimitMiB.ToString(), hint);
                Assert.Contains(Config.MediaIndexCacheMaximumLimitMiB.ToString(), hint);
            }
            Texts.SetLanguage("de");
            string german = Texts.T("budgets.mediaIndexCache") + " " + Texts.T("budgets.mediaIndexCacheHint");
            Assert.DoesNotContain("Sie ", german);
            Assert.DoesNotContain("Beitrag", german);
            Assert.DoesNotContain(" …", german);
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void TheNearAndOverLimitSentencesNameTheSettingAndThePageInEveryLanguage()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                string setting = Texts.T("budgets.mediaIndexCache"), page = Texts.T("settings.storageLimits");
                string near = Texts.T("media.cache.nearLimit", "200 MiB", "256 MiB", setting, page);
                string over = Texts.T("media.cache.overLimit", "256 MiB", setting, page);
                foreach (string text in new[] { near, over })
                {
                    Assert.Contains(setting, text);
                    Assert.Contains(page, text);
                    Assert.Contains("256 MiB", text);
                    Assert.DoesNotContain("{", text);
                    Assert.DoesNotContain("media.cache", text);
                }
                Assert.Contains("200 MiB", near);
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void TheSettingsRowOffersTheWholeRangeAndNothingBeyondIt()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement field = Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == "MediaIndexCacheMiBInput");
        Assert.Equal(Config.MediaIndexCacheMinimumLimitMiB.ToString(), (string?)field.Attribute("Minimum"));
        Assert.Equal(Config.MediaIndexCacheMaximumLimitMiB.ToString(), (string?)field.Attribute("Maximum"));
        Assert.Equal("{Binding MediaIndexCacheMiB, Mode=TwoWay}", (string?)field.Attribute("Value"));
        Assert.Equal("{Binding ElementName=MediaIndexCacheMiBFieldLabel}", (string?)field.Attribute("AutomationProperties.LabeledBy"));
        // The row sits on the storage page, not behind the advanced expander.
        Assert.Contains(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsPage");
        Assert.DoesNotContain(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsHeading");
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
