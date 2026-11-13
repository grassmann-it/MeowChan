using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

/// <summary>
/// S2: the Danbooru catalog write limit is a validated setting (default 32 MiB, 8–128 MiB) applied to the profile's
/// store, and the watcher area warns from 75 % of the configured limit on, naming the setting that raises it.
/// </summary>
[Collection("Localization")]
public sealed class BooruCatalogLimitTests
{
    [Fact]
    public void TheLimitDefaultsTo32MiBSurvivesCopiesAndSaveReloadAndIsBounded()
    {
        using TempDirectory directory = new();
        Assert.Equal(32, new Config().BooruCatalogLimitMiB);
        Config original = new() { BooruCatalogLimitMiB = 64 };
        Config live = new();
        live.CopySettingsFieldsFrom(original.Clone());
        Assert.Equal(64, live.BooruCatalogLimitMiB);
        Config copy = new();
        copy.CopyFrom(live);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(64, loaded.BooruCatalogLimitMiB);

        foreach (int invalid in new[] { 0, 7, 129, int.MaxValue })
        {
            Config candidate = live.Clone();
            candidate.BooruCatalogLimitMiB = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
            Assert.Throws<ArgumentOutOfRangeException>(() => live.CopySettingsFieldsFrom(candidate));
            Assert.Equal(64, live.BooruCatalogLimitMiB);
        }
    }

    [Fact]
    public async Task TheHostAppliesTheConfiguredLimitAtStartAndAfterSettingsChange()
    {
        using TempDirectory directory = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, BooruCatalogLimitMiB = 16,
            DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(16L * 1024 * 1024, host.BooruCatalogLimitBytes);
        Config draft = host.Config.Clone();
        draft.BooruCatalogLimitMiB = 96;
        await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(96L * 1024 * 1024, host.BooruCatalogLimitBytes);
        Assert.Equal(96, Config.LoadConfig(directory.Path).BooruCatalogLimitMiB);
    }

    [Fact]
    public async Task TheProductionPortReportsSizeCountAndLimitAndTheModelWarnsOnlyNearTheLimit()
    {
        using TempDirectory profile = new();
        using MeowChan.Persistence.ProfileLease lease = MeowChan.Persistence.ProfileLease.Acquire(profile.Path);
        BooruCatalogStore catalog = new(profile.Path, lease);
        lease.Admission.CompleteStartup();
        catalog.UpsertPost(new BooruPostClaim("danbooru", "5", BooruPostClaimPresence.Tags), DateTimeOffset.UtcNow);
        DanbooruWatcherScheduler scheduler = new(catalog, new NoSearch(), new NoAdmission());
        scheduler.Load();
        using BooruSchedulerWatcherPort port = new(scheduler, catalog);
        BooruCatalogUsageView usage = Assert.IsType<BooruCatalogUsageView>(await port.GetCatalogUsageAsync(CancellationToken.None));
        Assert.Equal((new FileInfo(catalog.FilePath).Length, 1, (long)BooruCatalogStore.DefaultDocumentLimitBytes),
            (usage.DocumentBytes, usage.PostCount, usage.LimitBytes));
        Assert.False(usage.NearLimit);
        using BooruWatcherListModel model = new(port, TimeProvider.System);
        await model.RefreshAsync();
        Assert.Equal(usage, model.View.Storage);

        UsagePort near = new(new BooruCatalogUsageView(24L * 1024 * 1024, 900, 32L * 1024 * 1024));
        using BooruWatcherListModel warned = new(near, TimeProvider.System);
        await warned.RefreshAsync();
        Assert.True(warned.View.Storage!.NearLimit);
        Assert.Equal(75, warned.View.Storage.Percent);
        near.Usage = new BooruCatalogUsageView(23L * 1024 * 1024, 900, 32L * 1024 * 1024);
        await warned.RefreshAsync();
        Assert.False(warned.View.Storage!.NearLimit);
        // A failing usage read only hides the warning; the list itself stays usable.
        near.Fail = true;
        await warned.RefreshAsync();
        Assert.Null(warned.View.Storage);
        Assert.Equal(BooruWatcherListStatus.Empty, warned.View.Status);
    }

