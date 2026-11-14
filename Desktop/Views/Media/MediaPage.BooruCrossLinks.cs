using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

/// <summary>
/// Local-only bridge between the Danbooru workbench and the media library. The thread identity is part of
/// every lookup so a post number that merely occurs in another file name can never select the wrong tile.
/// </summary>
public sealed partial class MediaPage
{
    /// <summary>Exact saved-post identity used by the host and by the cross-link tests.</summary>
    internal static bool IsSavedBooruEntry(MediaLibraryEntry entry, string postId) =>
        entry.Presence == MediaLibraryPresence.Present
        && entry.BooruPostId == postId
        && entry.Thread == BooruMediaIdentity.Thread(postId)
        && entry.AssetId is { Length: > 0 };

    /// <summary>
    /// Applies the source and post-id filters for a local Danbooru original and focuses its one exact tile.
    /// No catalog/provider request is made here; the host has already admitted the local post.
    /// </summary>
    internal async Task<bool> ShowBooruPostAsync(string postId)
    {
        ResourceId thread = BooruMediaIdentity.Thread(postId);
        if (!BooruMediaIdentity.IsBooruThread(thread)) return false;
        if (_snapshot == null || !_snapshot.Entries.Any(entry => IsSavedBooruEntry(entry, postId)))
        {
            ShowSelectionNote(Texts.T("booru.crossLinks.missingLocal", postId));
            return false;
        }

        // The source choice is rebuilt from the current local snapshot. Setting it while localizing avoids
        // the combo's own event running with half of the provenance filter still unset.
        _localizing = true;
        try
        {
            RenderSourceOptions();
            Choice? danbooru = (_sourceFilter.ItemsSource as System.Collections.IEnumerable)?.Cast<Choice>()
                .FirstOrDefault(choice => choice.Value == BooruMediaIdentity.Source);
            if (danbooru == null)
            {
                ShowSelectionNote(Texts.T("booru.crossLinks.missingLocal", postId));
                return false;
            }
            _sourceFilter.SelectedItem = danbooru;
            RenderBoardOptions();
            _boardFilter.SelectedIndex = 0;
            _nameFilter.SetTextSilently(postId);
            _threadFilter = (thread, Texts.T("booru.media.tile.post", postId));
        }
        finally { _localizing = false; }

        FilterEdited();
        await PresentTask.ConfigureAwait(true);
        return await FocusExactEntryAsync(entry => IsSavedBooruEntry(entry, postId),
            Texts.T("booru.crossLinks.missingLocal", postId)).ConfigureAwait(true);
    }
}
