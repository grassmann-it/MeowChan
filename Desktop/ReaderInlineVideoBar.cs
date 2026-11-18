using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace MeowChan.Desktop;

/// <summary>
/// What the minimal video bar of the reader shows at a given width (one shared control for a post's video and the media sidebar).
/// The set is fixed: play or pause, a slim timeline, the time, mute or unmute and, only where the owner offers it, "Open in gallery".
/// Speed, repeat, A–B loop, next frame and volume belong to the gallery (their keys work in the reader, see the reader video keys).
/// Parts leave in this order when the bar gets narrow: the time first, then "Open in gallery", then the timeline, then mute; play stays.
/// All numbers are layout constants on the 8 dip grid (24 and 4 below <see cref="CompactBelow"/> keep a small portrait video usable).
/// </summary>
internal static class InlineVideoBarLayout
{
    internal const double RowHeight = WorkspaceMetrics.CompactRowHeight;
    internal const double WideButton = 32, CompactButton = 24;
    internal const double WideGap = 8, CompactGap = 4;
    /// <summary>Below this width the buttons are 24 dip with 4 dip gaps (a 9:16 video in a post is about 100 dip wide).</summary>
    internal const double CompactBelow = 160;
    /// <summary>The timeline is shown from this width on (it is as wide as what is left once the buttons are placed).</summary>
    internal const double SeekMinimum = 32;
    /// <summary>The time only shows when the timeline keeps at least this much beside it.</summary>
    internal const double SeekPreferred = 48;
    /// <summary>The names of the controls the bar may hold (the control set the tests and the smoke pin).</summary>
    internal static readonly string[] ControlNames = { "GalleryVideoPlayPause", "GalleryVideoSeek", "GalleryVideoTime", "GalleryVideoMute", "ReaderInlineVideoOpen" };

    internal readonly record struct Plan(bool Compact, double Button, double Gap, bool Mute, bool Seek, bool Open, bool Time, double SeekWidth);

    /// <summary>The parts that fit into <paramref name="width"/> dip; <paramref name="timeWidth"/> is the time text's own width.</summary>
    internal static Plan Decide(double width, double timeWidth, bool hasOpen)
    {
        if (!double.IsFinite(width)) width = 0;
        bool compact = width < CompactBelow;
        double button = compact ? CompactButton : WideButton, gap = compact ? CompactGap : WideGap;
        double used = button; // play
        bool mute = width >= used + gap + button;
        if (mute) used += gap + button;
        bool seek = mute && width >= used + gap + SeekMinimum;
        if (seek) used += gap + SeekMinimum;
        bool open = hasOpen && seek && width >= used + gap + button;
        if (open) used += gap + button;
        // What is left for the timeline once every other part has its place.
        double seekWidth = seek ? width - used + SeekMinimum : 0;
        bool time = seek && seekWidth - gap - timeWidth >= SeekPreferred;
        if (time) seekWidth -= gap + timeWidth;
        return new Plan(compact, button, gap, mute, seek, open, time, Math.Max(0, seekWidth));
    }
}

/// <summary>
/// The bar's layout: play on the left, mute (and "Open in gallery") on the right, the timeline in between taking what is left, the time
/// beside it when there is room. It is never wider than the width it is given. The time text is always measured, but arranged at zero
/// width while it does not fit, so its width is known when the bar widens again; the focusable parts are hidden, not just clipped.
/// </summary>
internal sealed class InlineVideoBarPanel : Panel
{
    private readonly Control _play, _seek, _time, _mute;
    private readonly Control? _open;
    private InlineVideoBarLayout.Plan _plan;

    internal InlineVideoBarPanel(Control play, Control seek, Control time, Control mute, Control? open = null)
    {
        _play = play; _seek = seek; _time = time; _mute = mute; _open = open;
        ClipToBounds = true;
        time.ClipToBounds = true;
        Children.Add(play); Children.Add(seek); Children.Add(time);
        if (open != null) Children.Add(open);
        Children.Add(mute);
        Height = InlineVideoBarLayout.RowHeight;
    }

    /// <summary>The parts shown at the last layout.</summary>
    internal InlineVideoBarLayout.Plan Current => _plan;
    internal Control Play => _play;
    internal Control Seek => _seek;
    internal Control Time => _time;
    internal Control Mute => _mute;
    internal Control? Open => _open;

