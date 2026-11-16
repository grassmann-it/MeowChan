using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using SkiaSharp;

namespace MeowChan.Desktop.Commands;

/// <summary>UI wiring for the shared action inventory. Targets are captured by the caller.</summary>
public static class CommandBindings
{
    // Catalog icons are immutable app resources. Keep one decoded mask per icon;
    // controls/brushes remain separate so foreground and theme bindings stay local.
    private static readonly Dictionary<string, Bitmap> IconMasks = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<Button, ButtonContent> ButtonContents = new();
    private static readonly ConditionalWeakTable<object, RefreshCallback> Presentations = new();
    // One callback holder per owner. Observe swaps Refresh in place and registers the owner with the ShortcutService once per
    // snapshot (Revision): re-applying a presentation many times must not add an observer entry each time. The service only
    // invokes an entry after taking it out of its list, so an invoked holder counts as unregistered again, whatever the revision.
    private sealed class RefreshCallback
    {
        public Action Refresh;
        public readonly Action Invoke;
        public long RegisteredRevision = -1;
        public RefreshCallback(Action refresh) { Refresh = refresh; Invoke = () => { RegisteredRevision = -1; Refresh(); }; }
    }
    private static readonly ConditionalWeakTable<Control, ActionPresentation> Actions = new();
    private sealed record ActionPresentation(string Id, bool PreserveHelp);
    private static void Track(Control control, string id, bool preserveHelp = false)
    { Actions.Remove(control); Actions.Add(control, new(id, preserveHelp)); }
    private static void Observe(object owner, Action refresh)
    {
        if (!Presentations.TryGetValue(owner, out RefreshCallback? callback))
        {
            callback = new RefreshCallback(refresh);
            Presentations.Add(owner, callback);
        }
        else callback.Refresh = refresh;
        // Publish clears the observer list and bumps the revision, so an entry of an older revision is gone.
        if (callback.RegisteredRevision == ShortcutService.Revision) return;
        callback.RegisteredRevision = ShortcutService.Revision;
        ShortcutService.Observe(callback.Invoke);
    }

    /// <summary>
    /// Re-runs <paramref name="refresh"/> whenever the bindings change, in place of the catalog refresh an <c>Apply</c> on the same control registered: for a
    /// control that builds its own tooltip from several commands (a favourite star), so it has one tooltip source and a rebinding never restores a stale one.
    /// </summary>
    internal static void ObservePresentation(Control owner, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(refresh);
        Observe(owner, () => { refresh(); ObservePresentation(owner, refresh); });
    }

    /// <summary>Number of presentations currently registered for a shortcut-snapshot refresh (tests: it must not grow on re-apply).</summary>
    internal static int ObserverCount => ShortcutService.ObserverCount;

    private sealed record ButtonContent(string Id, int Size, bool WithLabel, Control Root, TextBlock? Label);

    public static MenuItem CreateMenuItem(string id, Action execute, Func<bool>? canExecute = null,
        Func<string?>? unavailableReason = null, Func<string>? label = null, bool showShortcut = true)
    {
        var item = new MenuItem { Command = new DelegateCommand(execute, canExecute) };
        Apply(item, id, unavailableReason, label, showShortcut);
        item.IsEnabled = canExecute?.Invoke() ?? true;
        return item;
    }

    public static NativeMenuItem CreateNativeMenuItem(string id, Action execute, Func<bool>? canExecute = null,
        Func<string>? label = null, Func<string?>? shortcut = null)
    {
        var item = new NativeMenuItem { Command = new DelegateCommand(execute, canExecute) };
        Apply(item, id, label, shortcut);
        return item;
    }

    public static Button CreateButton(string id, Action execute, Func<bool>? canExecute = null,
        Func<string?>? unavailableReason = null, bool iconOnly = false, int iconSize = 16,
        bool iconWithLabel = false)
    {
        var button = new Button { Command = new DelegateCommand(execute, canExecute) };
        Apply(button, id, unavailableReason, iconOnly, iconSize, iconWithLabel);
        return button;
    }

