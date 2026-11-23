using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Animation;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Reader ideas I1: a preview of the quoted post while the pointer rests on an inline >>No. or a quote button has the
// focus, and a one-time highlight of the post a jump lands on. One preview at a time, built lazily from the thread's
// post index: no cost per post, nothing is stored. P3 adds one outline overlay on the quoted post while the pointer or
// the keyboard focus rests on a reference (only when that post's container is realized and in view).
// Tom (16.11.2026): the preview shows the quoted post's first medium large (QuotePreviewMedia: decoded at the shown size, a GIF animates, a video
// plays muted and repeating in a player of its own, all released on close) and stays open while the pointer is on the reference or on the
// preview (QuotePreviewHoverState). Every reference previews: the inline >>No., the quote buttons, the reply cards and the References tab.
public partial class ArchivePage
{
    // QuotePreviewMaxLines, QuotePreviewMaxCharacters and QuotePreviewDelay are clamped instance properties (ArchivePage.ReaderLimits.cs).

    private DispatcherTimer? _quotePreviewTimer;
    // Held in TransientHover's owner slot (not a field) so ArchivePage's baselined CA1001 set does not grow.
    private TransientHover? _quotePointerLease => TransientHover.Current(this);
    private Window? _quoteHostWindow;
    private ArchiveQuoteRow? _quotePreviewRow;
    private Control? _quotePreviewTarget;
    private Rect _quotePreviewArea;
    private int _jumpFlashRevision;
    private readonly QuotePreviewHoverState _quotePreviewHover = new();
    private DispatcherTimer? _quotePreviewCloseTimer;
    private QuotePreviewMedia? _quotePreviewMedia;
    // The last pointer position over the posts or the references (relative to that list), so leaving a reference can tell whether the pointer
    // is on its way to the preview. Forgotten when a preview opens: only a move after that counts.
    private Visual? _quotePreviewPointerHost;
    private Point _quotePreviewPointer;

    /// <summary>The medium of the quote preview (test and smoke seam).</summary>
    internal QuotePreviewMedia? QuotePreviewMediaView => _quotePreviewMedia;
    /// <summary>"Play videos and GIFs in quote previews" (on by default); applies to the next preview.</summary>
    internal bool QuotePreviewVideoAutoplay { get; set; } = true;
    /// <summary>"Mute videos in quote previews" (on by default); applies to the next preview.</summary>
    internal bool QuotePreviewVideoMuted { get; set; } = true;

    internal bool IsQuotePreviewOpen => QuotePreviewPopup.IsOpen;
    /// <summary>True while a hover/focus preview waits for its delay timer.</summary>
    internal bool IsQuotePreviewPending => _quotePreviewTimer?.IsEnabled == true;
    internal ArchiveQuoteRow? QuotePreviewRow => QuotePreviewPopup.IsOpen ? _quotePreviewRow : null;

    private void OnInlineQuoteHover(object? sender, ReaderQuoteHoverEventArgs e)
    {
        e.Handled = true;
        if (e.Quote == null) LeaveQuoteReference(sender as Control, focused: false);
        else RequestQuotePreview(e.Quote, sender as Control, e.Area, immediate: false);
    }

