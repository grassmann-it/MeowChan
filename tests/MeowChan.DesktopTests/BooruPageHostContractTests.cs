using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>
/// BP2 host contracts using real controls/models and in-memory ports. No application, Window,
/// renderer, dispatcher pump, bitmap, profile or network. These do not measure layout or prove
/// visual attach/detach behavior; those require the parent's native page-host smoke.
/// </summary>
[Collection("Localization")]
public sealed class BooruPageHostContractTests
{
    [Fact]
    public void TheActualViewAndAllThreePartsStayInsideThePage()
    {
        RecordingPorts ports = new();
        using BooruPage page = new(ports.Services);
        BooruWorkbenchView view = Assert.IsType<BooruWorkbenchView>(page.Content);
        Assert.Same(page.View, view);
        Assert.Same(page.Model, view.Model);
        Assert.Equal("BooruPage", page.Name);
        Assert.Equal("BooruWorkbenchView", view.Name);

        Grid layout = Assert.IsType<Grid>(view.Content);
        Assert.Equal(3, layout.RowDefinitions.Count);
        Assert.Equal(3, layout.Children.Count);
        Border heading = Assert.IsType<Border>(layout.Children[0]);
        Border surface = Assert.IsType<Border>(layout.Children[1]);
        Border notes = Assert.IsType<Border>(layout.Children[2]);
        Assert.Same(view.Header, heading.Child);
        Assert.Contains("workspace-page-header", view.Header.Classes);
        Assert.Contains("workspace-section-flush", surface.Classes);
        ScrollViewer scroll = Assert.IsType<ScrollViewer>(surface.Child);
        Assert.Same(view.Body, scroll.Content);
        Assert.Equal(ScrollBarVisibility.Disabled, scroll.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        Assert.Same(view.Footer, notes.Child);
        Assert.Equal(0, Grid.GetRow(heading));
        Assert.Equal(1, Grid.GetRow(surface));
        Assert.Equal(2, Grid.GetRow(notes));
        AssertNoRemoteWork(ports);
        Assert.Equal(0, ports.ListReads);
    }

    [Fact]
    public void BothRealWorkbenchTabsAndTheirContentNamesArePreserved()
    {
        RecordingPorts ports = new();
        using BooruPage page = new(ports.Services);
        TabControl tabs = Assert.IsType<TabControl>(page.View.Body);
        Assert.Equal(BooruWorkbenchView.TabsName, tabs.Name);
        TabItem[] segments = Assert.IsType<TabItem[]>(tabs.ItemsSource);
        Assert.Equal(2, segments.Length);
        Control search = Assert.IsAssignableFrom<Control>(segments[0].Content);
        Control watchers = Assert.IsAssignableFrom<Control>(segments[1].Content);
        string?[] names = search.GetVisualDescendants().Concat(watchers.GetVisualDescendants())
            .Concat(page.View.Footer.GetVisualDescendants()).OfType<Control>().Select(control => control.Name).ToArray();
        // Actual content, not aliases to a replacement/placeholder. Dynamic result and watcher
        // cards remain the view's responsibility and are covered by the existing workbench checks.
        foreach (string name in new[] { BooruWorkbenchView.ModeName, BooruWorkbenchView.InputName,
            BooruWorkbenchView.SubmitName, BooruWorkbenchView.CancelName, BooruWorkbenchView.RetryName,
            BooruWorkbenchView.QueryName, BooruWorkbenchView.StatusName, BooruWorkbenchView.PreviousName,
            BooruWorkbenchView.NextName, BooruWorkbenchView.CreateWatcherName, BooruWorkbenchView.ResultsName,
            BooruWorkbenchView.WatchersName, BooruWorkbenchView.RemoveNoteName, BooruWorkbenchView.NoFilterNoteName,
            BooruWorkbenchView.LinkNoteName, BooruWorkbenchView.InputIssueName, BooruWorkbenchView.NoticeName,
            BooruWorkbenchView.WatcherNoticeName, BooruWorkbenchView.ResetCooldownName, BooruWorkbenchView.CatalogStorageName })
            Assert.Contains(name, names);
        AssertNoRemoteWork(ports);
    }

    [Fact]
    public void ServiceIdentityIsFixedAndTheViewHookExposesTheOwnedModel()
    {
        RecordingPorts ports = new();
        using BooruPage page = new(ports.Services);
        Assert.Same(ports.Services, page.Services);
        Assert.Same(page.Services, page.View.Model.Services);
        Assert.Same(page.Model.Search, page.View.Model.Search);
        foreach (string property in new[] { nameof(BooruPage.Services), nameof(BooruPage.View), nameof(BooruPage.Model) })
            Assert.Null(typeof(BooruPage).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetSetMethod(true));
        Assert.Throws<ArgumentNullException>(() => new BooruPage(null!));
    }

    [Fact]
    public void QueryHandoffTagAdditionAndClearOnlyEditTheRealDraft()
    {
        RecordingPorts ports = new();
        using BooruPage page = new(ports.Services);
        TabControl tabs = Assert.IsType<TabControl>(page.View.Body);
        TabItem[] segments = Assert.IsType<TabItem[]>(tabs.ItemsSource);
        tabs.SelectedItem = segments[1];
        page.ShowQuery("cat");
        Assert.Same(segments[0], tabs.SelectedItem);
        Assert.Equal(BooruInputMode.Tags, page.Model.Search.View.Mode);
        Assert.Equal("cat", page.Model.Search.View.Input);
        page.AddTagToSearch("sky");
        Assert.Equal("cat sky", page.Model.Search.View.Input);
        Assert.True(page.TryClearQuery());
        Assert.Equal(string.Empty, page.Model.Search.View.Input);
        Assert.False(page.TryClearQuery());
        Assert.Equal(BooruSearchStatus.Idle, page.Model.Search.View.Status);
        Assert.Null(page.Model.Search.View.ExecutedQuery);
        Assert.Equal(0, ports.ListReads);
        AssertNoRemoteWork(ports);
    }

    [Fact]
    public async Task RepeatedActivationSharesOneLocalInitializationAndNeverSearches()
    {
        TaskCompletionSource<IReadOnlyList<BooruWatcherView>> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingPorts ports = new() { ListResult = release.Task };
        using BooruPage page = new(ports.Services);
        Task first = page.ActivateAsync();
        try
        {
            Assert.Same(first, page.ActivateAsync());
            Assert.Equal(1, ports.ListReads);
            Assert.False(first.IsCompleted);
            AssertNoRemoteWork(ports);
        }
        finally { release.TrySetResult(Array.Empty<BooruWatcherView>()); }
        await first;
        Assert.Same(first, page.ActivateAsync());
        Assert.Equal(1, ports.ListReads);
        Assert.Equal(1, ports.CatalogReads);
        Assert.Equal(BooruWatcherListStatus.Empty, page.Model.Watchers.View.Status);
        AssertNoRemoteWork(ports);
    }

    [Fact]
    public async Task EndLifetimeAndRepeatedDisposeReleaseEachPortSubscriptionOnce()
    {
        RecordingPorts ports = new();
        using BooruPage page = new(ports.Services);
        page.ShowQuery("cat");
        Assert.Equal(1, ports.QueueAdds);
        Assert.Equal(1, ports.WatcherAdds);
        page.EndLifetime();
        page.Dispose();
        page.EndLifetime();
        Assert.Equal(1, ports.QueueRemoves);
        Assert.Equal(1, ports.WatcherRemoves);
        Assert.Equal(0, ports.QueueSubscribers);
        Assert.Equal(0, ports.WatcherSubscribers);
        ports.RaiseLocalChanges();
        ports.RaiseWatcherChanges();
        await page.ActivateAsync();
        page.ShowQuery("dog");
        page.AddTagToSearch("sky");
        Assert.False(page.TryClearQuery());
        Assert.False(page.Model.Search.CanSubmit);
        Assert.Equal("cat", page.Model.Search.View.Input);
        Assert.Equal(0, ports.ListReads);
        AssertNoRemoteWork(ports);
    }

    [Fact]
    public async Task ParentReplacementUsesNewServicesEvenForTheSameProvider()
    {
        RecordingPorts oldPorts = new(), newPorts = new();
        using BooruPage oldPage = new(oldPorts.Services);
        oldPage.ShowQuery("old_profile");
        // This models the required parent hook; the page does not subscribe to a global session.
        oldPage.Dispose();
        using BooruPage replacement = new(newPorts.Services);
        Assert.Equal(oldPage.Model.Search.View.ProviderId, replacement.Model.Search.View.ProviderId);
        Assert.NotSame(oldPage.Model, replacement.Model);
        Assert.NotSame(oldPage.View, replacement.View);
        Assert.NotSame(oldPage.Services, replacement.Services);
        Assert.Same(newPorts.Services, replacement.Services);
        Assert.Equal(string.Empty, replacement.Model.Search.View.Input);
        oldPorts.RaiseLocalChanges();
        oldPorts.RaiseWatcherChanges();
        await replacement.ActivateAsync();
        Assert.Equal(0, oldPorts.ListReads);
        Assert.Equal(1, newPorts.ListReads);
        Assert.Equal(0, oldPorts.QueueSubscribers);
        Assert.Equal(0, oldPorts.WatcherSubscribers);
        Assert.Equal(1, newPorts.QueueSubscribers);
        Assert.Equal(1, newPorts.WatcherSubscribers);
        AssertNoRemoteWork(oldPorts);
        AssertNoRemoteWork(newPorts);
    }

    [Fact]
    public async Task DisposalCancelsPendingLocalInitializationAndRejectsItsLateRows()
    {
        TaskCompletionSource<IReadOnlyList<BooruWatcherView>> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingPorts ports = new() { ListResult = release.Task };
        using BooruPage page = new(ports.Services);
        Task pending = page.ActivateAsync();
        page.Dispose();
        try
        {
            Assert.True(ports.ListToken.IsCancellationRequested);
            Assert.Equal(0, ports.WatcherSubscribers);
        }
        finally { release.TrySetResult(new[] { BooruUiFixtures.Watcher("late", "cat") }); }
        await pending; // The fixture deliberately completes despite cancellation.
        Assert.Empty(page.Model.Watchers.View.Rows);
        await page.ActivateAsync();
        Assert.Equal(1, ports.ListReads);
        AssertNoRemoteWork(ports);
    }

    [Fact]
    public async Task DisposalCancelsAnExplicitSearchAndRejectsItsLateResult()
    {
        TaskCompletionSource<BooruCallResult<BooruResultPage>> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RecordingPorts ports = new() { SearchResult = release.Task };
        using BooruPage page = new(ports.Services);
        page.ShowQuery("cat");
        Task pending = page.Model.Search.SubmitAsync(); // Explicit user intent, not activation.
        page.Dispose();
        try { Assert.True(ports.SearchToken.IsCancellationRequested); }
        finally
        {
            release.TrySetResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page("cat", 1, true, BooruUiFixtures.Post("42", preview: false))));
        }
        await pending;
        Assert.Equal(1, ports.SearchReads);
        Assert.Empty(page.Model.Search.View.Rows);
        Assert.False(page.Model.Search.CanSubmit);
        Assert.Equal(0, ports.PreviewReads);
        Assert.Equal(0, ports.LocalStateReads);
        Assert.Equal(0, ports.QueueLoads);
    }

    [Fact]
    public void EscapeElsewhereDoesNotClearTheDraftOrBecomeACloseAction()
    {
        RecordingPorts ports = new();
        using BooruPage page = new(ports.Services);
        page.ShowQuery("cat");
        KeyEventArgs escape = Escape();
        page.RaiseEvent(escape);
        Assert.False(escape.Handled);
        Assert.Equal("cat", page.Model.Search.View.Input);
        Assert.True(page.TryClearQuery());
        KeyEventArgs empty = Escape();
        page.RaiseEvent(empty);
        Assert.False(empty.Handled);
        Assert.Same(page.View, page.Content);
        AssertNoRemoteWork(ports);
    }

    [Fact]
    public async Task EscapeCancelsOnlyAnOpenRemovalQuestionOnTheWatcherTab()
    {
        RecordingPorts ports = new()
        {
            ListResult = Task.FromResult<IReadOnlyList<BooruWatcherView>>(new[] { BooruUiFixtures.Watcher("w1", "cat") })
        };
        using BooruPage page = new(ports.Services);
        await page.ActivateAsync();
        TabControl tabs = Assert.IsType<TabControl>(page.View.Body);
        TabItem[] segments = Assert.IsType<TabItem[]>(tabs.ItemsSource);
        tabs.SelectedItem = segments[1];
        page.Model.Watchers.RequestRemoval("w1");
        Assert.True(Assert.Single(page.Model.Watchers.View.Rows).ConfirmingRemoval);
        KeyEventArgs modified = Escape(KeyModifiers.Shift);
        page.RaiseEvent(modified);
        Assert.False(modified.Handled);
        Assert.True(Assert.Single(page.Model.Watchers.View.Rows).ConfirmingRemoval);
        KeyEventArgs escape = Escape();
        page.RaiseEvent(escape);
        Assert.True(escape.Handled);
        Assert.False(Assert.Single(page.Model.Watchers.View.Rows).ConfirmingRemoval);
        AssertNoRemoteWork(ports);
    }

    [Fact]
    public void StaticHostDeclarationKeepsActivationExplicitAndLifetimeFinalNotDetachDriven()
    {
        // Source/API assertion only: not evidence of native layout, focus, bitmap or timer behavior.
        string source = PageSource();
        Assert.Contains("Content = _view;", source);
        Assert.Contains("new BooruWorkbenchView(model, ownsModel: true, buildLayout: BuildPageLayout)", source);
        Assert.Contains("_disposed ? Task.CompletedTask : _view.ActivateAsync()", source);
        Assert.Contains("if (_disposed) return;", source);
        Assert.Contains("KeyDown -= OnKeyDown;", source);
        Assert.Equal(1, source.Split("_view.Dispose();", StringSplitOptions.None).Length - 1);
        foreach (string forbidden in new[] { "WindowChrome.", "DialogFrame.", "DialogFit.", "Close();", "CloseButton", "BackButton",
            "OnAttachedToVisualTree", "OnDetachedFromVisualTree", "AttachedToVisualTree +=", "DetachedFromVisualTree +=",
            "SubmitAsync(", "RefreshAsync(", "CheckNowAsync(", "RetryAsync(", "SourceAdmission", "ShowBooruTools",
            "Texts.Changed", "DispatcherTimer", "Bitmap", "new BooruWorkbenchWindow" })
            Assert.DoesNotContain(forbidden, source);
    }

    private static KeyEventArgs Escape(KeyModifiers modifiers = KeyModifiers.None) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, KeyModifiers = modifiers };

    private static void AssertNoRemoteWork(RecordingPorts ports)
    {
        Assert.Equal(0, ports.SearchReads);
        Assert.Equal(0, ports.PostReads);
        Assert.Equal(0, ports.QueueLoads);
        Assert.Equal(0, ports.LocalStateReads);
        Assert.Equal(0, ports.PreviewReads);
        Assert.Equal(0, ports.WatcherCommands);
    }

    private static string PageSource([CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../Desktop/Views/Booru/BooruPage.cs")));

    /// <summary>Real port implementations with subscription counters; no uninitialized object or dynamic mock.</summary>
    private sealed class RecordingPorts : IBooruSearchPort, IBooruQueuePort, IBooruWatcherPort, IBooruPreviewPort
    {
        private Action? _localChanged, _watchersChanged;
        internal RecordingPorts() => Services = new(this, this, this, this, new BooruManualClock());
        internal BooruWorkbenchServices Services { get; }
        public string ProviderId => "danbooru";
        public string ProviderName => "Danbooru";
        internal int QueueAdds, QueueRemoves, WatcherAdds, WatcherRemoves;
        internal int SearchReads, PostReads, QueueLoads, LocalStateReads, PreviewReads, ListReads, CatalogReads, WatcherCommands;
        internal int QueueSubscribers => _localChanged?.GetInvocationList().Length ?? 0;
        internal int WatcherSubscribers => _watchersChanged?.GetInvocationList().Length ?? 0;
        internal CancellationToken ListToken, SearchToken;
        internal Task<IReadOnlyList<BooruWatcherView>>? ListResult { get; init; }
        internal Task<BooruCallResult<BooruResultPage>>? SearchResult { get; init; }

        public event Action? LocalStatesChanged
        {
            add { QueueAdds++; _localChanged += value; }
            remove { QueueRemoves++; _localChanged -= value; }
        }
        public event Action? Changed
        {
            add { WatcherAdds++; _watchersChanged += value; }
            remove { WatcherRemoves++; _watchersChanged -= value; }
        }
        internal void RaiseLocalChanges() => _localChanged?.Invoke();
        internal void RaiseWatcherChanges() => _watchersChanged?.Invoke();
        public Task<IReadOnlyList<BooruWatcherView>> ListAsync(CancellationToken cancellationToken)
        {
            ListReads++; ListToken = cancellationToken;
            return ListResult ?? Task.FromResult<IReadOnlyList<BooruWatcherView>>(Array.Empty<BooruWatcherView>());
        }
        public Task<BooruCatalogUsageView?> GetCatalogUsageAsync(CancellationToken cancellationToken)
        {
            CatalogReads++;
            return Task.FromResult<BooruCatalogUsageView?>(null);
        }
        public Task<BooruCallResult<BooruResultPage>> SearchAsync(string canonicalQuery, int page, CancellationToken cancellationToken)
        {
            SearchReads++; SearchToken = cancellationToken;
            return SearchResult ?? throw new InvalidOperationException("No search was expected.");
        }
        public Task<BooruCallResult<BooruPostView>> ReadPostAsync(string postId, CancellationToken cancellationToken)
        {
            PostReads++; throw new InvalidOperationException("No post read was expected.");
        }
        public Task<BooruLoadResult> LoadOriginalAsync(BooruPostView post, BooruLoadOrigin origin, CancellationToken cancellationToken)
        {
            QueueLoads++; throw new InvalidOperationException("No queue admission was expected.");
        }
        public Task<IReadOnlyDictionary<string, BooruLocalState>> GetLocalStatesAsync(IReadOnlyList<string> postIds, CancellationToken cancellationToken)
        {
            LocalStateReads++; throw new InvalidOperationException("No local-state follow-up was expected.");
        }
        public Task<byte[]?> LoadPreviewAsync(BooruPostView post, CancellationToken cancellationToken)
        {
            PreviewReads++; throw new InvalidOperationException("No preview was expected.");
        }
        public Task<BooruWatcherCommandResult> CreateAsync(string canonicalQuery, CancellationToken cancellationToken) => UnexpectedWatcherCommand();
        public Task<BooruWatcherCommandResult> CheckNowAsync(string watcherId, CancellationToken cancellationToken) => UnexpectedWatcherCommand();
        public Task<BooruWatcherCommandResult> PauseAsync(string watcherId, CancellationToken cancellationToken) => UnexpectedWatcherCommand();
        public Task<BooruWatcherCommandResult> ResumeAsync(string watcherId, CancellationToken cancellationToken) => UnexpectedWatcherCommand();
        public Task<BooruWatcherCommandResult> RemoveAsync(string watcherId, CancellationToken cancellationToken) => UnexpectedWatcherCommand();
        public Task<BooruWatcherCommandResult> RestartFromNowAsync(string watcherId, CancellationToken cancellationToken) => UnexpectedWatcherCommand();
        public Task<BooruWatcherCommandResult> ResetProviderCooldownAsync(CancellationToken cancellationToken) => UnexpectedWatcherCommand();
        private Task<BooruWatcherCommandResult> UnexpectedWatcherCommand()
        {
            WatcherCommands++; throw new InvalidOperationException("No watcher command was expected.");
        }
    }
}
