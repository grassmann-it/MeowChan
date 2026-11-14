using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Views.Shortcuts;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Commands;

/// <summary>
/// The Settings card of the keyboard shortcuts (modals plan KS2): a hint, the single-key switch, the number of own bindings
/// and the button that opens the one shortcuts window, where the table, search and recording live. The card owns the
/// Settings draft (<see cref="Session"/>); the window edits that same session, so both always show the same state. Capture
/// never persists or publishes bindings; Settings' Save or the window's Save does.
/// </summary>
public sealed class ShortcutEditor : UserControl
{
    private readonly StackPanel _root = new() { Spacing = 12 };
    private readonly TextBlock _heading = new() { Name = "SettingsShortcutsHeading" };
    public Control SectionHeading => _heading;
    // Counter, unsaved changes and issues sit in the settings group "More settings" (work views, WV8); hint, switch and
    // the window button stay above it.
    private readonly Expander _more = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _moreBody = new() { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
    /// <summary>The collapsible group of the card; its policy row is settings.group.shortcuts.</summary>
    internal Expander MoreGroup => _more;
    /// <summary>Whether the card currently shows shortcut issues (conflicts, advisory warnings); they always open the group.</summary>
    internal bool HasIssues => !string.IsNullOrEmpty(_errors.Text);
    private readonly Button _open = new() { Name = "SettingsShortcutsOpen", HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _count = new() { Name = "SettingsShortcutsCount", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _changes = new() { Name = "SettingsShortcutsChanges", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _errors = new() { Name = "SettingsShortcutsErrors", TextWrapping = TextWrapping.Wrap };
    // WCAG 2.1.4: the standard single-character shortcuts can be switched off as a whole.
    private readonly CheckBox _singleKeys = new() { Name = "SettingsShortcutsSingleKeys" };
    private bool _syncingSingleKeys;
    private readonly List<Action> _frameTexts = new();
    private readonly Action _publishedObserver;
    private long _validationFocusRequest;

    public ShortcutEditor()
    {
        Name = "ShortcutEditor";
        _heading.Classes.Add("section-title");
        _errors.Classes.Add("workspace-error");
        _count.Classes.Add("workspace-meta");
        _changes.Classes.Add("workspace-status");
        Content = _root;
        Session.Changed += OnSessionChanged;
        _singleKeys.IsCheckedChanged += (_, _) =>
        {
            if (_syncingSingleKeys || !Session.IsEditable || (_singleKeys.IsChecked == true) == Session.Draft.SingleKeyShortcuts) return;
            Session.SetSingleKeyShortcuts(_singleKeys.IsChecked == true);
        };
        _open.Click += (_, _) => OpenWindow();
        _errors.LostFocus += (_, _) => _validationFocusRequest++;
        DetachedFromVisualTree += (_, _) => _validationFocusRequest++;
        AttachedToVisualTree += (_, _) => { Texts.Changed += OnLanguageChanged; Localize(); };
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
        AutomationProperties.SetLiveSetting(_errors, AutomationLiveSetting.Assertive);
        // A binding published elsewhere (the window's Save, another settings path) reaches a clean card; a pending
        // draft is never overwritten. The service holds observers weakly; this card keeps the delegate alive.
        _publishedObserver = OnPublished;
        ShortcutService.Observe(_publishedObserver);
        BuildFrame();
        Refresh();
        Localize();
    }

    /// <summary>The Settings draft of the shortcuts, shared with the shortcuts window.</summary>
    public ShortcutDraftSession Session { get; } = new();
    public ShortcutBindingsSection Draft => Session.Draft;
    public bool IsDirty => Session.IsDirty;
    /// <summary>The Settings editor that commits this draft; the window button opens the window only with one.</summary>
    internal IShortcutDraftHost? Host { get; set; }

    /// <summary>Raised for explicit user edits of the draft (here or in the window), never for Load or language refresh.</summary>
    public event EventHandler? DraftEdited;

    private void OnLanguageChanged(object? sender, EventArgs e) => Localize();

    private void OnPublished()
    {
        ShortcutService.Observe(_publishedObserver);
        if (!Session.IsDirty && ShortcutService.CurrentSection.Raw.GetRawText() != Session.Draft.Raw.GetRawText())
            Session.Load(ShortcutService.CurrentSection);
    }

    public void Load(ShortcutBindingsSection section)
    {
        _validationFocusRequest++;
        Session.Load(section);
    }

    private void OnSessionChanged(object? sender, ShortcutDraftChange change)
    {
        Refresh();
        if (change == ShortcutDraftChange.Edited) DraftEdited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows the draft's issues. With a blocking one the group opens and the explanation receives the focus once Settings'
    /// synchronous busy scope has ended; the window (button above) shows the colliding actions side by side.
    /// </summary>
    public bool ValidateDraft()
    {
        ShowIssues(Session.Issues);
        if (!Session.HasBlockingIssues) return true;
        _more.IsExpanded = true;
        long request = ++_validationFocusRequest;
        ShortcutBindingsSection draft = Draft;
        string? message = _errors.Text;
        TopLevel? top = TopLevel.GetTopLevel(this);
        var focusAtRequest = top?.FocusManager?.GetFocusedElement();
        Dispatcher.UIThread.Post(() =>
        {
            if (request != _validationFocusRequest || !ReferenceEquals(Draft, draft) || _errors.Text != message || top == null
                || !ReferenceEquals(TopLevel.GetTopLevel(this), top) || !IsEffectivelyVisible || !IsEffectivelyEnabled
                || top is Window { IsActive: false } || !ReferenceEquals(top.FocusManager?.GetFocusedElement(), focusAtRequest)) return;
            top.UpdateLayout();
            _errors.BringIntoView();
            _errors.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Default);
        return false;
    }

    /// <summary>Opens the shortcuts window of this card's window on every area (Settings context).</summary>
    internal ShortcutsWindow? OpenWindow()
    {
        if (Host is not { } host || TopLevel.GetTopLevel(this) is not Window owner) return null;
        return ShortcutsWindow.Open(owner, host, ShortcutHelpContext.Settings);
    }

    private void Localize()
    {
        _heading.Text = Texts.T("shortcuts.title");
        _singleKeys.Content = Texts.T("settings.shortcutsEditor.singleKeys");
        AutomationProperties.SetName(_singleKeys, _singleKeys.Content.ToString());
        AutomationProperties.SetHelpText(_singleKeys, Texts.T("settings.shortcutsEditor.singleKeys.help"));
        _open.Content = Texts.T("shortcuts.card.open");
        AutomationProperties.SetName(_open, Texts.T("shortcuts.card.open"));
        AutomationProperties.SetHelpText(_open, Texts.T("shortcuts.card.hint"));
        foreach (var refresh in _frameTexts) refresh();
        RefreshCounts();
        ShowIssues(Session.Issues);
    }

    private void BuildFrame()
    {
        _root.Children.Add(_heading);
        var hint = LocalizedText(() => Texts.T("shortcuts.card.hint"));
        hint.Classes.Add("workspace-help");
        _root.Children.Add(hint);
        _root.Children.Add(_open);
        _root.Children.Add(_singleKeys);
        var singleKeysHelp = LocalizedText(() => Texts.T("settings.shortcutsEditor.singleKeys.help"));
        singleKeysHelp.Classes.Add("workspace-help");
        _root.Children.Add(singleKeysHelp);
        _more.Content = _moreBody;
        WorkViewElement.SetId(_more, "settings.group.shortcuts");
        _root.Children.Add(_more);
        var applyHint = LocalizedText(() => Texts.T("settings.shortcutsEditor.hint"));
        applyHint.Classes.Add("workspace-help");
        _moreBody.Children.Add(_count);
        _moreBody.Children.Add(_changes);
        _moreBody.Children.Add(applyHint);
        _moreBody.Children.Add(_errors);
    }

    private void Refresh()
    {
        _syncingSingleKeys = true;
        try { _singleKeys.IsChecked = Session.Draft.SingleKeyShortcuts; _singleKeys.IsEnabled = Session.IsEditable; }
        finally { _syncingSingleKeys = false; }
        RefreshCounts();
        ShowIssues(Session.Issues);
    }

    private void RefreshCounts()
    {
        _count.Text = Texts.T("shortcuts.card.custom", Session.CustomCount);
        _changes.Text = Session.IsDirty ? Texts.T("shortcuts.window.changes", Session.ChangeCount) : string.Empty;
        _changes.IsVisible = Session.IsDirty;
    }

    private void ShowIssues(IReadOnlyList<ShortcutIssue> issues)
    {
        _errors.Focusable = issues.Any(issue => issue.BlocksSave);
        bool hadIssues = HasIssues;
        _errors.Text = string.Join("\n", issues.Select(i => (CommandCatalog.TryGet(i.CommandId, out var c) ? c!.Label : i.CommandId)
            + (i.OtherCommandId != null ? " ↔ " + CommandCatalog.Label(i.OtherCommandId) : "") + ": " + ShortcutIssueCatalog.Render(i)));
        // R6: an error or warning is never behind a collapsed group (opens when it appears, not on every refresh).
        if (!hadIssues && HasIssues) _more.IsExpanded = true;
    }

    private TextBlock LocalizedText(Func<string> label)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void Refresh() => text.Text = label();
        _frameTexts.Add(Refresh); Refresh();
        return text;
    }
}
