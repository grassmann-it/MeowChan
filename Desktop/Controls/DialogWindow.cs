using System;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The one factory for the window of a dialog (modals plan MD1): window kind, size step, placement, screen fit and the
/// app caption. A modal dialog gets the <see cref="WindowChromeKind.Dialog"/> caption (no minimize: a minimized modal
/// would strand its blocked owner) and stays out of the taskbar; a modeless window keeps minimize and its taskbar entry.
/// </summary>
/// <remarks>
/// The dialog's title lives in the title bar only (owner decision E1): the window <see cref="Window.Title"/> is the
/// caption and the screen-reader name, and the body never repeats it. <see cref="DialogFrame"/> lays out the content,
/// <see cref="DialogFooter"/> the actions and <see cref="DialogEscape"/> the Escape key.
/// </remarks>
internal static class DialogWindow
{
    /// <summary>The caption variant for a modal or modeless window.</summary>
    internal static WindowChromeKind KindFor(bool modal) => modal ? WindowChromeKind.Dialog : WindowChromeKind.Window;

    /// <summary>A new dialog window with <paramref name="title"/>, configured by <see cref="Configure"/>.</summary>
    internal static Window Create(string title, DialogSize size, bool modal, bool heightToContent = false)
    {
        Window window = new() { Title = title };
        Configure(window, size, modal, heightToContent);
        return window;
    }

    /// <summary>
    /// Applies the size step, centring on the owner, the taskbar policy and the fit to the screen at the first opening.
    /// With <paramref name="heightToContent"/> the height follows the content up to the step's height (a short confirmation).
    /// Call once, before the window is shown.
    /// </summary>
    internal static void Configure(Window window, DialogSize size, bool modal, bool heightToContent = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        Size preferred = WorkspaceMetrics.DialogPreferredSize(size), minimum = WorkspaceMetrics.DialogMinimumSize(size);
        window.Width = preferred.Width;
        window.MinWidth = minimum.Width;
        if (heightToContent)
        {
            window.SizeToContent = SizeToContent.Height;
            window.MaxHeight = preferred.Height;
            window.MinHeight = WorkspaceMetrics.DialogContentMinimumHeight;
        }
        else
        {
            window.Height = preferred.Height;
            window.MinHeight = minimum.Height;
        }
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (modal) window.ShowInTaskbar = false;
        DialogFit.OnOpen(window);
    }

    /// <summary>Puts <paramref name="content"/> below the app caption of the right variant and makes it the window's content.</summary>
    internal static void Frame(Window window, Control content, bool modal, string? captionName = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Content = WindowChrome.Frame(window, content, KindFor(modal), captionName);
    }
}
