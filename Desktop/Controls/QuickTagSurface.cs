using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.Desktop.Controls;

/// <summary>Where the keyboard focus is inside the quick tag surface.</summary>
internal enum QuickTagFocus { Field, Suggestions, Assigned, Other }

/// <summary>What a key means to the quick tag surface (PLAN-SCHNELL-TAGGEN §1.2).</summary>
internal enum QuickTagKeyAction
{
    None, ToggleSuggestion, ToggleBadge, Move, MoveFirst, MoveLast, ToggleFocused, RemoveFocused,
    FocusField, FocusSuggestions, FocusAssigned, FocusLastAssigned, Undo, CloseList, Close,
    /// <summary>Esc with text in the field: the text goes (and its suggestion list with it); the next Esc closes the surface.</summary>
    ClearField,
    Next, Previous, MarkNext, UnmarkPrevious, CommitAndNext, ThreadSection,
    /// <summary>Ctrl+Shift+Enter (⌘⇧↩): give the stamp set to the shown medium or selection.</summary>
    ApplyStamp
}

/// <summary>
/// A routed key: the action and its value (the 0-based suggestion of <see cref="QuickTagKeyAction.ToggleSuggestion"/>, the
/// step of <see cref="QuickTagKeyAction.Move"/>, the upper-case letter of <see cref="QuickTagKeyAction.ToggleBadge"/>).
/// </summary>
internal readonly record struct QuickTagKeyRoute(QuickTagKeyAction Action, int Value = 0)
{
    internal static readonly QuickTagKeyRoute None = new(QuickTagKeyAction.None);
}

/// <summary>The gallery moves requested from the panel (TP2 wires them; a popover leaves navigation off).</summary>
internal enum QuickTagNavigation { Next, Previous, MarkNext, UnmarkPrevious }

/// <summary>
/// Fixed focus navigation and effective command routing of the quick tag surface. Rebindable commands have no local aliases;
/// only Escape, focus movement, focused-chip activation/removal and letter mnemonics remain structural keys.
/// </summary>
internal static class QuickTagKeys
{
    internal static KeyModifiers ChooseModifier(ShortcutPlatform platform) => KeyModifiers.Alt;
    internal static KeyModifiers PrimaryModifier(ShortcutPlatform platform) => platform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>1–9 of the main row or the number pad, else null.</summary>
    internal static int? Digit(Key key) =>
        key is >= Key.D1 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 : null;

    internal static char? Letter(Key key) => key is >= Key.A and <= Key.Z ? (char)('A' + (key - Key.A)) : null;

    /// <param name="listOpen">The field's suggestion list is open.</param>
    /// <param name="fieldEmpty">The field holds no text (Ctrl+Z then is the surface's undo, not the editor's).</param>
    /// <param name="navigation">The host moves through media (gallery panel); off in a popover.</param>
    internal static QuickTagKeyRoute Route(Key key, KeyModifiers modifiers, QuickTagFocus focus, bool listOpen, bool fieldEmpty,
        bool navigation, ShortcutPlatform platform, EffectiveShortcutBindings? bindings = null) =>
        Route(new KeyEventArgs { Key = key, KeyModifiers = modifiers }, focus, listOpen, fieldEmpty,
            navigation, bindings ?? EffectiveShortcutBindings.Build(platform: platform));

    internal static QuickTagKeyRoute Route(KeyEventArgs press, QuickTagFocus focus, bool listOpen, bool fieldEmpty,
        bool navigation, EffectiveShortcutBindings bindings)
    {
        QuickTagKeyRoute command = RouteCommand(press, focus, listOpen, fieldEmpty, navigation, bindings);
        if (command.Action != QuickTagKeyAction.None) return command;
        Key key = press.Key;
        KeyModifiers modifiers = press.KeyModifiers;
        if (key == Key.Escape && modifiers == KeyModifiers.None)
            return new(focus != QuickTagFocus.Field ? QuickTagKeyAction.Close
                : !fieldEmpty ? QuickTagKeyAction.ClearField
                : listOpen ? QuickTagKeyAction.CloseList : QuickTagKeyAction.Close);
        if (modifiers != KeyModifiers.None) return QuickTagKeyRoute.None;
        switch (focus)
        {
            case QuickTagFocus.Field:
                if (listOpen || !fieldEmpty) return QuickTagKeyRoute.None;
                return key switch
                {
                    Key.Back => new(QuickTagKeyAction.FocusLastAssigned),
                    Key.Tab or Key.Down => new(QuickTagKeyAction.FocusSuggestions),
                    Key.Up => new(QuickTagKeyAction.FocusAssigned),
                    _ => QuickTagKeyRoute.None
                };
            case QuickTagFocus.Suggestions:
                if (Letter(key) is { } letter) return new(QuickTagKeyAction.ToggleBadge, letter);
                return key switch
                {
                    Key.Left => new(QuickTagKeyAction.Move, -1),
                    Key.Right => new(QuickTagKeyAction.Move, 1),
                    Key.Home => new(QuickTagKeyAction.MoveFirst),
                    Key.End => new(QuickTagKeyAction.MoveLast),
                    Key.Space or Key.Enter => new(QuickTagKeyAction.ToggleFocused),
                    Key.Up => new(QuickTagKeyAction.FocusField),
                    Key.Down => new(QuickTagKeyAction.FocusAssigned),
                    _ => QuickTagKeyRoute.None
                };
            case QuickTagFocus.Assigned:
                return key switch
                {
                    Key.Delete or Key.Back => new(QuickTagKeyAction.RemoveFocused),
                    Key.Left => new(QuickTagKeyAction.Move, -1),
                    Key.Right => new(QuickTagKeyAction.Move, 1),
                    Key.Home => new(QuickTagKeyAction.MoveFirst),
                    Key.End => new(QuickTagKeyAction.MoveLast),
                    Key.Space or Key.Enter => new(QuickTagKeyAction.ToggleFocused),
                    Key.Up => new(QuickTagKeyAction.FocusSuggestions),
                    _ => QuickTagKeyRoute.None
                };
            default:
                return QuickTagKeyRoute.None;
        }
    }

    /// <summary>All rebindable quick-tag actions resolve from the same snapshot as the shortcut table.</summary>
    internal static QuickTagKeyRoute RouteCommand(KeyEventArgs press, QuickTagFocus focus, bool listOpen,
        bool fieldEmpty, bool navigation, EffectiveShortcutBindings bindings)
    {
        if (focus == QuickTagFocus.Field && FieldOwnsCharacter(press, bindings.Platform)) return QuickTagKeyRoute.None;
        foreach (string id in QuickTagCommands.All)
        {
            if (!bindings.Matches(press, id) || GalleryTagPanelCommands.RouteOf(id) is not { } route) continue;
            if (route.Action == QuickTagKeyAction.Undo && focus == QuickTagFocus.Field && !fieldEmpty) return QuickTagKeyRoute.None;
            bool moves = route.Action is QuickTagKeyAction.Next or QuickTagKeyAction.Previous or QuickTagKeyAction.MarkNext
                or QuickTagKeyAction.UnmarkPrevious or QuickTagKeyAction.CommitAndNext or QuickTagKeyAction.ThreadSection;
            if (moves && (!navigation || focus == QuickTagFocus.Field && listOpen)) return QuickTagKeyRoute.None;
            return route;
        }
        return QuickTagKeyRoute.None;
    }

    // Option-number can produce text even when the platform leaves KeySymbol empty.
    internal static bool FieldOwnsCharacter(KeyEventArgs press, ShortcutPlatform platform) =>
        TextEntryKeys.TypesText(press)
        || EffectiveShortcutBindings.IsSingleCharacterKey(new ShortcutGesture(press.Key.ToString(), ModifierNames(press.KeyModifiers)))
        || platform == ShortcutPlatform.MacOS && press.Key is >= Key.D0 and <= Key.D9
            && press.KeyModifiers is KeyModifiers.Alt or (KeyModifiers.Alt | KeyModifiers.Shift);

    private static IReadOnlyList<string> ModifierNames(KeyModifiers modifiers) =>
        new[] { (KeyModifiers.Control, "Control"), (KeyModifiers.Meta, "Meta"), (KeyModifiers.Alt, "Alt"), (KeyModifiers.Shift, "Shift") }
            .Where(part => modifiers.HasFlag(part.Item1)).Select(part => part.Item2).ToArray();

    /// <summary>The effective suggestion binding (0-based); null when unbound or beyond the numbered ones.</summary>
    internal static string? SuggestionGesture(int index, ShortcutPlatform platform) =>
        index is < 0 or >= QuickTagOptions.NumberedSuggestions ? null
        : BoundGesture(QuickTagCommands.Suggestion(index + 1), platform);

    /// <summary>
    /// The shortcut text of a command as it is bound now (the person may have changed or removed it, or a default may have yielded to another
    /// command), several gestures joined by " / "; null when the command has no key. The help texts name this, never the catalog default.
    /// </summary>
    internal static string? BoundGesture(string commandId, ShortcutPlatform platform)
    {
        IReadOnlyList<ShortcutGesture> gestures = ShortcutService.Current.For(commandId);
        return gestures.Count == 0 ? null : string.Join(" / ", gestures.Select(gesture => gesture.Display(platform)));
    }

    internal static string? UndoGesture(ShortcutPlatform platform) => BoundGesture(QuickTagCommands.Undo, platform);
    internal static string? CommitNextGesture(ShortcutPlatform platform) => BoundGesture(QuickTagCommands.CommitNext, platform);
    internal static string? StampGesture(ShortcutPlatform platform) => BoundGesture(QuickTagCommands.StampApply, platform);

    /// <summary>The text <paramref name="key"/> with the gesture, or <c>key + "NoKey"</c> (the same text without a shortcut) when the command has none.</summary>
    internal static string TextWithKey(string key, string? gesture, params object[] leading) =>
        gesture is null ? Texts.T(key + "NoKey", leading) : Texts.T(key, leading.Append(gesture).ToArray());
}

/// <summary>
/// How many suggestion chips fit a slot: the greedy line wrap of a wrap panel (items left to right, a new row when the next does not fit
/// the width, rows without line spacing) over the chips' measured sizes. The first <c>minimum</c> chips (the configured suggestion count)
/// are always taken, even when they overflow; later ones only while their row still fits the height. Pure, so it is tested without a window.
/// </summary>
internal struct QuickTagStripFit(double width, double height, double itemSpacing, int minimum, int minimumRows = 0)
{
    private double _x, _used, _rowHeight;
    private int _row = 1;

