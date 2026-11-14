using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

// Component coverage for the private exact-entry focus outcome. This deliberately uses a constructed
// MediaPage without a window, profile, provider, asset or native focus surface.
[Collection("Booru exact navigation")]
public sealed class BooruExactFocusOutcomeTests
{
    private const BindingFlags InstanceNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void CompleteSnapshotProvesAnAbsentExactIdentityIsMissing() => OnDispatcher(() =>
    {
        MediaLibraryEntry target = Entry("701"), other = Entry("702");
        using var fixture = NewFixture(new MediaLibrarySnapshot([other], [], [], complete: true));

        Assert.Equal(MediaPage.ExactEntryFocusResult.Missing,
            Focus(fixture.Page, entry => ReferenceEquals(entry, target), fixture.Run));
    });

    [Fact]
    public void PartialSnapshotCannotProveAnAbsentExactIdentity() => OnDispatcher(() =>
    {
        MediaLibraryEntry target = Entry("703"), partialEntry = Entry("704");
        using var fixture = NewFixture(new MediaLibrarySnapshot([partialEntry], [], [], complete: false));

        Assert.Equal(MediaPage.ExactEntryFocusResult.Failed,
            Focus(fixture.Page, entry => ReferenceEquals(entry, target), fixture.Run));
    });

    [Fact]
    public void CompleteSnapshotIdentityThatIsNotPresentedIsFailedNotMissing() => OnDispatcher(() =>
    {
        MediaLibraryEntry target = Entry("705");
        using var fixture = NewFixture(new MediaLibrarySnapshot([target], [], [], complete: true));

        Assert.Equal(MediaPage.ExactEntryFocusResult.Failed,
            Focus(fixture.Page, entry => ReferenceEquals(entry, target), fixture.Run));
    });

    [Fact]
    public void MultiplePresentedExactIdentitiesAreFailedRatherThanChoosingOneRow() => OnDispatcher(() =>
    {
        // Distinct occurrences with the same exact identity are still ambiguous to the focus method.
        MediaLibraryEntry first = Entry("706"), second = Entry("706");
        using var fixture = NewFixture(new MediaLibrarySnapshot([first, second], [], [], complete: true), first, second);

        Assert.Equal(MediaPage.ExactEntryFocusResult.Failed,
            Focus(fixture.Page, entry => MediaPage.IsSavedBooruEntry(entry, "706"), fixture.Run));
    });

    [Fact]
    public void RetiredRequestIsCancelledBeforeMissingCanBeAnnounced() => OnDispatcher(() =>
    {
        MediaLibraryEntry target = Entry("707"), other = Entry("708");
        using var fixture = NewFixture(new MediaLibrarySnapshot([other], [], [], complete: true));

        Assert.Equal(MediaPage.ExactEntryFocusResult.Cancelled,
            Focus(fixture.Page, entry => ReferenceEquals(entry, target), fixture.Run, stillRequested: () => false));
    });

    [Fact]
    public void RevokedPageLifetimeIsCancelledBeforeMissingCanBeAnnounced() => OnDispatcher(() =>
    {
        MediaLibraryEntry target = Entry("709"), other = Entry("710");
        using var fixture = NewFixture(new MediaLibrarySnapshot([other], [], [], complete: true));
        fixture.Page.EndLifetime();

        Assert.Equal(MediaPage.ExactEntryFocusResult.Cancelled,
            Focus(fixture.Page, entry => ReferenceEquals(entry, target), fixture.Run));
    });

    [Fact]
    public void ExactPresentedEntryWithoutAnActiveContainerIsFailedNotMissing() => OnDispatcher(() =>
    {
        MediaLibraryEntry target = Entry("711");
        MediaLibraryTile tile = new(target, [target]);
        using var fixture = NewFixture(new MediaLibrarySnapshot([target], [], [], complete: true), target);
        fixture.Page.Grid.ItemsSource = new[] { new MediaLibraryTileRow(tile, 0) };
        Set(fixture.Page, "_tiles", new[] { tile });

        Assert.Equal(MediaPage.ExactEntryFocusResult.Failed,
            Focus(fixture.Page, entry => ReferenceEquals(entry, target), fixture.Run));
    });

