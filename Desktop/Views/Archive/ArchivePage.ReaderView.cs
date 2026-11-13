using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

// Reader foundation (plan P0): the projection "thread -> shown order" that pages, navigation, the reading mark and every
// explicit jump work against, the "View" menu and the media switches as its first users. With nothing switched on the
// projection is the identity: built synchronously, the same rows and the same allocations as before.
public partial class ArchivePage
{
    internal const int ReaderPageSizeDefault = PageSize;
    internal const int ReaderPageSizeMinimum = 20;
    internal const int ReaderPageSizeMaximum = 200;
    // Conversation indentation: a local left margin on the post container, set before it is measured.
    internal const double ReaderIndentStep = ReaderNavigationLayout.ConversationIndentStep;
    internal const int ReaderIndentMaximumLevels = 16;
    internal const double ReaderIndentMaximumShare = ReaderNavigationLayout.ConversationIndentMaximumShare;
    // Mirrors the ListBoxItem margin of the post list's style ("6,4"); a local value replaces the whole margin.
    private static readonly Thickness ReaderPostCardMargin = new(6, 4);

    private readonly ReaderViewState _readerView = new();
    private readonly List<IReaderPostClassifier> _postClassifiers = new();
    private IReaderPostOrdering? _postOrdering;
    private readonly List<ReaderViewOption> _viewOptions = new();
    private sealed record ReaderViewOption(string CommandId, Func<bool>? IsChecked, Action Toggle);
    // Fixed menu order; options registered later (P2, P4) slot in here, unknown ones go last.
    private static readonly string[] ReaderViewOptionOrder =
        { "reader.view.hide-media", "reader.view.media-only", "reader.view.conversation", "reader.post-filters" };

    private ReaderPostProjection? _projection;
    private ReaderPostProjection? _announcedProjection;
    private ReaderQuoteGraph? _quoteGraph;
    private int _projectionRequest;
    private CancellationTokenSource? _projectionCancellation;
    private TaskCompletionSource? _projectionReady;
    private ReaderPostProjection.ProjectionInputs? _pendingProjectionInputs;
    private ReaderPostProjection.ProjectionInputs? _failedProjectionInputs;
    // After the next projection is in place, the page is the one that holds this post.
    private string? _projectionPagePost;
    // A view change keeps the post that was at the top (or selected) where it was.
    private ReaderAnchor? _projectionAnchor;
    private bool _projectionAnchorFocus;
    private bool _projectionRebuildQueued;
    private readonly HashSet<string> _revealedPosts = new(StringComparer.Ordinal);
    private ResourceId? _revealedThread;
    private int _readerPageSize = ReaderPageSizeDefault;

    /// <summary>The "View" switches; global for every thread, for this session only.</summary>
    internal ReaderViewState ReaderView => _readerView;
    /// <summary>The shown order of the open thread; null while a new one is still being prepared.</summary>
    internal ReaderPostProjection? Projection => _projection != null && _projection.Matches(_thread) ? _projection : null;
    /// <summary>Raised after the shown order of the open thread changed (a new thread, a view switch, a reveal).</summary>
    internal event EventHandler? ProjectionChanged;
    internal bool IsProjectionPending => _projectionReady != null || _projectionRebuildQueued;
    /// <summary>The anchor restoration after the latest view change (smoke seam).</summary>
    internal Task ReaderViewTask { get; private set; } = Task.CompletedTask;

    /// <summary>Posts per reader page (P5 maps its setting here). Changing it keeps the post at the top in view.</summary>
    internal int ReaderPageSize
    {
        get => _readerPageSize;
        set => ApplyReaderDisplayPreferences(ReaderThumbnailWidth, ReaderDateStyle, value);
    }

    // Implemented by the post filters (P2) and the conversation view (P4) in their own files.
    partial void AttachPostFilter();
    partial void AttachConversationView();

    private void InitializeReaderView()
    {
        RegisterPostClassifier(new ReaderMediaOnlyClassifier(_readerView));
        RegisterViewOption("reader.view.hide-media", () => _readerView.HideMedia, () => _readerView.HideMedia = !_readerView.HideMedia);
        RegisterViewOption("reader.view.media-only", () => _readerView.MediaOnly, () => _readerView.MediaOnly = !_readerView.MediaOnly);
        _readerView.Changed += (_, _) => RequestProjectionRebuild();
        // On the pane itself, so the shortcuts work in the embedded and in the detached reader.
        ArchiveReaderPane.AddHandler(KeyDownEvent, OnReaderViewKeyDown, RoutingStrategies.Bubble);
        LocalizeReaderView();
    }