    /// <summary>Places the chip of <paramref name="size"/> as number <paramref name="index"/> (0-based); false when it does not fit (and is not placed).</summary>
    internal bool TryAdd(int index, Size size)
    {
        bool newRow = _x > 0 && _x + itemSpacing + size.Width > width;
        double rowsAbove = newRow ? _used + _rowHeight : _used;
        double thisRow = newRow ? size.Height : Math.Max(_rowHeight, size.Height);
        int row = newRow ? _row + 1 : _row;
        if (index >= minimum && row > minimumRows && rowsAbove + thisRow > height) return false;
        _row = row;
        if (newRow) { _used += _rowHeight; _x = size.Width; _rowHeight = size.Height; }
        else { _x += (_x > 0 ? itemSpacing : 0) + size.Width; _rowHeight = thisRow; }
        return true;
    }

    /// <summary>
    /// True when the size of a chip has to be measured (again): it never was, or it was measured for another strip width (a chip's
    /// desired size can follow the width it is offered, so a size kept across a resize is a stale one).
    /// </summary>
    internal static bool NeedsMeasure(bool measured, double measuredWidth, double width) => !measured || measuredWidth != width;

    /// <summary>The number of chips of <paramref name="sizes"/> (in order) that fit, at least <paramref name="minimum"/> of them (or all, if fewer).</summary>
    internal static int Count(IReadOnlyList<Size> sizes, double width, double height, double itemSpacing, int minimum, int minimumRows = 0)
    {
        QuickTagStripFit fit = new(width, height, itemSpacing, minimum, minimumRows);
        for (int index = 0; index < sizes.Count; index++)
            if (!fit.TryAdd(index, sizes[index])) return index;
        return sizes.Count;
    }
}

/// <summary>Number and letter keys of the suggestion chips.</summary>
internal static class QuickTagBadges
{
    /// <summary>
    /// The letter key of each name: the first letter A–Z of the name (case ignored) that no earlier chip took, else none.
    /// Only ASCII letters, because the key is matched by the physical key while the strip has focus.
    /// </summary>
    internal static char?[] Letters(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        HashSet<char> taken = new();
        char?[] letters = new char?[names.Count];
        for (int index = 0; index < names.Count; index++)
            foreach (char raw in names[index] ?? string.Empty)
            {
                char letter = char.ToUpperInvariant(raw);
                if (letter is < 'A' or > 'Z' || !taken.Add(letter)) continue;
                letters[index] = letter;
                break;
            }
        return letters;
    }

    /// <summary>The 1-based number of a chip, or null beyond the numbered ones.</summary>
    internal static int? Number(int index) => index is >= 0 and < QuickTagOptions.NumberedSuggestions ? index + 1 : null;
}

/// <summary>Localized reason texts of the suggestion chips.</summary>
internal static class QuickTagReasons
{
    internal static string Text(QuickTagReason reason, string? detail = null) => reason switch
    {
        QuickTagReason.Recent => Texts.T("tags.quick.reason.recent"),
        QuickTagReason.Thread => Texts.T("tags.quick.reason.thread"),
        QuickTagReason.Neighbour => Texts.T("tags.quick.reason.neighbour"),
        QuickTagReason.SameFile => string.IsNullOrWhiteSpace(detail) ? Texts.T("tags.quick.reason.sameFile") : Texts.T("tags.quick.reason.sameFileIn", detail),
        QuickTagReason.FileName => Texts.T("tags.quick.reason.fileName"),
        QuickTagReason.Title => Texts.T("tags.quick.reason.title"),
        QuickTagReason.SourceTag => Texts.T("tags.quick.reason.sourceTag"),
        QuickTagReason.Stamp => Texts.T("tags.quick.reason.stamp"),
        _ => Texts.T("tags.quick.reason.frequent")
    };
}

