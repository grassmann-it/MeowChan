using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// Review 15.11. (Tom's decision): the Post segment of the side panel carries what the toolbar's rating button and the rating dialog did.
// Top to bottom, 12 dip between the sections, 8 inside a section, 4 from a caption to its content (the values the tag panel and the decision
// row use): where the post is, favourite and stars (a second GalleryDecisionRow, created once and put back on every rebuild), the medium's tags
// as chips (each a button that opens the Tags segment, like the link) with that link, a rule, then the post itself and the file. R (media.rating.edit,
// "Rate and tag") opens the Tags segment with its stars focused (RequestRateAndTag); a medium that cannot be tagged gets this segment's stars instead.
internal sealed partial class GalleryViewer
{
    internal const string PostDecisionName = "GalleryPostDecision";
    private int _postTagChipLimit = Config.GalleryPostTagChipLimitDefault;
    /// <summary>The most tags the Post segment lists as chips; the rest is one "+N more" chip (the full list is one click away in Tags).
    /// A setting (<see cref="Config.GalleryPostTagChipLimit"/>, clamped to its range); a changed value rebuilds the open panel.</summary>
    internal int PostTagChipLimit
    {
        get => _postTagChipLimit;
        set
        {
            int limit = Math.Clamp(value, Config.GalleryPostTagChipLimitMinimum, Config.GalleryPostTagChipLimitMaximum);
            if (limit == _postTagChipLimit) return;
            _postTagChipLimit = limit;
            if (_entry != null) UpdateContext(); // an open Post segment lists its tags again
        }
    }

    private GalleryDecisionRow? _postDecision;
    private StackPanel? _postDecisionSection;
    private readonly TextBlock _postRatingError = new() { Name = "GalleryPostRatingError", Classes = { "workspace-error" },
        TextWrapping = TextWrapping.Wrap, IsVisible = false };

    /// <summary>The favourite and rating row of the Post segment (null until the segment was first built).</summary>
    internal GalleryDecisionRow? PostDecisionRow => _postDecision;

    // What the Post row shows: the viewer's own state (this medium only; the marked media belong to the Tags segment's row). The row stays usable
    // while a write is under way so the focus is not lost under a keyboard user; its requests are ignored then (OnPostRatingRequested).
    private GalleryDecisionView PostDecisionView() => new(1, _favoriteSelected ? GalleryDecisionCoverage.All : GalleryDecisionCoverage.None,
        _ratingValue, false, _entry != null && !_terminalRetirement && _favoriteKnown && (_ratingAvailable || _ratingPending));

    private Control PostDecisionSection()
    {
        if (_postDecision == null)
        {
            _postDecision = new GalleryDecisionRow(PostDecisionName) { KeyContext = "gallery" };
            _postDecision.FavoriteRequested += OnPostFavoriteRequested;
            _postDecision.RatingRequested += OnPostRatingRequested;
            // An assertive live region: a failed write is said at once, the next click on a star tries again (the dialog's retry is gone).
            AutomationProperties.SetLiveSetting(_postRatingError, AutomationLiveSetting.Assertive);
            _postDecisionSection = new StackPanel { Name = "GalleryPostDecisionSection", Spacing = 4,
                Children = { _postDecision, _postRatingError } };
        }
        RefreshPostDecision();
        return _postDecisionSection!;
    }

    /// <summary>Shows the viewer's favourite and rating (and the error of a failed write) in the Post row without rebuilding the segment.</summary>
    internal void RefreshPostDecision()
    {
        if (_postDecision == null) return;
        _postDecision.Show(PostDecisionView());
        string? error = _ratingError;
        string text = string.IsNullOrEmpty(error) ? string.Empty
            : MediaRatingText.FormatError(MediaRatingText.Catalog("media.rating.error.save"), error);
        if (_postRatingError.Text != text) _postRatingError.Text = text;
        _postRatingError.IsVisible = text.Length > 0;
    }

    private void OnPostFavoriteRequested(bool selected)
    {
        if (_favoritePending) { RefreshPostDecision(); return; } // a write is under way: the click is ignored and the focus stays
        ToggleFavorite();
    }

    private void OnPostRatingRequested(int? value)
    {
        if (_entry == null || _ratingPending || !_ratingAvailable) { RefreshPostDecision(); return; }
        RatingSetRequested?.Invoke(_entry, value);
    }

    // ---- the tags of the shown medium ------------------------------------------------------------------------------------------

    private Control BuildPostTagsSection(Func<Panel, string, string, Action, Button> command)
    {
        StackPanel section = new() { Name = "GalleryPostTags", Spacing = 4 };
        IReadOnlyList<string>? names = _tagNames;
        int count = names?.Count ?? 0;
        section.Children.Add(new TextBlock { Name = "GalleryPostTagsCaption", Classes = { "workspace-meta" }, FontWeight = FontWeight.SemiBold,
            Text = count > 0 ? Texts.T("tags.media.buttonCount", count) : Texts.T("tags.media.button") });
        if (names == null)
        {
            // Unknown or not taggable: the reason, no link (the Tags segment is not offered either).
            if (!string.IsNullOrWhiteSpace(_tagsUnavailableReason))
                section.Children.Add(new TextBlock { Name = "GalleryPostTagsReason", Classes = { "workspace-help" }, Text = _tagsUnavailableReason,
                    TextWrapping = TextWrapping.Wrap });
            return section;
        }
        Action openTags = () => OnSidePanelSegmentRequested(GallerySidePanelSegment.Tags, focusContent: true);
        if (count == 0)
            section.Children.Add(new TextBlock { Name = "GalleryPostTagsEmpty", Classes = { "workspace-help" }, Text = Texts.T("tags.quick.assigned.empty"),
                TextWrapping = TextWrapping.Wrap });
        else
        {
            WrapPanel chips = new() { Name = "GalleryPostTagChips", ItemSpacing = 8, LineSpacing = 8 };
            // The chips are decoration of one line: a screen reader reads "Tags: a, b, c" once, not every chip.
            AutomationProperties.SetName(chips, Texts.T("tags.rowLabel", string.Join(", ", names)));
            AutomationProperties.SetAccessibilityView(chips, AccessibilityView.Content);
            // Dead-affordance review 15.11.: a chip (and "+N more") is a button that opens the Tags segment, as the link below does. The chips stay out
            // of the Tab order and out of the accessibility tree; the keyboard and a screen reader have the one link.
            int limit = _postTagChipLimit;
            foreach (string name in names.Take(limit)) chips.Children.Add(TagChip(name, more: false, openTags));
            if (count > limit) chips.Children.Add(TagChip(Texts.T("gallery.post.tags.more", count - limit), more: true, openTags));
            section.Children.Add(chips);
        }
        Button edit = command(section, "GalleryPostTagsEdit", Texts.T("gallery.post.tags.edit"), openTags);
        edit.Classes.Add("link");
        edit.HorizontalAlignment = HorizontalAlignment.Left;
        return section;
    }