    private static void SetVisible(Control control, bool visible) { if (control.IsVisible != visible) control.IsVisible = visible; }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The time is measured as it is (a hidden control is never measured, so it stays visible and is only arranged empty).
        _time.Measure(new Size(double.PositiveInfinity, InlineVideoBarLayout.RowHeight));
        double natural = 2 * InlineVideoBarLayout.WideButton + 2 * InlineVideoBarLayout.WideGap + 96 + _time.DesiredSize.Width + InlineVideoBarLayout.WideGap
            + (_open != null ? InlineVideoBarLayout.WideButton + InlineVideoBarLayout.WideGap : 0);
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : natural;
        _plan = InlineVideoBarLayout.Decide(width, _time.DesiredSize.Width, _open != null);
        Apply(_plan);
        _play.Measure(new Size(_plan.Button, _plan.Button));
        if (_plan.Mute) _mute.Measure(new Size(_plan.Button, _plan.Button));
        if (_plan.Seek) _seek.Measure(new Size(_plan.SeekWidth, InlineVideoBarLayout.RowHeight));
        if (_plan.Open) _open!.Measure(new Size(_plan.Button, _plan.Button));
        return new Size(width, InlineVideoBarLayout.RowHeight);
    }

    private void Apply(InlineVideoBarLayout.Plan plan)
    {
        SetVisible(_mute, plan.Mute);
        SetVisible(_seek, plan.Seek);
        if (_open != null) SetVisible(_open, plan.Open);
        foreach (Control button in new[] { _play, _mute, _open! })
        {
            if (button == null) continue;
            button.Width = plan.Button; button.Height = plan.Button;
            if (button is TemplatedControl templated) templated.Padding = new Thickness(plan.Compact ? 2 : 4);
        }
        if (_play is TemplatedControl round) round.CornerRadius = new CornerRadius(plan.Button / 2);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The plan follows the width the bar really gets (a parent may give less than it was offered).
        InlineVideoBarLayout.Plan plan = InlineVideoBarLayout.Decide(finalSize.Width, _time.DesiredSize.Width, _open != null);
        if (plan != _plan) { _plan = plan; Apply(plan); }
        double height = finalSize.Height;
        double y(double size) => (height - size) / 2;
        double x = 0;
        _play.Arrange(new Rect(x, y(plan.Button), Math.Min(plan.Button, finalSize.Width), plan.Button));
        x += plan.Button;
        double right = finalSize.Width;
        if (plan.Mute) { right -= plan.Button; _mute.Arrange(new Rect(right, y(plan.Button), plan.Button, plan.Button)); right -= plan.Gap; }
        if (plan.Open) { right -= plan.Button; _open!.Arrange(new Rect(right, y(plan.Button), plan.Button, plan.Button)); right -= plan.Gap; }
        if (plan.Time)
        {
            double timeWidth = _time.DesiredSize.Width;
            right -= timeWidth;
            _time.Arrange(new Rect(right, y(_time.DesiredSize.Height), timeWidth, _time.DesiredSize.Height));
            right -= plan.Gap;
        }
        else _time.Arrange(new Rect(x, y(_time.DesiredSize.Height), 0, _time.DesiredSize.Height));
        if (plan.Seek)
        {
            x += plan.Gap;
            _seek.Arrange(new Rect(x, 0, Math.Max(0, right - x), height));
        }
        return finalSize;
    }
}

/// <summary>
/// The row under a video's picture: the transport at exactly the picture frame's width (never wider), then the error line and the live
/// region at the block's width. <see cref="FrameWidth"/> is the frame's width as its owner measured it; without one (the media sidebar,
/// whose frame spans its column) the row's own width is the frame's.
/// </summary>
internal sealed class ReaderInlineVideoRow : Panel
{
    private const double Spacing = 4;
    private readonly Control _transport;
    private readonly Control[] _lines;

    internal ReaderInlineVideoRow(Control transport, params Control[] lines)
    {
        Name = "ReaderInlineVideoRow";
        _transport = transport;
        _lines = lines;
        Children.Add(transport);
        foreach (Control line in lines) Children.Add(line);
    }

    private double _frameWidth = double.NaN;

    /// <summary>The picture frame's width, or NaN to use the row's own. A new width lays the row out again (its owner measures it with an unchanged offer).</summary>
    internal double FrameWidth
    {
        get => _frameWidth;
        set
        {
            if (_frameWidth.Equals(value)) return;
            _frameWidth = value;
            InvalidateMeasure();
        }
    }

    /// <summary>The transport's width: the frame's, but not more than the row is given.</summary>
    internal static double TransportWidth(double frameWidth, double available) =>
        double.IsFinite(frameWidth) && frameWidth > 0 ? Math.Min(frameWidth, double.IsFinite(available) ? available : frameWidth)
            : double.IsFinite(available) ? available : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        double transportWidth = TransportWidth(FrameWidth, availableSize.Width);
        double lineWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : transportWidth;
        double height = 0;
        if (_transport.IsVisible)
        {
            _transport.Measure(new Size(transportWidth, double.PositiveInfinity));
            height += _transport.DesiredSize.Height;
        }
        foreach (Control line in _lines)
        {
            if (!line.IsVisible) continue;
            line.Measure(new Size(lineWidth, double.PositiveInfinity));
            if (line.DesiredSize.Height > 0 && height > 0) height += Spacing;
            height += line.DesiredSize.Height;
        }
        return new Size(Math.Max(transportWidth, lineWidth), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        double transportWidth = TransportWidth(FrameWidth, finalSize.Width);
        if (_transport.IsVisible)
        {
            _transport.Arrange(new Rect(0, y, transportWidth, _transport.DesiredSize.Height));
            y += _transport.DesiredSize.Height;
        }
        foreach (Control line in _lines)
        {
            if (!line.IsVisible) continue;
            if (line.DesiredSize.Height > 0 && y > 0) y += Spacing;
            line.Arrange(new Rect(0, y, finalSize.Width, line.DesiredSize.Height));
            y += line.DesiredSize.Height;
        }
        return finalSize;
    }
}
