using System;
using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

// V2 (PLAN-MEDIEN-VIEWER-UND-SEITE §1.1): the side panel host in the bordered slot of the post panel (_contextPanel, "GalleryContext").
// The slot keeps its placement (docked from 900 dip, over the picture below), its auto-hide hold while open (Reading), its work-view
// row "gallery.post-panel" and its focus return to the picture; the host adds the segment "Post | Tags", the isolated close button
// and the drag width. The post card is _context in a ScrollViewer; the tag panel (GalleryViewer.TagPanel.cs) puts its surface into
// the Tags slot while it is open. P shows the post, Ctrl+Shift+T the tags, Esc and × close the host.
internal sealed partial class GalleryViewer
{
    private GallerySidePanel _sidePanel = null!;
    private GalleryTagPanelPosition? _sidebarPosition;
    private double _panelWidth = Config.GalleryPanelWidthDefault;

    internal GallerySidePanel SidePanel => _sidePanel;

    /// <summary>Raised once when a drag of the panel's edge ends, with the clamped width the owner keeps in the layout profile.</summary>
    internal event Action<double>? PanelWidthChanged;

    /// <summary>The preferred panel width (dip) from the layout profile; clamped to the panel's limits.</summary>
    internal double PanelWidth
    {
        get => _panelWidth;
        set
        {
            double width = GallerySidePanel.ClampWidth(value);
            if (width == _panelWidth) return;
            _panelWidth = width;
            if (_contextOpen) LayoutContext();
        }
    }

    // Docked beside the picture: the wish, as long as the picture keeps its minimum width.
    private double DockedContextWidth => GallerySidePanel.EffectiveWidth(_panelWidth, Bounds.Width, overlayOption: false, narrow: false, MinimumPictureWidth);

    private void CreateSidePanel(Control postContent)
    {
        // The × keeps the name every smoke uses; it closes the whole host and the focus returns to the picture.
        PanelCloseButton close = new("GalleryReturnImage");
        close.Click += (_, _) => { if (_contextOpen) SetContextOpen(false); };
        _sidePanel = new GallerySidePanel(close) { PostContent = postContent };
        _sidePanel.SegmentRequested += OnSidePanelSegmentRequested;
        _sidePanel.WidthDragged += width =>
        {
            _panelWidth = width;
            if (_contextOpen) LayoutContext();
        };
        _sidePanel.WidthCommitted += width =>
        {
            _panelWidth = width;
            PanelWidthChanged?.Invoke(width);
        };
        double inset = WorkspaceMetrics.SectionInset;
        _contextPanel.Padding = new Thickness(inset, MediaSidebarHeader.TopInset, inset, inset);
        _contextPanel.Child = _sidePanel;
        SyncSidePanel();
    }

    // Segment, availability of Tags, labels and the close button's name from the viewer's state (also after a language change).
    private void SyncSidePanel()
    {
        if (_sidePanel == null) return;
        _sidePanel.Segment = _tagPanelOpen ? GallerySidePanelSegment.Tags : GallerySidePanelSegment.Post;
        bool tagsAvailable = _entry != null && _tagNames != null && !_terminalRetirement;
        _sidePanel.SetTagsAvailable(tagsAvailable, _tagsUnavailableReason);
        _sidePanel.Localize();
        RefreshPanelCounter(); // the host header's owner-supplied position text follows language changes too
        _sidePanel.CloseButton.Localize(Texts.T("gallery.panel.close"));
    }

    private void RefreshPanelCounter()
    {
        if (_sidePanel == null) return;
        GalleryTagPanelPosition? position = _sidebarPosition;
        bool marked = _tagController is { } controller && controller.IsMarked(controller.ShownEntry());
        _sidePanel.Position.Show(position, marked);
    }

    private void OnSidePanelSegmentRequested(GallerySidePanelSegment segment, bool focusContent)
    {
        if (!_contextOpen || _terminalRetirement) return;
        if (segment == GallerySidePanelSegment.Tags)
        {
            if (_tagPanelOpen) return;
            RequestTags(); // the owner opens the tag panel into the Tags slot (ShowTagPanel)
            if (focusContent && _tagPanelOpen) _tagSurface?.FocusField();
            return;
        }
        if (!_tagPanelOpen) return;
        ShowPostSegment();
        if (focusContent) _sidePanel.FocusContent();
    }

    // The post takes the host over from the tag panel (and is remembered as open): P, the Post segment.
    private void ShowPostSegment()
    {
        ReleaseTagPanel();
        EnsureContextCurrent();
        LayoutContext();
        UpdateContextButton();
        RefreshPlaybackControls();
        ContextOpenChanged?.Invoke(true);
    }
}
