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
    // A rated star with a diagonal strike: clearer than a generic close icon for removing a rating.
    internal const string StarClear = "star-clear";
    internal const string Check = "check";
    internal const string ChevronLeft = "chevron-left";
    internal const string ChevronUp = "chevron-up";
    internal const string ChevronDown = "chevron-down";
    internal const string ArrowDown = "arrow-down";
    internal const string ArrowUp = "arrow-up";
    // The tools of the media toolbar (M1): update (circular arrow), shuffle (two crossing arrows), enlarge the working area (diagonal arrows).
    internal const string Refresh = "refresh";
    internal const string Shuffle = "shuffle";
    internal const string Maximize = "maximize";
    // The plain question mark of the help button: no ring, the button is a flat square (a circled mark was dropped in the UX review).
    internal const string Help = "help";
    // MP4: the Booru mode of the media page (a list of tags in a column beside a grid).
    internal const string TagList = "tag-list";
    // The four message severities of a banner: four different shapes, so the severity is not carried by colour alone.
    internal const string SeverityError = "severity-error";
    internal const string SeverityWarning = "severity-warning";
    internal const string SeverityInfo = "severity-info";
    internal const string SeveritySuccess = "severity-success";

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
        [StarClear] = new("M8,1.6 L9.9,5.8 L14.4,6.3 L11.1,9.4 L12,13.8 L8,11.6 L4,13.8 L4.9,9.4 L1.6,6.3 L6.1,5.8 Z M2.5,2.5 L13.5,13.5"),
        [Check] = new("M3,8.5 L6.5,12 L13,4.5"),
        [ChevronLeft] = new("M10,3.5 L5.5,8 L10,12.5", HeavyClass),
        [ChevronUp] = new("M3.5,10 L8,5.5 L12.5,10", HeavyClass),
        [ChevronDown] = new("M3.5,6 L8,10.5 L12.5,6", HeavyClass),
        [ArrowDown] = new("M8,3 L8,13 M3,8 L8,13 L13,8"),
        [ArrowUp] = new("M8,13 L8,3 M3,8 L8,3 L13,8"),
        // Three quarters of a circle open at the right with an arrow head at its upper end.
        [Refresh] = new("M13.5,8 A5.5,5.5 0 1 1 11.9,4.1 M11.9,1.5 L11.9,4.1 L9.3,4.1"),
        // Two paths that cross (the lower one is broken at the crossing), each ending in an arrow head on the right.
        [Shuffle] = new("M2,4.5 L4.5,4.5 C7,4.5 8,11.5 10.5,11.5 L13.5,11.5 M11.5,9.5 L13.5,11.5 L11.5,13.5 M2,11.5 L4.5,11.5 C5.4,11.5 6.1,11.1 6.7,10.4 M9.3,5.6 C9.9,4.9 10.5,4.5 11.5,4.5 L13.5,4.5 M11.5,2.5 L13.5,4.5 L11.5,6.5"),
        // Two corner arrows pointing apart along the diagonal.
        [Maximize] = new("M9.5,2.5 L13.5,2.5 L13.5,6.5 M13.5,2.5 L8.8,7.2 M6.5,13.5 L2.5,13.5 L2.5,9.5 M2.5,13.5 L7.2,8.8"),
        // A question mark without a circle (the arc, the curl down to the stem, the dot). Curl circle and dot share the axis x = 8 (the
        // middle of the 16 dip grid; it was 7.92); the glyph sits a quarter dip below the geometric middle, because the open curl carries
        // its weight at the top and a geometrically centred "?" looks high in the round button. The dot is a 0.3 dip stroke, a little
        // taller than the stroke weight, so it does not read thinner than the curl. Drawn about 1.2 times its first size (12 of 16 dip high) so the bare mark reads
        // as large as the other icons now that no circle frames it.
        [Help] = new("M4.6,4.66 A3.6,3.6 0 0 1 11.6,5.86 C11.6,8.26 8,9.46 8,9.46 M8,13.96 L8,14.26"),
        // A frame split into a narrow left column with three list lines and a wide right part (the grid).
        [TagList] = new("M2,2.5 L14,2.5 L14,13.5 L2,13.5 Z M7,2.5 L7,13.5 M3.8,5.5 L5.2,5.5 M3.8,8 L5.2,8 M3.8,10.5 L5.2,10.5"),
        // Circle with a cross (error), triangle with a stroke and a dot (warning), circle with an "i" (information), circle with a tick (success).
        [SeverityError] = new("M14.2,8 A6.2,6.2 0 1 1 1.8,8 A6.2,6.2 0 1 1 14.2,8 Z M5.9,5.9 L10.1,10.1 M10.1,5.9 L5.9,10.1"),
        [SeverityWarning] = new("M8,2 L14.6,13.6 L1.4,13.6 Z M8,6.4 L8,9.4 M8,11.5 L8.1,11.5"),
        [SeverityInfo] = new("M14.2,8 A6.2,6.2 0 1 1 1.8,8 A6.2,6.2 0 1 1 14.2,8 Z M8,7.3 L8,11.3 M8,4.9 L8.1,4.9"),
        [SeveritySuccess] = new("M14.2,8 A6.2,6.2 0 1 1 1.8,8 A6.2,6.2 0 1 1 14.2,8 Z M5.1,8.2 L7.1,10.2 L10.9,6")
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
        // Calling it again (a language change) renames the button and keeps its icon.
        if (KeyOf(button.Content) != key) button.Content = Create(key);
        if (!button.Classes.Contains(SquareClass)) button.Classes.Add(SquareClass);
        if (subtle && !button.Classes.Contains("subtle")) button.Classes.Add("subtle");
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, tip ?? name);
    }

    /// <summary>The icon-only square button class (WorkspaceStyles.axaml): the input height in both directions, centred icon.</summary>
    internal const string SquareClass = "icon-square";
}
