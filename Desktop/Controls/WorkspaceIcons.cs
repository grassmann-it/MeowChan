using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The drawn icons that are not command icons: marks a button or a menu row needs without a command of its own (close, more,
/// the favourite star, a check, a chevron). They replace font glyphs ("×", "⋯", "★", "✓", "‹"), which draw differently with every
/// font and are read aloud as their character names. Command icons stay with <c>CommandBindings.Icon</c> (the feather catalog);
/// this table only holds what that catalog cannot give a fixed colour or fill state.
/// <para>Rules: every icon sits on a 16 dip grid (the small cross on 10) and is drawn with the stroke weight of its class
/// (<c>glyph-icon</c> 1.5, <c>glyph-thin</c> 1, <c>glyph-heavy</c> 2); no other weights. The geometry is built when the icon is
/// first shown, never in a static initializer, because <see cref="Geometry.Parse(string)"/> needs the render platform and a type
/// initializer that runs in a unit test without one would fail.</para>
/// </summary>
internal static class WorkspaceIcons
{
    internal const string Close = "close";
    internal const string CloseSmall = "close-small";
    internal const string More = "more";
    internal const string Star = "star";
    internal const string StarFilled = "star-filled";
    internal const string Check = "check";
    internal const string ChevronLeft = "chevron-left";
    internal const string ChevronUp = "chevron-up";
    internal const string ArrowDown = "arrow-down";
    internal const string ArrowUp = "arrow-up";

    /// <summary>The style class every icon carries (size, round caps, no hit testing; see WorkspaceStyles.axaml).</summary>
    internal const string StyleClass = "glyph-icon";
    internal const string SmallClass = "glyph-small";
    internal const string ThinClass = "glyph-thin";
    internal const string HeavyClass = "glyph-heavy";

    /// <summary>Stroke weights an icon may have; the style classes above are the only way to choose one.</summary>
    internal static readonly IReadOnlyList<double> StrokeWeights = [1.0, 1.5, 2.0];

    private sealed record Definition(string Data, string? ExtraClass = null);

    private static readonly Dictionary<string, Definition> Definitions = new()
    {
        [Close] = new("M4,4 L12,12 M12,4 L4,12"),
        [CloseSmall] = new("M1.5,1.5 L8.5,8.5 M8.5,1.5 L1.5,8.5", SmallClass),
        // Three dots as very short round-capped strokes, so they scale with the stroke weight like every other icon.
        [More] = new("M3.5,8 L3.6,8 M8,8 L8.1,8 M12.4,8 L12.5,8", HeavyClass),
        [Star] = new("M8,1.6 L9.9,5.8 L14.4,6.3 L11.1,9.4 L12,13.8 L8,11.6 L4,13.8 L4.9,9.4 L1.6,6.3 L6.1,5.8 Z"),
        [StarFilled] = new("M8,1.6 L9.9,5.8 L14.4,6.3 L11.1,9.4 L12,13.8 L8,11.6 L4,13.8 L4.9,9.4 L1.6,6.3 L6.1,5.8 Z"),
        [Check] = new("M3,8.5 L6.5,12 L13,4.5"),
        [ChevronLeft] = new("M10,3.5 L5.5,8 L10,12.5", HeavyClass),
        [ChevronUp] = new("M3.5,10 L8,5.5 L12.5,10", HeavyClass),
        [ArrowDown] = new("M8,3 L8,13 M3,8 L8,13 L13,8"),
        [ArrowUp] = new("M8,13 L8,3 M3,8 L8,3 L13,8")
    };

    private static readonly Dictionary<string, Geometry> Built = new();

    /// <summary>The icon keys, for tests and the lint of the style file.</summary>
    internal static IReadOnlyCollection<string> Keys => Definitions.Keys;

    /// <summary>The path data of an icon as text (no platform needed).</summary>
    internal static string DataOf(string key) =>
        Definitions.TryGetValue(key, out Definition? definition) ? definition.Data : throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown icon.");

    /// <summary>The geometry of an icon, parsed once on first use. Needs the render platform: call it from the UI path only.</summary>
    internal static Geometry GeometryOf(string key)
    {
        lock (Built)
        {
            if (!Built.TryGetValue(key, out Geometry? geometry)) Built.Add(key, geometry = Geometry.Parse(DataOf(key)));
            return geometry;
        }
    }

    /// <summary>
    /// A new icon. Its stroke follows the foreground of the control that holds it (also while that control is disabled), unless
    /// <paramref name="inheritForeground"/> is false and the caller sets Stroke itself. <see cref="Path.Tag"/> carries the key,
    /// so a test or a refresh can tell which icon a button shows without comparing geometry.
    /// </summary>
    internal static Path Create(string key, bool inheritForeground = true)
    {
        Path path = new();
        Attach(path, key, inheritForeground);
        return path;
    }

    /// <summary>Turns an existing <see cref="Path"/> (declared in XAML or built by the caller) into the icon <paramref name="key"/>.</summary>
    internal static void Attach(Path path, string key, bool inheritForeground = true)
    {
        Definition definition = Definitions.TryGetValue(key, out Definition? found) ? found : throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown icon.");
        path.Tag = key;
        path.IsHitTestVisible = false; // The style says so too; a control that holds the icon owns the input.
        if (!path.Classes.Contains(StyleClass)) path.Classes.Add(StyleClass);
        if (definition.ExtraClass is { } extra && !path.Classes.Contains(extra)) path.Classes.Add(extra);
        AutomationProperties.SetAccessibilityView(path, AccessibilityView.Raw);
        if (inheritForeground)
        {
            path.Bind(Shape.StrokeProperty, path.GetObservable(TextElement.ForegroundProperty));
            if (key == StarFilled) path.Bind(Shape.FillProperty, path.GetObservable(TextElement.ForegroundProperty));
        }
        // Built once the icon is shown: the geometry needs the render platform, a constructor or static initializer must not.
        path.AttachedToVisualTree += (_, _) => path.Data ??= GeometryOf(key);
        if (path.IsAttachedToVisualTree()) path.Data ??= GeometryOf(key);
    }

    /// <summary>The key of the icon a button or content shows, or null when it shows something else.</summary>
    internal static string? KeyOf(object? content) => (content as Path)?.Tag as string;

    /// <summary>
    /// Turns <paramref name="button"/> into an icon-only button of the square family: <see cref="SquareClass"/> (input-height square, no
    /// text), the icon as content, and the tooltip and accessible name the caller gives. The icon is never the only name.
    /// </summary>
    internal static void MakeSquare(Button button, string key, string name, string? tip = null, bool subtle = true)
    {
        button.Content = Create(key);
        if (!button.Classes.Contains(SquareClass)) button.Classes.Add(SquareClass);
        if (subtle && !button.Classes.Contains("subtle")) button.Classes.Add("subtle");
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, tip ?? name);
    }

    /// <summary>The icon-only square button class (WorkspaceStyles.axaml): the input height in both directions, centred icon.</summary>
    internal const string SquareClass = "icon-square";
}