    /// <param name="showShortcut">False for a menu whose surface does not run the command's default gesture (the reader's
    /// "Thread" menu offers commands whose shortcuts act in the lists): no gesture column and no shortcut in the tip.</param>
    public static void Apply(MenuItem item, string id, Func<string?>? unavailableReason = null, Func<string>? label = null,
        bool showShortcut = true)
    {
        Track(item, id);
        Observe(item, () => Apply(item, id, unavailableReason, label, showShortcut));
        var definition = CommandCatalog.Get(id);
        string visibleLabel = label?.Invoke() ?? CommandCatalog.Label(id);
        item.Header = visibleLabel;
        item.Icon = Icon(id);
        item.InputGesture = showShortcut ? Gesture(definition) : null;
        string? reason = unavailableReason?.Invoke();
        var tip = showShortcut ? Tooltip(id, reason, visibleLabel)
            : string.IsNullOrWhiteSpace(reason) ? visibleLabel : visibleLabel + " · " + reason;
        ToolTip.SetTip(item, tip);
        // A disabled entry explains itself: the reason is part of its tip and help text, so it must show while disabled.
        ToolTip.SetShowOnDisabled(item, true);
        AutomationProperties.SetName(item, visibleLabel);
        AutomationProperties.SetHelpText(item, tip);
    }

    /// <summary>A native menu entry. <paramref name="label"/> and <paramref name="shortcut"/> let the menu bar show its own
    /// title and only the chords it may advertise; both are read again whenever the bindings or the language change.</summary>
    public static void Apply(NativeMenuItem item, string id, Func<string>? label = null, Func<string?>? shortcut = null)
    {
        Observe(item, () => Apply(item, id, label, shortcut));
        var definition = CommandCatalog.Get(id);
        string? display = shortcut != null ? shortcut() : CommandCatalog.ShortcutDisplay(id);
        item.Header = (label?.Invoke() ?? CommandCatalog.Label(id)) + (string.IsNullOrEmpty(display) ? "" : "  " + display);
        // Clipboard import is a Downloads shortcut. The native tray action remains
        // explicit and must not register an application-wide paste accelerator.
        item.Gesture = null; // Routed context checks own execution; native accelerators cannot enforce them.
        item.Icon = NativeIcon(id);
    }

