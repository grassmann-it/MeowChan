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

    static ReaderPostLayout() => AffectsMeasure<ReaderPostLayout>(LargeReaderProperty, ThumbnailWidthProperty, MediaWidthProperty,
        FocusScalePercentProperty, FocusedProperty);

    public int MediaWidth { get => GetValue(MediaWidthProperty); set => SetValue(MediaWidthProperty, value); }

    /// <summary>How much larger the media of a card is drawn: only the focused card, only while the option is on.</summary>
    internal static double FocusFactor(bool focused, int scalePercent) => focused && scalePercent > 100 ? scalePercent / 100d : 1;

    private bool _beside;

    private bool IsLargeReader => GetValue(LargeReaderProperty);
    private double ThumbnailWidth =>
        (MediaWidth > 0 ? MediaWidth : ReaderDisplayPreferences.EffectiveThumbnailWidth(GetValue(ThumbnailWidthProperty), IsLargeReader))
        * FocusFactor(GetValue(FocusedProperty), GetValue(FocusScalePercentProperty));

    // The threshold includes the media column and the body breathing room. Defaults remain 600/760 DIP, while a
    // wider configured thumbnail grows the threshold instead of making the body overlap the media.
    private bool Beside(double width) => width >= ThumbnailMediaWidth + (IsLargeReader ? 384 : 332);
    private double ThumbnailMediaWidth => ThumbnailWidth + (IsLargeReader ? 16 : 28);

    // Smoke seam: how many times the post layouts were measured (scroll cost of the card width modes).
    internal static long MeasurePasses;

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasurePasses++;
        if (Children.Count != 2) return default;
        Control media = Children[0], body = Children[1];
        double width = availableSize.Width;
        media.Measure(new Size(Math.Min(width, ThumbnailMediaWidth), availableSize.Height));
        bool beside = _beside = Beside(width);
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
