using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

// GL3 (PLAN-GALERIE-LAYOUT-BLOECKE §3): the page-neutral half of a "Layout" menu. A page describes its rows once as a LayoutMenuModel
// (pure data: toggles, exclusive choices, actions, notes; a row that cannot be used carries the reason), and the two ways to show it are
// built from that one description:
//   * LayoutMenuPanel: the rows of a flyout under a "Layout" button; toggles keep the menu open, an action closes it;
//   * LayoutMenuBuilder.ToContextMenu: the same rows as a ContextMenuBuilder menu (a right click on free area).
// The gallery's rows and what each row does to the person's choice are in GalleryLayoutMenu (GalleryLayoutMenu.cs). Nothing here knows the
// gallery, so another page (the media page) can describe its own rows and reuse both presentations.

/// <summary>What a row of a layout menu is.</summary>
internal enum LayoutMenuRowKind
{
    /// <summary>A disabled sentence (the title that names what the menu edits, or an explanation).</summary>
    Note,
    /// <summary>A separator: the next row starts a new section.</summary>
    Separator,
    /// <summary>A switch (checked = shown).</summary>
    Toggle,
    /// <summary>One value of an exclusive choice; the rows of one <see cref="LayoutMenuRow.Group"/> are the values, one checked.</summary>
    Choice,
    /// <summary>A plain action (for example "Reset layout"); choosing it closes the menu.</summary>
    Action
}

/// <summary>
/// One row. <see cref="DisabledReason"/> non-null makes the row unusable and says why (tooltip, help text and, in the flyout, a second line);
/// the row still shows its state. <see cref="CommandId"/> only supplies the shortcut shown beside the row.
/// </summary>
internal sealed record LayoutMenuRow(LayoutMenuRowKind Kind, string Id, string Label)
{
    public bool Checked { get; init; }
    public string? Group { get; init; }
    public string? CommandId { get; init; }
    public string? DisabledReason { get; init; }
    /// <summary>Said for a usable row as its tooltip addition (for example "your choice" / "view default").</summary>
    public string? Hint { get; init; }
    /// <summary>What the row does, as the accessible help text of a usable row.</summary>
    public string? Description { get; init; }
    /// <summary>Indent level (0 or 1) in the flyout; the context menu is flat.</summary>
    public int Indent { get; init; }

    public bool Enabled => DisabledReason == null;
    public bool IsInteractive => Kind is LayoutMenuRowKind.Toggle or LayoutMenuRowKind.Choice or LayoutMenuRowKind.Action;
}

/// <summary>The description of a layout menu: a <see cref="Title"/> that names what it edits and the rows in order.</summary>
internal sealed record LayoutMenuModel(string Title, IReadOnlyList<LayoutMenuRow> Rows)
{
    /// <summary>The rows a person can choose, in order.</summary>
    internal IEnumerable<LayoutMenuRow> Interactive => Rows.Where(row => row.IsInteractive);

    internal LayoutMenuRow? Find(string id) => Rows.FirstOrDefault(row => row.Id == id);
}

/// <summary>Makes the context-menu presentation of a <see cref="LayoutMenuModel"/>.</summary>
internal static class LayoutMenuBuilder
{
    /// <summary>
    /// The model as a <see cref="ContextMenuBuilder"/>: the title as a note, the rows as switches, radios and items, a separator as a new
    /// section. <paramref name="invoke"/> runs with the chosen row; the menu is composed fresh at every opening (<see cref="ContextMenuHost"/>).
    /// </summary>
    internal static ContextMenuBuilder ToContextMenu(LayoutMenuModel model, Action<LayoutMenuRow> invoke, string? accessibleName = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(invoke);
        ContextMenuBuilder builder = new();
        if (!string.IsNullOrEmpty(accessibleName)) builder.Named(accessibleName);
        if (!string.IsNullOrEmpty(model.Title)) builder.Note(model.Title).Section();
        foreach (LayoutMenuRow row in model.Rows)
        {
            LayoutMenuRow chosen = row;
            switch (row.Kind)
            {
                case LayoutMenuRowKind.Separator: builder.Section(); break;
                case LayoutMenuRowKind.Note: builder.Note(row.Label); break;
                case LayoutMenuRowKind.Toggle:
                    builder.Check(row.Id, row.Label, row.Checked, () => invoke(chosen), row.Enabled, row.DisabledReason);
                    break;
                case LayoutMenuRowKind.Choice:
                    builder.Radio(row.Group ?? row.Id, row.Id, row.Label, row.Checked, () => invoke(chosen), row.Enabled, row.DisabledReason);
                    break;
                default:
                    builder.Item(row.Id, row.Label, () => invoke(chosen), row.Enabled, row.DisabledReason);
                    break;
            }
        }
        return builder;
    }
}

