using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Reader post cards: header actions of one post.
public partial class ArchivePage
{
    // Test seam for the clipboard write; production uses the window clipboard.
    internal Func<string, Task>? CopyPostReference { get; set; }

    private async void OnCopyPostNumberClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ArchivePostRow row }) return;
        e.Handled = true;
        await CopyPostReferenceAsync(row);
    }

    // Copies ">>No." (the quote form of the post number) and reports the result in the status overlay.
    internal async Task CopyPostReferenceAsync(ArchivePostRow row)
    {
        if (_ended) return;
        try
        {
            if (CopyPostReference != null) await CopyPostReference(row.QuoteReference);
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(row.QuoteReference);
            else throw new InvalidOperationException("The clipboard is unavailable.");
            if (!_ended) Message(Texts.T("reader.postNumberCopied", row.Id));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_ended) Message(Texts.T("reader.copyFailed"));
        }
    }
}
