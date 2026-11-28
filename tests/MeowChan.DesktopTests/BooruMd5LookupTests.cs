using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruMd5LookupTests
{
    private const string Base64 = "ASNFZ4mrze8BI0VniavN7w==";
    private const string Query = "md5:0123456789abcdef0123456789abcdef";
    private const string SecondHash = "EAAAAAAAAAAAAAAAAAAAAA==";
    private const string SecondQuery = "md5:10000000000000000000000000000000";

    [Fact]
    public void AttachmentMenuOnlySubmitsAnActiveSupportedSourceAndUsesTheExistingTabLimit()
    {
        BooruComponentDispatcher.Run(async () =>
        {
            FakeBooruSearchPort search = new();
            BooruWorkbenchServices danbooru = Services(search);
            BooruWorkbenchServices unsupported = Services(new FakeBooruSearchPort { ProviderId = "e926", ProviderName = "e926" });
            IReadOnlyList<BooruWorkbenchServices> active = [danbooru, unsupported];
            using BooruPage page = new(danbooru);
            page.View.SearchTabsLimit = () => 2;
            BooruMd5Lookup lookup = new(() => active, async (source, query) =>
            {
                if (page.View.TryShowQueryInNewTab(query, source.Search)) await page.View.SubmitSearchAsync();
            });
            ContextMenu Menu()
            {
                CapturedAttachment attachment = new(0, "media", "original", null, ExpectedMd5: Base64);
                ContextMenuBuilder builder = new();
                MediaSourceMenu.AddTo(builder, new(null, null, MediaSourceMenu.Md5(null, attachment)), () => true,
                    _ => Task.CompletedTask, lookup: lookup);
                return Assert.IsType<ContextMenu>(builder.Build());
            }

            Assert.Equal(Query, BooruMd5Lookup.Query(Base64));
            ContextMenu menu = Menu();
            MenuItem parent = FindItem(menu, MediaSourceMenu.Md5Id);
            MenuItem action = Assert.IsType<MenuItem>(Assert.Single(parent.Items));
            Assert.Equal($"{MediaSourceMenu.Md5Id}.danbooru.{search.InstanceId}", AutomationProperties.GetAutomationId(action));
            Assert.Null(action.InputGesture);
            Assert.Empty(search.Searches);

            active = [unsupported];
            Assert.DoesNotContain(Menu().Items.OfType<MenuItem>(), item => AutomationProperties.GetAutomationId(item) == MediaSourceMenu.Md5Id);
            action.Command!.Execute(null);
            Assert.Empty(search.Searches);

            active = [danbooru, unsupported];
            action.Command.Execute(null);
            await BooruTestSupport.WaitAsync(() => search.Requests == 1, "the explicit MD5 request");
            Assert.Equal((Query, 1), Assert.Single(search.Searches));
            Assert.Equal(Query, page.Model.Search.View.Input);
            Assert.False(page.Model.Search.CanCreateWatcher);

            Assert.True(page.View.TryShowQueryInNewTab("cat"));
            action.Command.Execute(null);
            TextBlock notice = Assert.Single(page.View.Header.GetVisualDescendants().OfType<TextBlock>(),
                block => AutomationProperties.GetAutomationId(block) == BooruWorkbenchView.TabLimitNoticeName);
            Assert.Equal(Texts.T("booru.tabs.limit", 2), notice.Text);
            Assert.Single(search.Searches);
        });
    }

    [Fact]
    public void TheMd5IssueIsLocalizedAndTheQueryIsNeverSavedAsOriginalProvenance()
    {
        BooruParsedInput invalid = BooruInput.Parse(BooruInputMode.Tags, Query + " cat");
        Assert.Equal(BooruInputIssue.Md5, invalid.Issue);
        Assert.Contains("32", BooruText.Issue(invalid.Issue), StringComparison.Ordinal);
        Assert.Null(BooruOriginalQueuePort.Provenance(BooruFindingKind.Search, Query));
        Assert.Equal("cat", BooruOriginalQueuePort.Provenance(BooruFindingKind.Search, "cat"));
    }

    [Theory]
    [InlineData("md5:0123456789abcdef0123456789abcde")]
    [InlineData(Query + "0")]
    [InlineData("md5:0123456789abcdef0123456789abcdeg")]
    [InlineData(Query + " cat")]
    public void BadMd5InputIsRejectedAtTheHostQueryBoundary(string query)
    {
        Assert.Equal(BooruInputKind.Invalid, BooruInput.Parse(BooruInputMode.Tags, query).Kind);
        Assert.Equal(Query + " rating:g", BooruInput.Parse(BooruInputMode.Tags, Query + " rating:g").Canonical);
        Assert.Equal("cat -dog rating:g", BooruInput.Parse(BooruInputMode.Tags, "cat -dog rating:g").Canonical);
    }

    [Fact]
    public void UppercaseMd5InputIsAcceptedAndCanonicalizedToLowercase()
    {
        BooruParsedInput parsed = BooruInput.Parse(BooruInputMode.Tags, "md5:0123456789ABCDEF0123456789abcdeF");
        Assert.NotEqual(BooruInputKind.Invalid, parsed.Kind);
        Assert.Equal(Query, parsed.Canonical);
    }

    [Fact]
    public void ProfileOptInDefaultsOffAndSurvivesTheNormalConfigRoundTrip()
    {
        Config defaults = Assert.IsType<Config>(JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config));
        Assert.False(defaults.BooruMd5BatchEnabled);
        defaults.BooruMd5BatchEnabled = true;
        string json = JsonSerializer.Serialize(defaults.Clone(), ConfigJsonContext.Default.Config);
        Config loaded = Assert.IsType<Config>(JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config));
        Config published = new();
        published.CopyFrom(loaded);
        Assert.True(published.BooruMd5BatchEnabled);
    }

    [Fact]
    public async Task BatchOptInAndCooldownGateEveryRequestAndDuplicateFilesReuseTheMatch()
    {
        BooruManualClock clock = new();
        FakeBooruSearchPort search = new();
        bool enabled = false;
        BooruContentFilter content = new(1, new(BooruPostClaimRating.General, "", []));
        BooruMd5Batch batch = new([Entry("first"), Entry("duplicate"), Entry("second"), Entry("unreadable")],
            (entry, _) => Task.FromResult(entry.Name switch { "second" => SecondHash, "unreadable" => null, _ => Base64 }),
            search, () => enabled, () => content, clock, 20);
        List<BooruMd5Match> matches = [];
        CancellationToken token = CancellationToken.None;
        Assert.Equal(BooruCallStatus.Unavailable, await batch.RunAsync(matches.AddRange, token));
        Assert.Empty(search.Searches);

        enabled = true;
        DateTimeOffset retryAt = clock.GetUtcNow().AddMinutes(1);
        search.OnSearch = (query, _, _) => Task.FromResult(query == Query
            ? BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, 1, true,
                WithHash("3", Base64), WithHash("2", Base64, BooruRating.Explicit), WithHash("1", SecondHash)))
            : BooruCallResult<BooruResultPage>.Of(BooruCallStatus.RateLimited, retryAt: retryAt));
        Assert.Equal(BooruCallStatus.RateLimited, await batch.RunAsync(matches.AddRange, token));
        Assert.Equal(2, batch.Completed);
        Assert.Equal(new[] { "first", "duplicate" }, matches.Select(match => match.Name));
        Assert.All(matches, match => Assert.Equal("3", match.Post.PostId));
        Assert.Equal(BooruCallStatus.Cooldown, await batch.RunAsync(matches.AddRange, token));
        Assert.Equal(new[] { (Query, 1), (SecondQuery, 1) }, search.Searches);

        clock.Advance(TimeSpan.FromMinutes(1));
        search.OnSearch = null;
        Assert.Equal(BooruCallStatus.Ok, await batch.RunAsync(matches.AddRange, token));
        Assert.Equal(new[] { (Query, 1), (SecondQuery, 1), (SecondQuery, 1) }, search.Searches);
        Assert.Equal(4, batch.Completed);
        Assert.Equal(1, batch.MissingHash);
    }

    [Fact]
    public async Task CancellationKeepsTheInterruptedFilePendingForResume()
    {
        CancellationToken token = CancellationToken.None;
        using CancellationTokenSource cancel = new();
        FakeBooruSearchPort search = new();
        BooruContentFilter filter = new(1, new(BooruPostClaimRating.General, "", []));
        BooruMd5Batch batch = new([Entry("first"), Entry("second")],
            (entry, _) => Task.FromResult<string?>(entry.Name == "first" ? Base64 : SecondHash),
            search, () => true, () => filter, new BooruManualClock(), 20);
        search.OnSearch = (query, _, current) =>
        {
            if (query == SecondQuery) cancel.Cancel();
            current.ThrowIfCancellationRequested();
            return Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, 1, true)));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.RunAsync(_ => { }, cancel.Token));
        Assert.Equal(1, batch.Completed);
        search.OnSearch = null;
        Assert.Equal(BooruCallStatus.Ok, await batch.RunAsync(_ => { }, token));
        Assert.Equal(2, batch.Completed);
        Assert.Equal(new[] { (Query, 1), (SecondQuery, 1), (SecondQuery, 1) }, search.Searches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenBatchHidesBlockedMatchesWhenItReadsRulesOrTheUserSelects(bool selectAfterTightening) =>
        BooruComponentDispatcher.Run(async () =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            using ProfileLease lease = ProfileLease.Acquire(profile.Path);
            string path = Path.Combine(profile.Path, BooruContentProfileStore.FileName);
            const string initial = """{"schemaVersion":1,"revision":1,"settings":{"maximumRating":"explicit","globalBlacklist":"","sourceBlacklists":[]}}""";
            await File.WriteAllTextAsync(path, initial);
            BooruWorkspaceContent content = BooruWorkspaceContent.ForProfile(lease);
            FakeBooruSearchPort search = new();
            search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true, WithHash("1", Base64, BooruRating.Explicit), WithHash("2", Base64))));
            TaskCompletionSource<string?> secondHash = new(TaskCreationOptions.RunContinuationsAsynchronously);
            BooruMd5Batch batch = new(selectAfterTightening ? [Entry("first")] : [Entry("first"), Entry("second")],
                (entry, token) => entry.Name == "first" ? Task.FromResult<string?>(Base64) : secondHash.Task.WaitAsync(token),
                search, () => true, () => content.ReadEffective() is { HasChoice: true } snapshot ? snapshot.Filter : null,
                new BooruManualClock(), 20);
            BooruMd5BatchWindow dialog = new(batch, content, true, _ => Task.CompletedTask, CancellationToken.None);
            dialog.Show();
            try
            {
                ListBox results = Find<ListBox>("matches");
                TextBox details = Find<TextBox>("details");
                Button run = Find<Button>("run");
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await BooruTestSupport.WaitAsync(() => results.Items.Count == 2, "the first two MD5 matches");
                results.SelectedIndex = 0;
                Assert.Contains("/posts/1", details.Text, StringComparison.Ordinal);

                if (selectAfterTightening)
                {
                    results.SelectedIndex = -1;
                    await File.WriteAllTextAsync(path, initial.Replace("\"explicit\"", "\"general\"", StringComparison.Ordinal));
                    results.SelectedIndex = 0;
                    Assert.Equal("2", Assert.IsType<BooruMd5Match>(Assert.Single(results.Items)).Post.PostId);
                    Assert.True(string.IsNullOrEmpty(details.Text));
                    results.SelectedIndex = 0;
                    Assert.Contains("/posts/2", details.Text, StringComparison.Ordinal);
                }
                else
                {
                    await File.WriteAllTextAsync(path, "{");
                    secondHash.SetResult(Base64);
                    await BooruTestSupport.WaitAsync(() => content.Snapshot.State == BooruContentProfileState.Unreadable,
                        "the batch to observe the read failure");
                    Assert.Empty(results.Items);
                    Assert.True(string.IsNullOrEmpty(details.Text));
                    Assert.Equal(1, batch.Completed);
                    Assert.Single(search.Searches);
                }
            }
            finally { dialog.Close(); }

            T Find<T>(string id) where T : Control => Assert.Single(dialog.GetVisualDescendants().OfType<T>(),
                control => AutomationProperties.GetAutomationId(control) == "booru.md5.batch." + id);
        });

    private static MenuItem FindItem(ItemsControl menu, string id) => Assert.Single(menu.Items.OfType<MenuItem>(),
        item => AutomationProperties.GetAutomationId(item) == id);

    private static BooruWorkbenchServices Services(IBooruSearchPort search) =>
        new(search, new FakeBooruQueuePort(), new FakeBooruWatcherPort(new BooruManualClock()), new FakeBooruPreviewPort());

    private static BooruPostView WithHash(string id, string hash, BooruRating rating = BooruRating.General)
    {
        BooruPost post = BooruUiFixtures.Post(id, rating: rating, preview: false).Post;
        MeowChan.PluginSdk.BooruMediaVariant original = Assert.Single(post.Media!);
        return BooruPostView.From(post with { Media = [original with { Extension = ".png", ExpectedMd5 = hash }] });
    }

    private static MediaLibraryEntry Entry(string name) => new(new ResourceId("test", "board", "1"), "thread", 1,
        DateTimeOffset.UnixEpoch, null, null, null, null, null, null, null, name, null, 1, name, "original", name,
        SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
}
