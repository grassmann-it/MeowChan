using System;
using Avalonia.Controls;
using Avalonia.Input;

namespace MeowChan.Desktop.Controls;

/// <summary>What one press of Escape does in a dialog.</summary>
internal enum DialogEscapeOutcome
{
    /// <summary>Not a plain Escape, or a child (an open drop-down, a flyout, a key recorder) already used it.</summary>
    Ignored,
    /// <summary>A local edit, recording or confirmation was cancelled; the dialog stays open.</summary>
    CancelledLocal,
    /// <summary>The dialog is busy (saving, deleting); Escape is swallowed and the dialog stays open.</summary>
    Busy,
    /// <summary>The dialog closes, exactly as its × would.</summary>
    Close
}

/// <summary>
/// The one Escape policy of every dialog (modals plan MD1): first cancel what is in progress locally (an edit, a
/// recording, an inline confirmation), then close, and never close while the dialog is busy. Escape is fixed: it is not
/// a freely assigned application gesture and does not go through the shortcut service.
/// </summary>
internal static class DialogEscape
{
    /// <summary>The pure decision, for tests. <paramref name="tryCancelLocal"/> runs only for a plain, unhandled Escape.</summary>
    internal static DialogEscapeOutcome Decide(Key key, KeyModifiers modifiers, bool handled, Func<bool>? tryCancelLocal, Func<bool>? isBusy)
    {
        if (handled || key != Key.Escape || modifiers != KeyModifiers.None) return DialogEscapeOutcome.Ignored;
        if (tryCancelLocal?.Invoke() == true) return DialogEscapeOutcome.CancelledLocal;
        return isBusy?.Invoke() == true ? DialogEscapeOutcome.Busy : DialogEscapeOutcome.Close;
    }

    /// <summary>
    /// Handles Escape on <paramref name="window"/>. <paramref name="close"/> replaces <see cref="Window.Close()"/>, for a
    /// prompt that returns a result (<c>() =&gt; prompt.Close(false)</c>); the window's own Closing guards still apply.
    /// </summary>
    internal static void Attach(Window window, Func<bool>? tryCancelLocal = null, Func<bool>? isBusy = null, Action? close = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.KeyDown += (_, e) =>
        {
            DialogEscapeOutcome outcome = Decide(e.Key, e.KeyModifiers, e.Handled, tryCancelLocal, isBusy);
            if (outcome == DialogEscapeOutcome.Ignored) return;
            e.Handled = true;
            if (outcome != DialogEscapeOutcome.Close) return;
            if (close != null) close(); else window.Close();
        };
    }
}
