using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class GalleryFavoriteStateTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static FavoriteAssetKey Key => new(new("fixture", "g", "42"), "image", "original", new string('a', 64));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalPendingIntentsShareCompletionAndPerformOnlyOneSave(bool desired)
    {
        await OnQueueOwnerAsync(async () =>
        {
            var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();
            var save = new ControlledSave(queue, Key, saved: !desired);
            Task first = save.SetAsync(desired);
            Write write = await save.NextAsync();
            for (int repeat = 0; repeat < 20; repeat++)
            {
                // Equal record copies must coalesce, not just the same key instance.
                Task repeated = queue.SetAsync(Key with { }, desired,
                    _ => throw new InvalidOperationException("A duplicate must not replace the save delegate."),
                    () => throw new InvalidOperationException("A duplicate must not replace the refresh delegate."));
                Assert.Same(first, repeated);
            }
            Assert.False(first.IsCompleted);
            Assert.True(queue.IsPending(Key));
            Assert.Equal(desired, queue.Target(Key, !desired));
            Assert.Equal(!desired, save.Saved);
            Assert.Single(save.Writes);
            write.Succeed();
            await first.WaitAsync(Timeout);
            Assert.Equal(desired, save.Saved);
            Assert.False(queue.IsPending(Key));
            Assert.Equal(new[] { desired }, save.Writes);
            // Once drained, Target must use the caller's persisted snapshot again.
            Assert.False(queue.Target(Key, saved: false));
            Assert.True(queue.Target(Key, saved: true));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OppositeIntentWaitsForTheFirstSaveThenPersistsInOrder(bool firstDesired)
    {
        await OnQueueOwnerAsync(async () =>
        {
            var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();
            var save = new ControlledSave(queue, Key, saved: !firstDesired);
            Task first = save.SetAsync(firstDesired);
            Write firstWrite = await save.NextAsync();
            Task opposite = save.SetAsync(!firstDesired);
            Assert.Same(first, opposite);
            Assert.Single(save.Writes);
            Assert.Equal(!firstDesired, queue.Target(Key, firstDesired));
            firstWrite.Succeed();
            Write secondWrite = await save.NextAsync();
            Assert.Equal(new[] { firstDesired, !firstDesired }, save.Writes);
            Assert.Equal(firstDesired, save.Saved);
            Assert.True(queue.IsPending(Key));
            Assert.False(first.IsCompleted);
            secondWrite.Succeed();
            await Task.WhenAll(first, opposite).WaitAsync(Timeout);
            Assert.Equal(!firstDesired, save.Saved);
            Assert.False(queue.IsPending(Key));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThirdLatestIntentSupersedesAnUnstartedOppositeWrite(bool desired)
    {
        await OnQueueOwnerAsync(async () =>
        {
            var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();
            var save = new ControlledSave(queue, Key, saved: !desired);
            Task first = save.SetAsync(desired);
            Write write = await save.NextAsync();
            Task second = save.SetAsync(!desired);
            Task third = save.SetAsync(desired);
            Assert.Same(first, second);
            Assert.Same(first, third);
            Assert.Equal(desired, queue.Target(Key, !desired));
            Assert.Single(save.Writes);
            write.Succeed();
            await Task.WhenAll(first, second, third).WaitAsync(Timeout);
            Assert.Equal(new[] { desired }, save.Writes);
            Assert.Equal(desired, save.Saved);
            Assert.False(queue.IsPending(Key));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThirdLatestIntentDuringSecondSaveRunsAfterThatSave(bool desired)
    {
        await OnQueueOwnerAsync(async () =>
        {
            var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();
            var save = new ControlledSave(queue, Key, saved: !desired);
            Task first = save.SetAsync(desired);
            Write firstWrite = await save.NextAsync();
            Task second = save.SetAsync(!desired);
            firstWrite.Succeed();
            Write secondWrite = await save.NextAsync();
            Task third = save.SetAsync(desired);
            Assert.Equal(new[] { desired, !desired }, save.Writes);
            Assert.Equal(desired, save.Saved);
            Assert.Equal(desired, queue.Target(Key, !desired));
            secondWrite.Succeed();
            Write thirdWrite = await save.NextAsync();
            Assert.Equal(!desired, save.Saved);
            Assert.Equal(new[] { desired, !desired, desired }, save.Writes);
            Assert.False(first.IsCompleted);
            thirdWrite.Succeed();
            await Task.WhenAll(first, second, third).WaitAsync(Timeout);
            Assert.Equal(desired, save.Saved);
            Assert.False(queue.IsPending(Key));
        });
    }

    [Theory]
    [InlineData("source")]
    [InlineData("board")]
    [InlineData("thread")]
    [InlineData("media")]
    [InlineData("media-case")]
    [InlineData("variant")]
    [InlineData("hash")]
    public async Task DifferentExactFavoriteKeysHaveIndependentPendingWrites(string difference)
    {
        await OnQueueOwnerAsync(async () =>
        {
            FavoriteAssetKey other = difference switch
            {
                "source" => Key with { Thread = Key.Thread with { Source = "other" } },
                "board" => Key with { Thread = Key.Thread with { Board = "other" } },
                "thread" => Key with { Thread = Key.Thread with { Thread = "43" } },
                "media" => Key with { MediaIdentifier = "other" },
                "media-case" => Key with { MediaIdentifier = "IMAGE" },
                "variant" => Key with { Variant = "thumbnail" },
                "hash" => Key with { Sha256 = new string('b', 64) },
                _ => throw new ArgumentOutOfRangeException(nameof(difference))
            };
            var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();
            var left = new ControlledSave(queue, Key, saved: false);
            var right = new ControlledSave(queue, other, saved: true);
            Task leftTask = left.SetAsync(true);
            Write leftWrite = await left.NextAsync();
            Task rightTask = right.SetAsync(false);
            Write rightWrite = await right.NextAsync();
            Assert.NotSame(leftTask, rightTask);
            Assert.True(queue.IsPending(Key));
            Assert.True(queue.IsPending(other));
            Assert.True(queue.Target(Key, false));
            Assert.False(queue.Target(other, true));
            leftWrite.Succeed();
            await leftTask.WaitAsync(Timeout);
            Assert.False(queue.IsPending(Key));
            Assert.True(queue.IsPending(other));
            Assert.False(rightTask.IsCompleted);
            Assert.True(left.Saved);
            Assert.True(right.Saved);
            rightWrite.Succeed();
            await rightTask.WaitAsync(Timeout);
            Assert.False(queue.IsPending(other));
            Assert.False(right.Saved);
            Assert.Equal(new[] { true }, left.Writes);
            Assert.Equal(new[] { false }, right.Writes);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFaultReachesAllWaitersClearsPendingAndAllowsExplicitRetry(bool saved)
    {
        await OnQueueOwnerAsync(async () =>
        {
            var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();
            var save = new ControlledSave(queue, Key, saved);
            Task first = save.SetAsync(!saved);
            Write write = await save.NextAsync();
            Task opposite = save.SetAsync(saved);
            var failure = new IOException("synthetic favorite write failure");
            write.Fail(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(Timeout)));
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => opposite.WaitAsync(Timeout)));
            Assert.True(first.IsFaulted);
            Assert.True(opposite.IsFaulted);
            Assert.False(queue.IsPending(Key));
            Assert.Equal(saved, save.Saved);
            Assert.Equal(saved, queue.Target(Key, save.Saved));
            Assert.Equal(new[] { !saved }, save.Writes); // No automatic success or retry of the queued opposite.
            Assert.Equal(new[] { new Refresh(true, saved), new Refresh(false, saved) }, save.Refreshes);

            Task retry = save.SetAsync(!saved);
            Assert.NotSame(first, retry);
            Write retryWrite = await save.NextAsync();
            Assert.True(queue.IsPending(Key));
            retryWrite.Succeed();
            await retry.WaitAsync(Timeout);
            Assert.Equal(!saved, save.Saved);
            Assert.False(queue.IsPending(Key));
            Assert.Equal(new[] { !saved, !saved }, save.Writes);
        });
    }

    [Fact]
    public async Task CleanupRefreshCannotReplaceTheOriginalSaveFailure()
    {
        await OnQueueOwnerAsync(async () =>
        {
            var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var original = new IOException("original persistence error");
            var cleanup = new InvalidOperationException("secondary refresh error");
            List<bool> pending = [];
            Task command = queue.SetAsync(Key, true, _ => gate.Task, () =>
            {
                pending.Add(queue.IsPending(Key));
                if (!queue.IsPending(Key)) throw cleanup;
            });
            gate.SetException(original);
            Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => command.WaitAsync(Timeout)));
            Assert.Equal(new[] { true, false }, pending);
            Assert.False(queue.IsPending(Key));
            await queue.SetAsync(Key, false, _ => Task.CompletedTask, () => { }).WaitAsync(Timeout);
            Assert.False(queue.IsPending(Key));
        });
    }

    [Fact]
    public async Task RefreshSeesPendingBeforeSaveAndPersistedStateBeforeFinalPendingRemoval()
    {
        await OnQueueOwnerAsync(async () =>
        {
            var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();
            var save = new ControlledSave(queue, Key, saved: false);
            Task command = save.SetAsync(true);
            Write write = await save.NextAsync();
            Assert.Equal(new[] { new Refresh(true, false) }, save.Refreshes);
            Assert.False(save.Saved);
            write.Succeed();
            await command.WaitAsync(Timeout);
            Assert.Equal(new[] { new Refresh(true, false), new Refresh(true, true), new Refresh(false, true) }, save.Refreshes);
        });
    }

    [Fact]
    public async Task SynchronousSaveStillRefreshesAtAdmissionAndAfterPendingRemoval()
    {
        await OnQueueOwnerAsync(async () =>
        {
            var queue = new GalleryFavoriteCommandQueue<FavoriteAssetKey>();
            List<bool> pending = [];
            int writes = 0;
            await queue.SetAsync(Key, true, desired =>
            {
                Assert.True(desired);
                Assert.True(queue.IsPending(Key));
                writes++;
                return Task.CompletedTask;
            }, () => pending.Add(queue.IsPending(Key))).WaitAsync(Timeout);
            Assert.Equal(1, writes);
            Assert.Equal(new[] { true, true, false }, pending);
            Assert.False(queue.IsPending(Key));
        });
    }

    [Fact]
    public void FavoriteIndicatorFactoryCreatesAFocusableToggleWithPassiveArtwork()
    {
        ToggleButton badge = GalleryViewer.CreateFavoriteIndicator("GalleryFavoriteIndicator");
        Assert.Equal("GalleryFavoriteIndicator", badge.Name);
        // Review 15.11.: the badge is a real toggle (pointer, Tab, Space and Enter), only its artwork is passive.
        Assert.True(badge.IsHitTestVisible);
        Assert.True(badge.Focusable);
        Assert.Contains("gallery-favorite-badge", badge.Classes);
        Assert.False(badge.IsVisible);
        Assert.Equal(HorizontalAlignment.Left, badge.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Top, badge.VerticalAlignment);
        Assert.Equal(new Thickness(12), badge.Margin);
        // As high as the way back beside it and at least a square of that size: the Workspace.Size.Control token, bound dynamically.
        Assert.NotNull(badge.GetBindingObservable(Layoutable.HeightProperty));
        Assert.NotNull(badge.GetBindingObservable(Layoutable.MinWidthProperty));
        Assert.Equal(16, badge.ZIndex);
        StackPanel glyph = Assert.IsType<StackPanel>(badge.Content);
        Assert.False(glyph.IsHitTestVisible);
        Assert.False(glyph.Focusable);
        // A drawn star (not a font glyph) and a pending "…" that starts hidden.
        Avalonia.Controls.Shapes.Path star = Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Children[0]);
        Assert.Equal(MeowChan.Desktop.Controls.WorkspaceIcons.Star, MeowChan.Desktop.Controls.WorkspaceIcons.KeyOf(star));
        Assert.False(star.IsHitTestVisible);
        TextBlock pending = Assert.IsType<TextBlock>(glyph.Children[1]);
        // The size is the Subtitle design token (16), bound dynamically, so it resolves only once the badge is in the tree.
        Assert.NotNull(pending.GetBindingObservable(Avalonia.Controls.Documents.TextElement.FontSizeProperty));
        Assert.False(pending.IsVisible);
        Assert.Equal((false, false), GalleryViewer.FavoriteBadgeState(badge));
    }

    [Fact]
    public void FavoriteIndicatorSitsTopLeftDirectlyRightOfTheWayBack()
    {
        ToggleButton badge = GalleryViewer.CreateFavoriteIndicator("GalleryFavoriteIndicator");

        // The top left edge belongs to the gallery's way back (Tom's rule of 14.11.); the badge sits directly right of it
        // (UpdateFavoriteIndicators sets the margin from the button's width) and the post panel starts below that row where it reaches it.
        Assert.Equal(HorizontalAlignment.Left, badge.HorizontalAlignment);
        Assert.Equal(VerticalAlignment.Top, badge.VerticalAlignment);
        Assert.Equal(new Thickness(12), badge.Margin);
        Assert.True(badge.ZIndex > 0);
    }

    // Tom's rule of 15.11.: the star beside the way back is gone with it (opacity 0, no hit test, no focus) while the overlay controls are
    // auto-hidden, and returns with it or alone after a change (GalleryAutoHideCleanTests drives the states).
    [Fact]
    public void TheStarHidesCompletelyAndReturnsWithTheWayBack()
    {
        string viewer = ViewerSource();
        Assert.DoesNotContain("0.3", viewer.Substring(viewer.IndexOf("private void UpdateCornerChrome", StringComparison.Ordinal), 1800));
        Assert.Contains("star.ApplyTo(_favoriteIndicator);", viewer);
        Assert.Contains("star.ApplyTo(_nativeFavoriteIndicator);", viewer);
        Assert.Contains("back.ApplyTo(_close);", viewer);
    }

    private static string ViewerSource([CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", "Desktop/GalleryViewer.cs")));

    [Fact]
    public void RealIndicatorInitializerKeepsDistinctNamedControlsInEachRoot() => BooruComponentDispatcher.Run(() =>
    {
        var h = new IndicatorFixture();
        Assert.NotSame(h.Managed, h.Native);
        Assert.NotSame(h.Managed.Content, h.Native.Content);
        Assert.Equal("GalleryFavoriteIndicator", h.Managed.Name);
        Assert.Equal("GalleryNativeFavoriteIndicator", h.Native.Name);
        Assert.NotEqual(h.Managed.Name, h.Native.Name);
        Assert.Same(h.Managed, Assert.Single(h.ManagedRoot.Children));
        Assert.Same(h.Native, Assert.Single(h.NativeRoot.Children));
        h.Viewer.SetFavorite(true);
        foreach (bool native in new[] { true, false, true, false })
        {
            SetField(h.Viewer, "_nativeOverlayActive", native);
            Invoke(h.Viewer, "UpdateFavoriteIndicators");
            Assert.Equal(!native, h.Managed.IsVisible);
            Assert.Equal(native, h.Native.IsVisible);
            Assert.Same(h.ManagedRoot, h.Managed.Parent);
            Assert.Same(h.NativeRoot, h.Native.Parent);
            Assert.Single(h.ManagedRoot.Children);
            Assert.Single(h.NativeRoot.Children);
        }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SetFavoriteShowsPersistedGlyphAndAccessibleStateEvenWithPendingAndHiddenTools(bool saved, bool pending) =>
        BooruComponentDispatcher.Run(() =>
        {
            var h = new IndicatorFixture();
            SetField(h.Viewer, "_overlayHidden", true);
            SetField(h.Viewer, "_mediaFocus", true);
            h.Button.IsVisible = false; // Already-hidden tool; no full viewer visibility/focus simulation.
            h.Viewer.SetFavorite(saved, pending);
            Assert.Equal(saved, h.Button.IsChecked);
            Assert.True(h.Button.IsEnabled); // Opposite intent remains available while saving.
            Assert.False(h.Button.IsVisible);
            Assert.True(h.Managed.IsVisible);
            Assert.False(h.Native.IsVisible);
            AssertArtwork(h, saved, pending);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingPresentationNeverOptimisticallyFlipsPersistedState(bool saved) => BooruComponentDispatcher.Run(() =>
    {
        var h = new IndicatorFixture();
        h.Viewer.SetFavorite(saved);
        h.Viewer.SetFavorite(saved, pending: true);
        AssertArtwork(h, saved, pending: true);
        Assert.Equal(saved, h.Button.IsChecked);
        // The committed session snapshot, not the pending intent, changes the star.
        h.Viewer.SetFavorite(!saved, pending: true);
        AssertArtwork(h, !saved, pending: true);
        h.Viewer.SetFavorite(!saved, pending: false);
        AssertArtwork(h, !saved, pending: false);
        Assert.Equal(!saved, h.Button.IsChecked);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownSelectionMissingEntryAndTerminalRetirementHideBothBadges(bool native) => BooruComponentDispatcher.Run(() =>
    {
        var h = new IndicatorFixture();
        SetField(h.Viewer, "_nativeOverlayActive", native);
        Invoke(h.Viewer, "UpdateFavoriteIndicators");
        Assert.False(h.Managed.IsVisible);
        Assert.False(h.Native.IsVisible);
        h.Viewer.SetFavorite(true);
        Assert.Equal(!native, h.Managed.IsVisible);
        Assert.Equal(native, h.Native.IsVisible);
        SetField(h.Viewer, "_favoriteKnown", false); // ShowMediaAsync's unknown persisted-state boundary.
        Invoke(h.Viewer, "UpdateFavoriteIndicators");
        Assert.False(h.Managed.IsVisible);
        Assert.False(h.Native.IsVisible);
        h.Viewer.SetFavorite(false, pending: true);
        SetField(h.Viewer, "_entry", null);
        Invoke(h.Viewer, "UpdateFavorite");
        Assert.False(h.Managed.IsVisible);
        Assert.False(h.Native.IsVisible);
        Assert.False(h.Button.IsEnabled);
        SetField(h.Viewer, "_entry", Entry());
        SetField(h.Viewer, "_terminalRetirement", true);
        h.Viewer.SetFavorite(true, pending: true);
        Assert.False(h.Managed.IsVisible);
        Assert.False(h.Native.IsVisible);
        Assert.False(h.Button.IsEnabled);
    });

    [Fact]
    public void NativeTransparencyFallbackPlacesManagedBadgeAboveVideoWithoutTransferringEitherBadge() => BooruComponentDispatcher.Run(() =>
    {
        var h = new IndicatorFixture();
        var body = new Grid();
        SetField(h.Viewer, "_body", body);
        SetField(h.Viewer, "_helpPanel", new Border());
        SetField(h.Viewer, "_favoriteConfirmPanel", new Border());
        SetField(h.Viewer, "_chrome", new Border());
        SetField(h.Viewer, "_infoOverlay", new Border());
        var dock = new StackPanel(); // GL1: the tools and the info line live in the dock, which owns the row below the picture
        SetField(h.Viewer, "_dockStack", dock);
        h.Viewer.SetFavorite(true);
        // Invoke actual fallback row configuration, but not window activation or player setup.
        Invoke(h.Viewer, "SetVideoFallbackLayout", true);
        Invoke(h.Viewer, "UpdateFavoriteIndicators");
        Assert.Equal(4, h.ManagedRoot.RowDefinitions.Count);
        Assert.Equal(GridLength.Auto, h.ManagedRoot.RowDefinitions[0].Height);
        Assert.Equal(0, Grid.GetRow(h.Managed));
        Assert.Equal(1, Grid.GetRow(body));
        Assert.Equal(2, Grid.GetRow(dock));
        Assert.True(h.Managed.IsVisible);
        Assert.False(h.Native.IsVisible);
        Assert.Same(h.ManagedRoot, h.Managed.Parent);
        Assert.Same(h.NativeRoot, h.Native.Parent);
        Invoke(h.Viewer, "SetVideoFallbackLayout", false);
        Invoke(h.Viewer, "UpdateFavoriteIndicators");
        Assert.Equal(3, h.ManagedRoot.RowDefinitions.Count);
        Assert.Equal(1, Grid.GetRow(dock));
        Assert.Equal(0, Grid.GetRow(body));
        Assert.Equal(0, Grid.GetRow(h.Managed));
        Assert.True(h.Managed.IsVisible);
    });

    private static void AssertArtwork(IndicatorFixture h, bool saved, bool pending)
    {
        foreach (ToggleButton badge in new[] { h.Managed, h.Native })
        {
            StackPanel glyph = Assert.IsType<StackPanel>(badge.Content);
            Assert.Equal((saved, pending), GalleryViewer.FavoriteBadgeState(badge));
            Avalonia.Controls.Shapes.Path star = Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Children[0]);
            Assert.Equal(saved ? Colors.Gold : Colors.White,
                Assert.IsAssignableFrom<ISolidColorBrush>(star.Stroke).Color);
            Assert.Equal(saved, star.Fill is ISolidColorBrush { Color.A: > 0 });
            string? label = AutomationProperties.GetName(badge);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.StartsWith(Texts.T("gallery.decision.favorite"), label!); // the toggle state (checked), not the name, says saved or not
            Assert.Equal(pending, label!.Contains('…'));
            Assert.Equal(saved, badge.IsChecked);
            Assert.False(glyph.IsHitTestVisible);
            Assert.True(badge.IsHitTestVisible);
            Assert.True(badge.Focusable);
            Assert.True(badge.IsEnabled); // never disabled while saving: the focus must stay where it is
        }
    }

    // The production queue is UI-owner-only. Use a private single-threaded
    // async context rather than depending on xUnit's worker scheduling. Gates
    // and bounded WaitAsync timeouts provide progress; no sleeps or polling.
    private static Task OnQueueOwnerAsync(Func<Task> test) => Task.Run(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        using var owner = new QueueOwnerContext();
        SynchronizationContext.SetSynchronizationContext(owner);
        try
        {
            Task running = test();
            while (!running.IsCompleted) owner.RunOne();
            running.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    });

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private sealed class QueueOwnerContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posted = new();
        public override void Post(SendOrPostCallback callback, object? state) => _posted.Add((callback, state));
        internal void RunOne()
        {
            if (!_posted.TryTake(out var work, Timeout))
                throw new TimeoutException("Favorite queue test did not make owner-context progress.");
            work.Callback(work.State);
        }
        public void Dispose() => _posted.Dispose();
    }

    private sealed record Refresh(bool Pending, bool Saved);

    private sealed class Write(bool desired)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Desired { get; } = desired;
        internal Task Completion => _completion.Task;
        internal void Succeed() => _completion.SetResult();
        internal void Fail(Exception error) => _completion.SetException(error);
    }

    // Only the persistence boundary is controlled. Production owns admission,
    // coalescing, serialization, pending removal, refresh delivery and fault propagation.
    private sealed class ControlledSave(GalleryFavoriteCommandQueue<FavoriteAssetKey> queue, FavoriteAssetKey key, bool saved)
    {
        private readonly Channel<Write> _started = Channel.CreateUnbounded<Write>();
        internal bool Saved { get; private set; } = saved;
        internal List<bool> Writes { get; } = [];
        internal List<Refresh> Refreshes { get; } = [];
        internal Task SetAsync(bool desired) => queue.SetAsync(key, desired, SaveAsync,
            () => Refreshes.Add(new(queue.IsPending(key), Saved)));
        internal Task<Write> NextAsync() => _started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        private async Task SaveAsync(bool desired)
        {
            var write = new Write(desired);
            Writes.Add(write.Desired);
            if (!_started.Writer.TryWrite(write)) throw new InvalidOperationException("Save gate was closed.");
            await write.Completion;
            Saved = desired;
        }
    }

    // Deliberately NOT a constructed viewer/native window. Initialize only the
    // exact fields consumed by real SetFavorite/Initialize/Update methods, as in
    // GalleryOverlayActivityTests. These checks prove control-state policy, not
    // native activation, hit routing, decoding, live layout or end-to-end persistence.
    private sealed class IndicatorFixture
    {
        internal GalleryViewer Viewer { get; } = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        internal ToggleButton Button { get; } = new();
        internal ToggleButton Managed { get; } = GalleryViewer.CreateFavoriteIndicator("GalleryFavoriteIndicator");
        internal ToggleButton Native { get; } = GalleryViewer.CreateFavoriteIndicator("GalleryNativeFavoriteIndicator");
        internal Grid ManagedRoot { get; } = new() { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        internal Grid NativeRoot { get; } = new();
        internal IndicatorFixture()
        {
            SetField(Viewer, "_favorite", Button);
            SetField(Viewer, "_favoriteIndicator", Managed);
            SetField(Viewer, "_nativeFavoriteIndicator", Native);
            SetField(Viewer, "_visualRoot", ManagedRoot);
            SetField(Viewer, "_nativeVideoOverlay", NativeRoot);
            SetField(Viewer, "_entry", Entry());
            Invoke(Viewer, "InitializeFavoriteIndicators");
        }
    }

    private static GalleryEntry Entry()
    {
        var asset = new MediaAssetReference("asset", "image", "original", "fixture.png", "fixture.png", 7,
            new string('a', 64), "https://fixture.invalid/fixture.png", "fixture.png", true);
        var manifest = new MediaAssetSnapshot("fixture", MediaManifestStatus.Available, 1, "generation", [asset]);
        return new(new(Key.Thread, manifest.Directory, asset.AssetId), manifest, asset, null, null, false, false);
    }

    private static void SetField(GalleryViewer viewer, string name, object? value) =>
        typeof(GalleryViewer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, value);
    private static void Invoke(GalleryViewer viewer, string name, params object[] args) =>
        typeof(GalleryViewer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, args);
}
