using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// No native window, personal profile or network. Exercise the production menu
// factory with real MenuItems/ThreadRows, plus a paused isolated host roundtrip.
[CollectionDefinition("Thread priority menu", DisableParallelization = true)]
public sealed class ThreadPriorityContextMenuCollection;

[Collection("Thread priority menu")]
public sealed class ThreadPriorityContextMenuTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, "priority.menuHigh")]
    [InlineData(true, "priority.menuNormal")]
    public void UniformSelectionOffersOnlyOppositeAction(bool high, string key)
    {
        using var model = new MenuModel();
        ThreadRow first = model.Add(1, high), second = model.Add(2, high);
        MenuItem item = Assert.Single(model.Build());
        AssertItem(item, key, 2);
        Click(item);
        var call = Assert.Single(model.Calls);
        Assert.Equal(!high, call.High);
        Assert.Equal(new[] { first, second }, call.Rows);
        Assert.Equal(high, first.IsHighPriority); // no optimistic projection
        Assert.Equal(high, second.IsHighPriority);
    }

    [Theory]
    [InlineData(true, "priority.menuHigh", 2)]
    [InlineData(false, "priority.menuNormal", 1)]
    public void MixedSelectionCountsAndActsOnOnlyOppositeRows(bool desired, string key, int count)
    {
        using var model = new MenuModel();
        ThreadRow first = model.Add(1, false), second = model.Add(2, false), third = model.Add(3, true);
        MenuItem[] items = model.Build();
        Assert.Equal(2, items.Length);
        AssertItem(items[0], "priority.menuHigh", 2);
        AssertItem(items[1], "priority.menuNormal", 1);
        Click(items.Single(item => Equals(item.Tag, key)));
        var call = Assert.Single(model.Calls);
        Assert.Equal(desired, call.High);
        Assert.Equal(count, call.Rows.Length);
        Assert.Equal(desired ? new[] { first, second } : new[] { third }, call.Rows);
    }

    [Fact]
    public void EmptyOrEntirelyStaleSelectionHasNoPriorityActions()
    {
        using var model = new MenuModel();
        Assert.Empty(model.Build());
        model.Add(1, false);
        model.Current.Clear();
        Assert.Empty(model.Build());
    }

    [Fact]
    public void DuplicateSelectionDoesNotInflateCountsOrDispatch()
    {
        using var model = new MenuModel();
        ThreadRow row = model.Add(1, false);
        model.Selection.Add(row);
        MenuItem item = Assert.Single(model.Build());
        AssertItem(item, "priority.menuHigh", 1);
        Click(item);
        Assert.Same(row, Assert.Single(Assert.Single(model.Calls).Rows));
    }

    [Fact]
    public void MenuDoesNotExpandToLaterSelectionOrNewlyOppositeRows()
    {
        using var model = new MenuModel();
        ThreadRow original = model.Add(1, false), originallyHigh = model.Add(2, true);
        MenuItem high = model.Build().Single(item => Equals(item.Tag, "priority.menuHigh"));
        originallyHigh.ApplyUserPriority(false);
        model.Selection.Clear();
        model.Add(3, false);
        Click(high);
        Assert.Same(original, Assert.Single(Assert.Single(model.Calls).Rows));
    }

    [Fact]
    public void RemovedAndSameWatchReplacementObjectsAreNotInherited()
    {
        using var model = new MenuModel();
        ThreadRow removed = model.Add(1, false), replaced = model.Add(2, false), survivor = model.Add(3, false);
        MenuItem item = Assert.Single(model.Build());
        model.Current.Remove(removed.WatchId);
        // Same WatchId AND same resource/URL is still a different row identity.
        ThreadRow replacement = model.Add(replaced.WatchId, false);
        Assert.Equal(replaced.Url, replacement.Url);
        Click(item);
        Assert.Same(survivor, Assert.Single(Assert.Single(model.Calls).Rows));
        Assert.False(replacement.IsHighPriority);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RowsAlreadyAtTargetWhenClickedAreSkipped(bool desired)
    {
        using var model = new MenuModel();
        ThreadRow changed = model.Add(1, !desired), unchanged = model.Add(2, !desired);
        MenuItem item = Assert.Single(model.Build());
        changed.ApplyUserPriority(desired);
        Click(item);
        Assert.Same(unchanged, Assert.Single(Assert.Single(model.Calls).Rows));
        model.Calls.Clear();
        unchanged.ApplyUserPriority(desired);
        Click(item);
        Assert.Empty(model.Calls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DisabledMenusCannotDispatchEvenIfGateLaterReopens(bool ready, bool available)
    {
        using var model = new MenuModel { Available = available };
        model.Add(1, false);
        MenuItem item = Assert.Single(model.Build(ready));
        Assert.False(item.IsEnabled);
        model.Available = true;
        Click(item); // synthetic routed event must not bypass admission
        Assert.Empty(model.Calls);
    }

    [Fact]
    public void LiveSafetyGateIsRecheckedAfterMenuWasBuilt()
    {
        using var model = new MenuModel();
        model.Add(1, false);
        MenuItem item = Assert.Single(model.Build());
        Assert.True(item.IsEnabled);
        // Production callback combines busy/restore/row-apply/closing/stale gates.
        model.Available = false;
        Click(item);
        Assert.Empty(model.Calls);
    }

    [Fact]
    public void RebuildReadsCurrentProjectionWithoutReusingOldHandlers() => OnOwnedDispatcher(() =>
    {
        using var model = new MenuModel();
        ThreadRow row = model.Add(1, false);
        var menu = new ContextMenu();
        FieldInfo languageEvent = typeof(Texts).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic)!;
        FieldInfo rowEvent = typeof(ThreadRow).GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object? languageListeners = languageEvent.GetValue(null), rowListeners = rowEvent.GetValue(row);
        for (int i = 0; i < 12; i++)
        {
            bool high = i % 2 != 0;
            row.ApplyUserPriority(high);
            menu.Items.Clear();
            foreach (MenuItem item in model.Build()) menu.Items.Add(item);
            MenuItem action = Assert.Single(menu.Items.OfType<MenuItem>());
            AssertItem(action, high ? "priority.menuNormal" : "priority.menuHigh", 1);
            Click(action);
            Assert.Equal(i + 1, model.Calls.Count);
            Assert.Equal(!high, model.Calls[^1].High);
            Assert.Equal(high, row.IsHighPriority);
        }
        // Rebuilding creates only menu-owned handlers, not subscriptions that
        // keep retired menus alive through a row or the static language event.
        Assert.Same(languageListeners, languageEvent.GetValue(null));
        Assert.Same(rowListeners, rowEvent.GetValue(row));
    });

    // Same pinned Avalonia 12.1.4 seam as PluginFooterLanguageTransitionBoundaryTests.
    // This nonparallel collection keeps all collection mutation, clicks and disposal
    // on its owner; restore the process-primary slot without draining another queue.
    private static void OnOwnedDispatcher(Action test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Avalonia primary dispatcher slot is missing.");
        object? previous = primary.GetValue(null);
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            owner.Invoke(() =>
            {
                owner.VerifyAccess();
                Assert.Same(owner, Dispatcher.UIThread);
                test();
            }, DispatcherPriority.Send);
        }
        finally { primary.SetValue(null, previous); }
    }

    [Fact]
    public void BatchRevalidationRejectsRowsRetiredOrChangedAfterAnEarlierDispatch()
    {
        using var model = new MenuModel();
        ThreadRow retired = model.Add(1, false), changed = model.Add(2, false), valid = model.Add(3, false);
        Click(Assert.Single(model.Build()));
        ThreadRow[] frozen = Assert.Single(model.Calls).Rows;
        model.Add(retired.WatchId, false);
        changed.ApplyUserPriority(true);
        // Same predicate used after RunSelectionCommandAsync's projection wait.
        Assert.Equal(new[] { valid }, frozen.Where(row => MainWindow.IsUserPriorityTarget(row, true, model.IsCurrent)));
    }

    [Fact]
    public Task HostSuccessRequiresProjectionAndRejectedHostChangeNeverMarksRow() => OnOwnedDispatcherAsync(async () =>
    {
        using TempDirectory directory = new();
        new Config { DownloadsPaused = true, AutomaticUpdateChecks = false,
            DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        using NoNetwork handler = new();
        using HttpClient client = new(handler);
        await using DesktopSessionHost host = new(directory.Path, [new FourChanImageBoard(client)]);
        await host.StartAsync().WaitAsync(Deadline);
        var added = await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Deadline);
        using var model = new MenuModel();
        ThreadRow row = model.Add(added.WatchId!.Value, false);
        Task<bool>? pending = null;
        TaskCompletionSource<bool>? release = null;
        async Task<bool> Submit(ThreadRow[] rows, bool high)
        {
            await release!.Task;
            return await host.SetUserThreadPriorityAsync(Assert.Single(rows).WatchId, high);
        }
        MenuItem BuildAction() => Assert.Single(MainWindow.CreateUserPriorityMenuItems(model.Selection, true,
            () => true, model.IsCurrent, (rows, high) => pending = Submit(rows, high)));

        foreach (bool desired in new[] { true, false })
        {
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(BuildAction());
            Assert.NotNull(pending);
            Assert.False(pending!.IsCompleted);
            Assert.Equal(!desired, row.IsHighPriority);
            release.SetResult(true);
            Assert.True(await pending.WaitAsync(Deadline));
            Assert.Equal(!desired, row.IsHighPriority); // host completion is not an optimistic UI write
            var projection = await host.CaptureProjectionAsync().WaitAsync(Deadline);
            row.ApplyUserPriority(projection.HighPriorityWatches.Contains(row.WatchId));
            Assert.Equal(desired, row.IsHighPriority);
            AssertItem(BuildAction(), desired ? "priority.menuNormal" : "priority.menuHigh", 1);
        }

        MenuItem staleAction = BuildAction();
        await host.RemoveThreadAsync(row.WatchId).WaitAsync(Deadline);
        var replacement = await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Deadline);
        Assert.NotEqual(row.WatchId, replacement.WatchId);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(staleAction);
        release.SetResult(true);
        Assert.False(await pending!.WaitAsync(Deadline));
        Assert.False(row.IsHighPriority);
        Assert.Empty((await host.CaptureProjectionAsync().WaitAsync(Deadline)).HighPriorityWatches);
        AssertItem(BuildAction(), "priority.menuHigh", 1);
        Assert.Equal(0, handler.Requests);
    });

    // Retain a single control owner across host awaits, including the menu built
    // before removal and clicked afterwards. Pump only this private async context;
    // never drain/reset Avalonia's dispatcher or block its pending continuations.
    private static Task OnOwnedDispatcherAsync(Func<Task> test) => Task.Run(() => OnOwnedDispatcher(() =>
    {
        SynchronizationContext? previous = SynchronizationContext.Current;
        using var context = new MenuOwnerContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            Task running = test();
            while (!running.IsCompleted) context.RunOne();
            running.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }));

    private sealed class MenuOwnerContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posted = new();
        public override void Post(SendOrPostCallback callback, object? state) => _posted.Add((callback, state));
        internal void RunOne()
        {
            if (!_posted.TryTake(out var work, Deadline))
                throw new TimeoutException("Priority menu test did not make owner-context progress.");
            Dispatcher.UIThread.VerifyAccess();
            work.Callback(work.State);
        }
        public void Dispose() => _posted.Dispose();
    }

    private static void AssertItem(MenuItem item, string key, int count)
    {
        Assert.Equal(key, item.Tag);
        string label = Texts.T(key, count);
        Assert.NotEqual(key, label);
        Assert.Contains(count.ToString(Texts.Culture), label);
        Assert.Equal(label, item.Header);
        Assert.Equal(label, AutomationProperties.GetName(item));
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    private sealed class MenuModel : IDisposable
    {
        private readonly List<ThreadRow> _owned = new();
        internal Dictionary<long, ThreadRow> Current { get; } = new();
        internal List<ThreadRow> Selection { get; } = new();
        internal List<(ThreadRow[] Rows, bool High)> Calls { get; } = new();
        internal bool Available { get; set; } = true;
        internal bool IsCurrent(ThreadRow row) => Current.TryGetValue(row.WatchId, out ThreadRow? current)
            && ReferenceEquals(row, current);
        internal MenuItem[] Build(bool ready = true) => MainWindow.CreateUserPriorityMenuItems(Selection, ready,
            () => Available, IsCurrent, (rows, high) => Calls.Add((rows, high)));
        internal ThreadRow Add(long id, bool high)
        {
            var row = new ThreadRow(new DesktopThreadEntry(id, new ThreadSnapshot(new ResourceId("fixture", "g", id.ToString()),
                "https://fixture.invalid/g/thread/" + id, "Priority fixture", "/never/read", ThreadActivity.Pending,
                ThreadSourceState.Active, null, 0, 0, new Dictionary<string, FailureInfo>(), null, new DownloadCounts(0, 0)),
                "Fixture", "Technology"));
            row.ApplyUserPriority(high);
            _owned.Add(row); Current[id] = row; Selection.Add(row);
            return row;
        }
        public void Dispose() { foreach (ThreadRow row in _owned) row.Dispose(); }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            throw new InvalidOperationException("Priority menu tests must not send network requests.");
        }
    }
}