/// <summary>
/// The flyout presentation: the rows of a <see cref="LayoutMenuModel"/> as full-width menu rows (the look of the gallery's "⋯" rows: a drawn
/// check, the label, the shortcut). <see cref="Refresh"/> reads the model again and updates the rows in place (no rebuild while the rows are the
/// same, so the keyboard focus stays on the row that was just chosen). A switch or a choice keeps the menu open; an action raises
/// <see cref="CloseRequested"/> after it ran.
/// </summary>
internal sealed class LayoutMenuPanel : StackPanel
{
    private sealed record RowView(LayoutMenuRow Row, Button? Button, Control? Check, TextBlock? Label, TextBlock? Reason, TextBlock? Gesture, Control Root);

    private readonly Func<LayoutMenuModel> _source;
    private readonly Action<LayoutMenuRow> _invoke;
    private readonly string _rowClass;
    private readonly List<RowView> _views = [];
    private string _signature = string.Empty;

    protected override Type StyleKeyOverride => typeof(StackPanel);

    /// <param name="name">The control name of the panel.</param>
    /// <param name="source">The model as it is now (called at every refresh).</param>
    /// <param name="invoke">Runs with the row a person chose.</param>
    /// <param name="rowClass">The style class of the rows (the gallery menu rows' look by default).</param>
    internal LayoutMenuPanel(string name, Func<LayoutMenuModel> source, Action<LayoutMenuRow> invoke, string rowClass = "gallery-overflow-item")
    {
        Name = name;
        Spacing = 2;
        // Menu width rules (CM1): at least 280 and at most 416 dip (the MenuFlyoutMaxWidth token); a longer label is trimmed, the shortcut stays whole.
        MinWidth = 280;
        MaxWidth = MenuLayout.DefaultMaximumWidth;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _invoke = invoke ?? throw new ArgumentNullException(nameof(invoke));
        _rowClass = rowClass;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
    }

    /// <summary>An action row ran (or Escape was pressed, <see cref="HandleMenuKey"/>): the owner closes the flyout.</summary>
    internal event Action? CloseRequested;

    /// <summary>The buttons of the usable and unusable rows, in order (tests and smokes).</summary>
    internal IReadOnlyList<Button> Buttons => _views.Where(view => view.Button != null).Select(view => view.Button!).ToArray();

    /// <summary>The button of the row <paramref name="id"/>, or null.</summary>
    internal Button? ButtonOf(string id) => _views.FirstOrDefault(view => view.Row.Id == id)?.Button;

    /// <summary>The model the panel shows now.</summary>
    internal LayoutMenuModel? Model { get; private set; }

    private IEnumerable<Button> UsableButtons() =>
        Buttons.Where(button => button.Focusable && button.IsEffectivelyVisible && button.IsEffectivelyEnabled);

    /// <summary>Puts the keyboard focus on the first usable row (a flyout that just opened); false when there is none.</summary>
    internal bool FocusFirst()
    {
        Button? first = UsableButtons().FirstOrDefault();
        first?.Focus(NavigationMethod.Tab);
        return first != null;
    }

