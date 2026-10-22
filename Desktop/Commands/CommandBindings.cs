using System;
using System.IO;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Platform;
using Avalonia.Styling;
using SkiaSharp;

namespace MeowChan.Desktop.Commands;

/// <summary>UI wiring for the shared action inventory. Targets are captured by the caller.</summary>
public static class CommandBindings
{
    public static MenuItem CreateMenuItem(string id, Action execute, Func<bool>? canExecute = null,
        Func<string?>? unavailableReason = null)
    {
        var item = new MenuItem { Command = new DelegateCommand(execute, canExecute) };
        Apply(item, id, unavailableReason);
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

    public static void Apply(MenuItem item, string id, Func<string?>? unavailableReason = null)
    {
        var definition = CommandCatalog.Get(id);
        item.Header = CommandCatalog.Label(id);
        item.Icon = Icon(id);
        item.InputGesture = Gesture(definition);
        var tip = Tooltip(id, unavailableReason?.Invoke());
        ToolTip.SetTip(item, tip);
        AutomationProperties.SetName(item, CommandCatalog.Label(id));
        AutomationProperties.SetHelpText(item, tip);
    }

    public static void Apply(NativeMenuItem item, string id)
    {
        var definition = CommandCatalog.Get(id);
        item.Header = CommandCatalog.Label(id);
        // Clipboard import is a Downloads shortcut. The native tray action remains
        // explicit and must not register an application-wide paste accelerator.
        item.Gesture = id == "watch.import-clipboard" ? null : Gesture(definition);
        item.Icon = NativeIcon(id);
    }

    public static void Apply(Button button, string id, Func<string?>? unavailableReason = null,
        bool iconOnly = false, int iconSize = 16, bool iconWithLabel = false)
    {
        string label = CommandCatalog.Label(id);
        button.Classes.Set("command-icon", iconOnly || iconWithLabel);
        if (iconWithLabel)
        {
            TextBlock text = new TextBlock { Text = CompactLabel(id), VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    ButtonIcon(id, iconSize, text),
                    text
                }
            };
            button.Content = content;
        }
        else button.Content = iconOnly ? ButtonIcon(id, iconSize) : label;
        string tip = Tooltip(id, unavailableReason?.Invoke());
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, tip);
    }

    /// <summary>Uses a context-specific visible label while retaining the catalog icon and shortcut.</summary>
    public static void ApplyLabeled(Button button, string id, string visibleLabel, int iconSize = 16)
    {
        button.Classes.Set("command-icon", true);
        TextBlock text = new TextBlock { Text = visibleLabel, VerticalAlignment = VerticalAlignment.Center };
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ButtonIcon(id, iconSize, text),
                text
            }
        };
        string tip = CommandCatalog.ShortcutDisplay(id) is string shortcut
            ? visibleLabel + " · " + shortcut : visibleLabel;
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, visibleLabel);
        AutomationProperties.SetHelpText(button, tip);
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

    public static string Tooltip(string id, string? unavailableReason = null)
    {
        string label = CommandCatalog.Label(id);
        string? shortcut = CommandCatalog.ShortcutDisplay(id);
        string result = shortcut is null ? label : label + " · " + shortcut;
        if (CommandCatalog.ScopeHint(id) is string scope) result += " · " + scope;
        return string.IsNullOrWhiteSpace(unavailableReason) ? result : result + " · " + unavailableReason;
    }

    public static Control Icon(string id, int size = 16, string? state = null)
    {
        string icon = CommandCatalog.IconForState(id, state);
        using Stream stream = AssetLoader.Open(new Uri(IconUri(icon)));
        return new Border
        {
            Width = size,
            Height = size,
            [!Border.BackgroundProperty] = new DynamicResourceExtension("Workspace.Text"),
            OpacityMask = new ImageBrush
            {
                Source = new Bitmap(stream),
                Stretch = Stretch.Uniform
            }
        };
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

    /// <summary>Resolves the same platform gesture used by menu and help presentations.</summary>
    public static bool Matches(KeyEventArgs e, string id)
    {
        KeyGesture? gesture = Gesture(CommandCatalog.Get(id));
        return gesture != null && gesture.Key == e.Key && gesture.KeyModifiers == e.KeyModifiers;
    }

    public static KeyGesture? Gesture(string id) => Gesture(CommandCatalog.Get(id));

    private static KeyGesture? Gesture(CommandDefinition definition)
    {
        if (definition.Shortcut is null) return null;
        string shortcut = definition.Shortcut.Replace("Primary",
            OperatingSystem.IsMacOS() ? "Meta" : "Ctrl", StringComparison.Ordinal);
        // Enum.Parse interprets bare digits as Key enum ordinals ("1" is Cancel).
        // Keep the catalog/display human readable while parsing digit-row keys explicitly.
        int keyStart = shortcut.LastIndexOf('+') + 1;
        string key = shortcut[keyStart..];
        if (key.Length == 1 && char.IsAsciiDigit(key[0])) shortcut = shortcut[..keyStart] + "D" + key;
        try { return KeyGesture.Parse(shortcut); }
        catch (FormatException) { return null; }
    }
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