    private static Button TagChip(string text, bool more, Action open)
    {
        Button chip = new() { Name = more ? "GalleryPostTagsMore" : "GalleryPostTag", Content = new TextBlock { Text = text, MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        // An assigned tag rests on the accent tint like the reader's tag bar; "+N more" is the offered (outlined) form.
        if (more) WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOffered(chip));
        else WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOwn(chip), WorkspaceChips.TagPillAssigned);
        if (more) chip.Classes.Add("chip-more");
        ToolTip.SetTip(chip, Texts.T("gallery.post.tags.edit"));
        AutomationProperties.SetAccessibilityView(chip, AccessibilityView.Raw);
        chip.Click += (_, _) => open();
        return chip;
    }

    private static Control PostRule() => new Border { Name = "GalleryPostRule", Height = 1, [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Border"),
        Focusable = false, IsHitTestVisible = false };

    // ---- R and "Rate and tag…": the Tags segment with the stars focused -------------------------------------------------------

    /// <summary>The medium shown can be rated or tagged now (the "Rate and tag" command and its menu entry are available).</summary>
    internal bool CanRateAndTag => _entry != null && !_terminalRetirement && (_ratingAvailable || CanOpenTagPanel);

    private bool CanOpenTagPanel => _entry != null && _tagNames != null && CanRequestFavorite;

    /// <summary>
    /// R and the menu entry "Rate and tag…" (review 15.11.): the combined surface of the gallery is the Tags segment, favourite and stars on top
    /// and the tag field under them. It opens when it is closed (the Post segment gives way to it), and its stars take the focus. A medium
    /// that cannot be tagged has no Tags segment: its Post segment's stars take the focus instead (<see cref="FocusRatingStars"/>).
    /// </summary>
    internal void RequestRateAndTag()
    {
        if (_entry == null || _terminalRetirement) return;
        // Neither the stars nor the tags can open (ratings unreadable or a write pending, no tags): the reason the menu entry shows, never silence.
        if (!CanRateAndTag) { ExplainBlocked(_tagsUnavailableReason ?? MeowChan.Desktop.MediaActions.MediaMenuReasons.Unavailable); return; }
        if (!_tagPanelOpen && CanOpenTagPanel) RequestTags(); // the owner opens the tag panel into the Tags slot (ShowTagPanel), synchronously
        if (FocusRatingStars()) return;
        if (_tagPanelOpen && _tagSurface != null) { _tagSurface.FocusField(); return; }
        // Neither the stars nor the field took the keyboard: once more after the panel that just opened was laid out, then say why instead of a key that
        // did nothing (audit 16.11.2026).
        GalleryEntry? asked = _entry;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_terminalRetirement || _entry == null || !ReferenceEquals(_entry, asked) || FocusRatingStars()) return;
            if (_tagPanelOpen && _tagSurface != null) { _tagSurface.FocusField(); return; }
            ExplainBlocked(_tagsUnavailableReason ?? MeowChan.Desktop.MediaActions.MediaMenuReasons.Unavailable);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Moves the focus to the checked star (star 1 while unrated) of the panel, as a Tab would. Panel closed: the Post segment opens first.
    /// The Tags segment open: it stays and its row's stars take the focus (switching would drop the marked-media context). False when no
    /// panel can take the focus.
    /// </summary>
    internal bool FocusRatingStars()
    {
        if (_entry == null || _terminalRetirement || !_ratingAvailable) return false;
        if (_tagPanelOpen && _decisionRow is { } tagsRow) return _contextOpen && tagsRow.FocusRating();
        if (!_contextOpen) SetContextOpen(true); // notify: R is an explicit open, the saved panel-open preference follows like the Post toggle
        if (!_contextOpen || _tagPanelOpen) return false;
        EnsureContextCurrent();
        return _postDecision is { } row && row.FocusRating();
    }

    // Keys of a focused star: Left/Right/Home/End rove along the group (never read as "previous / next medium"), Esc returns the focus to
    // the picture and leaves the panel open. Both rows (Post and Tags) behave alike.
    private bool RouteDecisionRowKey(KeyEventArgs e)
    {
        if (!_contextOpen) return false;
        foreach (GalleryDecisionRow? row in new[] { _postDecision, _decisionRow })
        {
            if (row == null || !row.IsKeyboardFocusWithin || !row.IsRatingFocused) continue;
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { FocusGallerySurface(); e.Handled = true; return true; }
            if (row.RouteStarKey(e)) { e.Handled = true; return true; }
        }
        return false;
    }
}
