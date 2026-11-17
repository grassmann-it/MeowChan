using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MeowChan.DesktopSmoke;

// Compiled only into the non-packable smoke executable. No product hooks or services.
internal static partial class ReviewArtifacts
{
    private static int _sequence;
    private static bool _writing;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static string? Output => Environment.GetEnvironmentVariable("MEOWCHAN_REVIEW_ARTIFACTS");

    internal static void Capture(Window window, string label)
    {
        if (_writing || string.IsNullOrWhiteSpace(Output)) return;
        _writing = true;
        try
        {
            string safe = string.Concat(Path.GetFileNameWithoutExtension(label).Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-'));
            string directory = Path.Combine(Output!, (++_sequence).ToString("D4") + "-" + safe);
            Directory.CreateDirectory(directory);
            Program.CaptureWindow(window, "screenshot.png", directory);
            Write(directory, "control-tree.json", new
            {
                schema_version = 1, coordinate_space = "window-client-dip", render_scaling = window.RenderScaling,
                elements = Tree(window).ToArray()
            });
            Write(directory, "state.json", Snapshot(window));
        }
        finally { _writing = false; }
    }

    private static void Write(string directory, string file, object value) =>
        File.WriteAllText(Path.Combine(directory, file), JsonSerializer.Serialize(value, JsonOptions) + "\n");

    internal static IEnumerable<object> Tree(Control root)
    {
        return Visit(root, "0", null);
        IEnumerable<object> Visit(Visual visual, string path, string? parent)
        {
            if (visual is Control c)
            {
                Point? origin = c.TranslatePoint(default, root);
                Rect? bounds = origin is { } p ? new Rect(p, c.Bounds.Size) : null;
                bool clipped = false;
                if (bounds is { } rect)
                {
                    clipped = !new Rect(root.Bounds.Size).Contains(rect);
                    foreach (Visual ancestor in c.GetVisualAncestors().Where(a => a.ClipToBounds))
                        if (ancestor.TranslatePoint(default, root) is { } a)
                            clipped |= !new Rect(a, ancestor.Bounds.Size).Contains(rect);
                }
                yield return new
                {
                    id = path, parent_id = parent, name = c.Name, type = c.GetType().FullName,
                    bounds = bounds is { } b ? new { x = b.X, y = b.Y, width = b.Width, height = b.Height } : null,
                    visible = c.IsVisible, effectively_visible = c.IsEffectivelyVisible, opacity = c.Opacity,
                    is_enabled = c.IsEnabled, effectively_enabled = c.IsEffectivelyEnabled,
                    is_pointer_over = c.IsPointerOver, is_focused = c.IsFocused, is_keyboard_focus_within = c.IsKeyboardFocusWithin,
                    focusable = c.Focusable, is_hit_test_visible = c.IsHitTestVisible,
                    visual_state_classes = c.Classes.Order(StringComparer.Ordinal).ToArray(),
                    accessible_name = AutomationProperties.GetName(c), accessible_help_text = AutomationProperties.GetHelpText(c),
                    tooltip = ToolTip.GetTip(c) as string,
                    text = c switch { TextBlock t => t.Text, TextBox t => t.Text, ContentControl { Content: string s } => s, _ => null },
                    desired_size = new { width = c.DesiredSize.Width, height = c.DesiredSize.Height },
                    clipped_or_overflow = clipped || c.DesiredSize.Width > c.Bounds.Width + 0.5 || c.DesiredSize.Height > c.Bounds.Height + 0.5,
                    clip_to_bounds = c.ClipToBounds
                };
                parent = path;
            }
            int index = 0;
            foreach (Visual child in visual.GetVisualChildren())
                foreach (object item in Visit(child, path + "/" + index++, parent)) yield return item;
        }
    }
}
