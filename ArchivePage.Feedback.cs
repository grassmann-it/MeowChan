using System;
using System.Collections.Generic;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    // Display-only channels. Busy, request generations and cancellation remain owned by each operation.
    private readonly Dictionary<string, string> _operationErrors = new(StringComparer.Ordinal);

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
        else Control<TextBlock>("ArchiveMessage").Text = text;
        RenderOperationErrors();
    }
    private void RenderOperationErrors()
    {
        if (_ended) return;
        Control<TextBlock>("ArchiveErrorText").Text = string.Join(Environment.NewLine, _operationErrors.Values);
        Control<Ursa.Controls.Banner>("ArchiveErrorBanner").IsVisible = _operationErrors.Count != 0;
    }
    private void UpdateFeedbackVisuals()
    {
        Control<Ursa.Controls.LoadingIcon>("ArchiveContentLoading").IsLoading = Control<Ursa.Controls.LoadingIcon>("ArchiveContentLoading").IsVisible = _busy && !_ended;
        Control<Ursa.Controls.LoadingIcon>("ArchiveMediaLoading").IsLoading = Control<Ursa.Controls.LoadingIcon>("ArchiveMediaLoading").IsVisible = _mediaLoading && !_ended;
        Control<TextBlock>("ArchiveContentLoadingText").Text = _busy && !_ended ? Texts.T("archive.loading") : string.Empty;
        Control<TextBlock>("ArchiveMediaLoadingText").Text = _mediaLoading ? Texts.T("archive.loadingMedia") : string.Empty;
    }
    internal Action<string> CaptureActionErrorReporter()
    {
        int request = _contentRequest;
        MeowChan.ResourceId? thread = _selectedThread;
        return text => { if (!_ended && request == _contentRequest && thread == _selectedThread) ShowActionError(text); };
    }
    internal void ShowActionError(string text) => OperationMessage("action", text, true);
}
