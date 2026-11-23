using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Reader post cards: header actions and inline references of one post.
public partial class ArchivePage
{
    // Test seam for the clipboard write; production uses the window clipboard.
    internal Func<string, Task>? CopyPostReference { get; set; }

    // Recycled containers are re-prepared for every item, so the class always follows the shown post.
    private void OnReaderPostContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        object? item = (sender as ItemsControl)?.ItemFromContainer(e.Container) ?? e.Container.DataContext;
        e.Container.Classes.Set("reader-op", item is ArchivePostRow { IsOpeningPost: true });
        DecorateReaderPost(e.Container, item as ArchivePostRow, ArchivePosts.Bounds.Width);
        if (item is ArchivePostRow post) LoadExpandedReplies(post);
    }

    // A click on an inline >>No. in the post text navigates exactly like its quote button.
    private async void OnInlineQuoteInvoked(object? sender, ReaderQuoteInvokedEventArgs e)
    {
        e.Handled = true;
        await NavigateQuoteAsync(e.Quote);
    }

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
