using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.Shared;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruLocalTagSuggestionTests(ITestOutputHelper output)
{
    private static readonly BooruLocalTagSuggestions.Scope Danbooru = new("danbooru", "default", null, null);
    private static readonly BooruLocalTagSuggestions.Scope E621 = new("e621", "default", null, null);
    private static readonly BooruLocalTagSuggestions.Scope OtherInstance = E621 with { InstanceId = "other" };
    private static readonly DateTimeOffset ObservedAt = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SuggestionsFollowScopeContentAndCatalogChanges()
    {
        BooruPostRecord[] posts =
        [
            Post(Danbooru, 101, Tag("cat", BooruPostClaimTagCategory.Character), Tag("car")),
            Post(Danbooru, 102, Tag("cat", BooruPostClaimTagCategory.Character)),
            Post(Danbooru, 103, Tag("calf"), Tag("rating:g"), Tag("name_(series)")),
            Post(E621, 101, Tag("canine")), Post(OtherInstance, 101, Tag("cardinal"))
        ];
        BooruLocalTagSuggestions suggestions = new();
        BooruCatalogSnapshot catalog = Snapshot(posts);
        var danbooru = await suggestions.FindAsync(() => catalog, Danbooru, "CA", 12);
        Assert.Equal(["cat", "calf", "car"], danbooru.Select(tag => tag.Name));
        Assert.Equal(new BooruLocalTagSuggestions.Suggestion("cat", BooruPostClaimTagCategory.Character, 2), danbooru[0]);
        Assert.Equal("canine", Assert.Single(await suggestions.FindAsync(() => catalog, E621, "ca", 12)).Name);
        Assert.Equal("cardinal", Assert.Single(await suggestions.FindAsync(() => catalog, OtherInstance, "ca", 12)).Name);
        Assert.Empty(await suggestions.FindAsync(() => catalog, Danbooru, "name", 12));

        BooruContentFilter filter = new(1, new(BooruPostClaimRating.General, "cat", []));
        Assert.Equal("calf", Assert.Single(await suggestions.FindAsync(() => catalog, Danbooru with { Filter = filter }, "ca", 12)).Name);
        catalog = Snapshot([Post(Danbooru, 104, Tag("camel"))]);
        Assert.Equal("camel", Assert.Single(await suggestions.FindAsync(() => catalog, Danbooru, "ca", 12)).Name);
        Assert.Empty(await suggestions.FindAsync(() => null, Danbooru, "ca", 12));
    }

    [Fact]
    public async Task CountHasAConfigRoundTripAndZeroSkipsTheCatalogRead()
    {
        using TempDirectory profile = new();
        Config settings = new() { DownloadPath = Path.Combine(profile.Path, "media") };
        Assert.Equal(12, settings.BooruLocalTagSuggestionCount);
        settings.BooruLocalTagSuggestionCount = 0;
        settings.SaveConfig(profile.Path);
        settings = Config.LoadConfig(profile.Path);
        Assert.Equal(0, settings.Clone().BooruLocalTagSuggestionCount);
        Config copy = new();
        copy.CopySettingsFieldsFrom(settings);
        Assert.Equal(0, copy.BooruLocalTagSuggestionCount);
        Assert.Empty(await new BooruLocalTagSuggestions().FindAsync(() => throw new InvalidOperationException(),
            Danbooru, "tag", settings.BooruLocalTagSuggestionCount));
        var index = BooruLocalTagSuggestions.Build([Post(Danbooru, 101,
            Enumerable.Range(0, 60).Select(i => Tag($"tag_{i:D2}")).ToArray())], Danbooru);
        Assert.Equal(50, BooruLocalTagSuggestions.Match(index, "tag", int.MaxValue).Length);
    }

    [Fact]
    public void KeyboardCompletesOnlyTheLastTermAndEnterWithoutSelectionUsesFreeText() => BooruComponentDispatcher.Run(async () =>
    {
        Texts.SetLanguage("en");
        BooruUiHarness harness = new();
        BooruCatalogSnapshot snapshot = Snapshot([Post(Danbooru, 101, Tag("calf"), Tag("cat")), Post(Danbooru, 102, Tag("cat"))]);
        using SearchFixture fixture = new(new BooruWorkbenchModel(harness.Services), () => snapshot);
        fixture.Type("base  -ca");
        await fixture.WaitForTag("cat");
        Assert.Equal("From your catalog", AutomationProperties.GetName(fixture.Popup!));
        fixture.Press(PhysicalKey.ArrowDown);
        fixture.Press(PhysicalKey.ArrowDown);
        fixture.Press(PhysicalKey.ArrowUp);
        fixture.Press(PhysicalKey.Tab);
        Assert.Equal("base  -cat", fixture.Input.Text);
        Assert.Null(fixture.Popup);
        Assert.Equal(0, harness.ProviderRequests);

        fixture.Type("ca");
        await fixture.WaitForTag("cat");
        fixture.Press(PhysicalKey.Escape);
        Assert.Equal("ca", fixture.Input.Text);
        Assert.Null(fixture.Popup);
        fixture.Press(PhysicalKey.Escape);
        Assert.False(fixture.Input.HasText);

        fixture.Type("ca");
        await fixture.WaitForTag("cat");
        fixture.Press(PhysicalKey.ArrowDown);
        fixture.Press(PhysicalKey.Enter);
        Assert.Equal("cat", fixture.Input.Text);
        Assert.Equal(0, harness.ProviderRequests);

        fixture.Type("ca");
        await fixture.WaitForTag("cat");
        fixture.Press(PhysicalKey.Enter);
        Assert.Equal(("ca", 1), Assert.Single(harness.Search.Searches));
        Assert.Null(fixture.Popup);

        fixture.View.Model.Search.SetMode(BooruInputMode.Post);
        fixture.Type("ca2");
        Assert.Null(fixture.Popup);
        Assert.Equal(1, harness.ProviderRequests);
    });

    [Fact]
    public void PendingReadsDoNotPublishSuggestionsFromThePreviousSourceOrInstance() => BooruComponentDispatcher.Run(async () =>
    {
        BooruUiHarness harness = new();
        BooruWorkbenchServices E621Services(string instance) => new(
            new FakeBooruSearchPort { ProviderId = "e621", ProviderName = instance, InstanceId = instance },
            harness.Queue, harness.Watchers, harness.Previews);
        BooruWorkbenchServices e621 = E621Services(E621.InstanceId);
        BooruWorkbenchServices other = E621Services(OtherInstance.InstanceId);
        BooruCatalogSnapshot snapshot = Snapshot(
            [Post(Danbooru, 101, Tag("cat")), Post(E621, 101, Tag("canine")), Post(OtherInstance, 101, Tag("cardinal"))]);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken cancellation = BooruComponentDispatcher.Cancellation;
        using SearchFixture fixture = new(new BooruWorkbenchModel(harness.Services, [harness.Services, e621, other]), () =>
        {
            started.TrySetResult();
            release.Task.WaitAsync(cancellation).GetAwaiter().GetResult();
            return snapshot;
        });
        try
        {
            fixture.Type("ca");
            await started.Task.WaitAsync(cancellation);
            fixture.View.SearchTabs.SelectSource(E621.SourceId, E621.InstanceId);
            fixture.Type("can");
            Assert.Null(fixture.Popup);
        }
        finally { release.TrySetResult(); }

        await fixture.WaitForTag("canine");
        Assert.Null(fixture.Row("cat"));
        Assert.Null(fixture.Row("cardinal"));
        fixture.View.SearchTabs.SelectSource(OtherInstance.SourceId, OtherInstance.InstanceId);
        fixture.Type("car");
        await fixture.WaitForTag("cardinal");
        Assert.Null(fixture.Row("canine"));
        Assert.Equal(0, harness.ProviderRequests);
        Assert.Equal(0, ((FakeBooruSearchPort)e621.Search).Requests);
        Assert.Equal(0, ((FakeBooruSearchPort)other.Search).Requests);
    });

    [Fact]
    public void LocalCompletionUsesTheRealCatalogAndMakesNoTransportRequests() => BooruComponentDispatcher.Run(async () =>
    {
        using TempDirectory profile = new();
        using DanbooruStub transport = new();
        using DanbooruTestBinding binding = new(transport);
        await using DesktopSessionHost host = await BooruCompositionHostTests.StartAsync(profile.Path, transport, () => binding.Binding, paused: true);
        BooruSessionComposition composition = Assert.IsType<BooruSessionComposition>(host.BooruComposition);
        composition.Catalog.UpsertPost(Post(Danbooru, 101, Tag("cloud")).Post, ObservedAt);
        await host.DefinePrivateTagAsync("cloud_private");
        using SearchFixture fixture = new(new BooruWorkbenchModel(composition.Services), host.ReadBooruCatalog);
        fixture.Type("cl");
        await fixture.WaitForTag("cloud");
        Assert.Null(fixture.Row("cloud_private"));
        fixture.Press(PhysicalKey.Tab);
        Assert.Equal("cloud", fixture.Input.Text);
        Assert.Empty(transport.Requests);
        Assert.Equal(0, binding.Worker.SearchCalls);
    });

    [Fact]
    public void FixtureLComparesTheSameIndexWithAndWithoutDispatcherWork() => BooruComponentDispatcher.Run(async () =>
    {
        BooruPostClaimTag[] tags = Enumerable.Range(0, 46).Select(i => Tag($"tag_{i:D2}")).ToArray();
        BooruCatalogSnapshot catalog = Snapshot(Enumerable.Range(1, 5_000).Select(i => Post(Danbooru, i, tags)).ToArray());
        BooruLocalTagSuggestions.Suggestion[] direct = BooruLocalTagSuggestions.Build(catalog.Posts, Danbooru);
        Assert.Equal(46, direct.Length);
        Assert.All(direct, tag => Assert.Equal(5_000, tag.Posts));
        // Warm the code path once, then pair cold index builds over the same in-memory catalog.
        Stopwatch timer = Stopwatch.StartNew();
        var expected = BooruLocalTagSuggestions.Match(BooruLocalTagSuggestions.Build(catalog.Posts, Danbooru), "tag", 12);
        TimeSpan inline = timer.Elapsed;

        bool readOnUiThread = true;
        timer.Restart();
        Task<BooruLocalTagSuggestions.Suggestion[]> pending = new BooruLocalTagSuggestions().FindAsync(() =>
        {
            readOnUiThread = Dispatcher.UIThread.CheckAccess();
            return catalog;
        }, Danbooru, "tag", 12);
        TimeSpan dispatch = timer.Elapsed;
        var actual = await pending;
        TimeSpan background = timer.Elapsed;
        Assert.Equal(expected, actual);
        Assert.False(readOnUiThread);
        output.WriteLine($"L fixture, 5,000 posts and 46 distinct tags. Inline {inline.TotalMilliseconds:F2} ms, "
            + $"worker round-trip {background.TotalMilliseconds:F2} ms, dispatcher scheduling {dispatch.TotalMilliseconds:F2} ms.");
    });

    private static BooruPostClaimTag Tag(string name, BooruPostClaimTagCategory category = BooruPostClaimTagCategory.General) => new(name, category);

    private static BooruPostRecord Post(BooruLocalTagSuggestions.Scope scope, int id, params BooruPostClaimTag[] tags)
    {
        string postId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string? instanceId = scope.InstanceId == BooruPostKey.DefaultInstance ? null : scope.InstanceId;
        return new(scope.SourceId, postId, new BooruPostClaim(scope.SourceId, postId,
            BooruPostClaimPresence.Tags | BooruPostClaimPresence.Rating, Rating: BooruPostClaimRating.General,
            Tags: tags, InstanceId: instanceId), ObservedAt, ObservedAt, InstanceId: instanceId);
    }

    private static BooruCatalogSnapshot Snapshot(BooruPostRecord[] posts) => new(BooruCatalogSnapshot.ScopedIdentitySchemaVersion, posts, []);

    private sealed class SearchFixture : IDisposable
    {
        private readonly Window _window;
        internal BooruWorkbenchView View { get; }
        internal SearchBox Input { get; }
        internal Control? Popup => ById<Border>(BooruWorkbenchView.LocalTagsName);

        internal SearchFixture(BooruWorkbenchModel model, Func<BooruCatalogSnapshot?> read)
        {
            View = new BooruWorkbenchView(model, ownsModel: true, readLocalTagCatalog: read);
            _window = new() { Content = View, RequestedThemeVariant = ThemeVariant.Dark };
            _window.Show();
            _window.Activate();
            _window.PlatformImpl!.Resize(new Size(1100, 700), WindowResizeReason.User);
            Settle();
            Input = Assert.IsType<SearchBox>(ById<SearchBox>(BooruWorkbenchView.InputName));
        }

        internal void Type(string text)
        {
            Assert.True(Input.Focus());
            Input.Text = text;
            Input.SelectionStart = Input.SelectionEnd = Input.CaretIndex = text.Length;
            Settle();
        }

        internal void Press(PhysicalKey key)
        {
            _window.KeyPressQwerty(key, RawInputModifiers.None);
            _window.KeyReleaseQwerty(key, RawInputModifiers.None);
            Settle();
        }

        internal Task WaitForTag(string tag) => BooruTestSupport.WaitAsync(() => Row(tag) is not null, "local suggestion " + tag);
        internal Button? Row(string tag) => ById<Button>(BooruWorkbenchView.LocalTagPrefix + tag);

        private T? ById<T>(string id) where T : Control => _window.GetVisualDescendants().OfType<T>().FirstOrDefault(control =>
            control.IsEffectivelyVisible && AutomationProperties.GetAutomationId(control) == id);

        private void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            _window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            _window.Close();
            View.Dispose();
            Settle();
        }
    }
}