/// <summary>
/// The shared core of the gallery tag panel (TP2) and the right-click tag popover (TP3): header with the title on the left and the
/// isolated close button on the right edge, the tag field (own tags, Danbooru source tags, "New tag"), the numbered suggestion
/// strip with reasons, the assigned chips (tri-state for a selection), the status line and the footer actions on the left
/// (undo, stamp, "Open tag editor"). Every change is written at once through <see cref="QuickTagSession"/>; nothing is assigned
/// automatically. The host frames it (overlay panel or flyout), handles <see cref="CloseRequested"/>,
/// <see cref="AdvancedRequested"/> and, in the gallery, <see cref="NavigationRequested"/>, and disposes it.
/// </summary>
internal sealed class QuickTagSurface : UserControl, IDisposable
{
    /// <summary>Below this width (the gallery side panel) the footer buttons are stacked, one per line at full width; from it they sit in one row. Both show icon and text.</summary>
    internal const double CompactWidth = 320;
    /// <summary>Gap between the footer buttons and between a button's icon and its text, on the 8 dip grid.</summary>
    internal const double FooterGap = 8;
    /// <summary>Horizontal gap between suggestion chips (the strip's wrap panel).</summary>
    private const double StripItemSpacing = 8;
    /// <summary>Gaps between assigned pills: 8 across and 8 between rows (a pill is 28 high, its remove half joined to it is 28 high too).</summary>
    private const double AssignedItemSpacing = 8, AssignedLineSpacing = 8;
    /// <summary>Minimum suggestion capacity in the shared scroll flow; each 28 dip pill has an 8 dip vertical hit-zone inset.</summary>
    internal const int MinimumSuggestionRows = 3;
    internal const double SuggestionRowHeight = 36;
    /// <summary>Delay of the re-fit after the panel was resized, in milliseconds (a drag fires many size changes).</summary>
    private const int FitDebounceMs = 60;
    private const string UndoIcon = "corner-up-left", StampIcon = "check-square", AdvancedIcon = "external-link";
    private readonly QuickTagSession _session;
    private readonly QuickTagOptions _options;
    private readonly ShortcutPlatform _platform;
    private readonly string _name;
    private readonly SourceTagSuggestions _sourceTags = new();
    // Gallery marking (E4): "3 marked" with a button that clears the marks; shown below the field while the host has marks.
    private readonly Border _markedBadge;
    private readonly TextBlock _markedLabel;
    private readonly Button _markedClear = new() { Classes = { "subtle" } };
    private readonly StackPanel _markBar;
    // The field with what sits around it: an optional decision row above (PT6), the field, the marks bar under it.
    private readonly StackPanel _fieldArea;
    private Control? _decisionContent;
    private MediaPanelSections? _sidebarSections;
    private ContentControl? _sidebarActions;
    // The hairline between the decision row and the field (12 dip of air on both sides: the field area's 8 plus the line's own 4).
    private Border? _decisionDivider;
    private IQuickTagExtraUndo? _extraUndo;
    private readonly TextBlock _title = new() { Classes = { "workspace-panel-title" }, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly PanelCloseButton _close;
    private readonly IdentitySuggestionBox _input = new() { Height = WorkspaceMetrics.InputHeight, ProtectComposition = true };
    private readonly TextBlock _suggestionsHeading = new() { FontWeight = FontWeight.SemiBold, Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _strip = new() { ItemSpacing = StripItemSpacing, LineSpacing = 0, VerticalAlignment = VerticalAlignment.Top };
    // One viewport for the entire surface in both hosts. Assigned tags and suggestions never scroll independently.
    private readonly ScrollViewer _flow = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Focusable = false };
    private readonly Border _stripHost = new();
    private readonly Border _upper = new();
    private readonly TextBlock _stripEmpty = new() { Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _more = new() { Classes = { "subtle" }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _allHeading = new() { Classes = { "workspace-meta" }, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _allChips = new() { ItemSpacing = AssignedItemSpacing, LineSpacing = AssignedLineSpacing };
    private readonly TextBlock _someHeading = new() { Classes = { "workspace-meta" }, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _someChips = new() { ItemSpacing = AssignedItemSpacing, LineSpacing = AssignedLineSpacing };
    private readonly TextBlock _assignedEmpty = new() { Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _assigned;
    private readonly ContentControl _extra = new() { IsVisible = false };
    // Two lines, then the ellipsis; a status cut there shows in full as its tooltip (narrow-window sweep, 16.11.2026).
    private readonly TextBlock _status = new TrimTipTextBlock { Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _undo = new();
    private readonly Button _stamp = new();
    private readonly Button _advanced = new();
    private readonly List<SuggestionChip> _suggestionChips = new();
    // One entry per chip now in the strip (the hit zone and its measured size); _suggestionChips is the same list seen as chips.
    private readonly List<StripItem> _stripItems = new();
    // The ranked suggestions the strip may show (up to the cap), and their letter keys; _shown is the prefix that is on screen.
    private IReadOnlyList<QuickTagSuggestion> _candidates = Array.Empty<QuickTagSuggestion>();
    private char?[] _candidateLetters = Array.Empty<char?>();
    private DispatcherTimer? _fitTimer;
    private bool _fillSuggestions;
    private bool _stripMeasured;
    // True while Refresh rebuilds the blocks: the strip is fitted by Refresh itself, against the layout it just took.
    private bool _refreshing;
    private readonly List<AssignedChip> _assignedChips = new();
    private IQuickTagSuggestionSource _source = FrequentQuickTagSuggestions.Instance;
    // The source the strip asks: the stamp set in front of _source, whatever source the host passes.
    private IQuickTagSuggestionSource _decorated = null!;
    private IReadOnlyList<QuickTagSuggestion> _shown = Array.Empty<QuickTagSuggestion>();
    private string _targetLabel = string.Empty;
    private int _markedCount;
    // A short host notice (a mark was set or refused) in the polite status line; it gives way to the next step's status.
    private string? _notice;
    private QuickTagStatus? _noticeBase;
    private bool _compact;
    private bool _attached;
    private bool _disposed;
    private bool _refreshQueued;

    internal QuickTagSurface(QuickTagSession session, QuickTagOptions? options = null, string name = "QuickTag", ShortcutPlatform? platform = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = (options ?? QuickTagOptions.Default).Normalized();
        _platform = platform ?? EffectiveShortcutBindings.CurrentPlatform;
        _name = string.IsNullOrWhiteSpace(name) ? "QuickTag" : name;
        Name = _name;
        Focusable = false;
        _close = new PanelCloseButton(_name + "Close");
        _input.Name = _name + "Input"; _strip.Name = _name + "Suggestions"; _more.Name = _name + "More";
        _allChips.Name = _name + "Assigned"; _someChips.Name = _name + "AssignedSome"; _status.Name = _name + "Status";
        _undo.Name = _name + "Undo"; _stamp.Name = _name + "Stamp"; _advanced.Name = _name + "Advanced"; _title.Name = _name + "Title"; _extra.Name = _name + "Extra";
        _stripHost.Name = _name + "SuggestionsHost"; _upper.Name = _name + "Body"; _flow.Name = _name + "Scroll";
        _stripEmpty.Name = _name + "SuggestionsEmpty"; _assignedEmpty.Name = _name + "AssignedEmpty";
        _markedClear.Name = _name + "MarkedClear";

        _markedBadge = WorkspaceChips.CreateBadge(_name + "Marked", string.Empty, compact: true);
        _markedLabel = (TextBlock)_markedBadge.Child!;
        _markedBadge.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_markedBadge, AutomationLiveSetting.Polite);
        _markBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Name = _name + "MarkBar", Children = { _markedBadge, _markedClear } };

        Grid header = new() { Height = WorkspaceMetrics.InputHeight, ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Name = _name + "Header" };
        header.Children.Add(_title);
        header.Children.Add(_close); Grid.SetColumn(_close, 1);
        _close.VerticalAlignment = VerticalAlignment.Center;

        _assigned = new StackPanel { Spacing = 4, Children = { _allHeading, _allChips, _someHeading, _someChips, _assignedEmpty } };
        // Natural-height siblings: scroll past every assigned row to reach the complete suggestion block.
        _upper.Child = new StackPanel { Spacing = 8, Children = { _assigned, _extra } };
        _stripHost.Child = new StackPanel { Children = { _strip, _stripEmpty } };
        Grid suggestionArea = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 4, Name = _name + "SuggestionArea" };
        Control suggestionsHeader = SuggestionsHeader();
        suggestionArea.Children.Add(suggestionsHeader);
        suggestionArea.Children.Add(_stripHost); Grid.SetRow(_stripHost, 1);
        suggestionArea.Children.Add(_more); Grid.SetRow(_more, 2);
        _more.VerticalAlignment = VerticalAlignment.Bottom;
        _more.Margin = new Thickness(0, 4, 0, 0);

        // The footer: three buttons of icon and text. In a narrow host (the side panel) they are stacked at full width, otherwise in a row.
        Grid actions = new() { Name = _name + "Actions" };
        actions.Children.Add(_undo);
        actions.Children.Add(_stamp);
        actions.Children.Add(_advanced);
        Footer = actions;
        BuildFooterButton(_undo, UndoIcon);
        BuildFooterButton(_stamp, StampIcon);
        BuildFooterButton(_advanced, AdvancedIcon);
        ApplyFooterLayout(stacked: false);

        Grid layout = new() { RowDefinitions = new RowDefinitions("32,Auto,Auto,Auto,Auto,Auto"), RowSpacing = 12, VerticalAlignment = VerticalAlignment.Top };
        layout.Children.Add(header);
        // The field with the marks bar under it (a single row, so a hidden bar leaves no gap).
        StackPanel fieldArea = new() { Spacing = 8, Children = { _input, _markBar }, Name = _name + "Field" };
        _fieldArea = fieldArea;
        layout.Children.Add(fieldArea); Grid.SetRow(fieldArea, 1);
        layout.Children.Add(_upper); Grid.SetRow(_upper, 2);
        layout.Children.Add(suggestionArea); Grid.SetRow(suggestionArea, 3);
        layout.Children.Add(_status); Grid.SetRow(_status, 4);
        layout.Children.Add(actions); Grid.SetRow(actions, 5);
        _flow.Content = layout;
        Content = _flow;
        // Keyboard navigation retains its existing order; reveal targets below the fold through the same viewport.
        _flow.AddHandler(GotFocusEvent, (_, e) => { if (e.Source is Control control) control.BringIntoView(); });
        Header = header;
        // Focus order (the visual one): field, assigned chips, extra section, suggestions, "More…", undo, stamp, "Open tag editor", and the close button last
        // (a popover cycles, so Shift+Tab from the field reaches it directly).
        KeyboardNavigation.SetTabIndex(header, 1);

        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _strip.PropertyChanged += OnStripPropertyChanged;
        _flow.SizeChanged += OnStripHostSizeChanged;
        _stripHost.SizeChanged += OnSuggestionWidthChanged;
        _close.Click += OnClose;
        _markedClear.Click += OnMarkedClear;
        _undo.Click += OnUndo;
        _stamp.Click += OnStamp;
        _advanced.Click += OnAdvanced;
        _more.Click += OnMore;
        _decorated = new QuickTagStampSource(_source, _session.Stamp);
        TagSuggestionInput.Attach(_input, () => _session.Busy ? null : _session.Snapshot, _session.OnAll, () => OpenActions.Start(() => AddTypedAsync(), "tags.quick.add.key"), _sourceTags);
        // Rate-and-tag opens with the rating row focused for R.  Some Avalonia/Ursa templates do not
        // transfer mouse focus from the flyout frame to the inner editor reliably, leaving the field
        // visibly clickable but sending subsequent typing to the star row.  Claim focus on an explicit
        // left click in the field after the template has processed the pointer, without changing the
        // normal caret/selection gesture.
        _input.AddHandler(PointerPressedEvent, OnInputPointerPressed, RoutingStrategies.Tunnel);
        _input.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        SizeChanged += OnSizeChanged;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        Localize();
        Refresh();
    }

    /// <summary>HB2: the "Suggestions" heading with the help button at the right edge (how suggestions are ranked and toggled).</summary>
    private Grid SuggestionsHeader()
    {
        HelpButton help = new() { Name = _name + "SuggestionsHelp", HelpKey = "help.tagSuggestions" };
        _suggestionsHeading.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(help, 1);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { _suggestionsHeading, help } };
    }

    internal QuickTagSession Session => _session;
    internal QuickTagOptions Options => _options;
    internal IdentitySuggestionBox Input => _input;
    internal Button CloseButton => _close;
    internal Button UndoButton => _undo;
    /// <summary>The footer button that gives the stamp set to the shown medium or selection.</summary>
    internal Button StampButton => _stamp;
    internal Button AdvancedButton => _advanced;
    internal Button MoreButton => _more;
    internal TextBlock Status => _status;
    internal TextBlock Title => _title;
    internal Grid Header { get; }

    /// <summary>Uses the gallery header and the shared panel section order.</summary>
    internal void UseSidebarHeader()
    {
        if (_sidebarSections != null) return;
        Grid layout = (Grid)_flow.Content!;
        Control suggestionArea = layout.Children[3];
        layout.Children.Clear();
        Header.Children.Clear();
        _close.IsVisible = false;
        _title.Classes.Remove("workspace-panel-title");
        _title.Classes.Add("workspace-meta");
        StackPanel tags = new() { Name = _name + "Tags", Spacing = 8,
            Children = { new StackPanel { Spacing = 4, Children = { _title, _fieldArea } }, _upper, suggestionArea, _status } };
        _sidebarActions = new ContentControl { IsVisible = false };
        UseWrappingActions();
        StackPanel actions = new() { Spacing = FooterGap, Children = { _sidebarActions, Footer } };
        _sidebarSections = new MediaPanelSections { Tags = tags, Actions = actions };
        _flow.Content = _sidebarSections;
        ApplyFooterMode();
        RefreshTexts();
    }

    internal void UseWrappingActions()
    {
        Footer.Children.Clear();
        Footer.ColumnDefinitions.Clear();
        Footer.RowDefinitions.Clear();
        Button[] buttons = [_undo, _stamp, _advanced];
        PanelActionStack actions = new(_name + "FooterActions");
        foreach (Button button in buttons)
        {
            PanelActionStack.SizeButton(button);
            button.Classes.Remove("quick-tag-compact-action");
            actions.Children.Add(button);
        }
        Footer.Children.Add(actions);
        FooterStacked = false;
    }

    internal void SetSidebarInfo(Control info, PanelActionStack actions)
    {
        if (_sidebarSections == null || _sidebarActions == null) return;
        _sidebarSections.Info = info;
        _sidebarActions.Content = actions.Children.Count > 0 ? actions : null;
        _sidebarActions.IsVisible = _sidebarActions.Content != null;
    }

    /// <summary>The footer: undo, stamp and the tag editor, in a row (wide host) or stacked (narrow host).</summary>
    internal Grid Footer { get; }
    /// <summary>True while the footer buttons are stacked, one per line at full width.</summary>
    internal bool FooterStacked { get; private set; }
    /// <summary>The icon name and the label text a footer button shows (null for any other control).</summary>
    internal (string Icon, string Text)? FooterLabelOf(Button button) =>
        button.Content is Grid { Children: [ContentControl { Tag: string icon }, TextBlock text] } ? (icon, text.Text ?? string.Empty) : null;
    /// <summary>The footer button's label control (to read its trimming), or null.</summary>
    internal TextBlock? FooterTextOf(Button button) => button.Content is Grid { Children: [_, TextBlock text] } ? text : null;
    /// <summary>The suggestions now shown as chips, in order (chip n = index n − 1).</summary>
    internal IReadOnlyList<QuickTagSuggestion> Shown => _shown;
    /// <summary>The suggestion chip buttons, in order.</summary>
    internal IReadOnlyList<Button> SuggestionButtons => _suggestionChips.Select(chip => chip.Button).ToArray();
    /// <summary>The assigned chips (on all, then on some), in order, with the tag each one stands for.</summary>
    internal IReadOnlyList<(Button Chip, Button Remove, TagId Tag)> AssignedButtons => _assignedChips.Select(chip => (chip.Button, chip.Remove, chip.Tag)).ToArray();
    /// <summary>The letter key of each shown suggestion (null when its name has no free letter A–Z).</summary>
    internal IReadOnlyList<char?> Letters { get; private set; } = Array.Empty<char?>();

    /// <summary>The file name or selection the surface tags; shown after "Tags ·" and in the accessible name.</summary>
    internal string TargetLabel
    {
        get => _targetLabel;
        set { _targetLabel = value ?? string.Empty; RefreshTexts(); }
    }

    /// <summary>How many media the host has marked ("3 marked" with a clear button under the field); 0 hides the bar.</summary>
    internal int MarkedCount
    {
        get => _markedCount;
        set { value = Math.Max(0, value); if (_markedCount == value) return; _markedCount = value; RefreshTexts(); }
    }

    internal Border MarkedBadge => _markedBadge;
    internal StackPanel MarkBar => _markBar;
    internal Button MarkedClearButton => _markedClear;

    /// <summary>
    /// Says <paramref name="text"/> once in the polite status line (a medium was marked, unmarked or refused). The next accepted
    /// tag change, undo or error replaces it, and an error of the session is never hidden by it.
    /// </summary>
    internal void Announce(string? text)
    {
        _notice = string.IsNullOrWhiteSpace(text) ? null : text;
        _noticeBase = _session.Status;
        RefreshTexts();
    }

    /// <summary>
    /// The ranked suggestions of the strip; the most used own tags until the host passes the context source. The stamp set always
    /// stands in front of them (<see cref="QuickTagStampSource"/>), so a host never has to decorate its own source.
    /// </summary>
    internal IQuickTagSuggestionSource Suggestions
    {
        get => _source;
        set
        {
            _source = value ?? FrequentQuickTagSuggestions.Instance;
            _decorated = new QuickTagStampSource(_source, _session.Stamp);
            Refresh();
        }
    }

    /// <summary>The Danbooru catalog reader for the field's source tag group; read once per <see cref="LoadSourceTags"/>.</summary>
    internal Func<MeowChan.Archive.BooruCatalogSnapshot?>? ReadSourceTags
    {
        get => _sourceTags.Read;
        set => _sourceTags.Read = value;
    }

    /// <summary>The gallery panel moves through media with Page Down/Up and Ctrl+Enter; off for a popover.</summary>
    internal bool NavigationEnabled
    {
        get => _navigation;
        set { if (_navigation == value) return; _navigation = value; if (!_disposed) Localize(); }
    }
    private bool _navigation;

    // The popover supplies the key-context used by its tooltip/help contract.
    internal string ShortcutContext { get; set; } = "gallery";

    /// <summary>An extra section under the assigned chips (the gallery panel's thread tags); null hides it.</summary>
    internal Control? ExtraContent
    {
        get => _extra.Content as Control;
        set { _extra.Content = value; _extra.IsVisible = value != null; }
    }

    /// <summary>
    /// A row above the field (the gallery panel's favorite and rating row, PT6); null removes it. It follows the field's tab order
    /// position visually: Shift+Tab from the field reaches it.
    /// </summary>
    internal Control? DecisionContent
    {
        get => _decisionContent;
        set
        {
            if (ReferenceEquals(_decisionContent, value)) return;
            if (_sidebarSections != null)
            {
                _sidebarSections.Rating = _decisionContent = value;
                return;
            }
            if (_decisionContent != null) _fieldArea.Children.Remove(_decisionContent);
            if (_decisionDivider != null) { _fieldArea.Children.Remove(_decisionDivider); _decisionDivider = null; }
            _decisionContent = value;
            ApplyFooterMode();
            if (value == null) return;
            _fieldArea.Children.Insert(0, value);
            // One divider between what is decided (favourite, rating) and what is typed (tags): the one line of the popover and the panel.
            _decisionDivider = new Border { Classes = { "quick-tag-divider" }, IsHitTestVisible = false, Name = _name + "DecisionDivider" };
            AutomationProperties.SetAccessibilityView(_decisionDivider, AccessibilityView.Raw);
            _fieldArea.Children.Insert(1, _decisionDivider);
        }
    }

    /// <summary>
    /// A second undo stack that shares the undo key and the undo button (the gallery panel's rating and favorite steps): the newest step of
    /// the two is undone first. Null: the button and the key undo tag changes only.
    /// </summary>
    internal IQuickTagExtraUndo? ExtraUndo
    {
        get => _extraUndo;
        set
        {
            if (ReferenceEquals(_extraUndo, value)) return;
            if (_extraUndo != null) _extraUndo.Changed -= OnSessionUpdated;
            _extraUndo = value;
            if (value != null) value.Changed += OnSessionUpdated;
            if (!_disposed) RefreshTexts();
        }
    }

    /// <summary>Puts the focus into the field when the surface is shown (both hosts want it; off for tests and special hosts).</summary>
    internal bool FocusOnAttach { get; set; } = true;

    internal event Action? CloseRequested;
    /// <summary>The marks button was pressed: the host clears its marks and retargets the surface.</summary>
    internal event Action? MarksClearRequested;
    internal event Action? AdvancedRequested;
    internal event Action<QuickTagNavigation>? NavigationRequested;
    /// <summary>Ctrl+T while navigation is on: the panel opens or focuses its thread tags section.</summary>
    internal event Action? ThreadSectionRequested;
    /// <summary>
    /// A stamp run was tried (key or footer button) and ended with the given outcome. The tagging mode listens to go on to the next
    /// medium, but only when the outcome is complete for the medium it still shows; a partial or refused run keeps the medium open.
    /// The surface itself never moves.
    /// </summary>
    internal event Action<QuickTagStampResult>? StampApplied;

    /// <summary>Starts the background read of source tags (each time the host opens the surface).</summary>
    internal void LoadSourceTags() => _sourceTags.Load();

    /// <summary>Puts the focus into the tag field (opening the surface, after a medium switch).</summary>
    internal void FocusField() => _input.Focus();

    /// <summary>
    /// Text typed while another part of the host holds the keyboard (the stars after Tab, a chip, a footer button): the field takes the focus and the text,
    /// at its caret, as if it had been typed there (TG-02: the letters went nowhere). The host marks the key handled, so the platform raises no TextInput
    /// of its own. False when the field cannot take it (disabled, disposed, not realized).
    /// </summary>
    internal bool TypeIntoField(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text) || !_input.IsEffectivelyEnabled || !_input.IsEffectivelyVisible) return false;
        _input.Focus(NavigationMethod.Unspecified);
        if (_input.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(box => box.IsFocused) is not { IsReadOnly: false } box) return false;
        box.RaiseEvent(new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = text });
        return true;
    }

