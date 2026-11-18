using System;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop;

// Decide the arrangement before measuring either child. Mutating Grid cells from
// SizeChanged made virtualized item heights depend on an earlier layout pass.
public sealed class ReaderPostLayout : Panel
{
    public static readonly AttachedProperty<bool> LargeReaderProperty =
        AvaloniaProperty.RegisterAttached<ReaderPostLayout, Control, bool>("LargeReader", inherits: true);
    public static readonly AttachedProperty<int> ThumbnailWidthProperty =
        AvaloniaProperty.RegisterAttached<ReaderPostLayout, Control, int>("ThumbnailWidth",
            ReaderDisplayPreferences.ReaderThumbnailWidthDefault, inherits: true);

    /// <summary>
    /// The effective media width of this post alone (already scaled for the large reader); 0 keeps the list-wide thumbnail width.
    /// Posts without text of their own use it for their larger image (R6).
    /// </summary>
    public static readonly StyledProperty<int> MediaWidthProperty =
        AvaloniaProperty.Register<ReaderPostLayout, int>(nameof(MediaWidth));
    /// <summary>The percentage the focused card's media is enlarged to; set on the list (inherited), 100 while the option is off.</summary>
    public static readonly AttachedProperty<int> FocusScalePercentProperty =
        AvaloniaProperty.RegisterAttached<ReaderPostLayout, Control, int>("FocusScalePercent", 100, inherits: true);
    /// <summary>True inside the focused (selected) card; set by the list's item style, inherited by everything in the card.</summary>
    public static readonly AttachedProperty<bool> FocusedProperty =
        AvaloniaProperty.RegisterAttached<ReaderPostLayout, Control, bool>("Focused", false, inherits: true);

    /// <summary>
    /// The visible height of the reader list (its scroll viewport), NaN while unknown; set on the list (inherited). The focused card's
    /// enlarged media is capped by it so the whole medium fits (<see cref="ReaderMediaFit"/>). Not a measure input of the post layout itself.
    /// </summary>
    public static readonly AttachedProperty<double> VisibleHeightProperty =
        AvaloniaProperty.RegisterAttached<ReaderPostLayout, Control, double>("VisibleHeight", double.NaN, inherits: true);

    static ReaderPostLayout() => AffectsMeasure<ReaderPostLayout>(LargeReaderProperty, ThumbnailWidthProperty, MediaWidthProperty,
        FocusScalePercentProperty, FocusedProperty);

    public int MediaWidth { get => GetValue(MediaWidthProperty); set => SetValue(MediaWidthProperty, value); }

    /// <summary>How much larger the media of a card is drawn: only the focused card, only while the option is on.</summary>
    internal static double FocusFactor(bool focused, int scalePercent) => focused && scalePercent > 100 ? scalePercent / 100d : 1;

    /// <summary>
    /// The enlargement the focused card's media really gets in a card <paramref name="width"/> wide: the configured
    /// <paramref name="factor"/>, but only as far as the body keeps its room beside the media (the arrangement is decided with the
    /// ordinary thumbnail, so the enlargement never moves the text under the picture). Never below 1; a card too narrow for the text
    /// beside even the ordinary thumbnail keeps the factor (its text is under the media anyway, the column bounds the picture).
    /// </summary>
    internal static double BoundedFocusFactor(double factor, double thumbnailWidth, double width, bool largeReader)
    {
        if (!(factor > 1) || !(thumbnailWidth > 0) || !double.IsFinite(width) || !Beside(width, thumbnailWidth, largeReader)) return factor;
        double room = width - BodyRoom(largeReader) - MediaPadding(largeReader);
        return Math.Clamp(room / thumbnailWidth, 1, factor);
    }

    private bool _beside;
    private double _width = double.PositiveInfinity;

    private bool IsLargeReader => GetValue(LargeReaderProperty);
    private double OrdinaryThumbnailWidth =>
        MediaWidth > 0 ? MediaWidth : ReaderDisplayPreferences.EffectiveThumbnailWidth(GetValue(ThumbnailWidthProperty), IsLargeReader);
    private double ThumbnailWidth => OrdinaryThumbnailWidth
        * BoundedFocusFactor(FocusFactor(GetValue(FocusedProperty), GetValue(FocusScalePercentProperty)), OrdinaryThumbnailWidth, _width, IsLargeReader);

    // The threshold includes the media column and the body breathing room. Defaults remain 600/760 DIP, while a
    // wider configured thumbnail grows the threshold instead of making the body overlap the media. It is decided with the
    // ordinary (unenlarged) thumbnail: the focused card keeps the arrangement of its neighbours.
    private static bool Beside(double width, double thumbnailWidth, bool largeReader) =>
        width >= thumbnailWidth + MediaPadding(largeReader) + BodyRoom(largeReader);
    private static double BodyRoom(bool largeReader) => largeReader ? 384 : 332;
    private static double MediaPadding(bool largeReader) => largeReader ? 16 : 28;
    private double ThumbnailMediaWidth => ThumbnailWidth + MediaPadding(IsLargeReader);

    // Smoke seam: how many times the post layouts were measured (scroll cost of the card width modes).
    internal static long MeasurePasses;

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasurePasses++;
        if (Children.Count != 2) return default;
        Control media = Children[0], body = Children[1];
        double width = _width = availableSize.Width;
        media.Measure(new Size(Math.Min(width, ThumbnailMediaWidth), availableSize.Height));
        bool beside = _beside = Beside(width, OrdinaryThumbnailWidth, IsLargeReader);
        double gap = media.IsVisible ? (beside ? 12 : 8) : 0;
        body.Measure(new Size(beside ? Math.Max(0, width - media.DesiredSize.Width - gap) : width,
            availableSize.Height));
        return beside
            ? new Size(media.DesiredSize.Width + gap + body.DesiredSize.Width,
                Math.Max(media.DesiredSize.Height, body.DesiredSize.Height))
            : new Size(Math.Max(media.DesiredSize.Width, body.DesiredSize.Width),
                media.DesiredSize.Height + gap + body.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return finalSize;
        Control media = Children[0], body = Children[1];
        bool beside = _beside;
        double gap = media.IsVisible ? (beside ? 12 : 8) : 0;
        double mediaWidth = Math.Min(finalSize.Width, media.DesiredSize.Width);
        media.Arrange(new Rect(0, 0, mediaWidth, media.DesiredSize.Height));
        body.Arrange(beside
            ? new Rect(mediaWidth + gap, 0, Math.Max(0, finalSize.Width - mediaWidth - gap), finalSize.Height)
            : new Rect(0, media.DesiredSize.Height + gap, finalSize.Width,
                Math.Max(0, finalSize.Height - media.DesiredSize.Height - gap)));
        return finalSize;
    }
}
