using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// External links in post text open only after a confirmation that shows the full address, with its host (in ASCII form,
// so look-alike hosts are visible) highlighted. The choice not to ask again lasts for this session and is never saved.
public partial class ArchivePage
{
    private Uri? _pendingExternalLink;
    private Control? _externalLinkReturnFocus;
    private bool _externalLinksTrusted;

    /// <summary>Test seam: receives the validated address instead of the system launcher. Nothing is opened when it is set.</summary>
    internal Func<Uri, Task>? ExternalLinkLauncher { get; set; }
    internal bool IsExternalLinkDialogOpen => ReaderLinkDialog.IsVisible;
    internal Uri? PendingExternalLink => ReaderLinkDialog.IsVisible ? _pendingExternalLink : null;
    internal bool ExternalLinksTrusted => _externalLinksTrusted;

    private void OnInlineLinkInvoked(object? sender, ReaderLinkInvokedEventArgs e)
    {
        e.Handled = true;
        RequestExternalLink(e.Uri, sender as Control);
    }

    // Only a click or Enter gets here (never hover, focus, a selection or a hidden spoiler: the body decides that);
    // the address is validated again before anything is shown or launched.
    internal void RequestExternalLink(Uri uri, Control? origin = null)
    {
        if (_ended || !ReaderExternalLink.IsOpenable(uri)) return;
        CloseQuotePreview();
        if (_externalLinksTrusted) { _ = LaunchExternalLinkAsync(uri); return; }
        _pendingExternalLink = uri;
        _externalLinkReturnFocus = origin;
        (string prefix, string host, string tail) = ReaderExternalLink.Display(uri);
        InlineCollection inlines = new();
        inlines.Add(new Run(prefix) { [!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted") });
        inlines.Add(new Run(host) { FontWeight = FontWeight.Bold, [!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Accent") });
        inlines.Add(new Run(tail) { [!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted") });
        ReaderLinkUrl.Inlines = inlines;
        ReaderLinkRemember.IsChecked = false;
        LocalizeReaderLink();
        ReaderLinkDialog.IsVisible = true;
        // The start focus is on Cancel: the Enter (or its auto-repeat) that opened this dialog must never confirm it.
        // Opening is a conscious Tab or click.
        ReaderLinkCancel.Focus();
    }

    private void LocalizeReaderLink()
    {
        ReaderLinkTitle.Text = Texts.T("reader.link.title");
        ReaderLinkHint.Text = Texts.T("reader.link.hint");
        ReaderLinkRemember.Content = Texts.T("reader.link.remember");
        ReaderLinkOpen.Content = Texts.T("reader.link.open");
        ReaderLinkCancel.Content = Texts.T("action.cancel");
    }

    internal void CloseExternalLinkDialog()
    {
        if (!ReaderLinkDialog.IsVisible) return;
        ReaderLinkDialog.IsVisible = false;
        _pendingExternalLink = null;
        Control? back = _externalLinkReturnFocus;
        _externalLinkReturnFocus = null;
        if (back is { IsEffectivelyVisible: true }) back.Focus();
    }

    private void OnReaderLinkCancel(object? sender, RoutedEventArgs e) => CloseExternalLinkDialog();

    private void OnReaderLinkOpen(object? sender, RoutedEventArgs e)
    {
        Uri? uri = _pendingExternalLink;
        if (ReaderLinkRemember.IsChecked == true) _externalLinksTrusted = true;
        CloseExternalLinkDialog();
        if (uri != null) _ = LaunchExternalLinkAsync(uri);
    }

    private async Task LaunchExternalLinkAsync(Uri uri)
    {
        if (!ReaderExternalLink.IsOpenable(uri)) return;
        try
        {
            if (ExternalLinkLauncher is { } seam) await seam(uri);
            else if (TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception exception) when (!_ended)
        {
            Message(exception.Message, true);
        }
    }
}