    /// <summary>Switches to another target (the next medium, a marked set) and its label; the undo stack stays.</summary>
    internal void Retarget(IQuickTagTarget target, string label)
    {
        _session.Retarget(target);
        _targetLabel = label ?? string.Empty;
        Refresh();
    }

    // ---- refresh ------------------------------------------------------------------------------------------------------

    private void OnSessionUpdated()
    {
        // Session updates may come from a write's continuation: repaint once, on the UI thread.
        if (_disposed || _refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() => { _refreshQueued = false; if (!_disposed) Refresh(); });
    }

    /// <summary>Rebuilds chips and texts from the session; keeps the focus on the same tag (or its neighbour) where it was.</summary>
    internal void Refresh()
    {
        if (_disposed) return;
        (QuickTagFocus zone, TagId? tag, string? key, int index) = CurrentFocus();
        PrivateTagStoreSnapshot? snapshot = _session.Snapshot;
        IReadOnlySet<TagId> carried = _session.Carried();
        int cap = SuggestionCap;
        _refreshing = true;
        try
        {
            // Keep the candidates and chips in agreement while refreshing the layout. The fit uses only the outer viewport,
            // never the residual height after assigned tags: those may grow beyond the fold without starving suggestions.
            BuildAssigned();
            if (_fillSuggestions && _attached)
            {
                RefreshTexts();
                UpdateLayout();
            }
            _candidates = snapshot == null ? Array.Empty<QuickTagSuggestion>() : Normalize(_decorated.Suggest(
                new QuickTagSuggestionRequest(snapshot, carried, cap)), snapshot, carried, cap);
            _candidateLetters = QuickTagBadges.Letters(_candidates.Select(item => item.Name.Display).ToArray());
            BuildSuggestions();
        }
        finally { _refreshing = false; }
        _input.IsEnabled = snapshot != null;
        if (snapshot == null) _input.Dismiss();
        RefreshTexts();
        RestoreFocus(zone, tag, key, index);
        // A write the surface did not start (the host's) has ended: take an Enter that waited for it.
        if (_deferredAdd != null) OpenActions.Start(ReplayDeferredAddAsync, "tags.quick.add.deferred");
    }