    [Fact]
    public async Task AboveALoweredLimitPauseResumeRemoveAndCancelStillWorkWhileGrowthStaysBlocked()
    {
        using TempDirectory profile = new();
        using MeowChan.Persistence.ProfileLease lease = MeowChan.Persistence.ProfileLease.Acquire(profile.Path);
        BooruCatalogStore catalog = new(profile.Path, lease);
        lease.Admission.CompleteStartup();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        catalog.UpsertPost(new BooruPostClaim("danbooru", "5", BooruPostClaimPresence.Tags), now);
        catalog.SetOriginalIntent("danbooru", "5", BooruOriginalIntent.Requested);
        await using DanbooruWatcherScheduler scheduler = new(catalog, new NoSearch(), new NoAdmission());
        scheduler.Load();
        string kept = Assert.IsType<DanbooruWatcherStatus>(scheduler.AddWatcher("cat").Watcher).WatcherId;
        string removed = Assert.IsType<DanbooruWatcherStatus>(scheduler.AddWatcher("dog").Watcher).WatcherId;

        // About 9 MiB in one post: written under the default limit, then the limit drops below the document.
        catalog.UpsertPost(new BooruPostClaim("danbooru", "95", BooruPostClaimPresence.Tags)
        {
            Tags = Enumerable.Range(0, 600).Select(index => new BooruPostClaimTag(index.ToString("D4") + new string('t', 15_000),
                BooruPostClaimTagCategory.General)).ToArray()
        }, now);
        catalog.DocumentLimitBytes = BooruCatalogStore.MinimumDocumentLimitBytes;
        Assert.True(new FileInfo(catalog.FilePath).Length > catalog.DocumentLimitBytes);

        // Growth is still refused ...
        InvalidDataException full = Assert.Throws<InvalidDataException>(() =>
            catalog.UpsertPost(new BooruPostClaim("danbooru", "96", BooruPostClaimPresence.Tags), now));
        Assert.Equal(BooruCatalogStore.FullMessage, full.Message);
        Assert.False(scheduler.AddWatcher("bird").Accepted);

        // ... but the user keeps control over watchers and downloads.
        AssertAccepted(scheduler.Pause(kept));
        Assert.Equal(DanbooruWatcherState.Paused, scheduler.GetStatus(kept)!.State);
        AssertAccepted(scheduler.Resume(kept));
        AssertAccepted(scheduler.Remove(removed));
        Assert.Null(scheduler.GetStatus(removed));
        Assert.Equal(BooruOriginalIntent.Cancelled,
            catalog.SetOriginalIntent("danbooru", "5", BooruOriginalIntent.Cancelled).OriginalIntent);
        Assert.Equal("Removed", Assert.Single(catalog.Read().Watchers, w => w.WatcherId == removed).State);
    }

    [Fact]
    public void TheWarningNamesTheSettingInEveryBuiltInLanguage()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
            {
                Texts.SetLanguage(language);
                string category = Texts.T("settings.storageLimits");
                string setting = Texts.T("budgets.booruCatalog");
                string text = Texts.T("booru.ui.watchers.catalogSize", "24 MiB", "32 MiB", 75, 900, category, setting);
                Assert.Contains(setting, text);
                Assert.Contains(category, text);
                Assert.Contains("75", text);
                Assert.DoesNotContain("booru.ui.watchers.catalogSize", text);
                Assert.False(string.IsNullOrWhiteSpace(Texts.T("budgets.booruCatalogHint")));
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void SettingsMarkupHasTheBoundedLimitFieldBesideTheFreeSpaceReserveWithAVisibleExplanation()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement field = Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == "BooruCatalogMiBInput");
        Assert.Contains(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsPage");
        Assert.DoesNotContain(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsHeading");
        Assert.Equal("{Binding BooruCatalogMiB, Mode=TwoWay}", (string?)field.Attribute("Value"));
        Assert.Equal(Config.BooruCatalogMinimumLimitMiB.ToString(), (string?)field.Attribute("Minimum"));
        Assert.Equal(Config.BooruCatalogMaximumLimitMiB.ToString(), (string?)field.Attribute("Maximum"));
        Assert.Contains(markup.Descendants(), e => (string?)e.Attribute("Text") == "{Binding BooruCatalogMiBHint}");
    }

    private static void AssertAccepted(DanbooruWatcherCommandResult result) => Assert.True(result.Accepted, result.Code);

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));

    private sealed class UsagePort(BooruCatalogUsageView usage) : IBooruWatcherPort
    {
        internal BooruCatalogUsageView Usage { get; set; } = usage;
        internal bool Fail { get; set; }
        public event Action? Changed { add { } remove { } }
        public Task<IReadOnlyList<BooruWatcherView>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BooruWatcherView>>(Array.Empty<BooruWatcherView>());
        public Task<BooruCatalogUsageView?> GetCatalogUsageAsync(CancellationToken cancellationToken) => Fail
            ? Task.FromException<BooruCatalogUsageView?>(new InvalidDataException("unreadable"))
            : Task.FromResult<BooruCatalogUsageView?>(Usage);
        public Task<BooruWatcherCommandResult> CreateAsync(string canonicalQuery, CancellationToken cancellationToken) => Rejected();
        public Task<BooruWatcherCommandResult> CheckNowAsync(string watcherId, CancellationToken cancellationToken) => Rejected();
        public Task<BooruWatcherCommandResult> PauseAsync(string watcherId, CancellationToken cancellationToken) => Rejected();
        public Task<BooruWatcherCommandResult> ResumeAsync(string watcherId, CancellationToken cancellationToken) => Rejected();
        public Task<BooruWatcherCommandResult> RemoveAsync(string watcherId, CancellationToken cancellationToken) => Rejected();
        private static Task<BooruWatcherCommandResult> Rejected() =>
            Task.FromResult(new BooruWatcherCommandResult(BooruWatcherCommandStatus.Rejected, "rejected"));
    }

    private sealed class NoSearch : IDanbooruWatcherSearchPort
    {
        public int MaximumPage => 5;
        public int PageSize => 20;
        public DateTimeOffset? GetProviderCooldownUntilUtc() => null;
        public IDanbooruWatcherSearchSession OpenCheck() => throw new InvalidOperationException("No check may run.");
    }

    private sealed class NoAdmission : IDanbooruWatcherAdmissionPort
    {
        public Task<DanbooruWatcherAdmissionResult> AdmitAsync(BooruPostClaim post, string watcherId, string query,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No admission may run.");
    }
}