    private void LocalizeReaderView()
    {
        string label = Texts.T("reader.view.menu");
        Button open = ReaderViewOpen;
        open.Content = label;
        ToolTip.SetTip(open, label);
        Avalonia.Automation.AutomationProperties.SetName(open, label);
    }

    internal void RegisterPostClassifier(IReaderPostClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        if (_postClassifiers.Contains(classifier)) return;
        _postClassifiers.Add(classifier);
        RequestProjectionRebuild();
    }

    internal void RegisterPostOrdering(IReaderPostOrdering ordering)
    {
        ArgumentNullException.ThrowIfNull(ordering);
        if (_postOrdering != null && !ReferenceEquals(_postOrdering, ordering))
            throw new InvalidOperationException("The reader has one post ordering at most.");
        _postOrdering = ordering;
        RequestProjectionRebuild();
    }

    /// <summary>One entry of the "View" menu, as a check item (or a plain item when <paramref name="isChecked"/> is
    /// null, like "Post filters…"). The command needs no default gesture; a user shortcut toggles it too.</summary>
    internal void RegisterViewOption(string commandId, Func<bool>? isChecked, Action toggle)
    {
        ArgumentNullException.ThrowIfNull(toggle);
        CommandCatalog.Get(commandId);
        _viewOptions.RemoveAll(option => option.CommandId == commandId);
        _viewOptions.Add(new ReaderViewOption(commandId, isChecked, toggle));
    }

    internal IReadOnlyList<string> ReaderViewOptionIds => OrderedViewOptions().Select(option => option.CommandId).ToArray();

    private IEnumerable<ReaderViewOption> OrderedViewOptions() => _viewOptions
        .Select((option, index) => (option, index))
        .OrderBy(item => Array.IndexOf(ReaderViewOptionOrder, item.option.CommandId) is var rank and >= 0 ? rank : ReaderViewOptionOrder.Length)
        .ThenBy(item => item.index).Select(item => item.option);

