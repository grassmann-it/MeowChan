using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Localization;
using Avalonia.Threading;

namespace MeowChan.Desktop;

/// <summary>
/// GL5c (UX spec S1 "Info = eine Zeile"): the pure rules of the gallery's single info line. The line has two columns: the left one holds
/// the segments "size · zoom · source · number" (the quality text and the scope), the integrity note (safety) and the rating (only while
/// no rating button is on the bar); the right one is the status segment (slideshow ready or running, the overlay note). The status is
/// transient where it only confirms (the slideshow's "ready" stays <c>GalleryStatusDwellMs</c>, then fades out); a running slideshow is a
/// state and stays, as do reasons, errors and warnings (safety).
/// </summary>
internal static class GalleryInfoLine
{
    /// <summary>The slideshow statuses that fade out after the dwell time: only the confirmation "ready". Running, loading and every reason or error stay.</summary>
    internal static bool IsTransient(SlideshowStatus status) => status == SlideshowStatus.Ready;

    /// <summary>Whether the status segment still shows: a transient status is gone once its dwell time ran out.</summary>
    internal static bool StatusShown(bool transient, bool expired) => !(transient && expired);

    /// <summary>The dwell time within its supported range (the setting is clamped on load; a viewer given a stray value clamps too).</summary>
    internal static int ClampDwell(int milliseconds) => Math.Clamp(milliseconds, Config.GalleryStatusDwellMsMinimum, Config.GalleryStatusDwellMsMaximum);

    /// <summary>
    /// The "·" separators of the left column: one stands before every segment that is shown while an earlier segment is shown too, so the
    /// first shown segment has none and a hidden segment leaves no stray dot behind. Result[i] belongs to segment i.
    /// </summary>
    internal static bool[] Separators(ReadOnlySpan<bool> shown)
    {
        bool[] result = new bool[shown.Length];
        bool any = false;
        for (int index = 0; index < shown.Length; index++)
        {
            result[index] = shown[index] && any;
            any |= shown[index];
        }
        return result;
    }
}

