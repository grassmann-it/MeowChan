using System;
using System.Threading.Tasks;
using Avalonia;
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
    internal static Parts Build(string title, string message, string acceptLabel, string cancelLabel, bool danger)
    {
        Window window = DialogWindow.Create(title, DialogSize.S, modal: true, heightToContent: true);
        Button cancel = new() { Content = cancelLabel };
        Button accept = new() { Content = acceptLabel };
        accept.Classes.Add(danger ? "danger" : "primary");
        TextBlock text = new() { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 16) };
        DialogWindow.Frame(window, DialogFrame.Build(header: null, text, DialogFooter.Build(accept, new Control[] { cancel })), modal: true);
        accept.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        // Keep stays the default focus: a stray Enter never confirms the destructive action.
        window.Opened += (_, _) => cancel.Focus();
        DialogEscape.Attach(window, close: () => window.Close(false));
        return new Parts(window, accept, cancel);
    }

    /// <summary>Asks <paramref name="owner"/>'s user; true only for the explicit accept action.</summary>
    internal static async Task<bool> ConfirmAsync(Window owner, string title, string message, string acceptLabel, string cancelLabel, bool danger = true)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return await Build(title, message, acceptLabel, cancelLabel, danger).Window.ShowDialog<bool>(owner);
    }
}
