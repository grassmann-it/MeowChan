using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan;

// Post filters in the reader (plan P2 part B): the classifier, "Post filters…" in the View menu, "Hide post" and "Show
// post again" in the post menu (and as shortcuts on the selected post), and the notes in reply cards and quote previews.
// The HTML export reads the archive itself and never sees these verdicts, so it stays unfiltered.
public partial class ArchivePage
{
    private const string PostFiltersCommand = "reader.post-filters";
    private readonly PostFilterMatcher _postFilter = new();
    private PostFilterSession? _postFilters;
    private bool _postFilterDialogOpen;
    private PostFilterSnapshot? _appliedPostFilters;
    private TextBlock? _quotePreviewFilterNote;

    /// <summary>The reader's post filter classifier (test and smoke seam).</summary>
    internal PostFilterMatcher PostFilter => _postFilter;
    internal PostFilterDialog? PostFilterWindow { get; private set; }
    /// <summary>The latest dialog, hide or show-again operation (smoke seam).</summary>
    internal Task PostFilterTask { get; private set; } = Task.CompletedTask;

    /// <summary>The window's post filters. Without them (tests, smokes with a bare page) the reader filters nothing and
    /// offers neither "Post filters…" nor the post menu entries.</summary>
    internal PostFilterSession? PostFilters
    {
        get => _postFilters;
        set
        {
            if (ReferenceEquals(_postFilters, value)) return;
            if (_postFilters != null) _postFilters.Changed -= OnPostFiltersChanged;
            _postFilters = value;
            if (value != null)
            {
                value.Changed += OnPostFiltersChanged;
                RegisterViewOption(PostFiltersCommand, null, () => PostFilterTask = OpenPostFiltersAsync());
            }
            else _viewOptions.RemoveAll(option => option.CommandId == PostFiltersCommand);
            ApplyPostFilterSnapshot(value?.Current);
        }
    }

    partial void AttachPostFilter()
    {
        RegisterPostClassifier(_postFilter);
        // H is a real reader shortcut, not ListBox type-ahead. Route it before a post item
        // can consume the letter; the handler below still rejects native editors, popups and foreign top-levels.
        ArchiveReaderPane.AddHandler(KeyDownEvent, OnPostFilterKeyDown, RoutingStrategies.Tunnel);
        ArchiveReaderPane.AddHandler(KeyDownEvent, OnPostCopyLinkKeyDown, RoutingStrategies.Bubble);
    }