    public static void Apply(Button button, string id, Func<string?>? unavailableReason = null,
        bool iconOnly = false, int iconSize = 16, bool iconWithLabel = false)
    {
        Track(button, id);
        Observe(button, () => Apply(button, id, unavailableReason, iconOnly, iconSize, iconWithLabel));
        string label = CommandCatalog.Label(id);
        button.Classes.Set("command-icon", iconOnly || iconWithLabel);
        if (iconOnly || iconWithLabel)
            ApplyButtonContent(button, id, iconSize, iconWithLabel ? CompactLabel(id) : null);
        else
        {
            ButtonContents.Remove(button);
            button.Content = label;
        }
        string tip = Tooltip(id, unavailableReason?.Invoke());
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, tip);
    }

    /// <summary>Uses a context-specific visible label while retaining the catalog icon and shortcut.</summary>
    public static void ApplyLabeled(Button button, string id, string visibleLabel, int iconSize = 16)
    {
        Track(button, id);
        Observe(button, () => ApplyLabeled(button, id, visibleLabel, iconSize));
        button.Classes.Set("command-icon", true);
        ApplyButtonContent(button, id, iconSize, visibleLabel);
        string tip = CommandCatalog.ShortcutDisplay(id) is string shortcut
            ? visibleLabel + " · " + shortcut : visibleLabel;
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, visibleLabel);
        AutomationProperties.SetHelpText(button, tip);
    }

    private static void ApplyButtonContent(Button button, string id, int iconSize, string? label)
    {
        bool withLabel = label != null;
        if (ButtonContents.TryGetValue(button, out ButtonContent? current)
            && current.Id == id && current.Size == iconSize && current.WithLabel == withLabel
            && ReferenceEquals(button.Content, current.Root))
        {
            if (current.Label != null) current.Label.Text = label;
            return;
        }

        TextBlock? text = withLabel
            ? new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } : null;
        Control content = ButtonIcon(id, iconSize, text);
        if (text != null)
            content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { content, text }
            };
        button.Content = content;
        ButtonContents.Remove(button);
        ButtonContents.Add(button, new ButtonContent(id, iconSize, withLabel, content, text));
    }

    private static string CompactLabel(string id)
    {
        return id switch
        {
            "reader.close" => MeowChan.Desktop.Localization.Texts.T("command.reader.close.compact"),
            "reader.expand" => MeowChan.Desktop.Localization.Texts.T("command.reader.expand.compact"),
            "gallery.open" => MeowChan.Desktop.Localization.Texts.T("command.gallery.open.compact"),
            "window.fullscreen" => MeowChan.Desktop.Localization.Texts.T("command.window.fullscreen.compact"),
            _ => CommandCatalog.Label(id)
        };
    }

    public static void Refresh(MenuItem item)
    {
        if (item.Command is not DelegateCommand command) return;
        item.IsEnabled = command.CanExecute(null);
        command.RaiseCanExecuteChanged();
    }
    public static void Refresh(Button button)
    {
        if (button.Command is not DelegateCommand command) return;
        button.IsEnabled = command.CanExecute(null);
        command.RaiseCanExecuteChanged();
    }
    public static void Refresh(NativeMenuItem item) => (item.Command as DelegateCommand)?.RaiseCanExecuteChanged();

    /// <summary>
    /// The tooltip of a command: line 1 is "Label · shortcut" (the label alone without a key), line 2 the hint where it applies and the
    /// reason it is unavailable, joined by " · " (HB1: no more row of four middle dots). The tooltip style wraps a line that is still too
    /// long at 360 dip; a command without a hint or reason has one line.
    /// </summary>
    public static string Tooltip(string id, string? unavailableReason = null, string? visibleLabel = null)
    {
        string label = visibleLabel ?? CommandCatalog.Label(id);
        string? shortcut = CommandCatalog.ShortcutDisplay(id);
        string headline = shortcut is null ? label : label + " · " + shortcut;
        List<string> notes = [];
        if (CommandCatalog.ScopeHint(id) is string scope) notes.Add(scope);
        if (CommandCatalog.DialogHint(id) is string dialog) notes.Add(dialog);
        if (!string.IsNullOrWhiteSpace(unavailableReason)) notes.Add(unavailableReason);
        return notes.Count == 0 ? headline : headline + "\n" + string.Join(" · ", notes);
    }

    /// <summary>The first line of <see cref="Tooltip"/> only ("Label · shortcut"), for a text that lists several commands in one line.</summary>
    internal static string TooltipHeadline(string id, string? visibleLabel = null)
    {
        string tip = Tooltip(id, null, visibleLabel);
        int lineBreak = tip.IndexOf('\n');
        return lineBreak < 0 ? tip : tip[..lineBreak];
    }

    public static Control Icon(string id, int size = 16, string? state = null) =>
        IconFromKey(CommandCatalog.IconForState(id, state), size);

    /// <summary>
    /// A catalog icon by its file name (<c>sliders</c>, <c>refresh-cw</c>) for a control that has no command of its own. Needs the
    /// platform's asset loader, so owners create it when they are shown. It follows the foreground of the control that holds it.
    /// </summary>
    public static Control NamedIcon(string icon, int size = 16)
    {
        Border border = (Border)IconFromKey(icon, size);
        border.Bind(Border.BackgroundProperty, border.GetObservable(TextElement.ForegroundProperty));
        return border;
    }

    /// <summary>The catalog mask <paramref name="icon"/> (a file name of Assets/icons/feather/masks, e.g. "search") as a themed icon.</summary>
    internal static Control IconFromKey(string icon, int size = 16)
    {
        return new Border
        {
            Width = size,
            Height = size,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Text"),
            OpacityMask = new ImageBrush
            {
                Source = IconMask(icon),
                Stretch = Stretch.Uniform
            }
        };
    }

    internal static Bitmap IconMask(string icon)
    {
        lock (IconMasks)
        {
            if (IconMasks.TryGetValue(icon, out Bitmap? bitmap)) return bitmap;
            try
            {
                using Stream stream = AssetLoader.Open(new Uri(IconUri(icon)));
                bitmap = new Bitmap(stream);
            }
            catch (IOException error)
            {
                // A name without a mask file is a bug of the caller (the icon-catalog guard test fails for it), but it must not take a page
                // down: the icon stays empty (a fully transparent mask) and the missing name is logged once.
                Serilog.Log.Warning(error, "The icon mask {Icon} does not exist in the catalog; an empty icon is shown instead.", icon);
                bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            }
            IconMasks.Add(icon, bitmap);
            return bitmap;
        }
    }

    /// <summary>
    /// Shows the catalog icon <paramref name="icon"/> before the text of a button whose content is a string, without replacing that
    /// string: the content template draws the icon and the text, so code that sets or reads <c>Content</c> keeps working and every
    /// action of a toolbar carries an icon (the Reader toolbar rule) whatever sets its label. A button that holds a control as
    /// content (a button of <see cref="ApplyLabeled"/>) is not templated.
    /// </summary>
    internal static void ShowIconBeforeText(Button button, string icon, int size = 16)
    {
        MeowChan.Desktop.Controls.IconButtons.Record(button, icon);
        button.ContentTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((content, _) =>
        {
            TextBlock text = new() { Text = content?.ToString(), VerticalAlignment = VerticalAlignment.Center };
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { ButtonIcon(icon, size, text, byKey: true), text }
            };
        });
    }

    /// <summary>
    /// <see cref="ShowIconBeforeText"/> once: for a button whose string label is set again with every language refresh, so the template
    /// is not rebuilt each time and a template that is already there (an icon chosen earlier) stays.
    /// </summary>
    internal static void EnsureIconBeforeText(Button button, string icon, int size = 16)
    {
        if (button.ContentTemplate == null) ShowIconBeforeText(button, icon, size);
    }

    internal static Control ButtonIcon(string id, int size, TextBlock? label = null, bool byKey = false)
    {
        Border icon = (Border)(byKey ? IconFromKey(id, size) : Icon(id, size));
        // Follow the rendered label, including Semi's TextBlock-specific disabled
        // opacity. Icon-only content inherits the presenter's state foreground.
        icon.Bind(Border.BackgroundProperty, label is null
            ? icon.GetObservable(TextElement.ForegroundProperty) : label.GetObservable(TextBlock.ForegroundProperty));
        return icon;
    }

    public static string IconUri(string icon) => "avares://MeowChan/Assets/icons/feather/masks/" + icon + ".png";

    private static Bitmap NativeIcon(string id)
    {
        using Stream source = AssetLoader.Open(new Uri(IconUri(CommandCatalog.Get(id).Icon)));
        using SKBitmap mask = SKBitmap.Decode(source);
        bool dark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        byte tone = dark ? (byte)240 : (byte)30;
        for (int y = 0; y < mask.Height; y++)
            for (int x = 0; x < mask.Width; x++)
                mask.SetPixel(x, y, new SKColor(tone, tone, tone, mask.GetPixel(x, y).Alpha));
        using SKImage image = SKImage.FromBitmap(mask);
        using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
        using Stream output = png.AsStream();
        return new Bitmap(output);
    }

    internal static void BindShortcutHelp(Control control, IReadOnlyList<string> ids, Func<string> fixedHelp)
    {
        void RefreshHint()
        {
            string help = fixedHelp();
            var bound = ids.Where(id => CommandCatalog.ShortcutDisplay(id) != null).Select(TooltipFor);
            string dynamicHelp = string.Join(Environment.NewLine, bound);
            if (dynamicHelp.Length > 0) help += Environment.NewLine + dynamicHelp;
            ToolTip.SetTip(control, help);
            AutomationProperties.SetHelpText(control, help);
            Observe(control, RefreshHint);
        }
        static string TooltipFor(string id) => Tooltip(id);
        RefreshHint();
    }

    internal static void BindExisting(Control control, string id, bool preserveHelp = false)
    {
        if (Actions.TryGetValue(control, out var current) && current.Id == id)
        {
            if (!preserveHelp || current.PreserveHelp) return;
            // A richer owner binding may be installed after a generic adapter. Rebind
            // the observer policy without touching the existing Name/Help binding.
        }
        Track(control, id, preserveHelp);
        void RefreshHint()
        {
            string tip = Tooltip(id);
            ToolTip.SetTip(control, tip);
            if (!preserveHelp) AutomationProperties.SetHelpText(control, tip);
            Observe(control, RefreshHint);
        }
        RefreshHint();
    }

    /// <summary>Continuation of the owning context's routed handler for visible actions without custom key logic.
    /// Invokes the existing button/menu executor. Multiple contextual targets require an explicit registration.</summary>
    public static bool RouteVisibleActions(Control context, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || CommandRouter.IsNativeInput(e.Source, e.Key)
            || !CommandRouter.IsRoutingContextActive(context, e.Source)) return false;
        LegacyCommandAdapters.Register(context);
        var controls = context.GetVisualDescendants().Prepend(context).OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && Actions.TryGetValue(c, out var a) && !CommandRouter.HasExplicitRegistration(a.Id, context) && Matches(e, a.Id))
            .Where(c => c is Button or MenuItem or ComboBox or TabItem or Expander or Ursa.Controls.ClosableTag).ToArray();
        // R2 of the work views: a matching action the view or a narrow window moved off its bar keeps its shortcut.
        if (controls.Length == 0) return MeowChan.Desktop.WorkViews.WorkViewShortcuts.RouteReduced(context, e);
        Control? focused = controls.FirstOrDefault(c => e.Source is Visual source && source.GetSelfAndVisualAncestors().Contains(c));
        // Do not invent a target when several per-post/per-media actions are materialized.
        if (focused == null && (controls.Length > 1 || controls[0] is Ursa.Controls.ClosableTag))
        { e.Handled = true; return true; }
        var target = focused ?? controls[0];
        e.Handled = true;
        if (!target.IsEffectivelyEnabled) return true;
        InvokeExistingControl(target);
        return true;
    }
    /// <summary>
    /// Whether a visible-action command of <paramref name="context"/> matches <paramref name="e"/> only behind <paramref name="host"/>, which is
    /// hidden now (the auto-hidden overlay footer): the owner shows it before <see cref="RouteVisibleActions"/> looks for visible controls.
    /// </summary>
    internal static bool HasActionBehind(Control context, Control host, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || host.IsEffectivelyVisible || CommandRouter.IsNativeInput(e.Source, e.Key)) return false;
        return host.GetVisualDescendants().OfType<Control>().Any(c => c.IsVisible && c is Button or MenuItem or ComboBox or TabItem or Expander
            && Actions.TryGetValue(c, out var a) && !CommandRouter.HasExplicitRegistration(a.Id, context) && Matches(e, a.Id));
    }
    internal static bool RouteMenuAction(ContextMenu menu, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || CommandRouter.IsNativeInput(e.Source, e.Key)) return false;
        var matches = menu.Items.OfType<MenuItem>().Where(item => Actions.TryGetValue(item, out var a) && Matches(e, a.Id)).ToArray();
        if (matches.Length == 0) return false;
        e.Handled = true;
        if (matches.Length == 1 && matches[0].IsEnabled) InvokeExistingControl(matches[0]);
        return true;
    }
    /// <summary>The catalog command a control presents (Apply/ApplyLabeled/BindExisting), if any.</summary>
    internal static string? ActionId(Control control) => Actions.TryGetValue(control, out var action) ? action.Id : null;

    /// <summary>Runs a control's existing executor exactly as a routed shortcut would.</summary>
    internal static void InvokeAction(Control target) => InvokeExistingControl(target);

    private static void InvokeExistingControl(Control target)
    {
        switch (target)
        {
            case Button button when button.Command != null:
                if (button.Command.CanExecute(button.CommandParameter)) button.Command.Execute(button.CommandParameter);
                break;
            case ToggleButton toggle:
                toggle.IsChecked = toggle.IsChecked != true;
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
            case Button button: button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
            case MenuItem item when item.Command != null:
                if (item.Command.CanExecute(item.CommandParameter)) item.Command.Execute(item.CommandParameter);
                break;
            case MenuItem item:
                if (item.ItemCount > 0) item.IsSubMenuOpen = true;
                else item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); break;
            case Ursa.Controls.ClosableTag tag:
                if (tag.Command?.CanExecute(null) == true) tag.Command.Execute(null);
                break;
            case ComboBox combo: combo.Focus(); combo.IsDropDownOpen = true; break;
            case TabItem tab: tab.IsSelected = true; tab.Focus(); break;
            case Expander expander: expander.IsExpanded = !expander.IsExpanded; break;
        }
    }

    /// <summary>Resolves the same platform gesture used by menu and help presentations.</summary>
    public static bool Matches(KeyEventArgs e, string id)
    {
        if (ShortcutService.IsCapturing) return false;
        if (CommandRouter.IsNativeInput(e.Source, e.Key) && !AllowsInputShortcut(id, e)) return false;
        return ShortcutService.Current.Matches(e, id);
    }

    // The caller first checks the active gallery/window/dialog context. Only these two
    // owned transport controls opt into media navigation/volume; generic sliders stay native.
    internal static bool MatchesGalleryTransport(KeyEventArgs e, string id, Control seek, Control volume)
    {
        if (e.Handled || ShortcutService.IsCapturing) return false;
        bool transportAction = id is "gallery.previous" or "gallery.next" or "video.volume-up" or "video.volume-down"
            or "video.seek-back" or "video.seek-forward" or "video.seek-back-far" or "video.seek-forward-far";
        if (transportAction && e.Source is Visual visual)
        {
            foreach (var parent in visual.GetSelfAndVisualAncestors())
            {
                // Stop at the actual owned slider before applying native-input policy:
                // the slider itself intentionally reserves these keys from generic routing.
                if (ReferenceEquals(parent, seek) || ReferenceEquals(parent, volume))
                    return ShortcutService.Current.Matches(e, id);
                // Check each nearer node, not IsNativeInput(source): that predicate also
                // sees the owned slider above decorative template children.
                if (parent is TextBox or ComboBox or NumericUpDown or ScrollBar or MenuItem or Ursa.Controls.AutoCompleteBox)
                    return false;
                if (parent is Slider && (e.Key is Key.Left or Key.Right or Key.Up or Key.Down
                    or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Enter or Key.Escape))
                    return false;
                if (parent is SelectableTextBlock text && text.SelectionStart != text.SelectionEnd)
                    return false;
                if ((e.Key is Key.Enter or Key.Space) && (parent is Button or Ursa.Controls.ClosableTag or Ursa.Controls.Rating or Expander))
                    return false;
                if ((e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
                    && (parent is SelectingItemsControl or DataGrid))
                    return false;
            }
        }
        return Matches(e, id);
    }

    /// <summary>A gesture that types no text: a function key or a chord with Ctrl/⌘. A bare letter, a Shift or Alt+letter (AltGr) stays text.</summary>
    internal static bool IsTextSafeChord(KeyEventArgs e) =>
        e.Key >= Key.F1 && e.Key <= Key.F24 || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

    /// <summary>The commands that move between the main pages (and pause everything): the window's own, with no meaning inside a text field, so a
    /// chord that types no text reaches them there too (Ctrl+3 in the search box opens Media). Their page footer shows these chords.</summary>
    internal static bool IsShellNavigationCommand(string id) =>
        MainPageOrder.Contains(id) || MainPageOrder.StatusSegmentCommandIds.Contains(id, StringComparer.Ordinal)
        || id is "navigation.plugins" or "session.pause";

    private static bool AllowsInputShortcut(string id, KeyEventArgs e)
    {
        bool functionKey = e.Key >= Key.F1 && e.Key <= Key.F24;
        bool primaryModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (IsShellNavigationCommand(id) && IsTextSafeChord(e)) return true;
        // macOS full screen (⌃⌘F) and help (⌘?) have no F-key: two or more modifiers, one of them Ctrl/⌘, cannot be typed text.
        bool chorded = primaryModifier && System.Numerics.BitOperations.PopCount(
            (uint)(e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt | KeyModifiers.Shift))) >= 2;
        return (id is "input.focus-url" or "input.focus-search" or "input.focus-download-search" or "input.focus-media-search" or "input.focus-settings-search" or "input.focus-diagnostics-search" or "navigation.settings")
            && (primaryModifier || functionKey)
            || (id is "window.fullscreen" or "viewer.shortcut-help") && (functionKey || chorded);
    }

    public static KeyGesture? Gesture(string id) => Gesture(CommandCatalog.Get(id));
    private static KeyGesture? Gesture(CommandDefinition definition) =>
        ShortcutService.Current.For(definition.Id).FirstOrDefault()?.Resolve(ShortcutService.Current.Platform);

}

public sealed class DelegateCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) execute();
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