    /// <summary>
    /// Drops carried tags, duplicates and anything over the limit, whatever the source returned. A name that already is an own
    /// tag (a source tag created earlier) stands for that tag: offered as it, or dropped when the target carries it.
    /// </summary>
    internal static IReadOnlyList<QuickTagSuggestion> Normalize(IReadOnlyList<QuickTagSuggestion>? items, PrivateTagStoreSnapshot snapshot,
        IReadOnlySet<TagId> carried, int limit)
    {
        if (items == null || limit <= 0) return Array.Empty<QuickTagSuggestion>();
        Dictionary<string, PrivateTagDefinition>? own = null;
        HashSet<string> keys = new(StringComparer.Ordinal);
        List<QuickTagSuggestion> result = new();
        foreach (QuickTagSuggestion? candidate in items)
        {
            if (candidate is not { } item) continue;
            QuickTagSuggestion shown = item;
            if (item.Id == null)
            {
                own ??= snapshot.Tags.GroupBy(tag => tag.Name.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
                if (own.TryGetValue(item.Name.Key, out PrivateTagDefinition? existing)) shown = item with { Name = existing.Name, Id = existing.Id };
            }
            if (shown.Id is { } id && carried.Contains(id) || !keys.Add(shown.Name.Key)) continue;
            result.Add(shown);
            if (result.Count >= limit) break;
        }
        return result;
    }

    /// <summary>
    /// Bounded candidate pool for either host. Width determines how many chips make three complete rows; the configured count
    /// remains a floor, not a one-row ceiling. The gallery may additionally fill a viewport worth of rows.
    /// </summary>
    private int SuggestionCap => Math.Max(_options.SuggestionCount, _options.PanelSuggestionLimit);

    /// <summary>
    /// True for a fixed-height gallery panel: realize up to a viewport worth of suggestions. Off: keep the configured count
    /// or three complete wrapped rows, whichever needs more chips. Both modes share the same scroll flow.
    /// </summary>
    internal bool FillSuggestions
    {
        get => _fillSuggestions;
        set
        {
            if (_fillSuggestions == value) return;
            _fillSuggestions = value;
            if (!_disposed) Refresh();
        }
    }

    /// <summary>
    /// The chips the strip always keeps, whatever the height: the configured suggestion count (<see cref="QuickTagOptions.SuggestionCount"/>).
    /// At least three complete rows are retained when available, independently of how many assigned tags precede them.
    /// </summary>
    internal int StripMinimum => _options.SuggestionCount;

    private void BuildSuggestions()
    {
        ClearStrip();
        ApplyFit();
    }

    private void ClearStrip()
    {
        foreach (StripItem item in _stripItems) item.Chip.Button.Click -= OnSuggestionClick;
        _stripItems.Clear();
        _suggestionChips.Clear();
        _strip.Children.Clear();
    }

    // Bound realization by the host viewport, not by the space left after assigned tags. Always keep three complete rows
    // (when candidates suffice) and the configured count. The content then overflows only through the shared scroll owner.
    // The viewport is independent of the content height, avoiding a measure/fit feedback loop.
    private void ApplyFit()
    {
        double width = _stripHost.Bounds.Width, height = _fillSuggestions ? _flow.Bounds.Height : 0;
        bool measurable = width > 0 && _flow.Bounds.Height > 0;
        _stripMeasured = measurable;
        int limit = measurable ? _candidates.Count : Math.Min(_candidates.Count, _options.SuggestionCount);
        int count = 0;
        QuickTagStripFit fit = new(width, height, StripItemSpacing, StripMinimum, MinimumSuggestionRows);
        for (int index = 0; index < limit; index++)
        {
            StripItem item = EnsureItem(index);
            if (measurable)
            {
                if (QuickTagStripFit.NeedsMeasure(item.Measured, item.MeasuredWidth, width))
                {
                    item.Zone.Measure(new Size(width, double.PositiveInfinity));
                    item.Size = item.Zone.DesiredSize;
                    item.MeasuredWidth = width;
                    item.Measured = true;
                }
                if (!fit.TryAdd(index, item.Size)) break;
            }
            count++;
        }
        TrimItems(count);
        _strip.MinHeight = count > 0 ? MinimumSuggestionRows * SuggestionRowHeight : 0;
        _shown = count == _candidates.Count ? _candidates : _candidates.Take(count).ToArray();
        Letters = count == _candidateLetters.Length ? _candidateLetters : _candidateLetters.Take(count).ToArray();
        for (int index = 0; index < count; index++)
            AutomationProperties.SetName(_stripItems[index].Chip.Button, AccessibleName(_shown[index], index, count));
        ShowLetters(_strip.IsKeyboardFocusWithin);
    }

    private StripItem EnsureItem(int index)
    {
        if (index < _stripItems.Count) return _stripItems[index];
        QuickTagSuggestion suggestion = _candidates[index];
        SuggestionChip chip = SuggestionChip.Create(_name + "Suggestion" + (index + 1), suggestion, index, _candidateLetters[index], _platform);
        chip.Button.Click += OnSuggestionClick;
        if (suggestion.Id is { } offered) AttachStampMenu(chip.Button, offered);
        if (QuickTagKeys.SuggestionGesture(index, _platform) is { } gesture) AutomationProperties.SetAcceleratorKey(chip.Button, gesture);
        StripItem item = new(chip, HitZone(chip.Button));
        _stripItems.Add(item);
        _suggestionChips.Add(chip);
        _strip.Children.Add(item.Zone);
        return item;
    }

    // Drops the chips beyond the fit; a focused one hands the focus to the last chip that stays (or "More…").
    private void TrimItems(int count)
    {
        if (_stripItems.Count <= count) return;
        bool focused = _stripItems.Skip(count).Any(item => item.Chip.Button.IsKeyboardFocusWithin);
        for (int index = _stripItems.Count - 1; index >= count; index--)
        {
            _stripItems[index].Chip.Button.Click -= OnSuggestionClick;
            _stripItems.RemoveAt(index);
            _suggestionChips.RemoveAt(index);
            _strip.Children.RemoveAt(index);
        }
        if (focused) (count > 0 ? _stripItems[count - 1].Chip.Button : _more).Focus(NavigationMethod.Directional);
    }

    // Only viewport changes affect capacity, never the growing assigned block. The first fit is immediate; later resizes are debounced.
    private void OnStripHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_disposed) return;
        // Refresh fits the strip itself right after the layout it asked for: nothing to schedule for this change.
        if (_refreshing) return;
        if (!_stripMeasured) { RefitStrip(); return; }
        _fitTimer ??= CreateFitTimer();
        _fitTimer.Stop();
        _fitTimer.Start();
    }

    // A scrollbar appearing/disappearing can change content width without resizing the outer viewer.
    private void OnSuggestionWidthChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) OnStripHostSizeChanged(sender, e);
    }

    private DispatcherTimer CreateFitTimer()
    {
        DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(FitDebounceMs) };
        timer.Tick += (_, _) => { timer.Stop(); if (!_disposed) RefitStrip(); };
        return timer;
    }

    // Fits again with the chips already built (grows or trims the strip); the ranking is not asked again.
    private void RefitStrip()
    {
        if (_disposed) return;
        int before = _stripItems.Count;
        (QuickTagFocus zone, TagId? tag, string? key, int index) = CurrentFocus();
        ApplyFit();
        if (_stripItems.Count != before) RefreshTexts();
        RestoreFocus(zone, tag, key, index);
    }

    private sealed class StripItem(SuggestionChip chip, Control zone)
    {
        internal SuggestionChip Chip { get; } = chip;
        internal Control Zone { get; } = zone;
        internal Size Size { get; set; }
        internal bool Measured { get; set; }
        // The strip width the size was measured for.
        internal double MeasuredWidth { get; set; }
    }

    private void BuildAssigned()
    {
        foreach (AssignedChip chip in _assignedChips) { chip.Button.Click -= OnAssignedClick; chip.Remove.Click -= OnRemoveClick; }
        _assignedChips.Clear();
        _allChips.Children.Clear();
        _someChips.Children.Clear();
        foreach (QuickTagAssignment assignment in _session.Assignments())
        {
            AssignedChip chip = AssignedChip.Create(_name + "Assigned" + (_assignedChips.Count + 1), assignment, _session.IsSelection);
            chip.Button.Click += OnAssignedClick;
            chip.Remove.Click += OnRemoveClick;
            AttachStampMenu(chip.Button, assignment.Tag.Id);
            _assignedChips.Add(chip);
            (assignment.State == TagToggleState.All ? _allChips : _someChips).Children.Add(chip.Host);
        }
        // All first, then some: the order of the chips in the focus cycle follows what is seen.
        _assignedChips.Sort((left, right) => (left.State == TagToggleState.All ? 0 : 1).CompareTo(right.State == TagToggleState.All ? 0 : 1));
    }

    private void RefreshTexts()
    {
        if (_disposed) return;
        string label = _targetLabel.Trim();
        TagCoverage coverage = _session.Coverage();
        if (_session.IsSelection && coverage.Total > 1 && label.Length == 0) label = Texts.Count("tags.quick.files", coverage.Total);
        _title.Text = _sidebarSections != null || label.Length == 0 ? Texts.T("tags.quick.heading") : Texts.T("tags.quick.title", label);
        ToolTip.SetTip(_title, _title.Text);
        _stripEmpty.IsVisible = _shown.Count == 0;
        _strip.IsVisible = _shown.Count > 0;
        _more.IsVisible = _session.Snapshot is { Tags.Count: > 0 };
        int all = _assignedChips.Count(chip => chip.State == TagToggleState.All), some = _assignedChips.Count - all;
        _allHeading.Text = _session.IsSelection ? Texts.T("tags.quick.assignedAll", all) : Texts.T("tags.quick.assigned", all);
        _someHeading.Text = Texts.T("tags.quick.assignedSome", some);
        _allHeading.IsVisible = true;
        _allChips.IsVisible = all > 0;
        _someHeading.IsVisible = _someChips.IsVisible = some > 0;
        _assignedEmpty.IsVisible = _assignedChips.Count == 0;

        QuickTagStatus status = _session.Status;
        bool noticed = _notice != null && !status.IsError && ReferenceEquals(_noticeBase, status);
        if (!noticed) _notice = null;
        _markBar.IsVisible = _markedCount > 0;
        _markedLabel.Text = _markedCount > 0 ? Texts.T("tags.quick.marked", _markedCount.ToString(Texts.Culture)) : string.Empty;
        AutomationProperties.SetName(_markedBadge, _markedLabel.Text);
        _status.Text = noticed ? _notice! : status.Text;
        _status.IsVisible = _status.Text.Length > 0;
        _status.Classes.Set("workspace-error", status.IsError);
        _status.Classes.Set("workspace-meta", !status.IsError);
        AutomationProperties.SetLiveSetting(_status, status.IsError ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);

        // Not disabled while a write runs (the session ignores the click), so a focused undo button keeps the focus.
        int extraSteps = _extraUndo?.Steps ?? 0;
        _undo.IsEnabled = _session.UndoSteps.Count > 0 && _session.Snapshot != null || extraSteps > 0;
        AutomationProperties.SetName(_undo, Texts.T("tags.quick.undoName", _session.UndoSteps.Count + extraSteps));
        _stamp.IsEnabled = _session.Snapshot != null && _session.Stamp.Count > 0;
        AutomationProperties.SetName(_stamp, StampName());
        ToolTip.SetTip(_stamp, StampName());
        _advanced.IsEnabled = _session.Snapshot != null;

        AutomationProperties.SetName(this, Texts.T("tags.quick.name", label.Length == 0 ? Texts.T("tags.quick.heading") : label));
        AutomationProperties.SetName(_flow, AutomationProperties.GetName(this));
        AutomationProperties.SetHelpText(this, Texts.T("tags.quick.help", _shown.Count, _assignedChips.Count));
    }

    private void Localize()
    {
        TagSuggestionInput.Localize(_input);
        string hint = Texts.T("tags.suggest.hint") + " " + Texts.T("tags.quick.fieldNavigation") + " " + Texts.T("tags.quick.fieldHint");
        if (NavigationEnabled) hint += " " + QuickTagKeys.TextWithKey("tags.quick.fieldHintNavigation", QuickTagKeys.CommitNextGesture(_platform));
        AutomationProperties.SetHelpText(_input, hint);
        ToolTip.SetTip(_input, hint);
        _close.Localize(Texts.T("tags.quick.close"));
        _suggestionsHeading.Text = Texts.T("tags.quick.suggestions");
        AutomationProperties.SetName(_strip, Texts.T("tags.quick.suggestions"));
        AutomationProperties.SetHelpText(_strip, Texts.T("tags.quick.suggestions.help"));
        _stripEmpty.Text = Texts.T("tags.quick.suggestions.empty");
        _markedClear.Content = Texts.T("tags.quick.marked.clear");
        AutomationProperties.SetName(_markedClear, Texts.T("tags.quick.marked.clearName"));
        ToolTip.SetTip(_markedClear, Texts.T("tags.quick.marked.clearName"));
        _more.Content = Texts.T("tags.quick.more");
        AutomationProperties.SetName(_more, Texts.T("tags.quick.moreName"));
        ToolTip.SetTip(_more, Texts.T("tags.quick.moreName"));
        AutomationProperties.SetHelpText(_allChips, Texts.T("tags.quick.assigned.help"));
        AutomationProperties.SetHelpText(_someChips, Texts.T("tags.quick.assigned.help"));
        _assignedEmpty.Text = Texts.T("tags.quick.assigned.empty");
        ToolTip.SetTip(_undo, QuickTagKeys.TextWithKey("tags.quick.undoTip", QuickTagKeys.UndoGesture(_platform)));
        if (QuickTagKeys.StampGesture(_platform) is { } stampKey) AutomationProperties.SetAcceleratorKey(_stamp, stampKey);
        else _stamp.ClearValue(AutomationProperties.AcceleratorKeyProperty);
        AutomationProperties.SetName(_advanced, Texts.T("tags.quick.advancedName"));
        ToolTip.SetTip(_advanced, Texts.T("tags.quick.advancedName"));
        AutomationProperties.SetHelpText(_undo, QuickTagKeys.TextWithKey("tags.quick.undoHelp", QuickTagKeys.UndoGesture(_platform)));
        AutomationProperties.SetHelpText(_stamp, QuickTagKeys.TextWithKey("tags.quick.stamp.help", QuickTagKeys.StampGesture(_platform)));
        AutomationProperties.SetHelpText(_advanced, Texts.T("tags.quick.advancedHelp"));
        // A disabled button still explains itself (the stamp says why it is empty).
        foreach (Button button in new[] { _undo, _stamp, _advanced }) ToolTip.SetShowOnDisabled(button, true);
        ApplyFooterMode();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        Localize();
        Refresh();
    });

    // The help texts name the keys as bound now: a rebinding in the shortcut window rewrites them (the surface is attached while it can show).
    private void OnShortcutsChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || !_attached) return;
        Localize();
        Refresh();
    });

    // Below the compact width (the side panel) the footer buttons are stacked at full width; icon and text stay in both layouts.
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyWidth(e.NewSize.Width);
    }

    /// <summary>Chooses the footer layout for a surface of <paramref name="width"/> dip (0 or less keeps the current one).</summary>
    internal void ApplyWidth(double width)
    {
        bool compact = width > 0 && width < CompactWidth;
        if (width <= 0 || compact == _compact) return;
        _compact = compact;
        ApplyFooterMode();
    }

    // One button: a grid of the icon slot (filled once the control is in a window, the icon needs the platform) and a one-line label
    // that is trimmed with an ellipsis, never wrapped; the full text is in the tooltip and the accessible name.
    private static void BuildFooterButton(Button button, string icon)
    {
        ContentControl slot = new() { Width = 16, Height = 16, Tag = icon, Focusable = false, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, FooterGap, 0) };
        TextBlock text = new() { Classes = { "panel-action-label" }, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        button.Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { slot, text } };
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
    }

    private void ApplyFooterLayout(bool stacked)
    {
        if (Footer.Children is [PanelActionStack]) return;
        FooterStacked = stacked;
        Button[] buttons = [_undo, _stamp, _advanced];
        Footer.ColumnDefinitions.Clear();
        Footer.RowDefinitions.Clear();
        if (stacked)
        {
            Footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (int index = 0; index < buttons.Length; index++) Footer.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Footer.RowSpacing = FooterGap;
            Footer.ColumnSpacing = 0;
            Footer.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            // Two buttons take their text on the left; the last one ("Edit tags…", it leaves the surface) sits at the right edge, in the column that takes the
            // rest of the row, and trims when a language needs more.
            Footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Footer.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Footer.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Footer.RowSpacing = 0;
            Footer.ColumnSpacing = FooterGap;
            Footer.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        for (int index = 0; index < buttons.Length; index++)
        {
            buttons[index].Classes.Set("quick-tag-compact-action", !stacked && _compact);
            Grid.SetRow(buttons[index], stacked ? index : 0);
            Grid.SetColumn(buttons[index], stacked ? 0 : index);
            buttons[index].HorizontalAlignment = stacked ? HorizontalAlignment.Stretch : index == buttons.Length - 1 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }

    private void ApplyFooterMode()
    {
        ApplyFooterLayout(_sidebarSections != null || _compact && _decisionContent == null);
        SetFooterLabel(_undo, Texts.T("tags.quick.undo"));
        SetFooterLabel(_stamp, Texts.T("tags.quick.stamp.button"));
        SetFooterLabel(_advanced, Texts.T("tags.quick.advanced"));
    }

    private void SetFooterLabel(Button button, string label)
    {
        if (button.Content is not Grid { Children: [ContentControl slot, TextBlock text] }) return;
        text.Text = label;
        // At the popover minimum keep the full command names; the outbound editor retains its arrow.
        slot.IsVisible = Footer.Children is [PanelActionStack] || !_compact || FooterStacked || ReferenceEquals(button, _advanced);
        // The icon follows the label's foreground (also its disabled look). Only a control in a window can load it.
        if (_attached && slot.Content == null && slot.Tag is string icon)
        {
            Control mark = CommandBindings.IconFromKey(icon, 16);
            mark.Bind(Border.BackgroundProperty, text.GetObservable(TextBlock.ForegroundProperty));
            slot.Content = mark;
        }
    }

    // ---- actions ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Enter (or Ctrl+Enter with <paramref name="thenNext"/>) in the field: adds the typed name. While another write runs the
    /// draft stays and the Enter is taken once, right after that write, if the field still holds the same text. After the
    /// write only the text that was written is cleared: what the user typed meanwhile for the next tag stays in the field.
    /// False when the draft stays in the field (a write is running or the name was refused).
    /// </summary>
    internal async Task<bool> AddTypedAsync(bool thenNext = false)
    {
        string name = _input.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (!_session.Ready)
            {
                if (_session.Snapshot != null) _deferredAdd = (name, thenNext);
                return false;
            }
            await Guard(() => _session.AddAsync(name), "tags.quick.add");
            if (_disposed || _session.Status.IsError) return false;
            if (string.Equals(_input.Text, name, StringComparison.Ordinal)) _input.Text = string.Empty;
        }
        if (thenNext) NavigationRequested?.Invoke(QuickTagNavigation.Next);
        return true;
    }

    // An Enter that came while a write was running; taken once that write is done (see AddTypedAsync).
    private (string Name, bool ThenNext)? _deferredAdd;

    private async Task ReplayDeferredAddAsync()
    {
        if (_deferredAdd is not { } deferred || _disposed || !_session.Ready) return;
        _deferredAdd = null;
        // The user went on editing after that Enter: the draft is theirs again, nothing is written for them.
        if (string.Equals(_input.Text, deferred.Name, StringComparison.Ordinal)) await AddTypedAsync(deferred.ThenNext);
    }

    /// <summary>
    /// Takes over the typed name like Enter, without moving on (the tagging mode's Ctrl+Enter then finishes the medium itself). False when
    /// the draft stays in the field: a write is running or the name was refused.
    /// </summary>
    internal Task<bool> CommitTypedAsync() => AddTypedAsync();

    private async void OnSuggestionClick(object? sender, RoutedEventArgs e)
    {
        if (_suggestionChips.FirstOrDefault(chip => ReferenceEquals(chip.Button, sender)) is { } chip) await OpenActions.RunAsync(() => ToggleSuggestionAsync(chip.Index), "tags.quick.suggestion.click");
    }

    /// <summary>Toggles suggestion <paramref name="index"/> (0-based), as a click, Alt+n or n in the strip does.</summary>
    internal Task ToggleSuggestionAsync(int index) =>
        index < 0 || index >= _shown.Count ? Task.CompletedTask : Guard(() => _session.AcceptAsync(_shown[index]), "tags.quick.suggestion");

    private async void OnAssignedClick(object? sender, RoutedEventArgs e)
    {
        if (_assignedChips.FirstOrDefault(chip => ReferenceEquals(chip.Button, sender)) is { } chip)
            await OpenActions.RunAsync(() => Guard(() => _session.ToggleAsync(chip.Tag), "tags.quick.toggle"), "tags.quick.toggle.click");
    }

    private async void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (_assignedChips.FirstOrDefault(chip => ReferenceEquals(chip.Remove, sender)) is { } chip) await OpenActions.RunAsync(() => RemoveAsync(chip), "tags.quick.remove.click");
    }

    private async Task RemoveAsync(AssignedChip chip)
    {
        int index = _assignedChips.IndexOf(chip);
        _pendingAssignedFocus = index;
        await Guard(() => _session.RemoveAsync(chip.Tag), "tags.quick.remove");
    }

    private int? _pendingAssignedFocus;

    private async void OnUndo(object? sender, RoutedEventArgs e)
    {
        await OpenActions.RunAsync(UndoAsync, "tags.quick.undo.click");
        // The last step is gone and the button with it: the focus goes back to the field instead of nowhere.
        if (!_disposed && _session.UndoSteps.Count == 0 && (_extraUndo?.Steps ?? 0) == 0 && _undo.IsKeyboardFocusWithin) _input.Focus();
    }

    // The newest step of the two stacks goes first: a rating or favorite change made after the last tag change is undone before it.
    internal Task UndoAsync() => Guard(async () =>
    {
        if (_extraUndo is { IsNewest: true } extra)
        {
            if (extra.CanUndo) Announce(await extra.UndoAsync());
            return;
        }
        await _session.UndoAsync();
    }, "tags.quick.undo");

    private void OnMarkedClear(object? sender, RoutedEventArgs e) => MarksClearRequested?.Invoke();

    private async void OnStamp(object? sender, RoutedEventArgs e) => await OpenActions.RunAsync(() => ApplyStampAsync(), "tags.quick.stamp.click");

    /// <summary>Gives the stamp set to the shown medium or selection (Ctrl+Shift+Enter, the footer button).</summary>
    internal async Task<QuickTagStampResult> ApplyStampAsync()
    {
        QuickTagStampResult result = QuickTagStampResult.None;
        await Guard(async () => result = await _session.ApplyStampWithOutcomeAsync(), "tags.quick.stamp");
        if (!_disposed && result.Attempted) StampApplied?.Invoke(result);
        return result;
    }

    // The tooltip and name of the footer button: what it gives (or why it cannot), with its key.
    private string StampName()
    {
        string? gesture = QuickTagKeys.StampGesture(_platform);
        if (_session.Stamp.Count == 0) return QuickTagKeys.TextWithKey("tags.quick.stamp.nameEmpty", gesture);
        string names = string.Join(", ", _session.Stamp.Resolve(_session.Snapshot).Select(tag => tag.Name.Display));
        return names.Length == 0 ? QuickTagKeys.TextWithKey("tags.quick.stamp.nameEmpty", gesture) : QuickTagKeys.TextWithKey("tags.quick.stamp.name", gesture, names);
    }

    // Right click, Menu key or Shift+F10 on a chip of an own tag: put it into the stamp or take it out. The menu is filled when it
    // opens (on the UI thread, and always with the current stamp), so a chip needs no item collection while the strip is rebuilt.
    private void AttachStampMenu(Button chip, TagId tag)
    {
        ContextMenu menu = new();
        menu.Opening += (_, _) => menu.ItemsSource = StampMenuItems(tag);
        chip.ContextMenu = menu;
    }

    /// <summary>The context menu entries of the chip of <paramref name="tag"/>: "Add to stamp" or "Remove from stamp".</summary>
    internal IReadOnlyList<MenuItem> StampMenuItems(TagId tag)
    {
        bool inStamp = _session.InStamp(tag);
        MenuItem item = new() { Header = Texts.T(inStamp ? "tags.quick.stamp.menuRemove" : "tags.quick.stamp.menuAdd"), Name = _name + (inStamp ? "StampRemove" : "StampAdd") };
        item.Click += (_, _) => { if (inStamp) _session.RemoveFromStamp(tag); else _session.AddToStamp(tag); };
        return new[] { item };
    }

    private void OnAdvanced(object? sender, RoutedEventArgs e) => AdvancedRequested?.Invoke();
    private void OnClose(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void OnMore(object? sender, RoutedEventArgs e) => _input.OpenExplicitList();

    private async Task Guard(Func<Task> action, string context)
    {
        try { await action(); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, context)) { /* Logged and shown. */ }
        await ReplayDeferredAddAsync();
    }

    // ---- keys ---------------------------------------------------------------------------------------------------------

    internal QuickTagFocus FocusZone() =>
        _input.IsKeyboardFocusWithin ? QuickTagFocus.Field
        : _strip.IsKeyboardFocusWithin ? QuickTagFocus.Suggestions
        : _assigned.IsKeyboardFocusWithin ? QuickTagFocus.Assigned
        : QuickTagFocus.Other;

    internal bool FieldHasComposition => _input.IsKeyboardFocusWithin && _input.IsComposing;

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || FieldHasComposition) return;
        QuickTagKeyRoute route = QuickTagKeys.Route(e, FocusZone(), _input.IsDropDownOpen,
            string.IsNullOrEmpty(_input.Text), NavigationEnabled, ShortcutService.Current);
        if (Execute(route)) e.Handled = true;
    }

    /// <summary>Carries out a routed key; false when the key is not the surface's (it then goes on to the focused control).</summary>
    internal bool Execute(QuickTagKeyRoute route)
    {
        switch (route.Action)
        {
            case QuickTagKeyAction.ToggleSuggestion:
                if (route.Value >= _shown.Count) return route.Value < QuickTagOptions.NumberedSuggestions;
                OpenActions.Start(() => ToggleSuggestionAsync(route.Value), "tags.quick.suggestion.key");
                return true;
            case QuickTagKeyAction.ToggleBadge:
                int badge = Array.IndexOf(Letters.ToArray(), (char?)(char)route.Value);
                if (badge >= 0) OpenActions.Start(() => ToggleSuggestionAsync(badge), "tags.quick.suggestion.key");
                return badge >= 0;
            case QuickTagKeyAction.Move:
            case QuickTagKeyAction.MoveFirst:
            case QuickTagKeyAction.MoveLast:
                return Move(route);
            case QuickTagKeyAction.ToggleFocused:
                if (FocusedSuggestion() is { } suggestion) { OpenActions.Start(() => ToggleSuggestionAsync(suggestion.Index), "tags.quick.suggestion.key"); return true; }
                if (FocusedAssigned() is { } toggled) { OpenActions.Start(() => Guard(() => _session.ToggleAsync(toggled.Tag), "tags.quick.toggle"), "tags.quick.toggle.key"); return true; }
                return false;
            case QuickTagKeyAction.RemoveFocused:
                if (FocusedAssigned() is not { } removed) return false;
                OpenActions.Start(() => RemoveAsync(removed), "tags.quick.remove.key");
                return true;
            case QuickTagKeyAction.FocusField: _input.Focus(); return true;
            case QuickTagKeyAction.FocusSuggestions:
                return FocusFirst(_suggestionChips.Select(chip => chip.Button)) || FocusFirst(new[] { _more });
            case QuickTagKeyAction.FocusAssigned: return FocusFirst(_assignedChips.Select(chip => chip.Button));
            case QuickTagKeyAction.FocusLastAssigned: return FocusFirst(_assignedChips.Select(chip => chip.Button).Reverse());
            case QuickTagKeyAction.Undo:
                if (_extraUndo is { IsNewest: true } newest)
                {
                    if (newest.CanUndo) OpenActions.Start(UndoAsync, "tags.quick.undo.key");
                    return true;
                }
                if (!_session.CanUndo) return _session.UndoSteps.Count > 0 || _session.Busy;
                OpenActions.Start(UndoAsync, "tags.quick.undo.key");
                return true;
            case QuickTagKeyAction.CloseList: _input.Dismiss(); return true;
            case QuickTagKeyAction.ClearField:
                _input.Text = string.Empty;
                _input.Dismiss(); // after the text: the emptied field must not open its list again
                return true;
            case QuickTagKeyAction.Close:
                // The host disposes the surface while handling the request (which clears the handlers): whether there was a host is asked before.
                Action? close = CloseRequested;
                close?.Invoke();
                return close != null;
            case QuickTagKeyAction.Next: return Navigate(QuickTagNavigation.Next);
            case QuickTagKeyAction.Previous: return Navigate(QuickTagNavigation.Previous);
            case QuickTagKeyAction.MarkNext: return Navigate(QuickTagNavigation.MarkNext);
            case QuickTagKeyAction.UnmarkPrevious: return Navigate(QuickTagNavigation.UnmarkPrevious);
            case QuickTagKeyAction.CommitAndNext:
                if (NavigationRequested == null) return false;
                OpenActions.Start(() => AddTypedAsync(thenNext: true), "tags.quick.add.key");
                return true;
            case QuickTagKeyAction.ApplyStamp:
                OpenActions.Start(() => ApplyStampAsync(), "tags.quick.stamp.key");
                return true;
            case QuickTagKeyAction.ThreadSection:
                ThreadSectionRequested?.Invoke();
                return ThreadSectionRequested != null;
            default:
                return false;
        }
    }

    private bool Navigate(QuickTagNavigation navigation)
    {
        if (NavigationRequested == null) return false;
        NavigationRequested(navigation);
        return true;
    }

    private bool Move(QuickTagKeyRoute route)
    {
        Button[] row = FocusZone() switch
        {
            QuickTagFocus.Suggestions => _suggestionChips.Select(chip => chip.Button).ToArray(),
            QuickTagFocus.Assigned => _assignedChips.Select(chip => chip.Button).ToArray(),
            _ => Array.Empty<Button>()
        };
        if (row.Length == 0) return false;
        int at = Array.FindIndex(row, button => button.IsKeyboardFocusWithin);
        int next = route.Action switch
        {
            QuickTagKeyAction.MoveFirst => 0,
            QuickTagKeyAction.MoveLast => row.Length - 1,
            _ => Math.Clamp((at < 0 ? 0 : at) + route.Value, 0, row.Length - 1)
        };
        row[next].Focus(NavigationMethod.Directional);
        return true;
    }

    private static bool FocusFirst(IEnumerable<Button> buttons)
    {
        Button? target = buttons.FirstOrDefault(button => button.IsVisible);
        target?.Focus(NavigationMethod.Directional);
        return target != null;
    }

    private SuggestionChip? FocusedSuggestion() => _suggestionChips.FirstOrDefault(chip => chip.Button.IsKeyboardFocusWithin);
    private AssignedChip? FocusedAssigned() => _assignedChips.FirstOrDefault(chip => chip.Button.IsKeyboardFocusWithin || chip.Remove.IsKeyboardFocusWithin);

    // Enter adds the typed text, unless the field takes it (TagSuggestionInput.FieldTakesEnter).
    private async void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        // Added after the field's own handler, so it runs before it: a highlighted suggestion is the field's to accept.
        if (TagSuggestionInput.FieldTakesEnter(_input, e)) return;
        e.Handled = true;
        _input.Dismiss();
        await OpenActions.RunAsync(() => AddTypedAsync(), "tags.quick.add.key");
    }

    private (QuickTagFocus Zone, TagId? Tag, string? Key, int Index) CurrentFocus()
    {
        QuickTagFocus zone = FocusZone();
        if (zone == QuickTagFocus.Suggestions && FocusedSuggestion() is { } suggestion)
            return (zone, suggestion.Suggestion.Id, suggestion.Suggestion.Name.Key, suggestion.Index);
        if (zone == QuickTagFocus.Assigned && FocusedAssigned() is { } assigned)
            return (zone, assigned.Tag, null, _pendingAssignedFocus ?? _assignedChips.IndexOf(assigned));
        return (zone, null, null, -1);
    }

    // After a rebuild the focus stays on the same tag; a toggled-away chip hands it to its neighbour, an empty row to the field.
    private void RestoreFocus(QuickTagFocus zone, TagId? tag, string? key, int index)
    {
        int? pending = _pendingAssignedFocus;
        _pendingAssignedFocus = null;
        if (!_attached || zone is QuickTagFocus.Field or QuickTagFocus.Other) return;
        if (zone == QuickTagFocus.Suggestions)
        {
            if (FocusedSuggestion() != null && _suggestionChips.Any(chip => chip.Button.IsKeyboardFocusWithin && chip.Suggestion.Name.Key == key)) return;
            SuggestionChip? same = _suggestionChips.FirstOrDefault(chip => chip.Suggestion.Name.Key == key);
            Button? target = same?.Button ?? (_suggestionChips.Count == 0 ? null : _suggestionChips[Math.Clamp(index, 0, _suggestionChips.Count - 1)].Button);
            if (target != null) target.Focus(NavigationMethod.Directional); else _input.Focus();
            return;
        }
        AssignedChip? kept = pending == null ? _assignedChips.FirstOrDefault(chip => chip.Tag == tag) : null;
        int at = pending ?? index;
        Button? next = kept?.Button ?? (_assignedChips.Count == 0 ? null : _assignedChips[Math.Clamp(at, 0, _assignedChips.Count - 1)].Button);
        if (next != null) next.Focus(NavigationMethod.Directional); else _input.Focus();
    }

    // Letter keys are shown only while the strip has the keyboard focus; the numbers always (they also name Alt+n).
    private void OnStripPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsKeyboardFocusWithinProperty) ShowLetters(_strip.IsKeyboardFocusWithin);
    }

    private void ShowLetters(bool visible)
    {
        foreach (SuggestionChip chip in _suggestionChips)
            if (chip.LetterBadge != null) chip.LetterBadge.IsVisible = visible;
    }

    // ---- lifetime -----------------------------------------------------------------------------------------------------

    private void OnInputPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Popup rows route through the input as logical children, but are not part of its editor's visual tree.
        // Refocusing the input during their captured press/release closes the popup on LostFocus, cancels the
        // candidate gesture and lets the adapter accept the old search text (the exact "New tag" row) instead.
        if (!_input.IsEffectivelyEnabled || e.Pointer.Type != PointerType.Mouse
            || e.GetCurrentPoint(_input).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || e.Source is not Visual source || (source != _input && !_input.IsVisualAncestorOf(source))) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && _input.IsEffectivelyEnabled && !_input.IsKeyboardFocusWithin) _input.Focus();
        }, DispatcherPriority.Input);
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_disposed || _attached) return;
        _attached = true;
        _session.Updated += OnSessionUpdated;
        Texts.Changed += OnLanguageChanged;
        ShortcutService.Changed += OnShortcutsChanged;
        Localize();
        ApplyFooterMode();
        Refresh();
        if (FocusOnAttach) Dispatcher.UIThread.Post(() => { if (FocusOnAttach && _attached && !_disposed) _input.Focus(); }, DispatcherPriority.Input);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_attached) return;
        _attached = false;
        _session.Updated -= OnSessionUpdated;
        Texts.Changed -= OnLanguageChanged;
        ShortcutService.Changed -= OnShortcutsChanged;
        _input.Dismiss();
    }

    public void Dispose()
    {
        if (_disposed) return;
        OnDetached(this, null!);
        _disposed = true;
        if (_extraUndo != null) { _extraUndo.Changed -= OnSessionUpdated; _extraUndo = null; }
        _input.Dispose();
        _sourceTags.End();
        _close.Click -= OnClose; _markedClear.Click -= OnMarkedClear; _undo.Click -= OnUndo; _stamp.Click -= OnStamp; _advanced.Click -= OnAdvanced; _more.Click -= OnMore;
        foreach (SuggestionChip chip in _suggestionChips) chip.Button.Click -= OnSuggestionClick;
        foreach (AssignedChip chip in _assignedChips) { chip.Button.Click -= OnAssignedClick; chip.Remove.Click -= OnRemoveClick; }
        _strip.PropertyChanged -= OnStripPropertyChanged;
        _flow.SizeChanged -= OnStripHostSizeChanged;
        _stripHost.SizeChanged -= OnSuggestionWidthChanged;
        if (_fitTimer != null) { _fitTimer.Stop(); _fitTimer = null; }
        _input.RemoveHandler(KeyDownEvent, OnInputKey);
        RemoveHandler(KeyDownEvent, OnKey);
        SizeChanged -= OnSizeChanged;
        AttachedToVisualTree -= OnAttached;
        DetachedFromVisualTree -= OnDetached;
        CloseRequested = null; AdvancedRequested = null; NavigationRequested = null; ThreadSectionRequested = null; MarksClearRequested = null; StampApplied = null;
    }

    // ---- chips --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A 28 dip tag pill in a transparent 36 dip hit zone: a tap beside the pill still reaches it. The 4 dip above and below also make the
    /// row gap of the strip 8 dip, the same as the column gap (<see cref="StripItemSpacing"/>); the fit measures the zone, so both flow through it.
    /// </summary>
    private static Control HitZone(Button chip)
    {
        Border zone = new() { Background = Brushes.Transparent, Padding = new Thickness(0, 4), Child = chip };
        AutomationProperties.SetAccessibilityView(zone, AccessibilityView.Raw);
        zone.Tapped += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, zone) || !chip.IsEffectivelyEnabled) return;
            e.Handled = true;
            chip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        return zone;
    }

    private static Border KeyBadge(string text)
    {
        TextBlock label = new() { Text = text, Classes = { WorkspaceChips.Label } };
        Border badge = new() { Child = label, Classes = { WorkspaceChips.Chip, WorkspaceChips.Badge, "chip-key" }, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(badge, AccessibilityView.Raw);
        return badge;
    }

    /// <summary>
    /// The mark of a stamp chip (the ⌁ of the plan, drawn as a path so it needs no font glyph): a small pulse line in the key badge
    /// shape. The shape marks the chip, the word "Stamp" is in its tooltip and accessible name, so colour is only an addition.
    /// </summary>
    internal static Border StampBadge()
    {
        Path pulse = new()
        {
            Width = 12, Height = 12, Stretch = Stretch.None, IsHitTestVisible = false, StrokeThickness = 1.5,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
            [!Shape.StrokeProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Text")
        };
        pulse.AttachedToVisualTree += (_, _) => pulse.Data ??= Geometry.Parse("M1,6 L3.5,6 L5,2 L7,10 L8.5,6 L11,6");
        Border badge = new() { Child = pulse, Name = "StampBadge", Classes = { WorkspaceChips.Chip, WorkspaceChips.Badge, "chip-key" }, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(badge, AccessibilityView.Raw);
        return badge;
    }

    private static TextBlock ChipLabel(string name) => new()
    {
        Text = name, MaxWidth = 200, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center, Classes = { WorkspaceChips.Label }
    };

    private sealed class SuggestionChip
    {
        internal required Button Button { get; init; }
        internal required QuickTagSuggestion Suggestion { get; init; }
        internal required int Index { get; init; }
        internal Border? LetterBadge { get; init; }

        internal static SuggestionChip Create(string name, QuickTagSuggestion suggestion, int index, char? letter, ShortcutPlatform platform)
        {
            string display = suggestion.Name.Display;
            StackPanel content = new() { Orientation = Orientation.Horizontal, Classes = { WorkspaceChips.TagPillContent } };
            bool keyed = false;
            if (QuickTagBadges.Number(index) is { } number) { content.Children.Add(KeyBadge(number.ToString(Texts.Culture))); keyed = true; }
            Border? letterBadge = null;
            if (letter is { } key)
            {
                letterBadge = KeyBadge(key.ToString());
                letterBadge.IsVisible = false;
                content.Children.Add(letterBadge);
                keyed = true;
            }
            if (suggestion.Reason == QuickTagReason.Stamp) { content.Children.Add(StampBadge()); keyed = true; }
            content.Children.Add(ChipLabel(display));
            Button button = new() { Name = name, Focusable = true };
            if (suggestion.CreatesTag)
            {
                // A provider tag or a new name: the dashed source chip (I8), never mistaken for an own tag.
                ChipOutline outline = new() { Child = content, Classes = { WorkspaceChips.Chip, WorkspaceChips.Source, WorkspaceChips.CategoryClass(SourceCategory.General), WorkspaceChips.TagPill } };
                if (keyed) outline.Classes.Add("chip-keyed");
                button.Content = outline;
                button.Classes.Add(WorkspaceChips.SourceButton);
            }
            else
            {
                button.Content = content;
                WorkspaceChips.MarkOffered(button);
                WorkspaceChips.MarkTagPill(button);
                if (keyed) button.Classes.Add("chip-keyed");
            }
            Describe(button, suggestion, index, platform);
            return new SuggestionChip { Button = button, Suggestion = suggestion, Index = index, LetterBadge = letterBadge };
        }

        private static void Describe(Button button, QuickTagSuggestion suggestion, int index, ShortcutPlatform platform)
        {
            string reason = QuickTagReasons.Text(suggestion.Reason, suggestion.Detail);
            string display = suggestion.Name.Display;
            string tip = QuickTagKeys.SuggestionGesture(index, platform) is { } gesture
                ? Texts.T("tags.quick.suggestion.tipShortcut", display, reason, gesture)
                : Texts.T("tags.quick.suggestion.tip", display, reason);
            ToolTip.SetTip(button, tip);
            AutomationProperties.SetHelpText(button, tip);
        }
    }

    /// <summary>Names a suggestion chip for assistive technology: name, position, reason, uses and what Enter does.</summary>
    internal static string AccessibleName(QuickTagSuggestion suggestion, int index, int count)
    {
        string reason = QuickTagReasons.Text(suggestion.Reason, suggestion.Detail);
        return suggestion.CreatesTag
            ? Texts.T("tags.quick.suggestion.nameCreate", suggestion.Name.Display, index + 1, count, reason)
            : Texts.T("tags.quick.suggestion.name", suggestion.Name.Display, index + 1, count, reason, suggestion.Uses);
    }

    /// <summary>Names an assigned chip: on this file, or on all / some of a selection, with what Delete and Space do.</summary>
    internal static string AccessibleName(QuickTagAssignment assignment, bool selection) =>
        !selection ? Texts.T("tags.quick.chip.on", assignment.Tag.Name.Display)
        : assignment.State == TagToggleState.All ? Texts.T("tags.quick.chip.all", assignment.Tag.Name.Display, assignment.Total)
        : Texts.T("tags.quick.chip.some", assignment.Tag.Name.Display, assignment.Carriers, assignment.Total);

    private sealed class AssignedChip
    {
        // The dash of a pill that is on some of a selection (a 12 dip wide line through the middle of the 12 dip glyph box).
        private const string DashGeometry = "M1.5,6 L10.5,6";
        /// <summary>The chip half of an assigned pill (left corners rounded, no right edge) and its remove half (right corners rounded, no left edge); WorkspaceStyles.axaml.</summary>
        internal const string JoinedClass = "tag-pill-joined", RemoveClass = "tag-pill-remove";
        internal required Control Host { get; init; }
        internal required Button Button { get; init; }
        internal required Button Remove { get; init; }
        internal required TagId Tag { get; init; }
        internal required TagToggleState State { get; init; }

        internal static AssignedChip Create(string name, QuickTagAssignment assignment, bool selection)
        {
            string display = assignment.Tag.Name.Display;
            StackPanel content = new() { Orientation = Orientation.Horizontal, Classes = { WorkspaceChips.TagPillContent } };
            // On all (or on the one file): no mark; the accent tint, the "On this media file" section and the joined cross say it. On some: a dash.
            if (StateGlyph(assignment.State) is { } glyph) content.Children.Add(glyph);
            content.Children.Add(ChipLabel(display));
            Button chip = new() { Name = name, Content = content };
            // All: the accent-tinted pill; some: the quiet pill with an accent edge and a dash (Tom, 16.11.2026: the check on assigned chips is dropped).
            if (assignment.State == TagToggleState.All) WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOwn(chip), WorkspaceChips.TagPillAssigned);
            else WorkspaceChips.MarkTagPill(WorkspaceChips.MarkOffered(chip), WorkspaceChips.TagPillPartial);
            // One pill with two hit halves: the chip is the rounded start of it, the remove half (below) its rounded end; no gap between them.
            chip.Classes.Add(JoinedClass);
            string accessible = AccessibleName(assignment, selection);
            AutomationProperties.SetName(chip, accessible);
            ToolTip.SetTip(chip, accessible);
            Button remove = new() { Name = name + "Remove" };
            string removeName = Texts.T(selection ? "tags.quick.chip.removeAll" : "tags.quick.chip.remove", display);
            remove.Content = WorkspaceIcons.Create(WorkspaceIcons.CloseSmall);
            remove.Classes.Add(WorkspaceIcons.SquareClass);
            // The end of the pill: 28 square (the class overrides the 32 dip square), the fill and edge of the pill's state, the cross muted until the pointer is on it.
            remove.Classes.Add(RemoveClass);
            remove.Classes.Add(assignment.State == TagToggleState.All ? WorkspaceChips.TagPillAssigned : WorkspaceChips.TagPillPartial);
            // Delete on the chip removes the tag as well (its name says so), so the Tab order has one stop per tag; the cross stays clickable and named.
            remove.IsTabStop = false;
            AutomationProperties.SetName(remove, removeName);
            ToolTip.SetTip(remove, removeName);
            StackPanel host = new() { Orientation = Orientation.Horizontal, Spacing = 0, Children = { chip, remove } };
            return new AssignedChip { Host = host, Button = chip, Remove = remove, Tag = assignment.Tag.Id, State = assignment.State };
        }

        // State is never colour alone: "on all" is the accent-tinted pill without a mark, "on some" is the quiet pill with an accent edge and a dash, so the
        // two stay told apart by form as well. The dash is drawn (stroke 2, the icon weight), never a text character.
        private static Control? StateGlyph(TagToggleState state)
        {
            if (state == TagToggleState.All) return null;
            Path dash = new() { Width = 12, Height = 12, Stretch = Stretch.None, IsHitTestVisible = false, StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center };
            dash.Bind(Shape.StrokeProperty, dash.GetObservable(TextElement.ForegroundProperty));
            dash.AttachedToVisualTree += (_, _) => dash.Data ??= Geometry.Parse(DashGeometry);
            AutomationProperties.SetAccessibilityView(dash, AccessibilityView.Raw);
            return dash;
        }
    }

    private enum SourceCategory { General }
}
