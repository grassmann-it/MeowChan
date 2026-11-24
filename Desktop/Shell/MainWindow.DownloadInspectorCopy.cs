using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

public partial class MainWindow
{
    private sealed record InspectorCopyValues(object Row, string? Url, string? Directory);

    private InspectorCopyValues? _inspectorCopyValues;
    private Task _inspectorClipboardWrite = Task.CompletedTask;

    private void BindInspectorCopyValues(object? row, string? url, string? directory)
    {
        if (!ReferenceEquals(_inspectorCopyValues?.Row, row) || _inspectorCopyValues?.Url != url
            || _inspectorCopyValues?.Directory != directory)
        {
            _inspectorCopyValues = row == null ? null : new InspectorCopyValues(row, url, directory);
            SetInspectorCopyStatus(InspectorCopyUrlStatus, null);
            SetInspectorCopyStatus(InspectorCopyDirectoryStatus, null);
        }

        bool hasDirectory = !string.IsNullOrWhiteSpace(directory);
        InspectorUrl.Text = url;
        InspectorDirectory.Text = hasDirectory ? directory : Texts.T("detail.noDirectory");
        ToolTip.SetTip(InspectorUrl, url);
        ToolTip.SetTip(InspectorDirectory, hasDirectory ? directory : null);
        InspectorCopyUrlButton.IsEnabled = !_isClosing && !string.IsNullOrWhiteSpace(url);
        InspectorCopyDirectoryButton.IsEnabled = !_isClosing && hasDirectory;
    }

    private void OnInspectorCopyUrlClick(object? sender, RoutedEventArgs e) =>
        CopyInspectorValue(_inspectorCopyValues?.Url, InspectorCopyUrlStatus, "detail.urlCopied");

    private void OnInspectorCopyDirectoryClick(object? sender, RoutedEventArgs e) =>
        CopyInspectorValue(_inspectorCopyValues?.Directory, InspectorCopyDirectoryStatus, "media.copyPath.done");

    private void CopyInspectorValue(string? value, TextBlock status, string successKey)
    {
        if (_inspectorCopyValues is not { } source || string.IsNullOrWhiteSpace(value) || !IsCurrentInspectorCopy(source)) return;
        _inspectorClipboardWrite = WriteAsync(_inspectorClipboardWrite);

        async Task WriteAsync(Task previous)
        {
            await previous;
            if (!IsCurrentInspectorCopy(source)) return;
            SetInspectorCopyStatus(status, null);
            string result = successKey;
            try
            {
                IClipboard clipboard = Clipboard ?? throw new InvalidOperationException("Clipboard is unavailable.");
                await clipboard.SetTextAsync(value);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "Could not copy the Downloads inspector value.");
                result = "ctxmenu.copyFailed";
            }
            if (IsCurrentInspectorCopy(source)) SetInspectorCopyStatus(status, result);
        }
    }

    private bool IsCurrentInspectorCopy(InspectorCopyValues source) =>
        !_isClosing && ReferenceEquals(source, _inspectorCopyValues) && ReferenceEquals(source.Row, GetWorkspaceSelection());

    private static void SetInspectorCopyStatus(TextBlock status, string? key)
    {
        status.Tag = key;
        status.Text = key == null ? string.Empty : Texts.T(key);
        status.Classes.Set("workspace-error", key == "ctxmenu.copyFailed");
        status.IsVisible = key != null;
    }
}
