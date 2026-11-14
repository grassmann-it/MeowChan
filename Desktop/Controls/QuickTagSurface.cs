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
using Avalonia.Media;
using Avalonia.Threading;
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
    Next, Previous, MarkNext, UnmarkPrevious, CommitAndNext, ThreadSection
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
/// Key routing of the quick tag surface as a pure function, so it can be tested and later fed from the shortcut editor (TP6).
/// Tom's decisions of 14.11.: Alt+1…9 (macOS ⌘1…9) toggles suggestion n in every focus state; bare digits and badge letters only
/// while the focus is in the suggestion strip (tag names like <c>2girls</c> stay typeable); Page Down/Up move through the
/// gallery, Ctrl+Enter adds and moves on, Shift+Page Down marks and moves on. Escape is layered: open list, then the surface.
/// </summary>
internal static class QuickTagKeys
{
    /// <summary>The modifier of the numbered suggestions: Alt, ⌘ on macOS.</summary>
    internal static KeyModifiers ChooseModifier(ShortcutPlatform platform) => platform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Alt;
    internal static KeyModifiers PrimaryModifier(ShortcutPlatform platform) => platform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>1–9 of the main row or the number pad, else null.</summary>
    internal static int? Digit(Key key) =>
        key is >= Key.D1 and <= Key.D9 ? key - Key.D0 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad0 : null;

    internal static char? Letter(Key key) => key is >= Key.A and <= Key.Z ? (char)('A' + (key - Key.A)) : null;

    /// <param name="listOpen">The field's suggestion list is open.</param>
    /// <param name="fieldEmpty">The field holds no text (Ctrl+Z then is the surface's undo, not the editor's).</param>
    /// <param name="navigation">The host moves through media (gallery panel); off in a popover.</param>
    internal static QuickTagKeyRoute Route(Key key, KeyModifiers modifiers, QuickTagFocus focus, bool listOpen, bool fieldEmpty,
        bool navigation, ShortcutPlatform platform)
    {
        KeyModifiers primary = PrimaryModifier(platform);
        if (modifiers == ChooseModifier(platform) && Digit(key) is { } chosen) return new(QuickTagKeyAction.ToggleSuggestion, chosen - 1);
        if (modifiers == primary && key == Key.Z)
            return focus == QuickTagFocus.Field && !fieldEmpty ? QuickTagKeyRoute.None : new(QuickTagKeyAction.Undo);
        if (key == Key.Escape && modifiers == KeyModifiers.None)
            return new(focus == QuickTagFocus.Field && listOpen ? QuickTagKeyAction.CloseList : QuickTagKeyAction.Close);
        if (navigation && !(focus == QuickTagFocus.Field && listOpen))
        {
            if (key == Key.PageDown && modifiers == KeyModifiers.None) return new(QuickTagKeyAction.Next);
            if (key == Key.PageUp && modifiers == KeyModifiers.None) return new(QuickTagKeyAction.Previous);
            if (key == Key.PageDown && modifiers == KeyModifiers.Shift) return new(QuickTagKeyAction.MarkNext);
            if (key == Key.PageUp && modifiers == KeyModifiers.Shift) return new(QuickTagKeyAction.UnmarkPrevious);
            if (key == Key.Enter && modifiers == primary) return new(QuickTagKeyAction.CommitAndNext);
            if (key == Key.T && modifiers == primary) return new(QuickTagKeyAction.ThreadSection);
        }
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
                if (Digit(key) is { } digit) return new(QuickTagKeyAction.ToggleSuggestion, digit - 1);
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

    /// <summary>The shortcut text of suggestion <paramref name="index"/> (0-based): "Alt+3", "⌘3"; null beyond the numbered ones.</summary>
    internal static string? SuggestionGesture(int index, ShortcutPlatform platform) =>
        index is < 0 or >= QuickTagOptions.NumberedSuggestions ? null
        : new ShortcutGesture("D" + (index + 1), new[] { platform == ShortcutPlatform.MacOS ? "Meta" : "Alt" }).Display(platform);

    internal static string UndoGesture(ShortcutPlatform platform) => ShortcutGesture.Parse("Primary+Z").Display(platform);
    internal static string CommitNextGesture(ShortcutPlatform platform) => ShortcutGesture.Parse("Primary+Enter").Display(platform);
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
        _ => Texts.T("tags.quick.reason.frequent")
    };
}

