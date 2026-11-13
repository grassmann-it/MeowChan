using System;
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
public partial class ArchivePage
{
    // QuotePreviewMaxLines, QuotePreviewMaxCharacters and QuotePreviewDelay are clamped instance properties (ArchivePage.ReaderLimits.cs).

    private DispatcherTimer? _quotePreviewTimer;
    private ArchiveQuoteRow? _quotePreviewRow;
    private Control? _quotePreviewTarget;
    private Rect _quotePreviewArea;
    private int _jumpFlashRevision;

    internal bool IsQuotePreviewOpen => QuotePreviewPopup.IsOpen;
    /// <summary>True while a hover/focus preview waits for its delay timer.</summary>
    internal bool IsQuotePreviewPending => _quotePreviewTimer?.IsEnabled == true;
    internal ArchiveQuoteRow? QuotePreviewRow => QuotePreviewPopup.IsOpen ? _quotePreviewRow : null;

    private void OnInlineQuoteHover(object? sender, ReaderQuoteHoverEventArgs e)
    {
        e.Handled = true;
        if (e.Quote == null) CloseQuotePreview();
        else RequestQuotePreview(e.Quote, sender as Control, e.Area, immediate: false);
    }

    private void OnQuoteButtonEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Button { DataContext: ArchiveQuoteRow row } button) RequestQuotePreview(row, button, default, immediate: false);
    }

    private void OnQuoteButtonExited(object? sender, PointerEventArgs e)
    {
        // A button that still has the keyboard focus keeps its preview.
        if (sender is Button { IsFocused: false }) CloseQuotePreview();
    }

    private void InitializeQuotePreview()
    {
        ListBox posts = ArchivePosts;
        posts.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is Button { DataContext: ArchiveQuoteRow row } button && IsQuoteButton(button))
                RequestQuotePreview(row, button, default, immediate: false);
        }, RoutingStrategies.Bubble);
        posts.AddHandler(LostFocusEvent, (_, e) =>
        {
            if (e.Source is Button button && IsQuoteButton(button) && ReferenceEquals(button, _quotePreviewTarget)) CloseQuotePreview();
        }, RoutingStrategies.Bubble);
        // Tunnel, so the quote button's own Space click and the reader's Escape never see these keys.
        posts.AddHandler(KeyDownEvent, OnQuotePreviewKeyDown, RoutingStrategies.Tunnel);
        posts.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && e.Source is Button button && IsQuoteButton(button)) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        posts.AddHandler(PointerWheelChangedEvent, (_, _) => CloseQuotePreview(), RoutingStrategies.Tunnel, handledEventsToo: true);
        posts.AddHandler(PointerPressedEvent, (_, _) => CloseQuotePreview(), RoutingStrategies.Tunnel, handledEventsToo: true);
        QuotePreviewPopup.Closed += (_, _) => _quotePreviewRow = null;
        // The outline is placed in list coordinates: scrolling removes it; a change of extent or size (images, text layout)
        // only moves the target, so it is placed again for the reference that is still hovered.
        posts.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (!ReaderQuoteHighlight.IsVisible) return;
            if (e is ScrollChangedEventArgs { OffsetDelta: { X: 0, Y: 0 } } && _quotePreviewRow is { } row) ShowQuoteHighlight(row);
            else HideQuoteHighlight();
        }, RoutingStrategies.Bubble);
        posts.SizeChanged += (_, _) =>
        {
            if (ReaderQuoteHighlight.IsVisible && _quotePreviewRow is { } row) ShowQuoteHighlight(row);
        };
    }

    // Only the quote buttons of a post's own reference row preview; reply buttons and others keep their plain behaviour.
    private static bool IsQuoteButton(Button button) => button.Classes.Contains("reader-quote") && button.DataContext is ArchiveQuoteRow;

    // Space on a focused quote button opens (or closes) its preview without jumping; Enter keeps jumping to the post.
    // Escape closes an open preview before anything else reads it (the large reader would close itself).
    private void OnQuotePreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Handled) return;
        if (e.Key == Key.Escape && IsQuotePreviewOpen)
        {
            CloseQuotePreview();
            e.Handled = true;
        }
        else if (e.Key == Key.Space && e.Source is Button { DataContext: ArchiveQuoteRow row } button && IsQuoteButton(button))
        {
            e.Handled = true;
            if (IsQuotePreviewOpen && ReferenceEquals(_quotePreviewTarget, button)) CloseQuotePreview();
            else RequestQuotePreview(row, button, default, immediate: true);
        }
    }

    internal void RequestQuotePreview(ArchiveQuoteRow row, Control? target, Rect area, bool immediate)
    {
        if (_ended || target == null || PreviewPost(row) == null)
        {
            CloseQuotePreview();
            return;
        }
        if (QuotePreviewPopup.IsOpen && ReferenceEquals(_quotePreviewRow, row) && ReferenceEquals(_quotePreviewTarget, target)) return;
        CloseQuotePreview();
        _quotePreviewRow = row;
        _quotePreviewTarget = target;
        _quotePreviewArea = area;
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
        ShowQuotePreview();
    }

    internal void CloseQuotePreview()
    {
        _quotePreviewTimer?.Stop();
        HideQuoteHighlight();
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
    // target and clipped to what the list shows. A target that is on another page, not realized or scrolled out of view
    // gets no outline.
    private void ShowQuoteHighlight(ArchiveQuoteRow row)
    {
        Border outline = ReaderQuoteHighlight;
        outline.IsVisible = false;
        ListBox posts = ArchivePosts;
        ArchivePostId? target = row.Backlink ? row.Quote.Origin : row.Quote.Target;
        if (_ended || _thread == null || row.IsDead || target == null || target.Thread != _thread.Summary.Id
            || posts.ItemsSource?.Cast<ArchivePostRow>().FirstOrDefault(item => item.Id == target.PostId) is not { } item
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
        if (_ended || _quotePreviewRow is not { } row || _quotePreviewTarget is not { } target || !target.IsEffectivelyVisible
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
        QuotePreviewPopup.PlacementTarget = target;
        QuotePreviewPopup.PlacementRect = _quotePreviewArea.Width > 0 ? _quotePreviewArea : null;
        QuotePreviewPopup.IsOpen = true;
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
        if (_ended || posts.ItemsSource?.Cast<ArchivePostRow>().FirstOrDefault(row => row.Id == postId) is not { } item
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
