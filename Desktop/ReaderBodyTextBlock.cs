using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Archive;

namespace MeowChan.Desktop;

internal sealed class ReaderQuoteInvokedEventArgs(RoutedEvent routedEvent, ArchiveQuoteRow quote) : RoutedEventArgs(routedEvent)
{
    public ArchiveQuoteRow Quote { get; } = quote;
}

// Keep the native selectable text control and its configured reading font. Only
// media-free bodies get a reading measure; attachment/text layouts remain intact.
// A formatted body (greentext, inline >>No.) is rendered as runs over the unchanged plain
// text, so selection, copy and the reader find offsets stay those of the plain text.
// Inline references are pointer targets only: Avalonia has no focusable inline hyperlink, and
// an InlineUIContainer button would turn the reference into U+FFFC for selection and copy.
// The keyboard and screen-reader path is the quote button row after the text (ArchivePage).
internal sealed class ReaderBodyTextBlock : SelectableTextBlock
{
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public static readonly StyledProperty<bool> IsTextOnlyProperty =
        AvaloniaProperty.Register<ReaderBodyTextBlock, bool>(nameof(IsTextOnly));

    public static readonly StyledProperty<ReaderBodyDocument?> DocumentProperty =
        AvaloniaProperty.Register<ReaderBodyTextBlock, ReaderBodyDocument?>(nameof(Document));

