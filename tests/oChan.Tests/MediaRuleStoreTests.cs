using oChan.Persistence;
using oChan.Rules;

namespace oChan.Tests;

public sealed class MediaRuleStoreTests
{
    [Fact]
    public async Task MissingStoreRequiresLoadThenProvidesUnrestrictedDefaultsWithoutWriting()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new MediaRuleStore(directory.Path, lease);

        Assert.Throws<InvalidOperationException>(() => store.Current);
        MediaRuleLoadResult loaded = await store.LoadAsync();

        Assert.False(loaded.Recovered);
        Assert.Null(loaded.Warning);
        Assert.Equal("default", loaded.RuleSet.Revision);
        Assert.Equal(MediaSelectionOutcome.Selected, store.Evaluate(new ResourceId("4chan", "g", "123"), null, null).Outcome);
        Assert.False(File.Exists(StorePath(directory)));
    }

    [Fact]
    public async Task RoundTripKeepsDisabledAndExplicitEmptyUnlimitedInheritDistinct()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new MediaRuleStore(directory.Path, lease);
        await store.LoadAsync();
        MediaRule[] rules = new[]
        {
            new MediaRule(MediaRuleScope.Global, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { "JPG", "PNG" }),
                sizeLimit: new MediaSizeLimit(MediaSizeLimitMode.Limited, 100)),
            new MediaRule(MediaRuleScope.ForSource("4chan"), enabled: false, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected)),
            new MediaRule(MediaRuleScope.ForBoard("4chan", "g"), sizeLimit: MediaSizeLimit.Unlimited),
            new MediaRule(MediaRuleScope.ForThread("4chan", "g", "123"), types: MediaTypeSelection.All)
        };

        MediaRuleSet saved = await store.SaveAsync(rules, store.Current.Revision);
        MediaRuleStore restarted = new MediaRuleStore(directory.Path, lease);
        MediaRuleLoadResult restored = await restarted.LoadAsync();

        Assert.Equal(saved.Revision, restored.RuleSet.Revision);
        Assert.Equal(4, restored.RuleSet.Rules.Count);
        Assert.False(restored.RuleSet.Rules[1].Enabled);
        Assert.Equal(MediaTypeSelectionMode.Selected, restored.RuleSet.Rules[1].Types.Mode);
        Assert.Empty(restored.RuleSet.Rules[1].Types.Extensions);
        Assert.Equal(MediaTypeSelectionMode.Inherit, restored.RuleSet.Rules[2].Types.Mode);
        Assert.Equal(MediaSizeLimitMode.Unlimited, restored.RuleSet.Rules[2].SizeLimit.Mode);
        Assert.Equal(MediaSelectionOutcome.Selected, restarted.Evaluate(new ResourceId("4chan", "g", "123"), "webm", 1000).Outcome);
        Assert.Equal(MediaSelectionReason.TypeExcluded, restarted.Evaluate(new ResourceId("4chan", "g", "456"), "webm", 1).Reason);
    }

    [Fact]
    public async Task SaveCapturesInputAndNewRevisionOnlyBecomesCurrentAfterCommit()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new MediaRuleStore(directory.Path, lease);
        await store.LoadAsync();
        List<MediaRule> input = new List<MediaRule> { ExcludeAll() };

        Task<MediaRuleSet> save = store.SaveAsync(input);
        input.Clear();
        MediaRuleSet saved = await save;

        Assert.Single(saved.Rules);
        Assert.Same(saved, store.Current);
        Assert.Equal(saved.Revision, (await new MediaRuleStore(directory.Path, lease).LoadAsync()).RuleSet.Revision);
        string durable = File.ReadAllText(StorePath(directory));
        Directory.CreateDirectory(StorePath(directory) + ".bak");
        Exception? failure = await Record.ExceptionAsync(() => store.SaveAsync(Array.Empty<MediaRule>()));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Same(saved, store.Current);
        Assert.Equal(durable, File.ReadAllText(StorePath(directory)));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task CancelledAndStaleEditsDoNotActivateOrOverwrite()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new MediaRuleStore(directory.Path, lease);
        await store.LoadAsync();
        string stale = store.Current.Revision;
        MediaRuleSet saved = await store.SaveAsync(new[] { ExcludeAll() }, stale);
        string durable = File.ReadAllText(StorePath(directory));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Array.Empty<MediaRule>(), stale));
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Array.Empty<MediaRule>(), cancellationToken: cancellation.Token));

        Assert.Same(saved, store.Current);
        Assert.Equal(durable, File.ReadAllText(StorePath(directory)));
    }

    [Fact]
    public async Task SeparateInstancesCannotOverwriteANewerDurableRevision()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore first = new MediaRuleStore(directory.Path, lease);
        MediaRuleStore second = new MediaRuleStore(directory.Path, lease);
        await first.LoadAsync();
        await second.LoadAsync();
        MediaRuleSet saved = await first.SaveAsync(new[] { ExcludeAll() });

        await Assert.ThrowsAsync<InvalidOperationException>(() => second.SaveAsync(Array.Empty<MediaRule>()));
        Assert.Equal(saved.Revision, (await second.LoadAsync()).RuleSet.Revision);
    }

    [Fact]
    public async Task ConcurrentDraftsHaveOneWinnerAndPreviouslyResolvedValuesStayImmutable()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new MediaRuleStore(directory.Path, lease);
        await store.LoadAsync();
        MediaSelectionResult before = store.Evaluate(new ResourceId("4chan", "g", "123"), "jpg", 100);
        string draftRevision = store.Current.Revision;

        Task<Exception?> first = Record.ExceptionAsync(() => store.SaveAsync(new[] { ExcludeAll() }, draftRevision));
        Task<Exception?> second = Record.ExceptionAsync(() => store.SaveAsync(Array.Empty<MediaRule>(), draftRevision));
        Exception?[] results = await Task.WhenAll(first, second);

        Assert.Single(results, failure => failure == null);
        Assert.IsType<InvalidOperationException>(Assert.Single(results, failure => failure != null));
        Assert.Equal(MediaSelectionOutcome.Selected, before.Outcome);
        Assert.Equal("default", before.EffectiveRule.Revision);
        Assert.Equal(store.Current.Revision, (await new MediaRuleStore(directory.Path, lease).LoadAsync()).RuleSet.Revision);
    }

    [Fact]
    public async Task DamagedPrimaryRecoversBackupWithWarningAndPreservesItOnSave()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new MediaRuleStore(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet first = await store.SaveAsync(new[] { ExcludeAll() });
        MediaRuleSet second = await store.SaveAsync(Array.Empty<MediaRule>());
        string backup = File.ReadAllText(StorePath(directory) + ".bak");
        File.WriteAllText(StorePath(directory), "damaged");

        MediaRuleStore restarted = new MediaRuleStore(directory.Path, lease);
        MediaRuleLoadResult recovered = await restarted.LoadAsync();

        Assert.True(recovered.Recovered);
        Assert.NotNull(recovered.Warning);
        Assert.Equal(first.Revision, recovered.RuleSet.Revision);
        Assert.Equal("damaged", File.ReadAllText(StorePath(directory)));
        MediaRuleSet repaired = await restarted.SaveAsync(Array.Empty<MediaRule>());
        Assert.NotEqual(first.Revision, repaired.Revision);
        Assert.NotEqual(second.Revision, repaired.Revision);
        Assert.Equal(backup, File.ReadAllText(StorePath(directory) + ".bak"));
        string preserved = Assert.Single(Directory.GetFiles(directory.Path, "media_rules.json.corrupt-*"));
        Assert.Equal("damaged", File.ReadAllText(preserved));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":\"one\",\"Revision\":\"x\",\"Rules\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Revision\":\"x\",\"Rules\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"Revision\":\"x\",\"Rules\":[null]}")]
    public async Task IrrecoverableDamageBlocksActivationAndPreservesOriginal(string json)
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        File.WriteAllText(StorePath(directory), json);
        MediaRuleStore store = new MediaRuleStore(directory.Path, lease);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        Assert.Throws<InvalidOperationException>(() => store.Current);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Array.Empty<MediaRule>()));
        Assert.Equal(json, File.ReadAllText(StorePath(directory)));
    }

    [Fact]
    public async Task UnsupportedPrimaryNeverFallsBackToOlderBackupOrOverwritesIt()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        MediaRuleStore store = new MediaRuleStore(directory.Path, lease);
        await store.LoadAsync();
        MediaRuleSet previous = await store.SaveAsync(new[] { ExcludeAll() });
        await store.SaveAsync(Array.Empty<MediaRule>());
        const string future = "{\"SchemaVersion\":2,\"NewShape\":true}";
        File.WriteAllText(StorePath(directory), future);

        await Assert.ThrowsAsync<NotSupportedException>(() => new MediaRuleStore(directory.Path, lease).LoadAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => store.SaveAsync(new[] { ExcludeAll() }));
        Assert.Equal(future, File.ReadAllText(StorePath(directory)));
        Assert.Contains(previous.Revision, File.ReadAllText(StorePath(directory) + ".bak"));
    }

    private static MediaRule ExcludeAll() => new MediaRule(MediaRuleScope.Global,
        types: new MediaTypeSelection(MediaTypeSelectionMode.Selected));
    private static string StorePath(TempDirectory directory) => Path.Combine(directory.Path, "media_rules.json");
}