    private void OnReaderViewMenuClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || sender is not Control origin) return;
        ContextMenu menu = BuildReaderViewMenu();
        origin.ContextMenu = menu;
        menu.Open(origin);
    }

    internal ContextMenu BuildReaderViewMenu()
    {
        ContextMenu menu = new();
        foreach (ReaderViewOption option in OrderedViewOptions())
        {
            ReaderViewOption frozen = option;
            MenuItem item = CommandBindings.CreateMenuItem(frozen.CommandId, () => { if (!_ended) frozen.Toggle(); },
                () => !_ended && !IsFilenameSearchOpen);
            item.Tag = frozen.CommandId;
            if (frozen.IsChecked != null)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = frozen.IsChecked();
            }
            menu.Items.Add(item);
        }
        return menu;
    }

    internal bool ToggleReaderViewOption(string commandId)
    {
        if (_ended || IsFilenameSearchOpen) return false;
        ReaderViewOption? option = _viewOptions.FirstOrDefault(candidate => candidate.CommandId == commandId);
        if (option == null) return false;
        option.Toggle();
        return true;
    }

    private void OnReaderViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _ended || ShortcutService.IsCapturing || IsReaderDialogOpen || _thread == null) return;
        foreach (ReaderViewOption option in OrderedViewOptions())
        {
            if (!CommandBindings.Matches(e, option.CommandId)) continue;
            e.Handled = true;
            option.Toggle();
            return;
        }
    }

    /// <summary>Recomputes the shown order on the next dispatcher turn (several requests become one), keeping the post at
    /// the top in place. Classifiers call this when their rules change.</summary>
    internal void RequestProjectionRebuild()
    {
        // Without an open thread there is nothing to rebuild: the next thread resolves its view when it is shown.
        if (_ended || _thread == null) return;
        if (_readerSettingsApply == null && _projectionAnchor == null)
        {
            _projectionAnchor = CaptureReaderAnchor();
            _projectionAnchorFocus = ArchivePosts.IsKeyboardFocusWithin;
        }
        if (_projectionRebuildQueued) return;
        _projectionRebuildQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _projectionRebuildQueued = false;
            if (_ended || _thread == null)
            {
                _projectionAnchor = null;
                _readerSettingsApply?.Completion.TrySetResult();
                _readerSettingsApply = null;
                return;
            }
            ReaderPostProjection? ready = ResolveProjection(out bool pending);
            if (!pending && ready != null) ApplyProjection(ready);
        }, DispatcherPriority.Background);
    }

    /// <summary>For explicit jumps (quote, find, gallery "Show in thread", bookmark, reading position): a post the view
    /// hides, or shows as a placeholder, is shown with a "Shown although this view hides it" line.</summary>
    internal async Task RevealPostAsync(string postId)
    {
        ArgumentNullException.ThrowIfNull(postId);
        if (_projectionReady is { } pending)
        {
            ResourceId? thread = _thread?.Summary.Id;
            await pending.Task;
            if (_ended || _thread?.Summary.Id != thread) return;
        }
        if (_ended || IsFilenameSearchOpen) return;
        RevealPostNow(postId);
    }

    // Synchronous part of a reveal: the classification is kept, only revealing and removing run again.
    private bool RevealPostNow(string postId)
    {
        if (_thread == null || _thread.FindPostIndex(postId) < 0) return false;
        ReaderPostProjection? projection = Projection;
        if (projection == null)
        {
            // Still preparing: the finished projection takes the reveal over, and its page holds the post.
            if (_projectionReady == null) return false;
            _revealedPosts.Add(postId);
            _projectionPagePost = postId;
            return true;
        }
        if (!projection.IsConcealed(postId)) return false;
        _revealedPosts.Add(postId);
        _projection = projection.WithRevealed(_revealedPosts);
        _projectionPagePost = postId;
        RenderThread();
        return true;
    }

    private void OnReaderStubShowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ArchivePostRow row }) return;
        e.Handled = true;
        SetPostRevealed(row.Id, true);
    }

    private void OnReaderHideAgainClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ArchivePostRow row }) return;
        e.Handled = true;
        SetPostRevealed(row.Id, false);
    }

    internal void SetPostRevealed(string postId, bool revealed)
    {
        if (_ended || IsFilenameSearchOpen || _busy || _thread == null || Projection is not { IsIdentity: false } projection) return;
        if (revealed ? !projection.IsConcealed(postId) || !_revealedPosts.Add(postId) : !_revealedPosts.Remove(postId)) return;
        ++_readerInteractionRevision;
        // The post keeps its place on the screen; hidden again, the next shown post takes that place.
        ReaderAnchor? anchor = CapturePostAnchor(postId) ?? CaptureReaderAnchor();
        bool focus = ArchivePosts.IsKeyboardFocusWithin;
        _projection = projection.WithRevealed(_revealedPosts);
        _projectionPagePost = postId;
        RenderThread();
        if (anchor != null)
            ReaderViewTask = RestoreReaderAnchorAsync(anchor with { SelectedPostId = revealed ? postId : null }, restoreFocus: focus);
    }

    private ReaderAnchor? CapturePostAnchor(string postId)
    {
        if (_thread == null) return null;
        ListBox list = ArchivePosts;
        ArchivePostRow? row = (list.ItemsSource as IEnumerable<ArchivePostRow>)?.FirstOrDefault(candidate => candidate.Id == postId);
        if (row == null || list.ContainerFromItem(row) is not Control container
            || container.TranslatePoint(default, list) is not Point position) return null;
        return new ReaderAnchor(_thread.Summary.Id, postId, position.Y, (list.SelectedItem as ArchivePostRow)?.Id);
    }

    // The projection RenderThread shows. The identity is synchronous; anything else is built in the background (the
    // previous projection of the same read stays meanwhile; a new read shows "Preparing the view…").
    private ReaderPostProjection? ResolveProjection(out bool pending)
    {
        pending = false;
        ArchivedThreadView thread = _thread!;
        ResourceId id = thread.Summary.Id;
        if (_revealedThread != id) { _revealedPosts.Clear(); _revealedThread = id; }
        List<IReaderPostClassifier>? active = null;
        foreach (IReaderPostClassifier classifier in _postClassifiers)
            if (classifier.IsActive(id)) (active ??= new()).Add(classifier);
        IReaderPostOrdering? ordering = _postOrdering?.IsActive(id) == true ? _postOrdering : null;
        if (active == null && ordering == null) return UseIdentity(thread);
        IReadOnlyList<IReaderPostClassifier> classifiers = (IReadOnlyList<IReaderPostClassifier>?)active ?? Array.Empty<IReaderPostClassifier>();
        ReaderPostProjection.ProjectionInputs inputs = ReaderPostProjection.ProjectionInputs.For(thread, classifiers, ordering);
        // A view whose build failed shows every post until its inputs change (no rebuild loop).
        if (inputs.Equals(_failedProjectionInputs)) return UseIdentity(thread);
        if (_projection is { IsIdentity: false } current && current.Matches(thread) && current.Inputs.Equals(inputs))
        {
            CancelProjectionBuild();
            _projection = current.WithRevealed(_revealedPosts);
            return _projection;
        }
        if (_projectionReady == null || _pendingProjectionInputs == null || !_pendingProjectionInputs.Equals(inputs))
            StartProjectionBuild(thread, classifiers.ToArray(), ordering, inputs);
        pending = true;
        return _projection != null && _projection.Matches(thread) ? _projection : null;
    }

    private ReaderPostProjection UseIdentity(ArchivedThreadView thread)
    {
        CancelProjectionBuild();
        if (_projection is not { IsIdentity: true } current || !current.Matches(thread)) _projection = ReaderPostProjection.Identity(thread);
        return _projection;
    }

    private void StartProjectionBuild(ArchivedThreadView thread, IReaderPostClassifier[] classifiers, IReaderPostOrdering? ordering,
        ReaderPostProjection.ProjectionInputs inputs)
    {
        int request = ++_projectionRequest;
        _projectionCancellation?.Cancel();
        _projectionCancellation?.Dispose();
        CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _projectionCancellation = cancellation;
        _pendingProjectionInputs = inputs;
        _projectionReady ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        HashSet<string> revealed = new(_revealedPosts, StringComparer.Ordinal);
        ReaderQuoteGraph? graph = _quoteGraph?.Matches(thread) == true ? _quoteGraph : null;
        bool needsGraph = ordering != null || classifiers.Any(classifier => classifier.UsesQuoteGraph);
        _ = BuildProjectionAsync(request, thread, graph, needsGraph, classifiers, ordering, revealed, inputs, cancellation.Token);
    }

    private async Task BuildProjectionAsync(int request, ArchivedThreadView thread, ReaderQuoteGraph? graph, bool needsGraph,
        IReaderPostClassifier[] classifiers, IReaderPostOrdering? ordering, HashSet<string> revealed,
        ReaderPostProjection.ProjectionInputs inputs, CancellationToken token)
    {
        try
        {
            (ReaderPostProjection built, ReaderQuoteGraph? usedGraph) = await Task.Run(() =>
            {
                ReaderQuoteGraph? quotes = needsGraph ? graph ?? ReaderQuoteGraph.Build(thread, token) : null;
                return (ReaderPostProjection.Build(thread, quotes, classifiers, ordering, revealed, token), quotes);
            }, token);
            if (_ended || request != _projectionRequest) return;
            if (usedGraph != null) _quoteGraph = usedGraph;
            FinishProjectionBuild();
            ApplyProjection(built.WithRevealed(_revealedPosts));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_ended || request != _projectionRequest) return;
            Log.Warning(error, "The reader view for {Thread} could not be built; showing every post.", thread.Summary.Id);
            _failedProjectionInputs = inputs;
            FinishProjectionBuild();
            string message = ordering != null ? Texts.T("reader.conversation.failed", error.Message) : error.Message;
            Message(message, true);
            ApplyProjection(ReaderPostProjection.Identity(thread));
        }
        finally
        {
            if (request == _projectionRequest) FinishProjectionBuild();
        }
    }

    private void FinishProjectionBuild()
    {
        DiscardStaleReaderSettingsApply();
        if (_projectionReady is not { } ready) return;
        _projectionReady = null;
        _pendingProjectionInputs = null;
        _projectionCancellation?.Dispose();
        _projectionCancellation = null;
        ready.TrySetResult();
    }

    private void CancelProjectionBuild()
    {
        if (_projectionReady == null) return;
        ++_projectionRequest;
        _projectionCancellation?.Cancel();
        FinishProjectionBuild();
    }

    private Task AwaitProjectionAsync() => _projectionReady?.Task ?? Task.CompletedTask;

    /// <summary>Waits until every queued or running view change is in place (smoke and test seam).</summary>
    internal async Task WaitForReaderViewAsync()
    {
        for (int pass = 0; pass < 200 && (_projectionRebuildQueued || _projectionReady != null); pass++)
        {
            if (_projectionReady is { } ready) await ready.Task;
            else await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        await ReaderViewTask;
    }

    private void ApplyProjection(ReaderPostProjection projection)
    {
        if (_ended || _thread == null || !projection.Matches(_thread)) return;
        if (_readerSettingsApply != null)
        {
            // A previous worker may finish between the setters and the queued turn. Retain its result, but only
            // commit settings against the current inputs; never restore against an intermediate conversation tree.
            _projection = projection;
            if (_projectionRebuildQueued) return;
            ReaderPostProjection? final = ResolveProjection(out bool pending);
            if (pending || final == null) return;
            projection = final;
        }
        ReaderSettingsApply? settings = TakeReaderSettingsApply(projection);
        ReaderAnchor? anchor = settings == null ? _projectionAnchor : null;
        bool focus = _projectionAnchorFocus;
        _projectionAnchor = null;
        _projectionAnchorFocus = false;
        if (anchor != null && anchor.Thread != _thread.Summary.Id) anchor = null;
        _projection = projection;
        if (anchor != null && _projectionPagePost == null) _projectionPagePost = anchor.PostId;
        RenderThread();
        if (settings != null)
        {
            ReaderViewTask = RestoreReaderSettingsAsync(settings);
            return;
        }
        if (anchor == null || Projection is not { Count: > 0 } shown) return;
        int nearest = shown.NearestPosition(anchor.PostId);
        if (nearest < 0) return;
        string? selected = anchor.SelectedPostId != null && shown.PositionOf(anchor.SelectedPostId) >= 0 ? anchor.SelectedPostId : null;
        ReaderViewTask = RestoreReaderAnchorAsync(anchor with { PostId = shown[nearest].Post.PostId, SelectedPostId = selected },
            restoreFocus: focus);
    }

    // Called at the end of RenderThread: listeners learn about a new shown order once, after its rows exist.
    private void AnnounceProjection()
    {
        DiscardStaleReaderSettingsApply();
        ReaderPostProjection? shown = Projection;
        if (ReferenceEquals(shown, _announcedProjection)) return;
        _announcedProjection = shown;
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // Shown posts while a thread is open; 0 while a new view is still being prepared.
    private int ShownPostCount => _thread == null ? 0 : Projection?.Count ?? (_projectionReady != null ? 0 : _thread.PostCount);

    private string ReaderViewStatus(ReaderPostProjection? projection)
    {
        int concealed = projection is { IsIdentity: false } ? projection.HiddenCount + projection.StubCount : 0;
        return concealed > 0 ? " · " + Texts.Count("reader.view.hiddenCount", concealed, concealed) : string.Empty;
    }

    private static string ReaderStubText(string postId, string? reason)
    {
        string text = Texts.T("reader.view.stub", postId, reason ?? string.Empty);
        return string.IsNullOrEmpty(reason) ? text.TrimEnd().TrimEnd('·').TrimEnd() : text;
    }

    internal static Thickness ReaderPostIndentMargin(int depth, double listWidth)
    {
        double indent = ReaderNavigationLayout.ConversationIndent(depth, listWidth, ReaderNavigationLayout.CardMaxWidth);
        return new Thickness(ReaderPostCardMargin.Left + indent, ReaderPostCardMargin.Top, ReaderPostCardMargin.Right, ReaderPostCardMargin.Bottom);
    }

    // Placeholder, highlight and indentation of a (possibly recycled) post container, before it is measured.
    private void DecorateReaderPost(Control container, ArchivePostRow? row)
    {
        container.Classes.Set("reader-stub", row?.IsStub == true);
        container.Classes.Set("reader-highlight", row?.IsHighlighted == true);
        if (row is { Depth: > 0 }) container.Margin = ReaderPostIndentMargin(row.Depth, ArchivePosts.Bounds.Width);
        else if (container.IsSet(Layoutable.MarginProperty)) container.ClearValue(Layoutable.MarginProperty);
    }
}