internal sealed partial class GalleryViewer
{
    private readonly Grid _infoLine = new() { Name = "GalleryInfoLine", ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
    private readonly WrapPanel _infoSegments = new() { Name = "GalleryInfoSegments", Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _statusSegment = new() { Name = "GalleryStatusSegment", Orientation = Orientation.Horizontal, Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    // The segments of the left column in order, and the separator before each (the first has none).
    private TextBlock[] _infoSegmentTexts = Array.Empty<TextBlock>();
    private readonly TextBlock[] _infoSeparators = new TextBlock[4];
    private readonly DispatcherTimer _statusDwellTimer = new();
    // GL6: "Rating saved", the confirmation after a rating was written; transient like "ready" (GalleryStatusDwellMs) and a polite live region.
    private readonly TextBlock _ratingSaved = new() { Name = "GalleryRatingSaved", IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _ratingSavedTimer = new();
    private string _statusKey = string.Empty;
    private bool _statusExpired;
    private int _statusDwellMs = Config.GalleryStatusDwellMsDefault;

    /// <summary>How long a transient status stays (<see cref="Config.GalleryStatusDwellMs"/>, clamped to its range).</summary>
    internal int StatusDwellMs
    {
        get => _statusDwellMs;
        set => _statusDwellMs = GalleryInfoLine.ClampDwell(value);
    }

    /// <summary>
    /// GL6: confirms that the shown medium's rating was saved: a transient "Rating saved" in the status segment for <c>GalleryStatusDwellMs</c>,
    /// announced politely. The hidden "Hints" choice silences it like the other confirmations; a shown medium that changed meanwhile
    /// clears it (<see cref="ClearRatingSaved"/>).
    /// </summary>
    internal void ConfirmRatingSaved()
    {
        if (_terminalRetirement || _viewerInteractionStopped || _entry == null) return;
        _ratingSavedTimer.Stop();
        if (_modeTools != null && !BuildChromePlan().Hints) { ClearRatingSaved(); return; }
        _ratingSaved.Text = Texts.T("gallery.rating.saved");
        _ratingSaved.IsVisible = true;
        _ratingSavedTimer.Interval = TimeSpan.FromMilliseconds(_statusDwellMs);
        _ratingSavedTimer.Start();
        UpdateInfoLine();
    }

    // The confirmation belongs to the medium it was said for: a new medium, the end of the dwell and the viewer's retirement clear it.
    private void ClearRatingSaved()
    {
        _ratingSavedTimer.Stop();
        if (!_ratingSaved.IsVisible && string.IsNullOrEmpty(_ratingSaved.Text)) return;
        _ratingSaved.IsVisible = false;
        _ratingSaved.Text = string.Empty;
        UpdateInfoLine();
    }

    /// <summary>Whether the status segment shows a transient text now (tests and smokes).</summary>
    internal bool IsStatusExpired => _statusExpired;
    /// <summary>The info line (tests and smokes).</summary>
    internal Panel InfoLine => _infoLine;

    // Builds the one line: left the segments with their separators, right the status segment. The error and warning lines stay below it.
    private Control BuildInfoContent()
    {
        _infoSegmentTexts = new[] { _quality, _scope, _provenance, _ratingStatus };
        for (int index = 0; index < _infoSegmentTexts.Length; index++)
        {
            if (index > 0)
            {
                TextBlock separator = new() { Text = "·", IsVisible = false, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                separator.Classes.Add("workspace-meta");
                Avalonia.Automation.AutomationProperties.SetAccessibilityView(separator, Avalonia.Automation.AccessibilityView.Raw);
                _infoSeparators[index] = separator;
                _infoSegments.Children.Add(separator);
            }
            TextBlock segment = _infoSegmentTexts[index];
            segment.VerticalAlignment = VerticalAlignment.Center;
            _infoSegments.Children.Add(segment);
            segment.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty || e.Property == IsVisibleProperty) UpdateInfoLine();
            };
        }
        _infoSegments.Children.Add(_position); // never shown: the position stands between the arrows
        AutomationProperties.SetLiveSetting(_ratingSaved, AutomationLiveSetting.Polite);
        _ratingSavedTimer.Tick += (_, _) => ClearRatingSaved();
        foreach (TextBlock text in new[] { _ratingSaved, _slideshowStatus, _overlayFallbackNotice })
        {
            text.Classes.Add("workspace-meta");
            text.Margin = default;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.TextAlignment = Avalonia.Media.TextAlignment.Right;
            text.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty || e.Property == IsVisibleProperty) UpdateInfoLine();
            };
            _statusSegment.Children.Add(text);
        }
        _infoLine.Children.Add(_infoSegments);
        _infoLine.Children.Add(_statusSegment); Grid.SetColumn(_statusSegment, 1);
        _statusDwellTimer.Tick += (_, _) =>
        {
            _statusDwellTimer.Stop();
            if (_terminalRetirement || _viewerInteractionStopped) return;
            _statusExpired = true;
            RefreshSlideshowStatus();
        };
        StackPanel content = new() { Spacing = 2 };
        content.Children.Add(_infoLine);
        content.Children.Add(_warnings);
        content.Children.Add(_favoriteError);
        return content;
    }

    // The status text for the slideshow state after the dwell rule: a changed text starts a new dwell; a transient one that ran out is gone.
    private string ApplyStatusDwell(SlideshowStatus status, string text)
    {
        bool transient = GalleryInfoLine.IsTransient(status) && text.Length > 0;
        if (text != _statusKey)
        {
            _statusKey = text;
            _statusExpired = false;
            _statusDwellTimer.Stop();
            if (transient)
            {
                _statusDwellTimer.Interval = TimeSpan.FromMilliseconds(_statusDwellMs);
                _statusDwellTimer.Start();
            }
        }
        return GalleryInfoLine.StatusShown(transient, _statusExpired) ? text : string.Empty;
    }

    /// <summary>
    /// Re-reads which segments of the line show: the separators, the line itself and the info block as a whole (an empty block takes no
    /// row of the dock). Cheap and idempotent; it sets only the separators, the line and the block, never a segment, so the segments'
    /// change handlers cannot loop.
    /// </summary>
    private void UpdateInfoLine()
    {
        if (_infoSegmentTexts.Length == 0) return;
        Span<bool> shown = stackalloc bool[_infoSegmentTexts.Length];
        for (int index = 0; index < shown.Length; index++)
            shown[index] = _infoSegmentTexts[index].IsVisible && !string.IsNullOrEmpty(_infoSegmentTexts[index].Text);
        bool[] separators = GalleryInfoLine.Separators(shown);
        for (int index = 1; index < separators.Length; index++) _infoSeparators[index].IsVisible = separators[index];
        bool status = _ratingSaved.IsVisible && !string.IsNullOrEmpty(_ratingSaved.Text)
            || _slideshowStatus.IsVisible && !string.IsNullOrEmpty(_slideshowStatus.Text)
            || _overlayFallbackNotice.IsVisible && !string.IsNullOrEmpty(_overlayFallbackNotice.Text);
        _statusSegment.IsVisible = status;
        bool line = status;
        foreach (bool one in shown) line |= one;
        _infoLine.IsVisible = line;
        // Hidden tools keep only a slideshow reason on the screen (as before); otherwise the block shows while anything in it does.
        bool block = (line || _warnings.IsVisible || _favoriteError.IsVisible) && (!_mediaFocus || _slideshowStatus.IsVisible || _ratingSaved.IsVisible);
        if (_infoOverlay.IsVisible != block)
        {
            _infoOverlay.IsVisible = block;
            ApplyDockMargin();
        }
    }
}