/// <summary>
/// The shared core of the gallery tag panel (TP2) and the right-click tag popover (TP3): header with the title on the left and the
/// isolated close button on the right edge, the tag field (own tags, Danbooru source tags, "New tag"), the numbered suggestion
/// strip with reasons, the assigned chips (tri-state for a selection), the status line and the footer actions on the left
/// (undo, "Advanced…"). Every change is written at once through <see cref="QuickTagSession"/>; nothing is assigned
/// automatically. The host frames it (overlay panel or flyout), handles <see cref="CloseRequested"/>,
/// <see cref="AdvancedRequested"/> and, in the gallery, <see cref="NavigationRequested"/>, and disposes it.
/// </summary>
internal sealed class QuickTagSurface : UserControl, IDisposable
{
    /// <summary>Below this width the footer buttons show only their icon (with tooltip and accessible name).</summary>
    internal const double CompactWidth = 320;
    private readonly QuickTagSession _session;
    private readonly QuickTagOptions _options;
    private readonly ShortcutPlatform _platform;
    private readonly string _name;
    private readonly SourceTagSuggestions _sourceTags = new();
    private readonly Border _counter;
    private readonly TextBlock _counterLabel;
    // Gallery marking (E4): "3 marked" with a button that clears the marks; shown below the field while the host has marks.
    private readonly Border _markedBadge;
    private readonly TextBlock _markedLabel;
    private readonly Button _markedClear = new() { Classes = { "subtle" } };
    private readonly StackPanel _markBar;
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close = new();
    private readonly IdentitySuggestionBox _input = new();
    private readonly TextBlock _suggestionsHeading = new() { Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _strip = new() { ItemSpacing = 8, LineSpacing = 0 };
    private readonly TextBlock _stripEmpty = new() { Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _more = new() { Classes = { "subtle" }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _allHeading = new() { Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _allChips = new() { ItemSpacing = 8, LineSpacing = 0 };
    private readonly TextBlock _someHeading = new() { Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _someChips = new() { ItemSpacing = 8, LineSpacing = 0 };
    private readonly TextBlock _assignedEmpty = new() { Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _assigned;
    private readonly ContentControl _extra = new() { IsVisible = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _undo = new();
    private readonly Button _advanced = new();
    private readonly List<SuggestionChip> _suggestionChips = new();
    private readonly List<AssignedChip> _assignedChips = new();
    private IQuickTagSuggestionSource _source = FrequentQuickTagSuggestions.Instance;
    private IReadOnlyList<QuickTagSuggestion> _shown = Array.Empty<QuickTagSuggestion>();
    private string _targetLabel = string.Empty;
    private string? _counterText;
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
        _close.Name = _name + "Close"; _input.Name = _name + "Input"; _strip.Name = _name + "Suggestions"; _more.Name = _name + "More";
        _allChips.Name = _name + "Assigned"; _someChips.Name = _name + "AssignedSome"; _status.Name = _name + "Status";
        _undo.Name = _name + "Undo"; _advanced.Name = _name + "Advanced"; _title.Name = _name + "Title"; _extra.Name = _name + "Extra";
        _stripEmpty.Name = _name + "SuggestionsEmpty"; _assignedEmpty.Name = _name + "AssignedEmpty";
        _markedClear.Name = _name + "MarkedClear";

        _counter = WorkspaceChips.CreateBadge(_name + "Counter", string.Empty, compact: true);
        _counterLabel = (TextBlock)_counter.Child!;
        _counter.IsVisible = false;
        _counter.VerticalAlignment = VerticalAlignment.Center;

        _markedBadge = WorkspaceChips.CreateBadge(_name + "Marked", string.Empty, compact: true);
        _markedLabel = (TextBlock)_markedBadge.Child!;
        _markedBadge.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLiveSetting(_markedBadge, AutomationLiveSetting.Polite);
        _markBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Name = _name + "MarkBar", Children = { _markedBadge, _markedClear } };

        // Header: counter and title on the left, the close button alone at the right edge (owner rule 14.11.), 24 dip apart at least.
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,24,Auto"), ColumnSpacing = 0, Name = _name + "Header" };
        header.Children.Add(_counter);
        header.Children.Add(_title); Grid.SetColumn(_title, 1);
        header.Children.Add(_close); Grid.SetColumn(_close, 3);
        _counter.Margin = new Thickness(0, 0, 8, 0);
        WorkspaceIcons.MakeSquare(_close, WorkspaceIcons.Close, Texts.T("tags.quick.close"), Texts.T("tags.quick.closeTip"));
        _close.VerticalAlignment = VerticalAlignment.Center;

        _assigned = new StackPanel { Spacing = 8, Children = { _allHeading, _allChips, _someHeading, _someChips, _assignedEmpty } };
        StackPanel body = new()
        {
            Spacing = 12,
            Children =
            {
                new StackPanel { Spacing = 8, Children = { _suggestionsHeading, _strip, _stripEmpty, _more } },
                _assigned,
                _extra
            }
        };
        ScrollViewer scroll = new() { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Name = _name + "Body" };

        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, Name = _name + "Actions" };
        actions.Children.Add(_undo);
        actions.Children.Add(_advanced);
        Footer = actions;

        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), RowSpacing = 12 };
        layout.Children.Add(header);
        // The field with the marks bar under it (a single row, so a hidden bar leaves no gap).
        StackPanel fieldArea = new() { Spacing = 8, Children = { _input, _markBar }, Name = _name + "Field" };
        layout.Children.Add(fieldArea); Grid.SetRow(fieldArea, 1);
        layout.Children.Add(scroll); Grid.SetRow(scroll, 2);
        layout.Children.Add(_status); Grid.SetRow(_status, 3);
        layout.Children.Add(actions); Grid.SetRow(actions, 4);
        Content = layout;
        Header = header;
        // Focus order: field, suggestions, "More…", assigned chips, extra section, undo, "Advanced…", and the close button last
        // (a popover cycles, so Shift+Tab from the field reaches it directly).
        KeyboardNavigation.SetTabIndex(header, 1);

        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _strip.PropertyChanged += OnStripPropertyChanged;
        _close.Click += OnClose;
        _markedClear.Click += OnMarkedClear;
        _undo.Click += OnUndo;
        _advanced.Click += OnAdvanced;
        _more.Click += OnMore;
        TagSuggestionInput.Attach(_input, () => _session.Busy ? null : _session.Snapshot, _session.OnAll, () => _ = AddTypedAsync(), _sourceTags);
        _input.AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        SizeChanged += OnSizeChanged;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        Localize();
        Refresh();
    }

    internal QuickTagSession Session => _session;
    internal QuickTagOptions Options => _options;
    internal IdentitySuggestionBox Input => _input;
    internal Button CloseButton => _close;
    internal Button UndoButton => _undo;
    internal Button AdvancedButton => _advanced;
    internal Button MoreButton => _more;
    internal TextBlock Status => _status;
    internal TextBlock Title => _title;
    internal Grid Header { get; }
    internal StackPanel Footer { get; }
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

    /// <summary>A host-given counter ("3 marked"); null shows the file count of a selection automatically.</summary>
    internal string? CounterText
    {
        get => _counterText;
        set { _counterText = value; RefreshTexts(); }
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

    /// <summary>The ranked suggestions of the strip; the most used own tags until the host passes the context source.</summary>
    internal IQuickTagSuggestionSource Suggestions
    {
        get => _source;
        set { _source = value ?? FrequentQuickTagSuggestions.Instance; Refresh(); }
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

    /// <summary>An extra section under the assigned chips (the gallery panel's thread tags); null hides it.</summary>
    internal Control? ExtraContent
    {
        get => _extra.Content as Control;
        set { _extra.Content = value; _extra.IsVisible = value != null; }
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

    /// <summary>Starts the background read of source tags (each time the host opens the surface).</summary>
    internal void LoadSourceTags() => _sourceTags.Load();

    /// <summary>Puts the focus into the tag field (opening the surface, after a medium switch).</summary>
    internal void FocusField() => _input.Focus();

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
        _shown = snapshot == null ? Array.Empty<QuickTagSuggestion>() : Normalize(_source.Suggest(
            new QuickTagSuggestionRequest(snapshot, carried, _options.SuggestionCount)), snapshot, carried, _options.SuggestionCount);
        Letters = QuickTagBadges.Letters(_shown.Select(item => item.Name.Display).ToArray());
        BuildSuggestions();
        BuildAssigned();
        _input.IsEnabled = snapshot != null;
        if (snapshot == null) _input.Dismiss();
        RefreshTexts();
        RestoreFocus(zone, tag, key, index);
        // A write the surface did not start (the host's) has ended: take an Enter that waited for it.
        if (_deferredAdd != null) _ = ReplayDeferredAddAsync();
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

    private void BuildSuggestions()
    {
        foreach (SuggestionChip chip in _suggestionChips) chip.Button.Click -= OnSuggestionClick;
        _suggestionChips.Clear();
        _strip.Children.Clear();
        for (int index = 0; index < _shown.Count; index++)
        {
            SuggestionChip chip = SuggestionChip.Create(_name + "Suggestion" + (index + 1), _shown[index], index, Letters[index], _platform);
            chip.Button.Click += OnSuggestionClick;
            AutomationProperties.SetName(chip.Button, AccessibleName(_shown[index], index, _shown.Count));
            if (QuickTagKeys.SuggestionGesture(index, _platform) is { } gesture) AutomationProperties.SetAcceleratorKey(chip.Button, gesture);
            _suggestionChips.Add(chip);
            _strip.Children.Add(HitZone(chip.Button));
        }
        ShowLetters(_strip.IsKeyboardFocusWithin);
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
        _title.Text = label.Length == 0 ? Texts.T("tags.quick.heading") : Texts.T("tags.quick.title", label);
        ToolTip.SetTip(_title, _title.Text);
        TagCoverage coverage = _session.Coverage();
        string? counter = _counterText ?? (_session.IsSelection && coverage.Total > 1 ? Texts.Count("tags.quick.files", coverage.Total) : null);
        _counter.IsVisible = !string.IsNullOrEmpty(counter);
        _counterLabel.Text = counter ?? string.Empty;
        AutomationProperties.SetName(_counter, counter ?? string.Empty);

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
        _status.Classes.Set("workspace-help", !status.IsError);
        AutomationProperties.SetLiveSetting(_status, status.IsError ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);

        // Not disabled while a write runs (the session ignores the click), so a focused undo button keeps the focus.
        _undo.IsEnabled = _session.UndoSteps.Count > 0 && _session.Snapshot != null;
        AutomationProperties.SetName(_undo, Texts.T("tags.quick.undoName", _session.UndoSteps.Count));
        _advanced.IsEnabled = _session.Snapshot != null;

        AutomationProperties.SetName(this, Texts.T("tags.quick.name", label.Length == 0 ? Texts.T("tags.quick.heading") : label));
        AutomationProperties.SetHelpText(this, Texts.T("tags.quick.help", _shown.Count, _assignedChips.Count));
    }

    private void Localize()
    {
        TagSuggestionInput.Localize(_input);
        string first = QuickTagKeys.SuggestionGesture(0, _platform)!, last = QuickTagKeys.SuggestionGesture(QuickTagOptions.NumberedSuggestions - 1, _platform)!;
        string hint = Texts.T("tags.suggest.hint") + " " + Texts.T("tags.quick.fieldHint", first, last);
        if (NavigationEnabled) hint += " " + Texts.T("tags.quick.fieldHintNavigation", QuickTagKeys.CommitNextGesture(_platform));
        AutomationProperties.SetHelpText(_input, hint);
        ToolTip.SetTip(_input, hint);
        AutomationProperties.SetName(_close, Texts.T("tags.quick.close"));
        ToolTip.SetTip(_close, Texts.T("tags.quick.closeTip"));
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
        ToolTip.SetTip(_undo, Texts.T("tags.quick.undoTip", QuickTagKeys.UndoGesture(_platform)));
        AutomationProperties.SetName(_advanced, Texts.T("tags.quick.advancedName"));
        ToolTip.SetTip(_advanced, Texts.T("tags.quick.advancedName"));
        ApplyFooterMode();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        Localize();
        Refresh();
    });

    // Below the compact width the footer shows icons only; the name and tooltip keep the full text.
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width > 0 && e.NewSize.Width < CompactWidth;
        if (compact == _compact) return;
        _compact = compact;
        ApplyFooterMode();
    }

    private void ApplyFooterMode()
    {
        if (_compact && _attached)
        {
            _undo.Content = CommandBindings.NamedIcon("corner-up-left");
            _advanced.Content = CommandBindings.NamedIcon("external-link");
        }
        else
        {
            _undo.Content = Texts.T("tags.quick.undo");
            _advanced.Content = Texts.T("tags.quick.advanced");
        }
    }

    // ---- actions ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Enter (or Ctrl+Enter with <paramref name="thenNext"/>) in the field: adds the typed name. While another write runs the
    /// draft stays and the Enter is taken once, right after that write, if the field still holds the same text. After the
    /// write only the text that was written is cleared: what the user typed meanwhile for the next tag stays in the field.
    /// </summary>
    internal async Task AddTypedAsync(bool thenNext = false)
    {
        string name = _input.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (!_session.Ready)
            {
                if (_session.Snapshot != null) _deferredAdd = (name, thenNext);
                return;
            }
            await Guard(() => _session.AddAsync(name), "tags.quick.add");
            if (_disposed || _session.Status.IsError) return;
            if (string.Equals(_input.Text, name, StringComparison.Ordinal)) _input.Text = string.Empty;
        }
        if (thenNext) NavigationRequested?.Invoke(QuickTagNavigation.Next);
    }

    // An Enter that came while a write was running; taken once that write is done (see AddTypedAsync).
    private (string Name, bool ThenNext)? _deferredAdd;

    private Task ReplayDeferredAddAsync()
    {
        if (_deferredAdd is not { } deferred || _disposed || !_session.Ready) return Task.CompletedTask;
        _deferredAdd = null;
        // The user went on editing after that Enter: the draft is theirs again, nothing is written for them.
        return string.Equals(_input.Text, deferred.Name, StringComparison.Ordinal) ? AddTypedAsync(deferred.ThenNext) : Task.CompletedTask;
    }

    private async void OnSuggestionClick(object? sender, RoutedEventArgs e)
    {
        if (_suggestionChips.FirstOrDefault(chip => ReferenceEquals(chip.Button, sender)) is { } chip) await ToggleSuggestionAsync(chip.Index);
    }

    /// <summary>Toggles suggestion <paramref name="index"/> (0-based), as a click, Alt+n or n in the strip does.</summary>
    internal Task ToggleSuggestionAsync(int index) =>
        index < 0 || index >= _shown.Count ? Task.CompletedTask : Guard(() => _session.AcceptAsync(_shown[index]), "tags.quick.suggestion");

    private async void OnAssignedClick(object? sender, RoutedEventArgs e)
    {
        if (_assignedChips.FirstOrDefault(chip => ReferenceEquals(chip.Button, sender)) is { } chip)
            await Guard(() => _session.ToggleAsync(chip.Tag), "tags.quick.toggle");
    }

    private async void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (_assignedChips.FirstOrDefault(chip => ReferenceEquals(chip.Remove, sender)) is { } chip) await RemoveAsync(chip);
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
        await UndoAsync();
        // The last step is gone and the button with it: the focus goes back to the field instead of nowhere.
        if (!_disposed && _session.UndoSteps.Count == 0 && _undo.IsKeyboardFocusWithin) _input.Focus();
    }

    internal Task UndoAsync() => Guard(() => _session.UndoAsync(), "tags.quick.undo");

    private void OnMarkedClear(object? sender, RoutedEventArgs e) => MarksClearRequested?.Invoke();
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

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        QuickTagKeyRoute route = QuickTagKeys.Route(e.Key, e.KeyModifiers, FocusZone(), _input.IsDropDownOpen,
            string.IsNullOrEmpty(_input.Text), NavigationEnabled, _platform);
        if (Execute(route)) e.Handled = true;
    }