    private void OnQuoteButtonEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Button { DataContext: ArchiveQuoteRow row } button) RequestQuotePreview(row, button, default, immediate: false);
    }

    private void OnQuoteButtonExited(object? sender, PointerEventArgs e)
    {
        if (sender is not Button button) return;
        RememberQuotePreviewPointer(button, e);
        // A button that still has the keyboard focus keeps its preview.
        LeaveQuoteReference(button, focused: button.IsFocused);
    }

    // The pointer left a reference: the waiting preview is cancelled; an open one closes at once, or after a short grace when the pointer is on its
    // way to the preview (it stays while the pointer is over the preview).
    private void LeaveQuoteReference(Control? reference, bool focused)
    {
        if (reference != null && _quotePreviewTarget != null && !ReferenceEquals(reference, _quotePreviewTarget)) return;
        switch (_quotePreviewHover.LeaveLink(focused, QuotePreviewPointerOnTheWay()))
        {
            case QuotePreviewLeave.CloseNow: CloseQuotePreview(); break;
            case QuotePreviewLeave.CloseAfterGrace: StartQuotePreviewCloseGrace(); break;
        }
    }

    private void RememberQuotePreviewPointer(Visual host, PointerEventArgs e)
    {
        _quotePreviewPointerHost = host;
        _quotePreviewPointer = e.GetPosition(host);
    }

    // Whether the last pointer position lies between the reference and the open preview (screen pixels; both may be in different windows).
    private bool QuotePreviewPointerOnTheWay()
    {
        if (!QuotePreviewPopup.IsOpen || _quotePreviewTarget is not { } target || _quotePreviewPointerHost is not { } host) return false;
        try
        {
            Rect area = _quotePreviewArea.Width > 0 ? _quotePreviewArea : new Rect(target.Bounds.Size);
            PixelPoint anchorTopLeft = target.PointToScreen(area.TopLeft), anchorBottomRight = target.PointToScreen(area.BottomRight);
            Border card = QuotePreviewCard;
            if (!card.IsEffectivelyVisible || card.Bounds.Width <= 0) return false;
            PixelPoint cardTopLeft = card.PointToScreen(default), cardBottomRight = card.PointToScreen(new Point(card.Bounds.Width, card.Bounds.Height));
            return QuotePreviewMediaRules.InCorridor(host.PointToScreen(_quotePreviewPointer),
                new PixelRect(anchorTopLeft, anchorBottomRight), new PixelRect(cardTopLeft, cardBottomRight));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { return false; } // a visual that left its window has no screen position
    }

    private void StartQuotePreviewCloseGrace()
    {
        DispatcherTimer timer = _quotePreviewCloseTimer ??= CreateQuotePreviewCloseTimer();
        timer.Stop();
        timer.Start();
    }

    private DispatcherTimer CreateQuotePreviewCloseTimer()
    {
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(QuotePreviewHoverState.CloseGraceMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_quotePreviewHover.GraceElapsed()) CloseQuotePreview();
        };
        return timer;
    }

    private void OnQuoteHostDeactivated(object? sender, EventArgs e) => CloseQuotePreview();
    private void OnQuoteHostChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty || e.Property == IsEnabledProperty) CloseQuotePreview();
    }

    private void InitializeQuotePreview()
    {
        AttachedToVisualTree += (_, _) =>
        {
            _quoteHostWindow = TopLevel.GetTopLevel(this) as Window;
            if (_quoteHostWindow == null) return;
            _quoteHostWindow.Deactivated += OnQuoteHostDeactivated;
            _quoteHostWindow.PropertyChanged += OnQuoteHostChanged;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CloseQuotePreview();
            if (_quoteHostWindow == null) return;
            _quoteHostWindow.Deactivated -= OnQuoteHostDeactivated;
            _quoteHostWindow.PropertyChanged -= OnQuoteHostChanged;
            _quoteHostWindow = null;
        };
        // The posts and the References tab preview alike: the quote buttons, the reply cards and the reference rows by focus, the pointer on any of them.
        foreach (ListBox list in new[] { ArchivePosts, ArchiveReferences })
        {
            list.AddHandler(GotFocusEvent, (_, e) =>
            {
                if (e.Source is Button { DataContext: ArchiveQuoteRow row } button && IsQuoteButton(button))
                    RequestQuotePreview(row, button, default, immediate: false, pointer: false);
            }, RoutingStrategies.Bubble);
            list.AddHandler(LostFocusEvent, (_, e) =>
            {
                if (e.Source is Button button && IsQuoteButton(button) && ReferenceEquals(button, _quotePreviewTarget)) CloseQuotePreview();
            }, RoutingStrategies.Bubble);
            // Tunnel, so the quote button's own Space click and the reader's Escape never see these keys.
            list.AddHandler(KeyDownEvent, OnQuotePreviewKeyDown, RoutingStrategies.Tunnel);
            list.AddHandler(KeyUpEvent, (_, e) =>
            {
                if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && e.Source is Button button && IsSpacePreviewButton(button)) e.Handled = true;
            }, RoutingStrategies.Tunnel);
            list.AddHandler(PointerWheelChangedEvent, (_, _) => CloseQuotePreview(), RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(PointerPressedEvent, (_, _) => CloseQuotePreview(), RoutingStrategies.Tunnel, handledEventsToo: true);
            // Before the text sees the move (its inline >>No. reports leaving on a move): the position the leave is judged by.
            ListBox host = list;
            list.AddHandler(PointerMovedEvent, (_, e) => RememberQuotePreviewPointer(host, e), RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        // Another tab (Posts, Media, References) closes the preview of the one left.
        ArchiveContentTabs.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, ArchiveContentTabs)) CloseQuotePreview(); };
        Border card = QuotePreviewCard;
        // Keep the entire card hit-testable before its popup joins the resource tree.
        // SetCurrentValue preserves the dynamic theme binding for attachment/theme changes.
        if (card.Background == null)
            card.SetCurrentValue(Border.BackgroundProperty, Brushes.Transparent);
        // The preview is as wide as its medium (a portrait picture does not spread the text over the whole box), at least as wide as without one.
        _quotePreviewMedia = new QuotePreviewMedia
        {
            PlaybackGate = PlaybackGate,
            FrameSized = size =>
            {
                if (!QuotePreviewMediaHost.IsVisible) return;
                Thickness padding = card.Padding, border = card.BorderThickness;
                card.MaxWidth = Math.Max(QuotePreviewMediaRules.TextOnlyMaxWidth, size.Width + padding.Left + padding.Right + border.Left + border.Right);
            }
        };
        QuotePreviewMediaHost.Children.Add(_quotePreviewMedia.View);
        // The preview takes the pointer: it stays open while the pointer is on it, and a click opens the quoted post (as the reference does).
        card.PointerEntered += (_, _) =>
        {
            _quotePreviewHover.EnterPreview();
            _quotePreviewCloseTimer?.Stop();
        };
        card.PointerExited += (_, _) =>
        {
            if (_quotePreviewHover.LeavePreview(_quotePreviewTarget is Button { IsFocused: true }) == QuotePreviewLeave.CloseAfterGrace) StartQuotePreviewCloseGrace();
        };
        card.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            // The video's own click (pause, continue) is handled by its player.
            if (e.Handled || e.InitialPressMouseButton != MouseButton.Left || _quotePreviewRow is not { } row) return;
            e.Handled = true;
            OpenActions.Start(() => NavigateQuoteAsync(row), "reader.quotePreview.open");
        }, RoutingStrategies.Bubble);
        QuotePreviewPopup.Closed += (_, _) => CloseQuotePreview();
        // The outline is placed in list coordinates: scrolling removes it; a change of extent or size (images, text layout)
        // only moves the target, so it is placed again for the reference that is still hovered.
        ListBox posts = ArchivePosts;
        posts.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e is ScrollChangedEventArgs scroll && scroll.OffsetDelta != default) { CloseQuotePreview(); return; }
            if (!ReaderQuoteHighlight.IsVisible) return;
            if (e is ScrollChangedEventArgs { OffsetDelta: { X: 0, Y: 0 } } && _quotePreviewRow is { } row) ShowQuoteHighlight(row);
            else HideQuoteHighlight();
        }, RoutingStrategies.Bubble);
        posts.SizeChanged += (_, _) =>
        {
            if (ReaderQuoteHighlight.IsVisible && _quotePreviewRow is { } row) ShowQuoteHighlight(row);
        };
    }

    // The buttons that stand for one quoted post: a post's own reference row, its reply cards and the rows of the References tab.
    private static bool IsQuoteButton(Button button) => button.DataContext is ArchiveQuoteRow
        && (button.Classes.Contains("reader-quote") || button.Classes.Contains("reader-reply-card") || button.Classes.Contains("reader-reference"));

    // Space toggles the preview only on a post's own reference row (its existing key); elsewhere Space keeps clicking (opening the post).
    private static bool IsSpacePreviewButton(Button button) => button.Classes.Contains("reader-quote") && button.DataContext is ArchiveQuoteRow;

    // Space on a focused quote button opens (or closes) its preview without jumping; Enter keeps jumping to the post.
    // Escape closes an open preview before anything else reads it (the large reader would close itself).
    private void OnQuotePreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        // Moving through the posts by keys leaves the preview of a reference that is not the focused one (the focus brings its own).
        if (ReaderVideoKeys.IsNavigationKey(e.Key) && (IsQuotePreviewOpen || IsQuotePreviewPending) && !ReferenceEquals(e.Source, _quotePreviewTarget))
        {
            CloseQuotePreview();
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Escape && IsQuotePreviewOpen)
        {
            CloseQuotePreview();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.Source is Button { DataContext: ArchiveQuoteRow row } button && IsSpacePreviewButton(button))
        {
            e.Handled = true;
            if (IsQuotePreviewOpen && ReferenceEquals(_quotePreviewTarget, button)) CloseQuotePreview();
            else RequestQuotePreview(row, button, default, immediate: true, pointer: false);
        }
    }

    /// <summary>
    /// Asks for the preview of <paramref name="row"/> at <paramref name="target"/> (<paramref name="area"/>: the reference's rectangle in it, for an inline
    /// run). <paramref name="pointer"/>: the pointer is on the reference (otherwise the keyboard focus). The same reference that is already previewed keeps
    /// its preview (the pointer came back to it).
    /// </summary>
    internal void RequestQuotePreview(ArchiveQuoteRow row, Control? target, Rect area, bool immediate, bool pointer = true)
    {
        if (_ended || target == null || PreviewPost(row) == null)
        {
            CloseQuotePreview();
            return;
        }
        if (QuotePreviewPopup.IsOpen && ReferenceEquals(_quotePreviewRow, row) && ReferenceEquals(_quotePreviewTarget, target))
        {
            if (pointer)
            {
                _quotePreviewHover.EnterLink();
                _quotePreviewCloseTimer?.Stop();
            }
            return;
        }
        CloseQuotePreview();
        _quotePreviewRow = row;
        _quotePreviewTarget = target;
        _quotePreviewArea = area;
        _quotePreviewHover.Request(pointer, immediate);
        if (pointer)
        {
            TransientHover.Replace(this, target, hovered => { if (!hovered) CloseQuotePreview(); },
                () => QuotePreviewPopup.IsOpen && (QuotePreviewCard.IsPointerOver
                    || _quotePreviewCloseTimer?.IsEnabled == true && TopLevel.GetTopLevel(target)?.IsPointerOver == true));
        }
        ShowQuoteHighlight(row);
        if (immediate) { ShowQuotePreview(); return; }
        _quotePreviewTimer ??= new DispatcherTimer { Interval = QuotePreviewDelay };
        _quotePreviewTimer.Tick -= OnQuotePreviewTimer;
        _quotePreviewTimer.Tick += OnQuotePreviewTimer;
        _quotePreviewTimer.Start();
    }

    private void OnQuotePreviewTimer(object? sender, EventArgs e)
    {
        _quotePreviewTimer?.Stop();
        if (_quotePointerLease != null && !_quotePointerLease.IsCurrent) { CloseQuotePreview(); return; }
        if (_quotePreviewHover.DelayElapsed()) ShowQuotePreview();
    }

    /// <summary>Closes the preview: the delay and the grace stop, its medium's load is cancelled and its picture, GIF and player are given back.</summary>
    internal void CloseQuotePreview()
    {
        _quotePreviewTimer?.Stop();
        TransientHover.Release(this);
        _quotePreviewCloseTimer?.Stop();
        _quotePreviewHover.Reset();
        HideQuoteHighlight();
        _quotePreviewMedia?.Clear();
        if (QuotePreviewPopup.IsOpen) QuotePreviewPopup.IsOpen = false;
        _quotePreviewRow = null;
        _quotePreviewTarget = null;
    }

    // The quoted post of this thread, or null when it is in another thread, was never captured or the row is a dead reference.
    private CapturedPost? PreviewPost(ArchiveQuoteRow row)
    {
        if (_thread == null || row.IsDead) return null;
        ArchivePostId? target = row.Backlink ? row.Quote.Origin : row.Quote.Target;
        if (target == null || target.Thread != _thread.Summary.Id) return null;
        int index = _thread.FindPostIndex(target.PostId);
        return index < 0 ? null : _thread.GetPosts(index, 1).FirstOrDefault();
    }

    private DateTimeOffset? ExcerptOpeningPostAt(ArchiveQuoteRow row) => ExcerptOpeningPostAt(_thread,
        (row.Backlink ? row.Quote.Origin : row.Quote.Target)?.Thread);

    // A preview must not borrow the OP of the randomly open reader for a foreign/unknown target.
    internal static DateTimeOffset? ExcerptOpeningPostAt(ArchivedThreadView? thread, ResourceId? targetThread)
    {
        if (thread == null || targetThread == null || thread.Summary.Id != targetThread) return null;
        string? op = ArchivePresentation.OpeningPostId(thread);
        return op == null ? null : thread.Posts.FirstOrDefault(post => post.PostId == op)?.PostedAt;
    }

    internal bool IsQuoteHighlightVisible => ReaderQuoteHighlight.IsVisible;

    private void HideQuoteHighlight()
    {
        if (ReaderQuoteHighlight.IsVisible) ReaderQuoteHighlight.IsVisible = false;
    }

    // Outlines the quoted post with the accent colour: one overlay, no animation, placed from the realized container of the
    // target and clipped to what the list shows. The outline follows a realized, visible target.
    private void ShowQuoteHighlight(ArchiveQuoteRow row)
    {
        Border outline = ReaderQuoteHighlight;
        outline.IsVisible = false;
        ListBox posts = ArchivePosts;
        ArchivePostId? target = row.Backlink ? row.Quote.Origin : row.Quote.Target;
        if (_ended || _thread == null || row.IsDead || target == null || target.Thread != _thread.Summary.Id
            || FindPostRow(target.PostId) is not { } item
            || posts.ContainerFromItem(item) is not Control container || !container.IsEffectivelyVisible || outline.Parent is not Visual host
            || container.TranslatePoint(default, host) is not Point origin || posts.TranslatePoint(default, host) is not Point listOrigin) return;
        Rect visible = new Rect(origin, container.Bounds.Size).Intersect(new Rect(listOrigin, posts.Bounds.Size));
        if (visible.Width < 1 || visible.Height < 1) return;
        outline.Margin = new Thickness(visible.X, visible.Y, 0, 0);
        outline.Width = visible.Width;
        outline.Height = visible.Height;
        outline.IsVisible = true;
    }

    private void ShowQuotePreview()
    {
        if (_quotePointerLease != null && !_quotePointerLease.IsCurrent) { CloseQuotePreview(); return; }
        if (_ended || _quotePreviewRow is not { } row || _quotePreviewTarget is not { } target || !TransientHover.WindowUsable(target)
            || PreviewPost(row) is not { } post) { CloseQuotePreview(); return; }
        (string header, string body) = ArchivePresentation.PostExcerpt(post, QuotePreviewMaxLines, QuotePreviewMaxCharacters,
            ReaderDateStyle, ExcerptOpeningPostAt(row), DateTimeOffset.Now);
        QuotePreviewHeader.Text = header;
        ToolTip.SetTip(QuotePreviewHeader, ReaderDateText.PostedFull(post.PostedAt));
        QuotePreviewSubject.Text = post.Subject ?? string.Empty;
        QuotePreviewSubject.IsVisible = !string.IsNullOrEmpty(post.Subject);
        QuotePreviewBody.Text = body;
        QuotePreviewBody.IsVisible = body.Length > 0;
        Avalonia.Automation.AutomationProperties.SetName(QuotePreviewPopup, header + (body.Length > 0 ? ": " + body : string.Empty));
        UpdateQuotePreviewFilterNote(post);
        ShowQuotePreviewMedia(post, target);
        QuotePreviewPopup.PlacementTarget = target;
        QuotePreviewPopup.PlacementRect = _quotePreviewArea.Width > 0 ? _quotePreviewArea : null;
        // Only a pointer move after the preview opened tells where the pointer goes when it leaves the reference.
        _quotePreviewPointerHost = null;
        QuotePreviewPopup.IsOpen = true;
    }

    // The quoted post's first medium above its text, as large as the reader and the window allow (QuotePreviewMediaRules). A post whose media the
    // view hides, or that a post filter conceals, shows its text only; a medium that is not saved leaves a short note instead of the frame.
    private void ShowQuotePreviewMedia(CapturedPost post, Control target)
    {
        Border card = QuotePreviewCard;
        Panel host = QuotePreviewMediaHost;
        card.MaxWidth = QuotePreviewMediaRules.TextOnlyMaxWidth;
        card.MaxHeight = QuotePreviewMediaRules.TextOnlyMaxHeight;
        QuotePreviewPopup.Placement = PlacementMode.Bottom;
        host.IsVisible = false;
        if (_quotePreviewMedia is not { } media) return;
        media.Clear();
        IReadOnlyList<CapturedAttachment> attachments = post.Attachments ?? Array.Empty<CapturedAttachment>();
        if (attachments.Count == 0 || _thread == null || _readerView.HideMedia || PostFilterConceals(post)) return;
        host.IsVisible = true;
        if (QuotePreviewMediaRules.FirstMedium(attachments) is not { } first)
        {
            media.ShowNote(Texts.T("archive.notSaved"));
            return;
        }
        if (TopLevel.GetTopLevel(target) is not { } top || target.TranslatePoint(_quotePreviewArea.Width > 0 ? _quotePreviewArea.TopLeft : default, top) is not Point origin)
        {
            host.IsVisible = false;
            return;
        }
        Size window = top.ClientSize;
        Size viewport = ReaderContentSurface.Bounds.Size;
        Rect anchor = new(origin, _quotePreviewArea.Width > 0 ? _quotePreviewArea.Size : target.Bounds.Size);
        Thickness padding = card.Padding, border = card.BorderThickness;
        Size chrome = new(padding.Left + padding.Right + border.Left + border.Right,
            padding.Top + padding.Bottom + border.Top + border.Bottom + QuotePreviewContent.Spacing);
        // The text goes below the medium: measured at the width it gets at least (a wider medium only gives it more room).
        host.IsVisible = false;
        QuotePreviewContent.Measure(new Size(QuotePreviewMediaRules.TextOnlyMaxWidth - chrome.Width, double.PositiveInfinity));
        double text = Math.Max(QuotePreviewContent.DesiredSize.Height, 48);
        QuotePreviewMediaBox placed = QuotePreviewMediaRules.Box(viewport, window, anchor, text, chrome);
        if (!placed.HasRoom) return;
        host.IsVisible = true;
        card.MaxHeight = double.PositiveInfinity; // the width follows the medium's frame (FrameSized)
        QuotePreviewPopup.Placement = placed.Below ? PlacementMode.Bottom : PlacementMode.Top;
        ArchiveAttachmentRow row = CreateAttachmentRow(_thread.Summary.Id, first, post.PostId, _mediaRead);
        media.Autoplay = QuotePreviewVideoAutoplay;
        media.Muted = QuotePreviewVideoMuted;
        media.Show(row, attachments.Count, placed.Box, top.RenderScaling, _inlineVideo.Options);
    }

    /// <summary>The first lines of a post, at most <paramref name="maxLines"/> lines (clamped; the default is the preview's default) and the characters that belong to them, with the ellipsis attached to the last word.</summary>
    internal static string ShortenQuotePreview(string text, int maxLines = ReaderDisplayLimits.PreviewLinesDefault) =>
        ShortenText(text, ReaderDisplayLimits.ClampPreviewLines(maxLines), ReaderDisplayLimits.PreviewCharacters(maxLines));

    internal static string ShortenText(string text, int maxLines, int maxCharacters)
    {
        string[] lines = text.Split('\n');
        string result = string.Join("\n", lines.Take(maxLines)).TrimEnd();
        bool cut = lines.Length > maxLines;
        if (result.Length > maxCharacters)
        {
            result = result[..maxCharacters];
            if (char.IsHighSurrogate(result[^1])) result = result[..^1];
            cut = true;
        }
        return cut ? result.TrimEnd() + "…" : result;
    }

    // The post a jump landed on flashes once: an accent wash over its card fades out in 400 ms. Reduced motion keeps the
    // same wash as a static state for the same time and removes it without a fade. One overlay for the whole list.
    internal async Task FlashPostAsync(string postId)
    {
        Border flash = ReaderJumpFlash;
        int revision = ++_jumpFlashRevision;
        flash.Transitions = null;
        flash.IsVisible = false;
        flash.Opacity = 0;
        ListBox posts = ArchivePosts;
        if (_ended || FindPostRow(postId) is not { } item
            || posts.ContainerFromItem(item) is not Control container || flash.Parent is not Visual host
            || container.TranslatePoint(default, host) is not Point origin) return;
        flash.Margin = new Thickness(origin.X, origin.Y, 0, 0);
        flash.Width = container.Bounds.Width;
        flash.Height = container.Bounds.Height;
        flash.Opacity = 0.35;
        flash.IsVisible = true;
        if (!ReaderMotion.Reduced)
        {
            // The start value must be rendered once before the transition animates away from it.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (revision != _jumpFlashRevision) return;
            flash.Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = ReaderMotion.Highlight }];
            flash.Opacity = 0;
        }
        await Task.Delay(ReaderMotion.Highlight + TimeSpan.FromMilliseconds(60));
        if (revision != _jumpFlashRevision) return;
        flash.IsVisible = false;
        flash.Transitions = null;
        flash.Opacity = 0;
    }
}