    /// <summary>
    /// The keys of an open flyout for an owner that does not bring its own: Escape raises <see cref="CloseRequested"/>, Up, Down, Home and End
    /// move through the usable rows (cycling). True when the key was used (the owner marks the event handled). Call it from a tunnelling
    /// key handler on the panel.
    /// </summary>
    internal bool HandleMenuKey(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return false;
        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke();
            return true;
        }
        if (e.Key is not (Key.Down or Key.Up or Key.Home or Key.End)) return false;
        Button[] rows = UsableButtons().ToArray();
        if (rows.Length == 0) return false;
        int current = Array.FindIndex(rows, row => row.IsKeyboardFocusWithin);
        int next = e.Key switch
        {
            Key.Home => 0,
            Key.End => rows.Length - 1,
            Key.Down => current < 0 ? 0 : (current + 1) % rows.Length,
            _ => current < 0 ? rows.Length - 1 : (current - 1 + rows.Length) % rows.Length
        };
        rows[next].Focus(NavigationMethod.Tab);
        return true;
    }

    /// <summary>Reads the model again: rows are updated in place, or rebuilt when the set of rows changed.</summary>
    internal void Refresh()
    {
        LayoutMenuModel model = _source();
        Model = model;
        string signature = string.Join('|', model.Rows.Select(row => row.Kind + ":" + row.Id + ":" + row.Indent)) + "#" + model.Title;
        if (signature != _signature) Rebuild(model, signature);
        else for (int i = 0; i < _views.Count; i++) Update(i, model.Rows[i]);
    }

    private void Rebuild(LayoutMenuModel model, string signature)
    {
        _signature = signature;
        Children.Clear();
        _views.Clear();
        // The title: a first line above the rows (it names what the menu edits, for example the mode).
        Children.Add(TitleBlock(model.Title));
        foreach (LayoutMenuRow row in model.Rows)
        {
            RowView view = Create(row);
            _views.Add(view);
            Children.Add(view.Root);
            Update(_views.Count - 1, row);
        }
    }

    private TextBlock TitleBlock(string text)
    {
        TextBlock block = new() { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16, 8, 16, 4), IsVisible = text.Length > 0 };
        block.Classes.Add("workspace-meta");
        AutomationProperties.SetName(block, text);
        return block;
    }

    private RowView Create(LayoutMenuRow row)
    {
        switch (row.Kind)
        {
            case LayoutMenuRowKind.Separator:
                return new RowView(row, null, null, null, null, null, new Border
                {
                    Height = 1, Margin = new Thickness(8, 4), IsHitTestVisible = false,
                    [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Border")
                });
            case LayoutMenuRowKind.Note:
                TextBlock note = new() { Text = row.Label, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16, 4) };
                note.Classes.Add("workspace-meta");
                return new RowView(row, null, null, note, null, null, note);
        }
        bool check = row.Kind != LayoutMenuRowKind.Action;
        Control lead;
        Control? mark = null;
        if (check)
        {
            // The drawn check of the "⋯" rows (not the font's tick): visible while the row is on.
            mark = WorkspaceIcons.Create(WorkspaceIcons.Check);
            mark.Opacity = 0;
            lead = mark;
        }
        else lead = new Border { Width = 16, Height = 16 };
        TextBlock label = new() { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        TextBlock reason = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, IsHitTestVisible = false };
        reason.Classes.Add("workspace-meta");
        StackPanel text = new() { Spacing = 0, VerticalAlignment = VerticalAlignment.Center, Children = { label, reason } };
        TextBlock gesture = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, IsHitTestVisible = false };
        gesture.Classes.Add("workspace-meta");
        Grid root = new() { ColumnDefinitions = new ColumnDefinitions("20,*,Auto"), ColumnSpacing = 8 };
        root.Children.Add(lead);
        root.Children.Add(text); Grid.SetColumn(text, 1);
        root.Children.Add(gesture); Grid.SetColumn(gesture, 2);
        Button button = check ? new ToggleButton { Name = "LayoutMenuRow" } : new Button { Name = "LayoutMenuRow" };
        button.Classes.Add(_rowClass);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Content = root;
        button.Tag = row.Id;
        button.Margin = new Thickness(row.Indent * 24, 0, 0, 0);
        RowView view = new(row, button, mark, label, reason, gesture, button);
        button.Click += (_, _) =>
        {
            LayoutMenuRow chosen = _views.First(item => ReferenceEquals(item.Button, button)).Row;
            if (!chosen.Enabled) return;
            _invoke(chosen);
            if (chosen.Kind == LayoutMenuRowKind.Action) CloseRequested?.Invoke();
            else Refresh();
        };
        return view;
    }

    // The state of a row changed: the stored row is replaced, the controls stay.
    private void Update(int index, LayoutMenuRow row)
    {
        RowView view = _views[index] = _views[index] with { Row = row };
        if (view.Button == null)
        {
            if (view.Label != null) view.Label.Text = row.Label;
            return;
        }
        view.Label!.Text = row.Label;
        if (view.Check != null) view.Check.Opacity = row.Checked ? 1 : 0;
        if (view.Button is ToggleButton toggle && toggle.IsChecked != row.Checked) toggle.IsChecked = row.Checked;
        view.Button.IsEnabled = row.Enabled;
        view.Reason!.Text = row.DisabledReason ?? string.Empty;
        view.Reason.IsVisible = row.DisabledReason != null;
        string shortcut = row.CommandId == null ? string.Empty : CommandCatalog.ShortcutDisplay(row.CommandId) ?? string.Empty;
        view.Gesture!.Text = shortcut;
        // The room in front of a shortcut is 24 dip (the column spacing 8 plus 16), none for a row without one.
        view.Gesture.Margin = shortcut.Length == 0 ? default : new Thickness(MenuLayout.MinimumShortcutGap - 8, 0, 0, 0);
        string tip = row.DisabledReason ?? row.Hint ?? string.Empty;
        string tooltip = tip.Length == 0 ? row.Label : row.Label + " · " + tip;
        AutomationProperties.SetName(view.Button, row.Label);
        AutomationProperties.SetHelpText(view.Button, row.DisabledReason ?? row.Description ?? row.Hint ?? string.Empty);
        ToolTip.SetTip(view.Button, tooltip);
        ToolTip.SetShowOnDisabled(view.Button, true);
    }
}
