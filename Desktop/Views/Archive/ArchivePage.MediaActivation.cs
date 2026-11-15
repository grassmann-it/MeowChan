using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;

namespace MeowChan;

// Space and Enter on the selected post's medium (ReaderMediaActivation): the page gathers the facts of the post's media, the pure resolver picks the medium and what happens to it.
public partial class ArchivePage
{
    internal const string ActivateMediumCommandId = "reader.media.activate";

    /// <summary>The facts of the selected post's media for the keys (rows in position order, with their realized views).</summary>
    private sealed record PostMediaSnapshot(List<ArchiveAttachmentRow> Rows, List<ReaderPostMedium> Media, ArchiveAttachmentView?[] Views,
        int? SidebarIndex, bool SidebarLive, bool SidebarPlaying, int? FocusedIndex);

    private PostMediaSnapshot? SnapshotPostMedia(ArchivePostRow post, Control? source, bool videosOnly)
    {
        if (!post.ShowsMedia || post.Attachments.Count == 0 || _thread == null) return null;
        ReaderInlineVideoOptions options = _inlineVideo.Options;
        List<ArchiveAttachmentRow> rows = post.Attachments.OrderBy(row => row.Attachment.Position).ToList();
        ArchiveAttachmentView?[] views = new ArchiveAttachmentView?[rows.Count];
        if (ArchivePosts.ContainerFromItem(post) is Control container)
            foreach (ArchiveAttachmentView view in container.GetVisualDescendants().OfType<ArchiveAttachmentView>())
            {
                int found = rows.FindIndex(row => ReferenceEquals(row, view.DataContext));
                if (found >= 0) views[found] = view;
            }
        // The manifest scan decides whether a saved file is there; until it is known a not deleted medium counts (the gallery path waits for it).
        Task<IReadOnlyList<ArchiveAssetRow>> ready = _mediaReady.Task;
        IReadOnlyList<ArchiveAssetRow>? assets = ready.IsCompletedSuccessfully ? ready.Result : null;
        ResourceId resource = _thread.Summary.Id;
        List<ReaderPostMedium> media = new(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            ArchiveAttachmentRow row = rows[i];
            bool available = !row.Attachment.IsDeleted;
            if (available && assets != null)
                available = FindMatchingAsset(resource, post.Id, row.Attachment, assets) is { } match && File.Exists(match.Asset.Path);
            bool video = ReaderInlineVideoRules.IsVideo(row.Attachment.OriginalFileName ?? row.Attachment.MediaId);
            if (videosOnly) available &= ReaderInlineVideoRules.CanOffer(options, row);
            ReaderInlineVideoSlot? slot = views[i]?.InlineVideo;
            media.Add(new(video, available, slot is { IsLive: true }, slot?.IsPlaying == true));
        }
        int? sidebarIndex = null;
        if (_mediaSidebarShown && CurrentSidebarAttachment() is { } shown && rows.FindIndex(row => ReferenceEquals(row, shown)) is var at && at >= 0) sidebarIndex = at;
        int? focused = null;
        if (source != null)
            foreach (ArchiveAttachmentView view in source.GetSelfAndVisualAncestors().OfType<ArchiveAttachmentView>())
                if (rows.FindIndex(row => ReferenceEquals(row, view.DataContext)) is var index && index >= 0) { focused = index; break; }
        bool sidebarLive = _sidebarVideo is { IsOffered: true, IsLive: true };
        return new(rows, media, views, sidebarIndex, sidebarLive, sidebarLive && _sidebarVideo!.IsPlaying, focused);
    }

    /// <summary>Whether a saved reader or shell binding (not a video key, not the medium command itself) has this gesture: it wins over Space and Enter on a medium.</summary>
    private static bool ClaimedByOtherReaderCommand(KeyEventArgs e) => ClaimedByOtherReaderCommand(e, ShortcutService.Current);

    internal static bool ClaimedByOtherReaderCommand(KeyEventArgs e, EffectiveShortcutBindings bindings)
    {
        return bindings.Gestures.Keys.Any(id => id != ActivateMediumCommandId && bindings.Matches(e, id) && !ReaderVideoKeys.IsVideoCommand(id)
            && EffectiveShortcutBindings.ExecutionContexts(id).Any(context => context is "reader" or "shell"));
    }

