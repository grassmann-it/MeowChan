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

    public event EventHandler<ReaderQuoteInvokedEventArgs>? QuoteInvoked
    {
        add => AddHandler(QuoteInvokedEvent, value);
        remove => RemoveHandler(QuoteInvokedEvent, value);
    }

    /// <summary>The plain post text, also while the content lives in <see cref="TextBlock.Inlines"/>.</summary>
    internal string PlainText => Document?.Text ?? Text ?? string.Empty;

    // Reading line height follows the reading size (13/15/18 px -> 19/22/26 px), never a fixed gap.
    internal static double ReadingLineHeight(double fontSize) => Math.Round(fontSize * 1.45);

    private ReaderBodySegment? _pressedQuote;
    private bool _linkCursor;

    public ReaderBodyTextBlock() => UpdateReadingMetrics();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsTextOnlyProperty || change.Property == FontSizeProperty) UpdateReadingMetrics();
        if (change.Property == DocumentProperty) ShowDocument(change.GetNewValue<ReaderBodyDocument?>());
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
        Text = null;
        InlineCollection inlines = new();
        foreach (ReaderBodySegment segment in document.Segments)
        {
            Run run = new(document.Text.Substring(segment.Start, segment.Length));
            if (segment.Kind == ReaderBodySegmentKind.Greentext)
                run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.ReaderGreentext");
            else if (segment.Kind == ReaderBodySegmentKind.Quote)
            {
                run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Accent");
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
            }
            inlines.Add(run);
        }
        Inlines = inlines;
    }

    internal ReaderBodySegment? QuoteAt(Point point)
    {
        if (Document is not { IsFormatted: true } document) return null;
        TextHitTestResult hit = TextLayout.HitTestPoint(new Point(point.X - Padding.Left, point.Y - Padding.Top));
        if (!hit.IsInside) return null;
        int index = hit.CharacterHit.FirstCharacterIndex;
        return document.Segments.FirstOrDefault(segment => segment.Kind == ReaderBodySegmentKind.Quote
            && index >= segment.Start && index < segment.End);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        _pressedQuote = point.Properties.IsLeftButtonPressed && e.ClickCount == 1 ? QuoteAt(point.Position) : null;
        base.OnPointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ReaderBodySegment? pressed = _pressedQuote;
        _pressedQuote = null;
        // A drag that selected text is a selection, not a link click.
        if (pressed?.Quote == null || e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd
            || !ReferenceEquals(QuoteAt(e.GetPosition(this)), pressed)) return;
        e.Handled = true;
        RaiseEvent(new ReaderQuoteInvokedEventArgs(QuoteInvokedEvent, pressed.Quote));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) SetLinkCursor(QuoteAt(e.GetPosition(this)) != null);
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
