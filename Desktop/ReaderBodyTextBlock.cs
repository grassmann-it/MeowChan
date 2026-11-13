using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls.Documents;
using System.Text;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Archive;

namespace MeowChan.Desktop;

/// <summary>A click or Enter on an external link; carries the validated address only.</summary>
internal sealed class ReaderLinkInvokedEventArgs(RoutedEvent routedEvent, Uri uri) : RoutedEventArgs(routedEvent)
{
    public Uri Uri { get; } = uri;
}

internal sealed class ReaderQuoteInvokedEventArgs(RoutedEvent routedEvent, ArchiveQuoteRow quote) : RoutedEventArgs(routedEvent)
{
    public ArchiveQuoteRow Quote { get; } = quote;
}

/// <summary>The pointer entered (Quote set, Area = the reference's first line in the control) or left (Quote null) an inline reference.</summary>
internal sealed class ReaderQuoteHoverEventArgs(RoutedEvent routedEvent, ArchiveQuoteRow? quote, Rect area) : RoutedEventArgs(routedEvent)
{
    public ArchiveQuoteRow? Quote { get; } = quote;
    public Rect Area { get; } = area;
}

// Hovering an inline reference raises QuoteHover (the page shows the quote preview); leaving it raises it with null.
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

    public static readonly RoutedEvent<ReaderLinkInvokedEventArgs> LinkInvokedEvent =
        RoutedEvent.Register<ReaderBodyTextBlock, ReaderLinkInvokedEventArgs>(nameof(LinkInvoked), RoutingStrategies.Bubble);

    public static readonly StyledProperty<bool> ExpandedProperty =
        AvaloniaProperty.Register<ReaderBodyTextBlock, bool>(nameof(Expanded), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly DirectProperty<ReaderBodyTextBlock, bool> IsFoldableProperty =
        AvaloniaProperty.RegisterDirect<ReaderBodyTextBlock, bool>(nameof(IsFoldable), block => block.IsFoldable);

    public static readonly DirectProperty<ReaderBodyTextBlock, string> FoldLabelProperty =
        AvaloniaProperty.RegisterDirect<ReaderBodyTextBlock, string>(nameof(FoldLabel), block => block._foldLabel);

    public static readonly RoutedEvent<ReaderQuoteHoverEventArgs> QuoteHoverEvent =
        RoutedEvent.Register<ReaderBodyTextBlock, ReaderQuoteHoverEventArgs>(nameof(QuoteHover), RoutingStrategies.Bubble);

    /// <summary>
    /// How spoilers are painted. Inherited, so one value set on the post list (or the reader pane) reaches every body;
    /// an undefined value counts as <see cref="ReaderSpoilerMode.Hide"/>.
    /// </summary>
    public static readonly AttachedProperty<ReaderSpoilerMode> SpoilerModeProperty =
        AvaloniaProperty.RegisterAttached<ReaderBodyTextBlock, Control, ReaderSpoilerMode>("SpoilerMode", ReaderSpoilerMode.Hide,
            inherits: true, coerce: (_, value) => ReaderDisplayLimits.ClampSpoilerMode(value));

    /// <summary>
    /// A body over this many lines is folded until the post is expanded (presentation, not a resource limit). Inherited and
    /// clamped to <see cref="ReaderDisplayLimits.FoldLinesMinimum"/>..<see cref="ReaderDisplayLimits.FoldLinesMaximum"/>.
    /// </summary>
    public static readonly AttachedProperty<int> FoldThresholdLinesProperty =
        AvaloniaProperty.RegisterAttached<ReaderBodyTextBlock, Control, int>("FoldThresholdLines", ReaderDisplayLimits.FoldLinesDefault,
            inherits: true, coerce: (_, value) => ReaderDisplayLimits.ClampFoldLines(value));

    public static ReaderSpoilerMode GetSpoilerMode(Control control) => control.GetValue(SpoilerModeProperty);
    public static void SetSpoilerMode(Control control, ReaderSpoilerMode value) => control.SetValue(SpoilerModeProperty, value);
    public static int GetFoldThresholdLines(Control control) => control.GetValue(FoldThresholdLinesProperty);
    public static void SetFoldThresholdLines(Control control, int value) => control.SetValue(FoldThresholdLinesProperty, value);

    // Fade of the last lines of a folded body: the surface behind (card, active card or page) shows through.
    private static readonly IBrush FoldFade = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 0.72), new GradientStop(Colors.Transparent, 1) }
    }.ToImmutable();

    private int _lineCount;
    private bool _foldableReported;
    private bool _spoilersHovered;
    private string _foldLabel = string.Empty;

    /// <summary>The current fold threshold (clamped), in wrapped lines.</summary>
    internal int FoldThresholdLines => GetValue(FoldThresholdLinesProperty);
    /// <summary>The lines a folded body shows: three quarters of the threshold.</summary>
    internal int FoldVisibleLines => ReaderDisplayLimits.FoldVisibleLines(FoldThresholdLines);
    internal ReaderSpoilerMode SpoilerMode => GetValue(SpoilerModeProperty);

    /// <summary>The long body is shown in full (the owning post keeps this for the session).</summary>
    public bool Expanded
    {
        get => GetValue(ExpandedProperty);
        set => SetValue(ExpandedProperty, value);
    }

    /// <summary>Wrapped lines at the current width, measured with the last layout.</summary>
    internal int LineCount => _lineCount;
    public bool IsFoldable => _lineCount > FoldThresholdLines;
    public string FoldLabel => _foldLabel;
    internal bool IsFolded => IsFoldable && !Expanded;

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

    /// <summary>The spoilers are painted readable: revealed by the post, always shown by the mode, or shown while the pointer is over the body in hover mode.</summary>
    internal bool SpoilersShown => ReaderSpoilerPolicy.Shown(SpoilerMode, SpoilersRevealed, _spoilersHovered);

    internal bool HasHiddenSpoilers => Document is { HasSpoilers: true } && !SpoilersShown;

    /// <summary>A click on a spoiler that is not yet revealed for good keeps it shown (not needed when the mode shows everything anyway).</summary>
    private bool SpoilerClickReveals => ReaderSpoilerPolicy.ClickReveals(SpoilerMode, SpoilersRevealed);

    /// <summary>The pointer entered or left the body (the hover mode shows every spoiler of the post while it is inside).</summary>
    internal void SetSpoilerHover(bool hovered)
    {
        if (_spoilersHovered == hovered) return;
        _spoilersHovered = hovered;
        if (SpoilerMode == ReaderSpoilerMode.Hover) RefreshSpoilers();
    }

    // Only the painting and the accessible name depend on the spoiler state; the selection stays.
    private void RefreshSpoilers()
    {
        if (Document is not { HasSpoilers: true }) return;
        if (Document.IsFormatted)
        {
            int start = SelectionStart, end = SelectionEnd;
            Render(Document);
            if (start != end) { SelectionStart = start; SelectionEnd = end; }
        }
        UpdateAccessibleName();
    }

    /// <summary>
    /// What a screen reader gets while spoilers are hidden: the post text with every hidden spoiler replaced by one neutral
    /// "Spoiler, hidden" (the painted text only hides the pixels; the accessibility tree must hide the words too).
    /// After the reveal, and for a body without spoilers, the real text is exposed as before.
    /// </summary>
    internal string AccessibleText() => Document is not { } document ? PlainText
        : ReaderSpoilerPolicy.AccessibleText(document, SpoilersShown, Localization.Texts.T("reader.spoilerHidden"));

    // The peer answers with the current language at every query; the property only announces that the name changed
    // (a screen reader that already read the hidden name learns when the real text becomes available).
    private void UpdateAccessibleName()
    {
        if (HasHiddenSpoilers) AutomationProperties.SetName(this, AccessibleText());
        else ClearValue(AutomationProperties.NameProperty);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new BodyPeer(this);

    private sealed class BodyPeer(ReaderBodyTextBlock owner) : TextBlockAutomationPeer(owner)
    {
        protected override string? GetNameCore() => owner.HasHiddenSpoilers ? owner.AccessibleText() : base.GetNameCore();
    }

    public event EventHandler<ReaderQuoteHoverEventArgs>? QuoteHover
    {
        add => AddHandler(QuoteHoverEvent, value);
        remove => RemoveHandler(QuoteHoverEvent, value);
    }

    public event EventHandler<ReaderLinkInvokedEventArgs>? LinkInvoked
    {
        add => AddHandler(LinkInvokedEvent, value);
        remove => RemoveHandler(LinkInvokedEvent, value);
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

    private static readonly FontFamily CodeFont = new("monospace");
    private ReaderBodySegment? _pressedQuote;
    private ArchiveQuoteRow? _hoverQuote;
    private bool _linkCursor;

    static ReaderBodyTextBlock() => AffectsMeasure<ReaderBodyTextBlock>(ExpandedProperty, FoldThresholdLinesProperty);

    public ReaderBodyTextBlock() => UpdateReadingMetrics();

    // A long body is clipped to its first lines inside the same measure pass, so the list never sees the full height.
    protected override Size MeasureOverride(Size availableSize)
    {
        Size size = base.MeasureOverride(availableSize);
        _lineCount = TextLayout.TextLines.Count; // Bindings read the getter when the change is raised.
        bool foldable = IsFoldable; // Also changes when only the threshold moved.
        if (foldable != _foldableReported)
        {
            bool previousFoldable = _foldableReported;
            _foldableReported = foldable;
            RaisePropertyChanged(IsFoldableProperty, previousFoldable, foldable);
        }
        int visibleLines = FoldVisibleLines;
        string label = !IsFoldable ? string.Empty : Expanded ? Localization.Texts.T("reader.foldLess")
            : Localization.Texts.Count("reader.foldMore", _lineCount - visibleLines);
        if (label != _foldLabel)
        {
            string previous = _foldLabel;
            _foldLabel = label;
            RaisePropertyChanged(FoldLabelProperty, previous, label);
        }
        bool folded = IsFolded;
        if (ClipToBounds != folded) ClipToBounds = folded;
        if ((OpacityMask != null) != folded) OpacityMask = folded ? FoldFade : null;
        return folded ? new Size(size.Width, Math.Min(size.Height, visibleLines * LineHeight)) : size;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsTextOnlyProperty || change.Property == FontSizeProperty) UpdateReadingMetrics();
        if (change.Property == DocumentProperty) ShowDocument(change.GetNewValue<ReaderBodyDocument?>());
        else if ((change.Property == SpoilersRevealedProperty || change.Property == SpoilerModeProperty) && Document is { IsFormatted: true })
        {
            Render(Document);
        }
        if (change.Property == DocumentProperty || change.Property == SpoilersRevealedProperty || change.Property == SpoilerModeProperty)
            UpdateAccessibleName();
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
        _hoverQuote = null;
        _spoilersHovered = false; // A recycled body starts a new post; its pointer state is unknown until the next enter.
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
                case ReaderBodySegmentKind.QuoteMarker:
                    run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
                    break;
                case ReaderBodySegmentKind.DeadNote:
                    run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
                    run.FontStyle = FontStyle.Italic;
                    break;
            }
            if (segment.Link != null && segment.Kind is ReaderBodySegmentKind.Text or ReaderBodySegmentKind.Greentext)
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
                if (!SpoilersShown) run[!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.ReaderSpoiler");
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
        && (ClickRevealsSegment(segment) || segment.Quote != null || segment.Link != null) ? segment : null;

    // A click on a spoiler keeps it shown (unless the mode shows it anyway); a link that sits in or spans a hidden spoiler
    // is revealed first and opened by the next click.
    private bool ClickRevealsSegment(ReaderBodySegment segment) =>
        segment.IsSpoiler ? SpoilerClickReveals : segment.LinkNeedsReveal && !SpoilersShown;

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
        if (ClickRevealsSegment(pressed)) { SpoilersRevealed = true; SetLinkCursor(false); }
        else if (pressed.Quote != null) RaiseEvent(new ReaderQuoteInvokedEventArgs(QuoteInvokedEvent, pressed.Quote));
        else if (ReaderExternalLink.ForActivation(pressed, SpoilersShown, SelectionStart != SelectionEnd) is { } link)
            RaiseEvent(new ReaderLinkInvokedEventArgs(LinkInvokedEvent, link));
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
        // Enter opens (after its confirmation) the link inside the selection, or the only link of the body; on a post with
        // media and no selection it stays the media key and reaches the reader.
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && !HasHiddenSpoilers
            && ReaderExternalLink.ForKeyboard(Document, SelectionStart, SelectionEnd, SpoilersShown,
                DataContext is ArchivePostRow { HasActivatableMedia: true }) is { } keyboardLink)
        {
            e.Handled = true;
            RaiseEvent(new ReaderLinkInvokedEventArgs(LinkInvokedEvent, keyboardLink));
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { SetHoverQuote(null); return; }
        Point position = e.GetPosition(this);
        SetLinkCursor(ActionAt(position) != null);
        SetHoverQuote(QuoteAt(position));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        SetSpoilerHover(true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetSpoilerHover(false);
        SetLinkCursor(false);
        SetHoverQuote(null);
    }

    private void SetHoverQuote(ReaderBodySegment? segment)
    {
        if (ReferenceEquals(_hoverQuote, segment?.Quote)) return;
        _hoverQuote = segment?.Quote;
        Rect area = default;
        if (segment != null)
            foreach (Rect rect in TextLayout.HitTestTextRange(segment.Start, segment.Length))
            {
                area = new Rect(rect.X + Padding.Left, rect.Y + Padding.Top, rect.Width, rect.Height);
                break;
            }
        RaiseEvent(new ReaderQuoteHoverEventArgs(QuoteHoverEvent, _hoverQuote, area));
    }

    private void SetLinkCursor(bool link)
    {
        if (_linkCursor == link) return;
        _linkCursor = link;
        if (link) Cursor = new Cursor(StandardCursorType.Hand);
        else ClearValue(CursorProperty);
    }
}
