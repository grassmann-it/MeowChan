using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// The settings search: the list follows the typed text after a pause on a fake clock, typing and the arrow keys never jump,
/// Enter does, and the groups a jump opened fold again with the end of the search unless a value in them was changed.
/// </summary>
[Collection("Localization")]
public sealed class SettingsSearchSessionTests : IDisposable
{
    public SettingsSearchSessionTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private sealed class ManualTime : TimeProvider
    {
        private long _now;
        private readonly List<ManualTimer> _timers = [];
        public override long GetTimestamp() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            _now += duration.Ticks;
            foreach (ManualTimer timer in _timers.Where(t => t.Due is long due && due <= _now).ToArray()) { timer.Due = null; timer.Deliver(); }
        }
        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            internal long? Due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime.Ticks; return true; }
            internal void Deliver() => callback(state);
            public void Dispose() => Due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static SettingsSearchDocument Doc(string id, string label, string hint, string group, string? key) =>
        new(id, label, hint, group, key, SettingsSearchAliases.For(key, "settings.downloads"), []);

    private static readonly SettingsSearchIndex Index = SettingsSearchMatcher.Build(new[]
    {
        Doc("download", "Download folder", "Where new files are saved.", "Downloads", "DownloadPath"),
        Doc("parallel", "Parallel downloads", "How many files load at once.", "Downloads", "ParallelDownloads"),
        Doc("bandwidth", "Bandwidth limit (MiB/s)", "Zero does not mean unlimited.", "Downloads", "Bandwidth"),
        Doc("language", "Language", "The language of the interface.", "Interface", "Language"),
    });

    private sealed class Harness
    {
        internal readonly List<SettingsSearchHit> Jumps = [];
        internal readonly SettingsSearchSession Session;
        internal int IndexReads;
        internal Harness() => Session = new(() => { IndexReads++; return Index; }, Jumps.Add);
    }

    [Fact]
    public void TheQueryDelayIsBetween120And150Milliseconds()
    {
        Assert.InRange(SettingsSearchSession.QueryDelay.TotalMilliseconds, 120, 150);
        SearchBox box = new();
        SettingsSearchSession.Configure(box);
        Assert.Equal(SettingsSearchSession.QueryDelay, box.Debounce);
    }

    [Fact]
    public void FastTypingComputesTheListOnceAfterThePauseOfTheLastKeystroke()
    {
        ManualTime clock = new();
        Harness harness = new();
        SearchBox box = new() { Clock = clock, Post = action => action() };
        SettingsSearchSession.Configure(box);
        box.QueryChanged += (_, _) => harness.Session.Search(box.Text);

        foreach (string typed in new[] { "d", "do", "dow", "down" })
        {
            box.Text = typed;
            clock.Advance(TimeSpan.FromMilliseconds(60)); // faster than the pause
        }
        Assert.Equal(0, harness.Session.Searches);
        Assert.Empty(harness.Session.Hits);

        clock.Advance(TimeSpan.FromMilliseconds(69)); // 129 ms after the last key
        Assert.Equal(0, harness.Session.Searches);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, harness.Session.Searches);
        Assert.NotEmpty(harness.Session.Hits);
        Assert.Empty(harness.Jumps);
    }

    [Fact]
    public void FlushingTheFieldBeforeEnterAppliesThePendingQueryAtOnce()
    {
        ManualTime clock = new();
        Harness harness = new();
        SearchBox box = new() { Clock = clock, Post = action => action() };
        SettingsSearchSession.Configure(box);
        box.QueryChanged += (_, _) => harness.Session.Search(box.Text);
        box.Text = "bandwith";
        Assert.Empty(harness.Session.Hits);
        box.FlushQuery();
        Assert.Equal("bandwidth", harness.Session.Hits[0].Document.Id);
        Assert.True(harness.Session.Commit());
        Assert.Equal("bandwidth", Assert.Single(harness.Jumps).Document.Id);
    }

    [Fact]
    public void TypingListsAndHighlightsTheFirstHitWithoutJumping()
    {
        Harness harness = new();
        foreach (string query in new[] { "d", "do", "down", "downl" })
        {
            harness.Session.Search(query);
            Assert.Equal(harness.Session.Hits.Count > 0 ? 0 : -1, harness.Session.Highlight);
        }
        Assert.NotEmpty(harness.Session.Hits);
        Assert.Empty(harness.Jumps);
    }

    [Fact]
    public void ArrowKeysMoveTheHighlightOnlyAndEnterJumpsToTheHighlightedHit()
    {
        Harness harness = new();
        harness.Session.Search("download");
        int count = harness.Session.Hits.Count;
        Assert.True(count >= 2);

        Assert.True(harness.Session.Move(1));
        Assert.Equal(1, harness.Session.Highlight);
        Assert.True(harness.Session.Move(-1));
        Assert.Equal(0, harness.Session.Highlight);
        Assert.False(harness.Session.Move(-1));    // clamped at the first hit
        Assert.Empty(harness.Jumps);

        harness.Session.Select(count - 1);
        Assert.False(harness.Session.Move(10));    // clamped at the last hit
        Assert.Equal(count - 1, harness.Session.Highlight);
        Assert.Empty(harness.Jumps);

        Assert.True(harness.Session.Commit());
        Assert.Equal(harness.Session.Hits[count - 1].Document.Id, Assert.Single(harness.Jumps).Document.Id);
    }

    [Fact]
    public void ACommitWithoutHitsDoesNothingAndAClickCommitsThatHit()
    {
        Harness harness = new();
        harness.Session.Search("zzzzqq");
        Assert.Empty(harness.Session.Hits);
        Assert.False(harness.Session.Commit());
        Assert.False(harness.Session.Move(1));
        Assert.Empty(harness.Jumps);

        harness.Session.Search("language");
        harness.Session.Commit(harness.Session.Hits[0]);
        Assert.Equal("language", Assert.Single(harness.Jumps).Document.Id);
    }

    [Fact]
    public void ABlankQueryEmptiesTheListWithoutReadingTheIndex()
    {
        Harness harness = new();
        harness.Session.Search("download");
        int reads = harness.IndexReads;
        harness.Session.Search("   ");
        Assert.Empty(harness.Session.Hits);
        Assert.Equal(-1, harness.Session.Highlight);
        Assert.Equal(reads, harness.IndexReads);
        Assert.Empty(harness.Jumps);
    }

    private static (Expander Outer, Expander Inner) Groups() => (new Expander { Header = "More settings" }, new Expander { Header = "Limits" });

    [Fact]
    public void GroupsOpenedByAJumpCloseWhenTheSearchEnds()
    {
        (Expander outer, Expander inner) = Groups();
        SettingsSearchGroups groups = new();
        groups.Open([outer, inner]);
        Assert.True(outer.IsExpanded && inner.IsExpanded);
        Assert.Equal(2, groups.OpenedCount);

        Assert.Equal(2, groups.Release());
        Assert.False(outer.IsExpanded);
        Assert.False(inner.IsExpanded);
        Assert.Equal(0, groups.OpenedCount);
    }

    [Fact]
    public void AGroupTheUserHadOpenedStaysOpen()
    {
        (Expander outer, Expander inner) = Groups();
        outer.IsExpanded = true;
        SettingsSearchGroups groups = new();
        groups.Open([outer, inner]);
        Assert.Equal(1, groups.OpenedCount);
        groups.Release();
        Assert.True(outer.IsExpanded);
        Assert.False(inner.IsExpanded);
    }

    [Fact]
    public void AGroupWithAChangedValueStaysOpenWhileItsSiblingsInTheChainClose()
    {
        (Expander outer, Expander inner) = Groups();
        SettingsSearchGroups groups = new();
        groups.Open([outer, inner]);
        groups.NoteEdited([inner]); // the user edited a value in the inner group
        Assert.False(groups.IsOpenedBySearch(inner));
        groups.Release();
        Assert.True(inner.IsExpanded);
        Assert.False(outer.IsExpanded);
    }

    [Fact]
    public void ANewJumpClosesGroupsOfTheEarlierOneItDoesNotNeedAndKeepsSharedOnes()
    {
        Expander shared = new(), first = new(), second = new();
        SettingsSearchGroups groups = new();
        groups.Open([shared, first]);
        groups.Open([shared, second]);
        Assert.True(shared.IsExpanded);
        Assert.False(first.IsExpanded);
        Assert.True(second.IsExpanded);
        groups.Release();
        Assert.False(shared.IsExpanded);
        Assert.False(second.IsExpanded);
    }

    [Fact]
    public void ReleasingNeverTouchesAGroupTheUserClosedHimself()
    {
        Expander group = new();
        SettingsSearchGroups groups = new();
        groups.Open([group]);
        group.IsExpanded = false;
        Assert.Equal(0, groups.Release());
        Assert.False(group.IsExpanded);
    }

    [Fact]
    public void AGroupTheUserClosesAndReopensAfterAJumpStaysOpen()
    {
        Expander group = new();
        SettingsSearchGroups groups = new();
        groups.Open([group]);
        group.IsExpanded = false;
        group.IsExpanded = true;
        groups.Open([group]);
        Assert.Equal(0, groups.OpenedCount);
        Assert.Equal(0, groups.Release());
        Assert.True(group.IsExpanded);
    }

    [Fact]
    public void RepeatedJumpsKeepOneLedgerEntryAndReleaseDetachesOwnership()
    {
        Expander group = new();
        SettingsSearchGroups groups = new();
        for (int visit = 0; visit < 3; visit++)
        {
            for (int jump = 0; jump < 5; jump++) groups.Open([group]);
            Assert.Equal(1, groups.OpenedCount);
            Assert.Equal(1, groups.Release());
            Assert.Equal(0, groups.OpenedCount);
            Assert.False(group.IsExpanded);
        }
    }

    [Fact]
    public void EditingWithoutAnySearchOpenedGroupIsANoOp()
    {
        Expander group = new() { IsExpanded = true };
        SettingsSearchGroups groups = new();
        groups.NoteEdited([group]);
        Assert.Equal(0, groups.Release());
        Assert.True(group.IsExpanded);
    }
}
