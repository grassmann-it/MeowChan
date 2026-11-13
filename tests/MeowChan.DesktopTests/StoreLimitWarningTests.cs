using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

/// <summary>
/// The favorite, thread favorite and rating lists warn from 75 percent of their limits (status bar), and a full list
/// answers the toggle with a sentence in the user's language that names the setting instead of the store's English text.
/// </summary>
[Collection("Localization")]
public sealed class StoreLimitWarningTests
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };

    [Fact]
    public void TheWarningStartsAtSeventyFivePercentOfTheEntryLimitOrTheFileLimit()
    {
        Assert.Empty(StoreLimitUsage.Evaluate(new long[] { 7_499, 100 }, 10_000, new long[] { 6L * 1024 * 1024 - 1 }, 8L * 1024 * 1024));
        Assert.Empty(StoreLimitUsage.Evaluate(Array.Empty<long>(), 10_000, Array.Empty<long>(), 8L * 1024 * 1024));
        StoreLimitUsage.Item list = Assert.Single(StoreLimitUsage.Evaluate(new long[] { 7_500, 100 }, 10_000, new long[] { 1 }, 8L * 1024 * 1024));
        Assert.Equal((PrivateStoreLimitKind.FavoriteEntries, 75), (list.Kind, list.Percent));
        StoreLimitUsage.Item file = Assert.Single(StoreLimitUsage.Evaluate(new long[] { 1 }, 10_000, new long[] { 6L * 1024 * 1024 }, 8L * 1024 * 1024));
        Assert.Equal((PrivateStoreLimitKind.FavoriteFile, 75), (file.Kind, file.Percent));
        // Above the limit (a lowered setting keeps old data) the warning stays and the fuller one comes first.
        IReadOnlyList<StoreLimitUsage.Item> both = StoreLimitUsage.Evaluate(new long[] { 12_000 }, 10_000, new long[] { 7L * 1024 * 1024 }, 8L * 1024 * 1024);
        Assert.Equal(new[] { PrivateStoreLimitKind.FavoriteEntries, PrivateStoreLimitKind.FavoriteFile }, both.Select(item => item.Kind));
        Assert.Equal(120, both[0].Percent);
        Assert.Null(StoreLimitUsage.Present(Array.Empty<StoreLimitUsage.Item>()));
    }

    [Fact]
    public void TheWarningNamesTheSettingAndThePageInEveryBuiltInLanguage()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                string page = Texts.T("settings.storageLimits");
                foreach ((PrivateStoreLimitKind kind, string setting) in new[]
                    { (PrivateStoreLimitKind.FavoriteEntries, "budgets.favoriteEntries"), (PrivateStoreLimitKind.FavoriteFile, "budgets.favoriteFile") })
                {
                    string text = StoreLimitUsage.Describe(new StoreLimitUsage.Item(kind, 8_000, 10_000));
                    Assert.Contains(Texts.T(setting), text);
                    Assert.Contains(page, text);
                    Assert.Contains("80", text);
                    Assert.DoesNotContain("limit.warn", text);
                    Assert.DoesNotContain("{", text);
                }
            }
            Texts.SetLanguage("de");
            string german = StoreLimitUsage.Describe(new StoreLimitUsage.Item(PrivateStoreLimitKind.FavoriteEntries, 8_000, 10_000));
            Assert.DoesNotContain("Sie ", german);
            Assert.DoesNotContain("Beitrag", german);
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public async Task TheHostReportsAFullListAndAnswersTheToggleWithTheLocalizedSettingSentence()
    {
        string previous = Texts.Language;
        using TempDirectory directory = new();
        try
        {
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, FavoriteEntryLimit = 10_000, Language = "de",
                DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
            // 12,000 saved thread favorites against a limit of 10,000: everything stays readable, only growth is refused.
            File.WriteAllBytes(Path.Combine(directory.Path, "thread-favorites.json"), JsonSerializer.SerializeToUtf8Bytes(
                new { Version = 1, Revision = 0L, Threads = Enumerable.Range(0, 12_000).Select(index => new ResourceId("s", "b", index.ToString())).ToArray() }));
            await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Texts.SetLanguage("de");
            Assert.Equal(12_000, host.GetThreadFavorites().Count);

            StoreLimitUsage.Item near = Assert.Single(host.ReadStoreLimitUsage(), item => item.Kind == PrivateStoreLimitKind.FavoriteEntries);
            Assert.Equal((12_000L, 10_000L), (near.Used, near.Limit));

            InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(
                () => host.SetThreadFavoriteAsync(new ResourceId("s", "b", "99999"), true));
            Assert.Contains(Texts.T("budgets.favoriteEntries"), refused.Message);
            Assert.Contains("Speicher und Grenzen", refused.Message);
            Assert.DoesNotContain("The favorites index", refused.Message);
            // Removing still works, and raising the setting lifts the refusal.
            await host.SetThreadFavoriteAsync(new ResourceId("s", "b", "5"), false);
            Config draft = host.Config.Clone();
            draft.FavoriteEntryLimit = 50_000;
            await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
            await host.SetThreadFavoriteAsync(new ResourceId("s", "b", "99999"), true);
            Assert.Empty(host.ReadStoreLimitUsage());
        }
        finally { Texts.SetLanguage(previous); }
    }
}