    private void OnPostFiltersChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => OnPostFiltersChanged(sender, e)); return; }
        if (_ended || !ReferenceEquals(sender, _postFilters)) return;
        ApplyPostFilterSnapshot(_postFilters?.Current);
    }

    // A reload of the same revision changes nothing; rules that neither applied nor apply to the open thread leave its
    // rows alone (the identity stays the identity, nothing is re-rendered).
    private void ApplyPostFilterSnapshot(PostFilterSnapshot? snapshot)
    {
        PostFilterSnapshot? previous = _appliedPostFilters;
        if (snapshot != null && previous != null && snapshot.Revision == previous.Revision && snapshot.State == previous.State) return;
        _appliedPostFilters = snapshot;
        ResourceId? open = _thread?.Summary.Id;
        bool before = open != null && _postFilter.IsActive(open);
        _postFilter.Update(snapshot);
        if (before || open != null && _postFilter.IsActive(open)) RequestProjectionRebuild();
    }

    private bool CanChangePostFilters => !_ended && !IsFilenameSearchOpen && _postFilters?.Current?.CanWrite == true;

    // ---- Post menu and shortcuts --------------------------------------------------------------------------------------

    /// <summary>"Hide post" and "Hide post and its replies", or "Show post again" for a post hidden by hand.</summary>
    private void AddPostFilterMenuItems(ContextMenuBuilder menu, ArchivePostRow row)
    {
        if (_postFilters is not { Current.CanWrite: true } filters || _thread == null) return;
        ResourceId thread = _thread.Summary.Id;
        string post = row.Id;
        menu.Section();
        if (filters.IsHidden(thread, post))
        {
            menu.Command("post.unhide", () => PostFilterTask = UnhidePostAsync(thread, post),
                available: CanChangePostFilters, availableWhen: () => CanChangePostFilters);
            return;
        }
        menu.Command("post.hide", () => PostFilterTask = HidePostAsync(thread, post, replies: false),
            available: CanChangePostFilters, availableWhen: () => CanChangePostFilters);
        menu.Item("post.hide-replies", Texts.T("postFilter.hideWithReplies"),
            () => { if (CanChangePostFilters) PostFilterTask = HidePostAsync(thread, post, replies: true); },
            available: CanChangePostFilters, availableWhen: () => CanChangePostFilters, icon: CommandCatalog.Get("post.hide").Icon);
    }

    private void OnPostFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || !CommandRouter.IsRoutingContextActive(ArchiveReaderPane, e.Source)
            || ReaderSelectionOwnsActivationKey(e) || IsReaderDialogOpen || !CanChangePostFilters || _thread == null
            || ArchivePosts.SelectedItem is not ArchivePostRow row || _thread.FindPostIndex(row.Id) < 0) return;
        ResourceId thread = _thread.Summary.Id;
        bool hidden = _postFilters!.IsHidden(thread, row.Id);
        if (!hidden && CommandBindings.Matches(e, "post.hide")) { e.Handled = true; PostFilterTask = HidePostAsync(thread, row.Id, replies: false); }
        else if (hidden && CommandBindings.Matches(e, "post.unhide")) { e.Handled = true; PostFilterTask = UnhidePostAsync(thread, row.Id); }
    }

    /// <summary>Hides one post by hand (and its replies with <paramref name="replies"/>). An earlier "Show" of it is undone.</summary>
    internal async Task<bool> HidePostAsync(ResourceId thread, string post, bool replies)
    {
        if (_postFilters is not { } filters || !CanChangePostFilters) return false;
        try { await filters.HidePostAsync(thread, post, replies, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            if (!_ended) Message(PostFilterSession.ErrorText(error), true);
            return false;
        }
        if (_ended) return false;
        // The explicit hide wins over an earlier reveal; the rebuild queued by the new rules picks this up.
        if (_thread?.Summary.Id == thread && _revealedPosts.Remove(post)) RequestProjectionRebuild();
        Message(Texts.T("postFilter.hideDone", post));
        return true;
    }

    internal async Task<bool> UnhidePostAsync(ResourceId thread, string post)
    {
        if (_postFilters is not { } filters || !CanChangePostFilters) return false;
        try { await filters.UnhidePostAsync(thread, post, _lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            if (!_ended) Message(PostFilterSession.ErrorText(error), true);
            return false;
        }
        if (!_ended) Message(Texts.T("postFilter.unhideDone", post));
        return !_ended;
    }

    // ---- Dialog -------------------------------------------------------------------------------------------------------

    internal async Task OpenPostFiltersAsync()
    {
        Window? owner = TopLevel.GetTopLevel(ArchiveReaderPane) as Window ?? TopLevel.GetTopLevel(this) as Window;
        if (_ended || _postFilterDialogOpen || _postFilters is not { } filters || IsFilenameSearchOpen || owner == null
            || !owner.IsVisible || owner.OwnedWindows.Any(child => child.IsVisible)) return;
        _postFilterDialogOpen = true;
        try
        {
            PostFilterDialogModel model = new(filters, _thread?.Summary.Id);
            PostFilterDialog dialog = new(model, _lifetime.Token)
            {
                Width = Math.Max(360, Math.Min(720, owner.ClientSize.Width - 32)),
                Height = Math.Max(360, Math.Min(640, owner.ClientSize.Height - 32))
            };
            PostFilterWindow = dialog;
            using CancellationTokenRegistration ending = _lifetime.Token.Register(() =>
                Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) dialog.Close(); }));
            Task shown = dialog.ShowDialog(owner);
            // Another window of the profile may have changed the file since the reader loaded it.
            await model.LoadAsync(_lifetime.Token);
            await shown;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_ended) Message(Texts.T("postFilter.loadFailed", error.Message), true); }
        finally
        {
            PostFilterWindow = null;
            _postFilterDialogOpen = false;
        }
    }

    // ---- Notes --------------------------------------------------------------------------------------------------------

    // The filter's verdict on a post of the open read: hidden or a placeholder (not merely highlighted).
    private bool PostFilterConceals(CapturedPost post) => _thread != null
        && _postFilter.VerdictOf(_thread, post.PostId) is { Visibility: not ReaderPostVisibility.Shown };

    /// <summary>A reply card of a post the filter hides says "hidden" instead of quoting it, unless the post was shown
    /// explicitly; the card itself stays, so the count from the archive keeps matching.</summary>
    private string PostFilterReplyExcerpt(CapturedPost post, string excerpt) =>
        PostFilterConceals(post) && !_revealedPosts.Contains(post.PostId) ? Texts.T("postFilter.hiddenReply") : excerpt;

    /// <summary>The quote preview of a filtered post ends with "Hidden by your post filter".</summary>
    private void UpdateQuotePreviewFilterNote(CapturedPost post)
    {
        bool show = PostFilterConceals(post);
        if (_quotePreviewFilterNote == null)
        {
            if (!show || QuotePreviewBody.Parent is not Panel panel) return;
            _quotePreviewFilterNote = new TextBlock { Name = "QuotePreviewFilterNote", TextWrapping = TextWrapping.Wrap, FontStyle = FontStyle.Italic };
            _quotePreviewFilterNote.Classes.Add("workspace-meta");
            panel.Children.Insert(panel.Children.IndexOf(QuotePreviewBody) + 1, _quotePreviewFilterNote);
        }
        _quotePreviewFilterNote.IsVisible = show;
        if (!show) return;
        string note = Texts.T("postFilter.previewNote");
        _quotePreviewFilterNote.Text = note;
        string? name = AutomationProperties.GetName(QuotePreviewPopup);
        AutomationProperties.SetName(QuotePreviewPopup, string.IsNullOrEmpty(name) ? note : name + " · " + note);
    }

    /// <summary>The note line of the quote preview is visible (smoke seam).</summary>
    internal bool QuotePreviewShowsFilterNote => _quotePreviewFilterNote?.IsVisible == true && QuotePreviewPopup.IsOpen;
}
