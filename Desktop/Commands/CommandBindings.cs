using System;
using System.IO;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
        return item;
    }

    public static NativeMenuItem CreateNativeMenuItem(string id, Action execute, Func<bool>? canExecute = null)
    {
        var item = new NativeMenuItem { Command = new DelegateCommand(execute, canExecute) };
        Apply(item, id);
        return item;
    }

    public static Button CreateButton(string id, Action execute, Func<bool>? canExecute = null,
        Func<string?>? unavailableReason = null, bool iconOnly = false, int iconSize = 16)
    {
        var button = new Button { Command = new DelegateCommand(execute, canExecute) };
        Apply(button, id, unavailableReason, iconOnly, iconSize);
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
        item.Gesture = Gesture(definition);
        item.Icon = NativeIcon(id);
    }

    public static void Apply(Button button, string id, Func<string?>? unavailableReason = null,
        bool iconOnly = false, int iconSize = 16)
    {
        string label = CommandCatalog.Label(id);
        button.Content = iconOnly ? Icon(id, iconSize) : label;
        string tip = Tooltip(id, unavailableReason?.Invoke());
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetHelpText(button, tip);
    }

    public static void Refresh(MenuItem item) => (item.Command as DelegateCommand)?.RaiseCanExecuteChanged();
    public static void Refresh(Button button) => (button.Command as DelegateCommand)?.RaiseCanExecuteChanged();
    public static void Refresh(NativeMenuItem item) => (item.Command as DelegateCommand)?.RaiseCanExecuteChanged();

    public static string Tooltip(string id, string? unavailableReason = null)
    {
        string label = CommandCatalog.Label(id);
        string? shortcut = CommandCatalog.ShortcutDisplay(id);
        string result = shortcut is null ? label : label + " · " + shortcut;
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
            [!Border.BackgroundProperty] = new Avalonia.Data.Binding("Foreground")
            {
                RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor)
                {
                    AncestorType = typeof(TemplatedControl)
                }
            },
            OpacityMask = new ImageBrush
            {
                Source = new Bitmap(stream),
                Stretch = Stretch.Uniform
            }
        };
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

    private static KeyGesture? Gesture(CommandDefinition definition)
    {
        if (definition.Shortcut is null) return null;
        string shortcut = definition.Shortcut.Replace("Primary",
            OperatingSystem.IsMacOS() ? "Meta" : "Ctrl", StringComparison.Ordinal);
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
