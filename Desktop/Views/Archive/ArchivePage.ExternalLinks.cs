using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
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

    /// <summary>Test seam: receives the validated address instead of the system launcher and answers whether it started. Nothing is opened when it is set.</summary>
    internal Func<Uri, Task<bool>>? ExternalLinkLauncher { get; set; }
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
        inlines.Add(new Run(host) { FontWeight = FontWeight.SemiBold, [!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Accent") });
        inlines.Add(new Run(tail) { [!TextElement.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted") });
        ReaderLinkUrl.Inlines = inlines;
        ReaderLinkRemember.IsChecked = false;
        LocalizeReaderLink(prefix + host + tail);
        ReaderLinkDialog.IsVisible = true;
        // The start focus is on Cancel: the Enter (or its auto-repeat) that opened this dialog must never confirm it.
        // Opening is a conscious Tab or click.
        ReaderLinkCancel.Focus();
    }

    // The address as one string, for assistive technology: the inlines alone are not announced as text.
    private string _externalLinkAddress = string.Empty;

    private void LocalizeReaderLink(string? address = null)
    {
        if (address != null) _externalLinkAddress = address;
        // The dialog is modal for the keyboard: Tab cycles inside it and never reaches the dimmed posts below.
        KeyboardNavigation.SetTabNavigation(ReaderLinkDialog, KeyboardNavigationMode.Cycle);
        string announcement = Texts.T("reader.link.dialogName", _externalLinkAddress);
        AutomationProperties.SetName(ReaderLinkDialog, announcement);
        AutomationProperties.SetName(ReaderLinkUrl, _externalLinkAddress);
        AutomationProperties.SetLiveSetting(ReaderLinkUrl, AutomationLiveSetting.Assertive);
        // Focus starts on Cancel, so the screen reader reads the address along with it; Open names its target too.
        AutomationProperties.SetHelpText(ReaderLinkCancel, announcement);
        AutomationProperties.SetName(ReaderLinkOpen, Texts.T("reader.link.openNamed", _externalLinkAddress));
        AutomationProperties.SetHelpText(ReaderLinkOpen, announcement);
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
        bool started = false;
        string? detail = null;
        try
        {
            if (ExternalLinkLauncher is { } seam) started = await seam(uri);
            else if (TopLevel.GetTopLevel(this) is { } top) started = await top.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception exception)
        {
            detail = exception.Message;
        }
        // LaunchUriAsync answers false when nothing was started (no default browser, a restricted environment): say so in
        // the user's language; the system's own text, when there is one, only follows as detail.
        if (started || _ended) return;
        Message(string.IsNullOrWhiteSpace(detail) ? Texts.T("reader.link.failed") : Texts.T("reader.link.failedWith", detail), true);
    }
}
