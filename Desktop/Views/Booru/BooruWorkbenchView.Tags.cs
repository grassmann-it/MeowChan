using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

/// <summary>The source tags of a result card and what a tag does: add to or exclude from the search field, copy its name.</summary>
internal sealed partial class BooruWorkbenchView
{
    private readonly HashSet<string> _expandedTagPosts = new(StringComparer.Ordinal);

    private Control CreateTags(BooruPostView post) =>
        BooruTagChips.Create(post, _expandedTagPosts.Contains(post.PostId), new BooruTagChipActions(tag => AddTagToSearch(tag), ComposeTagMenu, ExpandTags));

    /// <summary>
    /// I8: a source tag chip of a result adds its tag to the search field (tag search) up to the parser's limit. Only the field
    /// changes; nothing is requested until Search. The outcome is said in the polite link note line.
    /// </summary>
    internal void AddTagToSearch(string tag, bool exclude = false)
    {
        if (_closed) return;
        string current = _model.Search.View.Mode == BooruInputMode.Tags ? _input.Text ?? string.Empty : string.Empty;
        string[] terms = current.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // An exclusion is the provider's "-tag" term. It replaces the same tag's inclusion (and the other way round): both would never match anything.
        string term = exclude ? "-" + tag : tag, opposite = exclude ? tag : "-" + tag;
        if (terms.Contains(term, StringComparer.Ordinal)) { SetLinkNote(("booru.ui.tags.alreadyInSearch", term, false)); return; }
        if (!BooruSourceTagSearch.Plan(new[] { tag }).IsValid) { SetLinkNote(("booru.ui.tags.unusable", tag, true)); return; }
        bool replaces = terms.Contains(opposite, StringComparer.Ordinal);
        if (!replaces && terms.Length >= BooruSourceTagSearch.MaximumTags)
        {
            SetLinkNote(("booru.ui.tags.searchFull", BooruSourceTagSearch.MaximumTags.ToString(Texts.Culture), true));
            return;
        }
        string query = string.Join(' ', replaces ? terms.Select(existing => existing == opposite ? term : existing) : terms.Append(term));
        // The parser has the last word (an exclusion alone is not a search of its own on every provider).
        if (exclude && BooruInput.Parse(BooruInputMode.Tags, query).Kind != BooruInputKind.Tags) { SetLinkNote(("booru.ui.tags.unusable", term, true)); return; }
        _model.Search.SetMode(BooruInputMode.Tags);
        _input.Text = query;
        _model.Search.SetInput(query);
        SetLinkNote(("booru.ui.tags.addedToSearch", term, false));
    }

    /// <summary>The menu of a source-tag chip: add it to the search, exclude it from the search, copy its name.</summary>
    internal ContextMenuBuilder? ComposeTagMenu(string tag)
    {
        if (_closed) return null;
        return new ContextMenuBuilder().Named(tag)
            .Item(MenuTagAdd, Texts.T("booru.ui.tags.menuAdd"), () => AddTagToSearch(tag), icon: "plus")
            .Item(MenuTagExclude, Texts.T("booru.ui.tags.menuExclude"), () => AddTagToSearch(tag, exclude: true), icon: "minus")
            .Section()
            .Command("tags.copy-name", () => _ = CopyTagNameAsync(tag));
    }

    private async Task CopyTagNameAsync(string tag)
    {
        if (!TryBeginLinkAction(out long revision)) return;
        if (await MenuClipboard.CopyAsync(this, tag, _ => SetLinkNote(("booru.ui.link.copyFailed", string.Empty, true), revision)))
            SetLinkNote(("booru.ui.tags.copied", tag, false), revision);
    }

    /// <summary>"+N more" on a card: the card lists every source tag from now on (until the page is searched again).</summary>
    private void ExpandTags(string postId)
    {
        if (_closed || !_expandedTagPosts.Add(postId)) return;
        Render(force: false); // the results key carries the flag: only this hit's card is built again
    }
}
