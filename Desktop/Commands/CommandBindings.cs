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
    private sealed record RefreshCallback(Action Refresh);
    private static readonly ConditionalWeakTable<Control, ActionPresentation> Actions = new();
    private sealed record ActionPresentation(string Id);
    private static void Track(Control control, string id)
    { Actions.Remove(control); Actions.Add(control, new(id)); }
    private static void Observe(object owner, Action refresh)
    {
        Action? guarded = null;
        guarded = () => { if (Presentations.TryGetValue(owner, out var current) && ReferenceEquals(current.Refresh, guarded)) refresh(); };
        Presentations.Remove(owner);
        Presentations.Add(owner, new(guarded));
        ShortcutService.Observe(guarded);
    }

    private sealed record ButtonContent(string Id, int Size, bool WithLabel, Control Root, TextBlock? Label);

    public static MenuItem CreateMenuItem(string id, Action execute, Func<bool>? canExecute = null,
        Func<string?>? unavailableReason = null, Func<string>? label = null)
    {
        var item = new MenuItem { Command = new DelegateCommand(execute, canExecute) };
        Apply(item, id, unavailableReason, label);
        item.IsEnabled = canExecute?.Invoke() ?? true;
        return item;
    }

    public static NativeMenuItem CreateNativeMenuItem(string id, Action execute, Func<bool>? canExecute = null)
    {
        var item = new NativeMenuItem { Command = new DelegateCommand(execute, canExecute) };
        Apply(item, id);
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

    public static void Apply(MenuItem item, string id, Func<string?>? unavailableReason = null, Func<string>? label = null)
    {
        Track(item, id);
        Observe(item, () => Apply(item, id, unavailableReason, label));
        var definition = CommandCatalog.Get(id);
        string visibleLabel = label?.Invoke() ?? CommandCatalog.Label(id);
        item.Header = visibleLabel;
        item.Icon = Icon(id);
        item.InputGesture = Gesture(definition);
        var tip = Tooltip(id, unavailableReason?.Invoke(), visibleLabel);
        ToolTip.SetTip(item, tip);
        AutomationProperties.SetName(item, visibleLabel);
        AutomationProperties.SetHelpText(item, tip);
    }

    public static void Apply(NativeMenuItem item, string id)
    {
        Observe(item, () => Apply(item, id));
        var definition = CommandCatalog.Get(id);
        item.Header = CommandCatalog.Label(id) + (CommandCatalog.ShortcutDisplay(id) is string display ? "  " + display : "");
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
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { content, text }
            };
        button.Content = content;
        ButtonContents.Remove(button);
        ButtonContents.Add(button, new ButtonContent(id, iconSize, withLabel, content, text));
    }

    private static string CompactLabel(string id)
    {
        bool german = MeowChan.Desktop.Localization.Texts.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        return id switch
        {
            "reader.close" => german ? "Schließen" : "Close",
            "reader.expand" => german ? "Lesen" : "Read",
            "gallery.open" => german ? "Galerie" : "Gallery",
            "window.fullscreen" => german ? "Vollbild" : "Full screen",
            "export.open-html-dialog" => "HTML…",
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

    public static string Tooltip(string id, string? unavailableReason = null, string? visibleLabel = null)
    {
        string label = visibleLabel ?? CommandCatalog.Label(id);
        string? shortcut = CommandCatalog.ShortcutDisplay(id);
        string result = shortcut is null ? label : label + " · " + shortcut;
        if (CommandCatalog.ScopeHint(id) is string scope) result += " · " + scope;
        return string.IsNullOrWhiteSpace(unavailableReason) ? result : result + " · " + unavailableReason;
    }

    public static Control Icon(string id, int size = 16, string? state = null)
    {
        string icon = CommandCatalog.IconForState(id, state);
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

    private static Bitmap IconMask(string icon)
    {
        lock (IconMasks)
        {
            if (IconMasks.TryGetValue(icon, out Bitmap? bitmap)) return bitmap;
            using Stream stream = AssetLoader.Open(new Uri(IconUri(icon)));
            bitmap = new Bitmap(stream);
            IconMasks.Add(icon, bitmap);
            return bitmap;
        }
    }

    private static Control ButtonIcon(string id, int size, TextBlock? label = null)
    {
        Border icon = (Border)Icon(id, size);
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

    internal static void BindExisting(Control control, string id)
    {
        if (Actions.TryGetValue(control, out var current) && current.Id == id) return;
        Track(control, id);
        void RefreshHint()
        {
            string tip = Tooltip(id);
            ToolTip.SetTip(control, tip);
            AutomationProperties.SetHelpText(control, tip);
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
        if (controls.Length == 0) return false;
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
    internal static bool RouteMenuAction(ContextMenu menu, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || CommandRouter.IsNativeInput(e.Source, e.Key)) return false;
        var matches = menu.Items.OfType<MenuItem>().Where(item => Actions.TryGetValue(item, out var a) && Matches(e, a.Id)).ToArray();
        if (matches.Length == 0) return false;
        e.Handled = true;
        if (matches.Length == 1 && matches[0].IsEnabled) InvokeExistingControl(matches[0]);
        return true;
    }
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

    private static bool AllowsInputShortcut(string id, KeyEventArgs e)
    {
        bool functionKey = e.Key >= Key.F1 && e.Key <= Key.F24;
        bool primaryModifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        return (id is "input.focus-url" or "input.focus-search" or "input.focus-download-search" or "navigation.settings")
            && (primaryModifier || functionKey)
            || (id is "window.fullscreen" or "viewer.shortcut-help") && functionKey;
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