    [Fact]
    public void ARecycledDifferentEntryAtTheExactIndexCannotBeFocused() => OnDispatcher(() =>
    {
        MediaLibraryEntry target = Entry("712"), neighbour = Entry("713");
        MediaLibraryTile recycled = new(neighbour, [neighbour]);
        using var fixture = NewFixture(new MediaLibrarySnapshot([target], [], [], complete: true), target);
        fixture.Page.Grid.ItemsSource = new[] { new MediaLibraryTileRow(recycled, 0) };
        Set(fixture.Page, "_tiles", new[] { recycled });

        Assert.Equal(MediaPage.ExactEntryFocusResult.Failed,
            Focus(fixture.Page, entry => ReferenceEquals(entry, target), fixture.Run));
    });

    private static FocusFixture NewFixture(MediaLibrarySnapshot snapshot, params MediaLibraryEntry[] shown) =>
        new(snapshot, shown);

    private static MediaPage.ExactEntryFocusResult Focus(MediaPage page, Func<MediaLibraryEntry, bool> match,
        object run, Func<bool>? stillRequested = null)
    {
        MethodInfo method = typeof(MediaPage).GetMethods(InstanceNonPublic)
            .Single(candidate => candidate.Name == "FocusExactEntryAsync" && candidate.GetParameters().Length == 4);
        Task operation = (Task)method.Invoke(page, [match, "synthetic missing", run, stillRequested])!;
        // Missing/failed/cancelled preconditions complete synchronously. The active-container case
        // may pass through the real Loaded dispatcher await, which can be drained without a window.
        for (int attempt = 0; !operation.IsCompleted && attempt < 500; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (!operation.IsCompleted) Thread.Sleep(1);
        }
        Assert.True(operation.IsCompleted, "The component focus operation did not settle on the headless dispatcher.");
        return (MediaPage.ExactEntryFocusResult)operation.GetType().GetProperty("Result", InstanceAny)!.GetValue(operation)!;
    }

    private sealed class FocusFixture : IDisposable
    {
        internal MediaPage Page { get; }
        internal object Run { get; } = new();
        internal CancellationTokenSource Lifetime { get; }

        internal FocusFixture(MediaLibrarySnapshot snapshot, IReadOnlyList<MediaLibraryEntry> shown)
        {
            Page = new MediaPage(_ => throw new InvalidOperationException("The focus fixture must not open archive data."));
            Page.IsVisible = true;
            SetEffectiveVisibility(Page);
            Set(Page, "_snapshot", snapshot);
            Set(Page, "_shown", shown);
            Set(Page, "_returnRun", Run);
            Lifetime = (CancellationTokenSource)Get(Page, "_lifetime")!;
        }

        public void Dispose()
        {
            Page.EndLifetime();
            Lifetime.Dispose();
        }
    }

    private static MediaLibraryEntry Entry(string post) => new(BooruMediaIdentity.Thread(post), "Post " + post, 0,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, null, null, null, null, "/synthetic/not-opened",
        "asset-" + post, new string('b', 64), 7, BooruMediaIdentity.MediaIdentifier(post), "original", post + ".jpg",
        SavedMediaKind.Image, MediaLibraryPresence.Present, true, false) { BooruPostId = post };

    private static object? Get(MediaPage page, string field) => typeof(MediaPage).GetField(field, InstanceNonPublic)!.GetValue(page);

    private static void Set(MediaPage page, string field, object? value) =>
        typeof(MediaPage).GetField(field, InstanceNonPublic)!.SetValue(page, value);

    private static void SetEffectiveVisibility(MediaPage page)
    {
        MethodInfo method = typeof(MediaPage).GetMethod("UpdateIsEffectivelyVisible", InstanceAny)!;
        method.Invoke(page, [true]);
        Assert.True(page.IsEffectivelyVisible);
    }

    private static void OnDispatcher(Action test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", InstanceAny | BindingFlags.Static)!;
        object? previous = primary.GetValue(null);
        string language = Texts.Language;
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            owner.Invoke(() => { Texts.Initialize(); Texts.SetLanguage("en"); test(); }, DispatcherPriority.Send);
        }
        finally
        {
            Texts.SetLanguage(language);
            primary.SetValue(null, previous);
        }
    }
}
