using System;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Views.Shortcuts;

/// <summary>
/// The one window for every keyboard shortcut (modals plan KS2, Tom: "one window for everything"): F1, the app menu, the macOS
/// Help menu and the Settings card open it. Modeless, one per owner window, size step L; it stays open while the owner is used
/// and does not block the owner's shortcuts. F1 again (here or in the owner) closes it; Escape first ends a recording, then
/// closes. It edits the owner's Settings draft (<see cref="IShortcutDraftHost"/>), so Settings and the window never disagree.
/// </summary>
internal sealed class ShortcutsWindow : Window
{
    private static readonly ConditionalWeakTable<Window, ShortcutsWindow> Instances = new();
    private readonly Window _owner;
    private bool _closeConfirmed;

    private ShortcutsWindow(Window owner, IShortcutDraftHost host, ShortcutHelpContext context)
    {
        _owner = owner;
        Name = "ShortcutsWindow";
        Title = Texts.T("shortcuts.title");
        DialogWindow.Configure(this, DialogSize.L, modal: false);
        Editor = new ShortcutBindingsEditor(host.ShortcutSession)
        {
            SaveHandler = host.CommitShortcutsAsync,
            IsSaveBlocked = () => host.IsShortcutCommitBlocked
        };
        Editor.ApplyContext(context);
        DialogWindow.Frame(this, DialogFrame.Build(header: null, Editor, footer: null, fillBody: true), modal: false);
        DialogEscape.Attach(this, Editor.TryCancelLocal, () => Editor.IsSaving);
        KeyDown += OnKeyDown;
        Opened += (_, _) => Editor.SearchInput.Focus();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            Texts.Changed -= OnLanguageChanged;
            Editor.Detach();
            Instances.Remove(_owner);
        };
        Texts.Changed += OnLanguageChanged;
    }

    internal ShortcutBindingsEditor Editor { get; }
    internal Window OwnerWindow => _owner;
    /// <summary>The former F1 list of the opening context (automation, smokes).</summary>
    internal string PlainText => Editor.PlainText;

    /// <summary>The open window of <paramref name="owner"/>, if any.</summary>
    internal static ShortcutsWindow? For(Window owner) => Instances.TryGetValue(owner, out ShortcutsWindow? window) ? window : null;

    /// <summary>Owned windows that are this window do not block their owner's shortcuts (it is modeless help).</summary>
    internal static bool IsPassive(Window window) => window is ShortcutsWindow;

    /// <summary>Shows the window of <paramref name="owner"/> on <paramref name="context"/> (opens it, or brings it forward).</summary>
    internal static ShortcutsWindow Open(Window owner, IShortcutDraftHost host, ShortcutHelpContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(host);
        ShortcutHelpContext applied = context ?? ShortcutHelpContext.Settings;
        if (For(owner) is { } open)
        {
            if (!open.Editor.IsRecording && !open.Editor.IsAskingBeforeClose) open.Editor.ApplyContext(applied);
            open.Activate();
            return open;
        }
        ShortcutsWindow window = new(owner, host, applied);
        Instances.AddOrUpdate(owner, window);
        window.Show(owner);
        return window;
    }

    /// <summary>F1: closes an open window of <paramref name="owner"/>, otherwise opens it. True when it is open afterwards.</summary>
    internal static bool Toggle(Window owner, IShortcutDraftHost host, ShortcutHelpContext? context = null)
    {
        if (ToggleDecision(For(owner) is { IsVisible: true }) == ShortcutsWindowToggle.Close)
        {
            For(owner)!.RequestClose();
            return For(owner) != null;
        }
        Open(owner, host, context);
        return true;
    }

    /// <summary>The pure decision of <see cref="Toggle"/>.</summary>
    internal static ShortcutsWindowToggle ToggleDecision(bool open) => open ? ShortcutsWindowToggle.Close : ShortcutsWindowToggle.Open;

    /// <summary>Closes like ×: with unsaved changes the window asks first (Keep editing, Discard, Save).</summary>
    internal void RequestClose() => Close();

    /// <summary>Closes without the question; an unsaved draft stays with Settings (owner shutdown, automation).</summary>
    internal void CloseWithoutAsking()
    {
        _closeConfirmed = true;
        Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing) return;
        if (CommandBindings.Matches(e, "viewer.shortcut-help")) { e.Handled = true; RequestClose(); }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // The owner closing or the app quitting never waits for this question; the draft stays with Settings.
        if (_closeConfirmed || e.CloseReason != WindowCloseReason.WindowClosing || !Editor.IsDirty) return;
        e.Cancel = true;
        if (Editor.IsAskingBeforeClose) return;
        Editor.AskBeforeClose(close =>
        {
            if (!close) return;
            _closeConfirmed = true;
            Close();
        });
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Title = Texts.T("shortcuts.title");
}

internal enum ShortcutsWindowToggle { Open, Close }