    public static readonly StyledProperty<bool> SpoilersRevealedProperty =
        AvaloniaProperty.Register<ReaderBodyTextBlock, bool>(nameof(SpoilersRevealed), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly RoutedEvent<ReaderQuoteInvokedEventArgs> QuoteInvokedEvent =
        RoutedEvent.Register<ReaderBodyTextBlock, ReaderQuoteInvokedEventArgs>(nameof(QuoteInvoked), RoutingStrategies.Bubble);

    public bool IsTextOnly
    {
        get => GetValue(IsTextOnlyProperty);
        set => SetValue(IsTextOnlyProperty, value);
    }

    public ReaderBodyDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Spoilers of this post are shown (one reveal for all of them; the owning post keeps the state).</summary>
    public bool SpoilersRevealed
    {
        get => GetValue(SpoilersRevealedProperty);
        set => SetValue(SpoilersRevealedProperty, value);
    }

    internal bool HasHiddenSpoilers => Document is { HasSpoilers: true } && !SpoilersRevealed;

    public event EventHandler<ReaderQuoteInvokedEventArgs>? QuoteInvoked
    {
        add => AddHandler(QuoteInvokedEvent, value);
        remove => RemoveHandler(QuoteInvokedEvent, value);
    }

    /// <summary>The plain post text, also while the content lives in <see cref="TextBlock.Inlines"/>.</summary>
    internal string PlainText => Document?.Text ?? Text ?? string.Empty;

    // Reading line height follows the reading size (13/15/18 px -> 19/22/26 px), never a fixed gap.
    internal static double ReadingLineHeight(double fontSize) => Math.Round(fontSize * 1.45);

    private static readonly FontFamily CodeFont = new("monospace");
    private ReaderBodySegment? _pressedQuote;
    private bool _linkCursor;

    public ReaderBodyTextBlock() => UpdateReadingMetrics();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsTextOnlyProperty || change.Property == FontSizeProperty) UpdateReadingMetrics();
        if (change.Property == DocumentProperty) ShowDocument(change.GetNewValue<ReaderBodyDocument?>());
        else if (change.Property == SpoilersRevealedProperty && Document is { IsFormatted: true }) Render(Document);
    }

    private void UpdateReadingMetrics()
    {
        // Roughly 80 typical Latin characters at the configured reading size.
        MaxWidth = IsTextOnly ? FontSize * 44 : double.PositiveInfinity;
        HorizontalAlignment = IsTextOnly ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        LineHeight = ReadingLineHeight(FontSize);
    }

    private void ShowDocument(ReaderBodyDocument? document)
    {
        _pressedQuote = null;
        SetLinkCursor(false);
        SelectionStart = SelectionEnd = 0;
        if (document is not { IsFormatted: true })
        {
            // Unformatted bodies keep the plain Text path (and TextBlock.Text) unchanged.
            Inlines?.Clear();
            Text = document?.Text;
            return;
        }
        Render(document);
    }

    // Runs over the document text. Links and references are colour (references) and underline (external links);
    // a hidden spoiler paints its text in its own surface colour, so the area keeps its size and stays selectable.
    private void Render(ReaderBodyDocument document)
    {
        Text = null;
        InlineCollection inlines = new();
        foreach (ReaderBodySegment segment in document.Segments)
        {
            Run run = new(document.Text.Substring(segment.Start, segment.Length));
            switch (segment.Kind)
            {
                case ReaderBodySegmentKind.Greentext:
                    run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.ReaderGreentext");
                    break;
                case ReaderBodySegmentKind.Quote:
                    run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Accent");
                    break;
                case ReaderBodySegmentKind.DeadQuote:
                    run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
                    run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
                    break;
                case ReaderBodySegmentKind.DeadNote:
                    run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
                    run.FontStyle = FontStyle.Italic;
                    break;
            }
            if ((segment.Style & ArchiveTextStyle.Link) != 0 && segment.Kind is ReaderBodySegmentKind.Text or ReaderBodySegmentKind.Greentext)
            {
                run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Accent");
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
            }
            if ((segment.Style & ArchiveTextStyle.Code) != 0)
            {
                run.FontFamily = CodeFont;
                run[!TextElement.BackgroundProperty] = new DynamicResourceExtension("Workspace.ReaderCodeSurface");
            }
            if (segment.IsSpoiler)
            {
                run[!TextElement.BackgroundProperty] = new DynamicResourceExtension("Workspace.ReaderSpoiler");
                if (!SpoilersRevealed) run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.ReaderSpoiler");
            }
            inlines.Add(run);
        }
        Inlines = inlines;
    }

    internal ReaderBodySegment? SegmentAt(Point point)
    {
        if (Document is not { IsFormatted: true } document) return null;
        TextHitTestResult hit = TextLayout.HitTestPoint(new Point(point.X - Padding.Left, point.Y - Padding.Top));
        if (!hit.IsInside) return null;
        int index = hit.CharacterHit.FirstCharacterIndex;
        return document.Segments.FirstOrDefault(segment => index >= segment.Start && index < segment.End);
    }

    // A reference (also a dead one: its click reports that the post was not captured).
    internal ReaderBodySegment? QuoteAt(Point point) => SegmentAt(point) is { Quote: not null } segment ? segment : null;

    // The reveal comes first: a reference inside a hidden spoiler cannot be clicked before the text is shown.
    private ReaderBodySegment? ActionAt(Point point) => SegmentAt(point) is { } segment
        && (segment is { IsSpoiler: true } && !SpoilersRevealed || segment.Quote != null) ? segment : null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        _pressedQuote = point.Properties.IsLeftButtonPressed && e.ClickCount == 1 ? ActionAt(point.Position) : null;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ReaderBodySegment? pressed = _pressedQuote;
        _pressedQuote = null;
        // A drag that selected text is a selection, not a link click.
        if (pressed == null || e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd
            || !ReferenceEquals(ActionAt(e.GetPosition(this)), pressed)) return;
        e.Handled = true;
        if (pressed.IsSpoiler && !SpoilersRevealed) { SpoilersRevealed = true; SetLinkCursor(false); }
        else if (pressed.Quote != null) RaiseEvent(new ReaderQuoteInvokedEventArgs(QuoteInvokedEvent, pressed.Quote));
    }

    // Enter or Space on the focused body shows its spoilers (inline text has no focusable parts of its own).
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None && HasHiddenSpoilers)
        {
            SpoilersRevealed = true;
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) SetLinkCursor(ActionAt(e.GetPosition(this)) != null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetLinkCursor(false);
    }

    private void SetLinkCursor(bool link)
    {
        if (_linkCursor == link) return;
        _linkCursor = link;
        if (link) Cursor = new Cursor(StandardCursorType.Hand);
        else ClearValue(CursorProperty);
    }
}
