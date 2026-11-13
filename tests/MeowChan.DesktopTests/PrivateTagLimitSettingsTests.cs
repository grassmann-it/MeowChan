using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

/// <summary>
/// The own-tag count limit (default 1024, 1024–20000) and the private-tags.json write limit (default 32 MiB, 8–128 MiB)
/// are validated settings applied to the profile's tag stores at start and after a settings change, shown on the
/// advanced limits page with an explanation, and a refused write names the setting that raises it.
/// </summary>
[Collection("Localization")]
public sealed class PrivateTagLimitSettingsTests
{
    [Fact]
    public void TheLimitsHaveTheirDefaultsSurviveCopiesAndSaveReloadAndAreBounded()
    {
        using TempDirectory directory = new();
        Assert.Equal((1024, 32), (new Config().PrivateTagLimit, new Config().PrivateTagFileLimitMiB));
        Assert.Equal((1024, 20_000, 8, 128), (Config.PrivateTagMinimumLimit, Config.PrivateTagMaximumLimit,
            Config.PrivateTagFileMinimumLimitMiB, Config.PrivateTagFileMaximumLimitMiB));
        Config original = new() { PrivateTagLimit = 5000, PrivateTagFileLimitMiB = 64 };
        Config live = new();
        live.CopySettingsFieldsFrom(original.Clone());
        Assert.Equal((5000, 64), (live.PrivateTagLimit, live.PrivateTagFileLimitMiB));
        Config copy = new();
        copy.CopyFrom(live);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal((5000, 64), (loaded.PrivateTagLimit, loaded.PrivateTagFileLimitMiB));

        foreach (int invalid in new[] { 0, 1023, 20_001, int.MaxValue })
        {
            Config candidate = live.Clone();
            candidate.PrivateTagLimit = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => live.CopySettingsFieldsFrom(candidate));
        }
        foreach (int invalid in new[] { 0, 7, 129, int.MaxValue })
        {
            Config candidate = live.Clone();
            candidate.PrivateTagFileLimitMiB = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => live.CopySettingsFieldsFrom(candidate));
        }
        Assert.Equal((5000, 64), (live.PrivateTagLimit, live.PrivateTagFileLimitMiB));
    }

    [Fact]
    public async Task TheHostAppliesTheConfiguredLimitsAtStartAndAfterSettingsChange()
    {
        using TempDirectory directory = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, PrivateTagLimit = 2000, PrivateTagFileLimitMiB = 16,
            DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal((2000, 16L * 1024 * 1024), host.PrivateTagLimitValues);
        Config draft = host.Config.Clone();
        draft.PrivateTagLimit = 20_000;
        draft.PrivateTagFileLimitMiB = 128;
        await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal((20_000, 128L * 1024 * 1024), host.PrivateTagLimitValues);
        Config reloaded = Config.LoadConfig(directory.Path);
        Assert.Equal((20_000, 128), (reloaded.PrivateTagLimit, reloaded.PrivateTagFileLimitMiB));
    }

    [Fact]
    public async Task ALimitBelowTheCurrentCountKeepsTagsReadableAndRefusesANewTagWithTheSettingMessage()
    {
        using TempDirectory directory = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, PrivateTagLimit = 1030,
            DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        // 1025 tags, as a profile that had a higher limit would have them.
        File.WriteAllText(Path.Combine(directory.Path, PrivateTagStore.FileName), "{\"Version\":1,\"Revision\":1,\"Tags\":["
            + string.Join(",", Enumerable.Range(0, 1025).Select(index => "{\"Id\":\"" + new Guid(index + 1, 0, 0, new byte[8]).ToString("D")
                + "\",\"Name\":\"tag " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"}"))
            + "],\"Threads\":[],\"Media\":[]}");
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True((await host.DefinePrivateTagAsync("allowed while the limit is higher")).Changed);
        Config draft = host.Config.Clone();
        draft.PrivateTagLimit = 1024;
        await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));

        PrivateTagLoadSnapshot snapshot = host.GetPrivateTagSnapshot();
        Assert.Equal(PrivateTagLoadState.Ready, snapshot.State);
        Assert.Equal(1026, snapshot.Snapshot!.Tags.Count);
        byte[] before = File.ReadAllBytes(Path.Combine(directory.Path, PrivateTagStore.FileName));
        PrivateTagStoreException refused = await Assert.ThrowsAsync<PrivateTagStoreException>(() => host.DefinePrivateTagAsync("one more"));
        Assert.Equal(PrivateTagStoreError.TagLimitReached, refused.Error);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(directory.Path, PrivateTagStore.FileName)));
        Assert.Equal("tags.error.tagLimit", MediaTagSession.MessageFor(refused.Error));
        Assert.Equal("tags.error.fileLimit", MediaTagSession.MessageFor(PrivateTagStoreError.DocumentLimitReached));
        // An existing name is still answered at the limit.
        Assert.False((await host.DefinePrivateTagAsync("TAG 7")).Changed);
    }

    [Fact]
    public void TheExplanationsAndRefusalsNameTheSettingInEveryBuiltInLanguage()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
            {
                Texts.SetLanguage(language);
                string category = Texts.T("settings.storageLimits");
                string settings = Texts.T("settings.title");
                foreach ((string message, string setting) in new[] { ("tags.error.tagLimit", "budgets.privateTags"),
                    ("tags.error.fileLimit", "budgets.privateTagFile") })
                {
                    string text = Texts.T(message);
                    Assert.Contains(Texts.T(setting), text);
                    Assert.Contains(category, text);
                    Assert.Contains(settings, text);
                    Assert.DoesNotContain(message, text);
                }
                Assert.Contains("1024", Texts.T("budgets.privateTagsHint"));
                Assert.Contains("20000", Texts.T("budgets.privateTagsHint"));
                Assert.Contains("128", Texts.T("budgets.privateTagFileHint"));
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void SettingsMarkupHasBothBoundedFieldsBesideTheCatalogLimitWithAVisibleExplanation()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach ((string name, string binding, int minimum, int maximum) in new[]
        {
            ("PrivateTagLimit", "PrivateTagLimit", Config.PrivateTagMinimumLimit, Config.PrivateTagMaximumLimit),
            ("PrivateTagFileMiB", "PrivateTagFileMiB", Config.PrivateTagFileMinimumLimitMiB, Config.PrivateTagFileMaximumLimitMiB)
        })
        {
            XElement field = Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == name + "Input");
            Assert.Contains(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsPage");
            Assert.DoesNotContain(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsHeading");
            Assert.Equal("{Binding " + binding + ", Mode=TwoWay}", (string?)field.Attribute("Value"));
            Assert.Equal(minimum.ToString(), (string?)field.Attribute("Minimum"));
            Assert.Equal(maximum.ToString(), (string?)field.Attribute("Maximum"));
            Assert.Contains(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == name + "InputError");
            Assert.Contains(markup.Descendants(), e => (string?)e.Attribute("Text") == "{Binding " + name + "Hint}");
        }
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