    /// <summary>Carries out a routed key; false when the key is not the surface's (it then goes on to the focused control).</summary>
    internal bool Execute(QuickTagKeyRoute route)
    {
        switch (route.Action)
        {
            case QuickTagKeyAction.ToggleSuggestion:
                if (route.Value >= _shown.Count) return route.Value < QuickTagOptions.NumberedSuggestions;
                _ = ToggleSuggestionAsync(route.Value);
                return true;
            case QuickTagKeyAction.ToggleBadge:
                int badge = Array.IndexOf(Letters.ToArray(), (char?)(char)route.Value);
                if (badge >= 0) _ = ToggleSuggestionAsync(badge);
                return badge >= 0;
            case QuickTagKeyAction.Move:
            case QuickTagKeyAction.MoveFirst:
            case QuickTagKeyAction.MoveLast:
                return Move(route);
            case QuickTagKeyAction.ToggleFocused:
                if (FocusedSuggestion() is { } suggestion) { _ = ToggleSuggestionAsync(suggestion.Index); return true; }
                if (FocusedAssigned() is { } toggled) { _ = Guard(() => _session.ToggleAsync(toggled.Tag), "tags.quick.toggle"); return true; }
                return false;
            case QuickTagKeyAction.RemoveFocused:
                if (FocusedAssigned() is not { } removed) return false;
                _ = RemoveAsync(removed);
                return true;
            case QuickTagKeyAction.FocusField: _input.Focus(); return true;
            case QuickTagKeyAction.FocusSuggestions:
                return FocusFirst(_suggestionChips.Select(chip => chip.Button)) || FocusFirst(new[] { _more });
            case QuickTagKeyAction.FocusAssigned: return FocusFirst(_assignedChips.Select(chip => chip.Button));
            case QuickTagKeyAction.FocusLastAssigned: return FocusFirst(_assignedChips.Select(chip => chip.Button).Reverse());
            case QuickTagKeyAction.Undo:
                if (!_session.CanUndo) return _session.UndoSteps.Count > 0 || _session.Busy;
                _ = UndoAsync();
                return true;
            case QuickTagKeyAction.CloseList: _input.Dismiss(); return true;
            case QuickTagKeyAction.Close: CloseRequested?.Invoke(); return CloseRequested != null;
            case QuickTagKeyAction.Next: return Navigate(QuickTagNavigation.Next);
            case QuickTagKeyAction.Previous: return Navigate(QuickTagNavigation.Previous);
            case QuickTagKeyAction.MarkNext: return Navigate(QuickTagNavigation.MarkNext);
            case QuickTagKeyAction.UnmarkPrevious: return Navigate(QuickTagNavigation.UnmarkPrevious);
            case QuickTagKeyAction.CommitAndNext:
                if (NavigationRequested == null) return false;
                _ = AddTypedAsync(thenNext: true);
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

    // Runs after the field's own handler: an Enter that took a highlighted suggestion is already handled there.
    private async void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        _input.Dismiss();
        await AddTypedAsync();
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

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_disposed || _attached) return;
        _attached = true;
        _session.Updated += OnSessionUpdated;
        Texts.Changed += OnLanguageChanged;
        ApplyFooterMode();
        Refresh();
        if (FocusOnAttach) Dispatcher.UIThread.Post(() => { if (_attached && !_disposed) _input.Focus(); }, DispatcherPriority.Input);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_attached) return;
        _attached = false;
        _session.Updated -= OnSessionUpdated;
        Texts.Changed -= OnLanguageChanged;
        _input.Dismiss();
    }

    public void Dispose()
    {
        if (_disposed) return;
        OnDetached(this, null!);
        _disposed = true;
        _input.EndSuggestions();
        _sourceTags.End();
        _close.Click -= OnClose; _markedClear.Click -= OnMarkedClear; _undo.Click -= OnUndo; _advanced.Click -= OnAdvanced; _more.Click -= OnMore;
        foreach (SuggestionChip chip in _suggestionChips) chip.Button.Click -= OnSuggestionClick;
        foreach (AssignedChip chip in _assignedChips) { chip.Button.Click -= OnAssignedClick; chip.Remove.Click -= OnRemoveClick; }
        _strip.PropertyChanged -= OnStripPropertyChanged;
        _input.RemoveHandler(KeyDownEvent, OnInputKey);
        RemoveHandler(KeyDownEvent, OnKey);
        SizeChanged -= OnSizeChanged;
        AttachedToVisualTree -= OnAttached;
        DetachedFromVisualTree -= OnDetached;
        CloseRequested = null; AdvancedRequested = null; NavigationRequested = null; ThreadSectionRequested = null; MarksClearRequested = null;
    }

    // ---- chips --------------------------------------------------------------------------------------------------------

    /// <summary>A 24 dip chip in a transparent 32 dip hit zone: a tap beside the chip still reaches it.</summary>
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
            StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
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
            content.Children.Add(ChipLabel(display));
            Button button = new() { Name = name, Focusable = true };
            if (suggestion.CreatesTag)
            {
                // A provider tag or a new name: the dashed source chip (I8), never mistaken for an own tag.
                ChipOutline outline = new() { Child = content, Classes = { WorkspaceChips.Chip, WorkspaceChips.Source, WorkspaceChips.CategoryClass(SourceCategory.General) } };
                if (keyed) outline.Classes.Add("chip-keyed");
                button.Content = outline;
                button.Classes.Add(WorkspaceChips.SourceButton);
            }
            else
            {
                button.Content = content;
                WorkspaceChips.MarkOffered(button);
                if (keyed) button.Classes.Add("chip-keyed");
            }
            Describe(button, suggestion, index, platform);
            return new SuggestionChip { Button = button, Suggestion = suggestion, Index = index, LetterBadge = letterBadge };
        }

        private static void Describe(Button button, QuickTagSuggestion suggestion, int index, ShortcutPlatform platform)
        {
            string reason = QuickTagReasons.Text(suggestion.Reason, suggestion.Detail);
            string display = suggestion.Name.Display;
            ToolTip.SetTip(button, QuickTagKeys.SuggestionGesture(index, platform) is { } gesture
                ? Texts.T("tags.quick.suggestion.tipShortcut", display, reason, gesture)
                : Texts.T("tags.quick.suggestion.tip", display, reason));
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
        private const string CheckGeometry = "M1,6.4 L4.6,10 L11,2.4";
        private const string PartialOutline = "M1.5,1.5 L10.5,1.5 L10.5,10.5 L1.5,10.5 Z";
        private const string PartialFill = "M1.5,10.5 L10.5,1.5 L10.5,10.5 Z";
        internal required Control Host { get; init; }
        internal required Button Button { get; init; }
        internal required Button Remove { get; init; }
        internal required TagId Tag { get; init; }
        internal required TagToggleState State { get; init; }

        internal static AssignedChip Create(string name, QuickTagAssignment assignment, bool selection)
        {
            string display = assignment.Tag.Name.Display;
            StackPanel content = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(StateGlyph(assignment.State));
            content.Children.Add(ChipLabel(display));
            Button chip = new() { Name = name, Content = content };
            if (assignment.State == TagToggleState.All) WorkspaceChips.MarkOwn(chip);
            else WorkspaceChips.MarkOffered(chip); // "some": outline without fill, the half-square glyph says it too
            string accessible = AccessibleName(assignment, selection);
            AutomationProperties.SetName(chip, accessible);
            ToolTip.SetTip(chip, accessible);
            Button remove = new() { Name = name + "Remove" };
            string removeName = Texts.T(selection ? "tags.quick.chip.removeAll" : "tags.quick.chip.remove", display);
            remove.Content = WorkspaceIcons.Create(WorkspaceIcons.CloseSmall);
            remove.Classes.Add(WorkspaceIcons.SquareClass);
            remove.Classes.Add("subtle");
            AutomationProperties.SetName(remove, removeName);
            ToolTip.SetTip(remove, removeName);
            StackPanel host = new() { Orientation = Orientation.Horizontal, Spacing = 0, Children = { chip, remove } };
            return new AssignedChip { Host = host, Button = chip, Remove = remove, Tag = assignment.Tag.Id, State = assignment.State };
        }

        // State is never colour alone: a check on a filled chip (all), a half-filled square on an outlined chip (some).
        private static Control StateGlyph(TagToggleState state)
        {
            // Stroke 2 for the check, 1.5 for the square outline (the icon weights); the half fill has no stroke.
            Path Part(string data, double stroke)
            {
                Path path = new() { Width = 12, Height = 12, Stretch = Stretch.None, IsHitTestVisible = false,
                    StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center };
                path.StrokeThickness = stroke;
                path.Bind(stroke > 0 ? Shape.StrokeProperty : Shape.FillProperty, path.GetObservable(TextElement.ForegroundProperty));
                path.AttachedToVisualTree += (_, _) => path.Data ??= Geometry.Parse(data);
                return path;
            }
            Control glyph = state == TagToggleState.All ? Part(CheckGeometry, 2)
                : new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, Children = { Part(PartialOutline, 1.5), Part(PartialFill, 0) } };
            AutomationProperties.SetAccessibilityView(glyph, AccessibilityView.Raw);
            return glyph;
        }
    }

    private enum SourceCategory { General }
}