    private bool TryActivatePostMedium(KeyEventArgs e, ArchivePostRow post, Control source)
    {
        // A held key repeats: nothing happens until it is released (or while the gallery opens), so Space never flickers play and pause.
        if (ReaderMediaActivation.IsRepeat(_readerMediaHeldKeys, e.Key, _readerMediaActivationCancellation != null))
        {
            _readerMediaHeldKeys.Add(e.Key);
            e.Handled = true;
            return true;
        }
        PostMediaSnapshot? snapshot = SnapshotPostMedia(post, source, videosOnly: false);
        if (snapshot == null || ReaderMediaActivation.Choose(snapshot.Media, snapshot.SidebarIndex, snapshot.SidebarLive, snapshot.SidebarPlaying, snapshot.FocusedIndex) is not { } choice)
            return false;
        ArchiveAttachmentRow row = snapshot.Rows[choice.Index];
        ArchiveAttachmentView? view = snapshot.Views[choice.Index];
        ReaderInlineVideoOptions options = _inlineVideo.Options;
        ReaderMediumOutcome outcome = ReaderMediaActivation.Decide(choice, snapshot.Media[choice.Index], options, _mediaSidebarShown,
            snapshot.SidebarIndex == choice.Index, ReaderInlineVideoRules.CanOfferInPost(options, row) && view != null,
            ReaderInlineVideoRules.CanExpand(options, row) && view != null, OpenGallery != null);
        if (outcome == ReaderMediumOutcome.None) return false;
        _readerMediaHeldKeys.Add(e.Key);
        e.Handled = true;
        switch (outcome)
        {
            case ReaderMediumOutcome.ToggleInSidebar:
                _ = _sidebarVideo?.ToggleAsync();
                break;
            case ReaderMediumOutcome.ToggleInPost:
                view?.ToggleInlineVideoByKey();
                break;
            case ReaderMediumOutcome.PlayInPost:
                view?.ActivateMedium();
                break;
            case ReaderMediumOutcome.ExpandInPost:
                view?.SetExpanded(!view.IsExpanded); // also ends the hover enlargement
                break;
            case ReaderMediumOutcome.ShowInSidebar:
                // The focus stays on the posts: a key never moves it into the sidebar's bar.
                if (snapshot.SidebarIndex == choice.Index && _sidebarVideo is { IsOffered: true } shown) _ = shown.PlayAsync();
                else if (!ShowTileInMediaSidebar(row)) ReaderMediaActivationTask = OpenMediumInGalleryAsync(source, post, row);
                break;
            default:
                ReaderMediaActivationTask = OpenMediumInGalleryAsync(source, post, row);
                break;
        }
        return true;
    }

    // The gallery at exactly this medium, with the post's own control as the origin (closing the gallery returns the focus there), once the local manifest is known.
    // A missing file or a changed thread opens nothing and says nothing.
    private async Task OpenMediumInGalleryAsync(Control origin, ArchivePostRow post, ArchiveAttachmentRow row)
    {
        if (_thread == null) return;
        ResourceId resource = _thread.Summary.Id;
        Task<IReadOnlyList<ArchiveAssetRow>> ready = _mediaReady.Task;
        try
        {
            IReadOnlyList<ArchiveAssetRow> assets = await ready.WaitAsync(_lifetime.Token);
            if (_ended || _thread?.Summary.Id != resource || !ReferenceEquals(ready, _mediaReady.Task) || !ReferenceEquals(ArchivePosts.SelectedItem, post)) return;
            ArchiveAssetRow? match = FindMatchingAsset(resource, post.Id, row.Attachment, assets);
            if (match == null || !File.Exists(match.Asset.Path)) return;
            await OpenThreadGalleryAsync(resource, new GalleryEntryKey(new GalleryAssetKey(resource, match.Manifest.Directory, match.Asset.AssetId), post.Id, row.Attachment.Position), origin);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (!_ended) { Message(exception.Message, true); }
    }
}
