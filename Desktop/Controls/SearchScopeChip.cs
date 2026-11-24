using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>What the one search field of Archive and the reader searches. The query engines stay separate.</summary>
public enum SearchScope
{
    /// <summary>Titles, posts and identities (Archive) or the open thread's posts (reader).</summary>
    Text,
    /// <summary>Captured original file names in the whole local archive.</summary>
    FileName,
    /// <summary>Exact SHA-256 of the saved files of the open thread's media inventory.</summary>
    Sha256,
    /// <summary>The Media page's free text: file names, thread titles, boards and sources of the media library ("All").</summary>
    Media,
}

/// <summary>A "search elsewhere" entry of the scope chip menu, e.g. "Search “lamp” in Media".</summary>
public sealed record SearchCarryAction(string Name, string Label, bool IsEnabled, Action Execute, string? Reason = null);

/// <summary>
/// The scope chip in front of a <see cref="SearchBox"/>: shows the current scope ("Text ▾") and opens a menu with
/// the scopes as radio entries (none when there is only one) and the carry-over entries. It sits inside the field
/// (SearchBox.ScopeChip); a click, or Alt+Down / F4 in the field, opens the menu; the accessible name says
/// "Search scope: Text". The owner swaps the field behind it and decides what Enter does.
/// </summary>
public sealed class SearchScopeChip : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private static readonly Lazy<Geometry> Chevron = new(() => Geometry.Parse("M 1,1 L 5,5 L 9,1"));
    private static readonly SearchScope[] AllScopes = { SearchScope.Text, SearchScope.FileName, SearchScope.Sha256 };
    private SearchScope[] _scopes = AllScopes;

    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Path _chevron = new() { Width = 10, Height = 6, Stretch = Stretch.None, StrokeThickness = 1.5,
        StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private SearchScope _scope = SearchScope.Text;
    private bool _geometryApplied;

    public SearchScopeChip()
    {
        Classes.Add("search-scope");
        _chevron[!Shape.StrokeProperty] = new DynamicResourceExtension("Workspace.Muted");
        _label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _label, _chevron } };
        HorizontalContentAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        // Inside the SearchBox (InnerLeftContent): no own frame or Tab stop; the field opens the menu with Alt+Down / F4.
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(2, 0, 4, 0);
        Margin = new Thickness(0, 0, 4, 0);
        MinHeight = 0; MinWidth = 0;
        Focusable = false;
        IsTabStop = false;
        Refresh();
    }

    /// <summary>The current scope. Setting it updates the label and raises <see cref="ScopeChanged"/> when it differs.</summary>
    public SearchScope Scope
    {
        get => _scope;
        set
        {
            if (_scope == value) return;
            _scope = value;
            Refresh();
            ScopeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The user or the owner chose another scope.</summary>
    public event EventHandler? ScopeChanged;

    /// <summary>The scopes this field offers, in menu order (default: Text, File name, SHA-256). A page with one engine
    /// (Media: <see cref="SearchScope.Media"/>) offers one scope, so every search field shows the same chip and menu.</summary>
    public IReadOnlyList<SearchScope> Scopes
    {
        get => _scopes;
        set
        {
            if (value == null || value.Count == 0) throw new ArgumentException("A search field needs at least one scope.", nameof(value));
            _scopes = value.ToArray();
            if (Array.IndexOf(_scopes, _scope) < 0) Scope = _scopes[0];
            Refresh();
        }
    }

    /// <summary>"Search elsewhere" entries below the scopes (carry the query to another page or pane). Null hides an entry.</summary>
    public Func<IEnumerable<SearchCarryAction>>? CarryActions { get; set; }

    /// <summary>The field this chip currently sits in (set by <see cref="SearchBox.ScopeChip"/>).</summary>
    public SearchBox? Owner { get; internal set; }

    /// <summary>
    /// The keys that open the scope menu from inside the field, in the platform's own notation (Alt+Down and F4;
    /// macOS: ⌥↓ and F4 (Fn)). They are fixed field keys, not catalog commands, so they are rendered with the same
    /// gesture display the catalog uses instead of a hard-coded Windows spelling.
    /// </summary>
    public static string MenuShortcutDisplay() => MenuShortcutDisplay(OperatingSystem.IsMacOS());

    internal static string MenuShortcutDisplay(bool macOS)
    {
        ShortcutPlatform platform = macOS ? ShortcutPlatform.MacOS : ShortcutPlatform.Windows;
        return string.Join(" / ", new[] { "Alt+Down", "F4" }.Select(g => ShortcutGesture.Parse(g).Display(platform)));
    }

    /// <summary>The scope explanation with the keys that open the menu (the chip's tooltip, and the field's tooltip while the chip is hidden).</summary>
    public static string TipWithShortcut() => Texts.T("search.scope.tip") + " · " + MenuShortcutDisplay();

    /// <summary>The menu while it is open (test seam and focus checks).</summary>
    internal ContextMenu? OpenMenu { get; private set; }

    public static string Label(SearchScope scope) => Texts.T(scope switch
    {
        SearchScope.FileName => "search.scope.filename",
        SearchScope.Sha256 => "search.scope.hash",
        SearchScope.Media => "search.scope.media",
        _ => "search.scope.text"
    });

    protected override void OnClick()
    {
        base.OnClick();
        ShowMenu();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && ((e.Key == Key.Down && e.KeyModifiers == KeyModifiers.Alt) || (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None)))
        {
            e.Handled = true;
            ShowMenu();
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Opens the scope menu with the current scope checked and focused.</summary>
    public void ShowMenu()
    {
        if (!IsEffectivelyEnabled) return;
        ContextMenuBuilder builder = new ContextMenuBuilder();
        foreach (SearchScope scope in _scopes.Length > 1 ? _scopes : Array.Empty<SearchScope>())
        {
            SearchScope frozen = scope;
            builder.Radio("search-scope", "search.scope." + scope, Label(scope), scope == _scope,
                () => { Scope = frozen; Dispatcher.UIThread.Post(() => Owner?.Focus(NavigationMethod.Tab)); });
        }
        SearchCarryAction[] carry = CarryActions?.Invoke().ToArray() ?? Array.Empty<SearchCarryAction>();
        if (carry.Length > 0 && _scopes.Length > 1) builder.Section();
        if (builder.ToModel().IsEmpty && carry.Length == 0) return; // one scope and nowhere to carry: nothing to choose
        for (int index = 0; index < carry.Length; index++)
        {
            SearchCarryAction frozen = carry[index];
            builder.Item("search.carry." + index, frozen.Label, frozen.Execute, frozen.IsEnabled, frozen.Reason);
        }
        ContextMenu menu = builder.Build()!;
        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        foreach (int index in Enumerable.Range(0, carry.Length))
        {
            SearchCarryAction action = carry[index];
            if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "search.carry." + index)) is not { } item) continue;
            item.Name = action.Name;
            AutomationProperties.SetName(item, action.Label);
            if (!string.IsNullOrEmpty(action.Reason))
            {
                ToolTip.SetTip(item, action.Reason);
                ToolTip.SetShowOnDisabled(item, true);
                AutomationProperties.SetHelpText(item, action.Reason);
            }
        }
        MenuItem? current = menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "search.scope." + _scope));
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(OpenMenu, menu)) OpenMenu = null;
            // The checked scope and the carry entries are values of this open: not kept for a later right-click, every open composes anew (ShowMenu).
            if (ReferenceEquals(ContextMenu, menu)) ContextMenu = null;
        };
        ContextMenu = menu;
        OpenMenu = menu;
        // A work view may have taken the chip off the field (it is hidden while the scope is Text): the menu then opens at the field.
        menu.Open(IsEffectivelyVisible || Owner == null ? this : Owner);
        if (current != null) Dispatcher.UIThread.Post(() => current.Focus(NavigationMethod.Tab));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!_geometryApplied)
        {
            // Geometry needs the rendering platform: build it when the chip is shown, not when it is constructed.
            _chevron.Data = Chevron.Value;
            _geometryApplied = true;
        }
        Texts.Changed += OnTextsChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Texts.Changed -= OnTextsChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTextsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private string? _labelOverride;
    /// <summary>
    /// What the chip says instead of the scope's name while it is set (the media page: "All", the one field the text is searched in, or
    /// "3 fields"); null shows the scope's name. The owner keeps it current when the language or the text scope changes.
    /// </summary>
    internal string? LabelOverride { get => _labelOverride; set { if (_labelOverride == value) return; _labelOverride = value; Refresh(); } }

    private string CurrentLabel => string.IsNullOrEmpty(_labelOverride) ? Label(_scope) : _labelOverride;

    private void Refresh()
    {
        string label = CurrentLabel;
        _label.Text = label;
        // One scope (Media): a plain label without the chevron; the menu then holds only the carry-over entries.
        _chevron.IsVisible = _scopes.Length > 1;
        string tip = TipWithShortcut();
        AutomationProperties.SetName(this, Texts.T("search.scope.name", label));
        AutomationProperties.SetHelpText(this, tip);
        ToolTip.SetTip(this, tip);
        ApplyOwnerHelp();
    }

    /// <summary>
    /// The chip itself has no Tab stop, so a screen reader in the field would hear only the placeholder. The field carries the active
    /// scope, what it does and the keys to change it as its help text (updated here on every scope or language change).
    /// </summary>
    internal void ApplyOwnerHelp()
    {
        if (Owner is { } owner)
            AutomationProperties.SetHelpText(owner, Texts.T("search.scope.fieldHelp", CurrentLabel, Texts.T("search.scope.tip"), MenuShortcutDisplay())
                + (string.IsNullOrEmpty(FieldHelpNote) ? string.Empty : " " + FieldHelpNote));
    }

    private string? _fieldHelpNote;
    /// <summary>A sentence the owner adds to the field's help text (the media page: the query syntax, M3); null for none.</summary>
    internal string? FieldHelpNote { get => _fieldHelpNote; set { if (_fieldHelpNote == value) return; _fieldHelpNote = value; ApplyOwnerHelp(); } }
}
