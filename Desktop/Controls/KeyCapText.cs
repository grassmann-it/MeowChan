using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// A gesture as keycaps (modals plan KS1): one <c>Border.keycap</c> per key of <see cref="ShortcutGesture.DisplayParts"/>, so
/// "Ctrl+Shift+P" and "⌘⇧P" both read as three caps. Key names stay as everywhere else in the app (Strg/Ctrl/⌘ per platform
/// and language, decision E3); the accessible name is the plain gesture text.
/// </summary>
public sealed class KeyCapText : StackPanel
{
    public const string CapClass = "keycap";

    public KeyCapText(ShortcutGesture gesture, ShortcutPlatform platform)
    {
        Orientation = Orientation.Horizontal;
        Spacing = 4;
        VerticalAlignment = VerticalAlignment.Center;
        Gesture = gesture;
        Platform = platform;
        Refresh();
    }

    public ShortcutGesture Gesture { get; }
    public ShortcutPlatform Platform { get; }

    /// <summary>The texts of the caps (a gesture whose saved key is invalid shows its raw parts).</summary>
    public static IReadOnlyList<string> Parts(ShortcutGesture gesture, ShortcutPlatform platform)
    {
        try { return gesture.DisplayParts(platform); }
        catch (System.FormatException) { return [.. gesture.Modifiers, gesture.Key]; }
    }

    /// <summary>Relabels the caps in place (language change: Strg/Ctrl); the caps themselves stay the same controls.</summary>
    public void Refresh()
    {
        IReadOnlyList<string> parts = Parts(Gesture, Platform);
        if (Children.Count == parts.Count && Children.All(child => child is Border { Child: TextBlock }))
        {
            for (int i = 0; i < parts.Count; i++) ((TextBlock)((Border)Children[i]).Child!).Text = parts[i];
            AutomationProperties.SetName(this, Platform == ShortcutPlatform.MacOS ? string.Concat(parts) : string.Join("+", parts));
            return;
        }
        Children.Clear();
        foreach (string part in parts)
        {
            Border cap = new() { Child = new TextBlock { Text = part } };
            cap.Classes.Add(CapClass);
            Children.Add(cap);
        }
        AutomationProperties.SetName(this, Platform == ShortcutPlatform.MacOS ? string.Concat(parts) : string.Join("+", parts));
    }
}
