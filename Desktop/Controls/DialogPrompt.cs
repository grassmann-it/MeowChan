using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// A small modal question (modals plan MD3): the step-S dialog with a message, a secondary "keep" action and the primary
/// action right-most. The title stands in the title bar only; Escape and × answer "no", exactly like the keep action.
/// </summary>
internal static class DialogPrompt
{
    /// <summary>The prompt window and its two buttons, for callers that wire more than the answer (and for tests).</summary>
    internal sealed record Parts(Window Window, Button Accept, Button Cancel);

    /// <summary>Builds the prompt without showing it. <paramref name="danger"/> marks the accept action as destructive.</summary>
    internal static Parts Build(string title, string message, string acceptLabel, string cancelLabel, bool danger, string? automationId = null)
    {
        Window window = DialogWindow.Create(title, DialogSize.S, modal: true, heightToContent: true);
        Button cancel = new() { Content = cancelLabel };
        Button accept = new() { Content = acceptLabel };
        accept.Classes.Add(danger ? "danger" : "primary");
        TextBlock text = new() { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 16) };
        if (automationId != null)
        {
            AutomationProperties.SetAutomationId(window, automationId);
            AutomationProperties.SetAutomationId(text, automationId + ".message");
            AutomationProperties.SetAutomationId(accept, automationId + ".accept");
        }
        DialogWindow.Frame(window, DialogFrame.Build(header: null, text, DialogFooter.Build(accept, new Control[] { cancel })), modal: true);
        accept.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        // Keep stays the default focus: a stray Enter never confirms the destructive action.
        window.Opened += (_, _) => cancel.Focus();
        DialogEscape.Attach(window, close: () => window.Close(false));
        return new Parts(window, accept, cancel);
    }

    /// <summary>Asks <paramref name="owner"/>'s user; true only for the explicit accept action.</summary>
    internal static async Task<bool> ConfirmAsync(Window owner, string title, string message, string acceptLabel, string cancelLabel,
        bool danger = true, string? automationId = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return await Build(title, message, acceptLabel, cancelLabel, danger, automationId).Window.ShowDialog<bool>(owner);
    }

    internal static Window BuildNotice(string title, string message, string closeLabel, string? automationId = null)
    {
        Window window = DialogWindow.Create(title, DialogSize.S, modal: true, heightToContent: true);
        Button close = new() { Content = closeLabel };
        TextBlock text = new() { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 16) };
        if (automationId != null)
        {
            AutomationProperties.SetAutomationId(window, automationId);
            AutomationProperties.SetAutomationId(text, automationId + ".message");
            AutomationProperties.SetAutomationId(close, automationId + ".accept");
        }
        DialogWindow.Frame(window, DialogFrame.Build(header: null, text, DialogFooter.Build(close)), modal: true);
        close.Click += (_, _) => window.Close();
        window.Opened += (_, _) => close.Focus();
        DialogEscape.Attach(window);
        return window;
    }
}
