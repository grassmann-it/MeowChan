using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Serilog;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

/// <summary>"Show in Media", "Open post" and "Copy link" actions of cards, and the polite link note line they report to.</summary>
internal sealed partial class BooruWorkbenchView
{
    private Button CreateShowInMediaAction(string name, string accessibleName, string postId)
    {
        Button show = new() { Name = name, Content = Texts.T("booru.ui.result.showInMedia") };
        show.Classes.Add("ghost");
        AutomationProperties.SetName(show, accessibleName);
        ToolTip.SetTip(show, accessibleName);
        show.Click += async (_, _) =>
        {
            if (_showInMedia is not { } navigate || !TryBeginLinkAction(out long revision)) return;
            try
            {
                bool shown = await navigate(postId);
                if (shown || !IsCurrentLinkAction(revision)) return;
                SetLinkNote(("booru.crossLinks.missingLocal", postId, true), revision);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A host/profile transition may retire the navigation callback while it is awaited.
                // This is an action failure, not evidence that the local original is missing.
                Log.Warning(exception, "Could not show a Danbooru post in local media");
                SetLinkNote(("booru.crossLinks.navigationFailed", postId, true), revision);
            }
        };
        return show;
    }

    /// <summary>"Open in browser" and "Copy link" for one canonical post URL; nothing else is ever opened or copied.</summary>
    private Control CreateLinkActions(string openName, string copyName, string url, string openAccessibleName, string copyAccessibleName,
        ICollection<Button> workViewActions)
    {
        WrapPanel actions = new() { Classes = { "workspace-toolbar" } };
        Button open = new() { Name = openName, Content = Texts.T("booru.ui.post.open") };
        open.Classes.Add("ghost");
        CommandBindings.ShowIconBeforeText(open, "external-link");
        RegisterWorkViewAction(open, "booru.result.open", workViewActions);
        AutomationProperties.SetName(open, openAccessibleName);
        ToolTip.SetTip(open, Texts.T("booru.ui.post.openTip", url));
        open.Click += async (_, _) => await OpenLinkAsync(url);
        Button copy = new() { Name = copyName, Content = Texts.T("booru.ui.post.copy") };
        copy.Classes.Add("ghost");
        CommandBindings.ShowIconBeforeText(copy, "copy");
        RegisterWorkViewAction(copy, "booru.result.copy-link", workViewActions);
        AutomationProperties.SetName(copy, copyAccessibleName);
        ToolTip.SetTip(copy, url);
        copy.Click += async (_, _) => await CopyLinkAsync(url);
        actions.Children.Add(open);
        actions.Children.Add(copy);
        return actions;
    }

    private async Task OpenLinkAsync(string url)
    {
        // Only canonical post pages reach this method (built by BooruPostLinks); a launcher failure is shown, never retried.
        if (!TryBeginLinkAction(out long revision)) return;
        try
        {
            await _model.Services.Links.Open(url);
            SetLinkNote(null, revision);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warning(exception, "Could not open a Danbooru post in the browser");
            SetLinkNote(("booru.ui.link.openFailed", string.Empty, true), revision);
        }
    }

    private async Task CopyLinkAsync(string url)
    {
        if (!TryBeginLinkAction(out long revision)) return;
        try
        {
            if (_model.Services.Links.Copy is { } copy) await copy(url);
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(url);
            else throw new InvalidOperationException("The clipboard is unavailable.");
            SetLinkNote(("booru.ui.link.copied", url, false), revision);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Warning(exception, "Could not copy a Danbooru post link");
            SetLinkNote(("booru.ui.link.copyFailed", string.Empty, true), revision);
        }
    }

    private bool TryBeginLinkAction(out long revision)
    {
        if (_closed)
        {
            revision = 0;
            return false;
        }
        revision = Interlocked.Increment(ref _linkActionRevision);
        return true;
    }

    private bool IsCurrentLinkAction(long revision) =>
        !_closed && Volatile.Read(ref _linkActionRevision) == revision;

    /// <summary>
    /// Retires only the current host navigation's notice continuation. User takeover is not a
    /// missing original, and an old cancellation must never invalidate a newer link action.
    /// </summary>
    internal Action CaptureLinkActionCancellation()
    {
        long revision = Volatile.Read(ref _linkActionRevision);
        return () => Interlocked.CompareExchange(ref _linkActionRevision, unchecked(revision + 1), revision);
    }

    private void SetLinkNote((string Key, string Argument, bool Problem)? state)
    {
        if (!TryBeginLinkAction(out long revision)) return;
        SetLinkNote(state, revision);
    }

    private void SetLinkNote((string Key, string Argument, bool Problem)? state, long revision)
    {
        if (!IsCurrentLinkAction(revision)) return;
        _linkNoteState = state;
        _applying = true;
        try { RenderChrome(_model.Watchers.View); }
        finally { _applying = false; }
    }
}
