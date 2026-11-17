using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    // Display-only channels. Busy, request generations and cancellation remain owned by each operation.
    private readonly Dictionary<string, string> _operationErrors = new(StringComparer.Ordinal);
    // ARCH-DENSE-01: informational messages float over the content, so they expire instead of
    // covering the last rows indefinitely. Errors stay in the in-flow banner until resolved;
    // loading indicators stay while their operation runs.
    private readonly DispatcherTimer _statusMessageTimer = new();
    private bool _statusMessageExpired;
    private bool _statusOverlayHovered;

    internal static TimeSpan StatusMessageDuration(string text) =>
        TimeSpan.FromMilliseconds(Math.Clamp(5000 + 50 * text.Length, 5000, 15000));

    private void InitializeStatusOverlay()
    {
        _statusMessageTimer.Tick += (_, _) => ExpireStatusMessage();
        Border overlay = ArchiveStatusOverlay;
        // Hovering keeps the message (and its tooltip) readable; leaving restarts its time.
        _ = new MeowChan.Desktop.TransientHover(overlay, over =>
        {
            _statusOverlayHovered = over;
            if (over) _statusMessageTimer.Stop();
            else if (!MeowChan.Desktop.TransientHover.WindowUsable(overlay)) ExpireStatusMessage();
            else RestartStatusMessageTimer();
        });
        // A click on the message dismisses it, so it never has to be waited out to reach what it covers.
        overlay.PointerPressed += (_, e) => { e.Handled = true; ExpireStatusMessage(); };
        ArchiveSplit.LayoutUpdated += (_, _) => { if (ArchiveStatusOverlay.IsVisible) PlaceStatusOverlay(); };
    }

    private void BeginContentFeedback()
    {
        _operationErrors.Remove("content");
        _operationErrors.Remove("reference");
        _operationErrors.Remove("action");
        RenderOperationErrors();
    }
    private void ClearOperationError(string operation)
    {
        _operationErrors.Remove(operation);
        RenderOperationErrors();
    }
    private void OperationMessage(string operation, string text, bool error = false)
    {
        if (_ended) return;
        if (error && !string.IsNullOrWhiteSpace(text)) _operationErrors[operation] = text;
        else if (operation == "media") _operationErrors.Remove(operation);
        else
        {
            ArchiveMessage.Text = text;
            _statusMessageExpired = false;
            RestartStatusMessageTimer();
        }
        RenderOperationErrors();
    }
    private void RenderOperationErrors()
    {
        if (_ended) return;
        ArchiveErrorText.Text = string.Join(Environment.NewLine, _operationErrors.Values);
        ArchiveErrorBanner.IsVisible = _operationErrors.Count != 0;
        // ARCH-DENSE-01: errors stay in layout flow below the boxes and are never auto-hidden.
        // Without an error the status row is empty and the split spans it, so the boxes end
        // at the page margin instead of above an unused spacing.
        ArchiveStatusStrip.IsVisible = ArchiveErrorBanner.IsVisible;
        Grid.SetRowSpan(ArchiveSplit, ArchiveStatusStrip.IsVisible ? 1 : 2);
        RefreshStatusLine();
    }
    private void RestartStatusMessageTimer()
    {
        _statusMessageTimer.Stop();
        string text = ArchiveMessage.Text ?? string.Empty;
        if (_ended || _statusMessageExpired || _statusOverlayHovered || string.IsNullOrWhiteSpace(text)) return;
        _statusMessageTimer.Interval = StatusMessageDuration(text);
        _statusMessageTimer.Start();
    }
    // The text stays for later reads; only its overlay presentation ends.
    private void ExpireStatusMessage()
    {
        _statusMessageTimer.Stop();
        if (_ended || string.IsNullOrWhiteSpace(ArchiveMessage.Text)) return;
        _statusMessageExpired = true;
        RefreshStatusLine();
    }
    // Text first, then visibility, like the other archive live regions. Empty
    // channels leave the shared line so no spacing or blank wrap is reserved;
    // the overlay itself is shown only while any channel has something to show.
    private void RefreshStatusLine()
    {
        ArchiveContentLoadingText.IsVisible = !string.IsNullOrWhiteSpace(ArchiveContentLoadingText.Text);
        ArchiveMessage.IsVisible = !_statusMessageExpired && !string.IsNullOrWhiteSpace(ArchiveMessage.Text);
        ArchiveMediaLoadingText.IsVisible = !string.IsNullOrWhiteSpace(ArchiveMediaLoadingText.Text);
        TextBlock[] texts = { ArchiveContentLoadingText, ArchiveMessage, ArchiveMediaLoadingText };
        bool any = ArchiveContentLoading.IsVisible || ArchiveMediaLoading.IsVisible || texts.Any(text => text.IsVisible);
        // The message is capped at a few lines; the tooltip always carries the full text.
        string tip = string.Join(Environment.NewLine, texts.Where(text => text.IsVisible).Select(text => text.Text));
        ToolTip.SetTip(ArchiveStatusOverlay, tip.Length == 0 ? null : tip);
        if (any) PlaceStatusOverlay();
        ArchiveStatusOverlay.IsVisible = any;
        if (!any) _statusOverlayHovered = false;
    }
    // The overlay floats at the bottom left of the column that shows the reader (side by side)
    // or the stacked list, above that column's docked footer (result pager, post pager, media
    // or reference footer), so it never covers a control the user needs to reach.
    private void PlaceStatusOverlay()
    {
        Grid split = ArchiveSplit;
        double bottom = 8;
        Control[] footers = { ArchiveResultsBar, ArchivePostNavigation, ArchiveMediaFooter, ArchiveReferencesNavigation };
        foreach (Control footer in footers)
        {
            if (!footer.IsEffectivelyVisible || footer.Bounds.Height <= 0 || footer.TranslatePoint(default, split) is not Point top) continue;
            // Only footers in the overlay's column count; a side-by-side list pager is never covered.
            Rect box = new(top, footer.Bounds.Size);
            Rect column = new(split.Bounds.Width - split.ColumnDefinitions[2].ActualWidth, 0, split.ColumnDefinitions[2].ActualWidth, split.Bounds.Height);
            if (!box.Intersects(column)) continue;
            bottom = Math.Max(bottom, split.Bounds.Height - top.Y + 6);
        }
        Thickness margin = new(8, 8, 8, bottom);
        if (ArchiveStatusOverlay.Margin != margin) ArchiveStatusOverlay.Margin = margin;
    }
    private void UpdateFeedbackVisuals()
    {
        ArchiveContentLoading.IsLoading = ArchiveContentLoading.IsVisible = _busy && !_ended;
        ArchiveMediaLoading.IsLoading = ArchiveMediaLoading.IsVisible = _mediaLoading && !_ended;
        ArchiveContentLoadingText.Text = _busy && !_ended ? Texts.T("archive.loading") : string.Empty;
        ArchiveMediaLoadingText.Text = _mediaLoading ? Texts.T("archive.loadingMedia") : string.Empty;
        if (_ended) _statusMessageTimer.Stop();
        RefreshStatusLine();
    }
    internal Action<string> CaptureActionErrorReporter()
    {
        int request = _contentRequest;
        MeowChan.ResourceId? thread = _selectedThread;
        return text => { if (!_ended && request == _contentRequest && thread == _selectedThread) ShowActionError(text); };
    }
    internal void ShowActionError(string text) => OperationMessage("action", text, true);
}
