using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static partial class ReviewArtifacts
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static object Snapshot(Window window)
    {
        Control[] controls = window.GetVisualDescendants().OfType<Control>().Prepend(window).ToArray();
        MainWindow? main = Program.OwningMainWindow(window);
        TaggingPage? tagging = main?.TaggingPage;
        return new
        {
            schema_version = 1,
            page = main?.SelectedPage.ToString(), work_view = main?.SessionHost.Config.WorkspaceMode,
            theme = window.ActualThemeVariant.ToString(), language = Texts.Language,
            window = new { active = window.IsActive, state = window.WindowState.ToString(), width = window.Bounds.Width, height = window.Bounds.Height },
            selection_sets = controls.OfType<ListBox>().Select(list => new
            {
                control = Identity(list), visible = list.IsEffectivelyVisible,
                model_indexes = list.Selection.SelectedIndexes.ToArray(),
                selected_items = list.SelectedItems?.Cast<object>().Select(ItemKey).ToArray(),
                lead = list.SelectedItem is { } lead ? ItemKey(lead) : null
            }).ToArray(),
            tagging = tagging == null ? null : new
            {
                view_mode = tagging.ViewMode.ToString(), filter = tagging.Filter.ToString(),
                selected_count = tagging.SelectedCount, counter_label = tagging.SelectionText.Text,
                selected_keys = tagging.ShownList.SelectedItems?.Cast<TaggingQueueRow>().Select(row => row.Key.ToString()).Order(StringComparer.Ordinal).ToArray(),
                progress_label = tagging.ProgressText.Text, busy = tagging.IsBusy,
                clear_question_open = tagging.ClearQuestionOpen, stamp_open = tagging.StampOpen
            },
            counter_labels = controls.OfType<TextBlock>().Where(c => c.Name != null &&
                new[] { "Count", "Status", "Progress", "Selection" }.Any(s => c.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .Select(c => new { control = Identity(c), text = c.Text, visible = c.IsEffectivelyVisible }).ToArray(),
            view_modes = controls.OfType<GalleryViewer>().Select(g => new
            {
                control = Identity(g), current_entry = g.CurrentEntry?.Key.ToString(), panel_width = g.PanelWidth,
                tag_panel_open = g.IsTagPanelOpen, favorite_confirm_open = g.IsFavoriteConfirmOpen
            }).ToArray(),
            media = controls.OfType<MediaPage>().Select(p => new
            {
                control = Identity(p), sort = p.Sort.ToString(), panel_shown = p.IsPanelShown,
                saved_panel_width = p.PageLayout.PanelWidth,
                selected_keys = p.SelectedTiles.Select(t => t.Entry.Sha256 ?? t.Entry.AssetId).Order(StringComparer.Ordinal).ToArray(),
                overflow_membership = p.SelectionActionsMovedByWidth.Select(Identity).ToArray()
            }).ToArray(),
            filters = controls.Where(c => c is TextBox or ComboBox or ToggleButton)
                .Select(c => new { control = Identity(c), text = c is TextBox text ? text.Text : null,
                    selected_index = c is ComboBox box ? (int?)box.SelectedIndex : null, is_checked = (c as ToggleButton)?.IsChecked }).ToArray(),
            panel_widths = controls.Where(c => c.Name?.Contains("Panel", StringComparison.OrdinalIgnoreCase) == true || c.Name?.Contains("Sidebar", StringComparison.OrdinalIgnoreCase) == true)
                .Select(c => new { control = Identity(c), width = c.Bounds.Width, visible = c.IsEffectivelyVisible }).ToArray(),
            detached_popups = DetachedRoots(window).Select(c => new { control = Identity(c), type = c.GetType().Name, visible = c.IsEffectivelyVisible }).ToArray(),
            open_popovers_overlays = controls.Where(c => c is Popup { IsOpen: true } || c.IsEffectivelyVisible &&
                (c.Name?.Contains("Overlay", StringComparison.OrdinalIgnoreCase) == true || c.Name?.Contains("Popover", StringComparison.OrdinalIgnoreCase) == true))
                .Select(c => new { control = Identity(c), type = c.GetType().Name, opacity = c.Opacity, hit_test_visible = c.IsHitTestVisible }).ToArray(),
            owned_windows = window.OwnedWindows.Select(w => new { type = w.GetType().Name, visible = w.IsVisible, active = w.IsActive }).ToArray(),
            background_work_tokens = WorkTokens(controls).ToArray(),
            coverage = "Realized controls and direct product-owned Task/CTS/busy/pending fields (plus one helper level); CTS existence is not proof of running work. Unexposed jobs are not inferred idle."
        };
    }

    private static IEnumerable<Control> DetachedRoots(Window window)
    {
        var roots = new HashSet<Control>();
        if (Program.OwningMainWindow(window)?.TaggingPage is { } tagging)
        {
            if (tagging.OpenListMenu is { IsOpen: true } list) roots.Add(list);
            if (tagging.OpenStateMenu is { IsOpen: true } state) roots.Add(state);
        }
        foreach (Control c in window.GetVisualDescendants().OfType<Control>())
        {
            if (c.ContextMenu is { IsOpen: true } menu) roots.Add(menu);
            if (c is Popup { IsOpen: true, Child: { } child }) roots.Add(child);
        }
        return roots.OrderBy(Identity, StringComparer.Ordinal);
    }

    private static string Identity(Control control)
    {
        var parts = new Stack<string>();
        for (Visual? c = control; c != null; c = c.GetVisualParent())
        {
            Visual? parent = c.GetVisualParent();
            int index = parent?.GetVisualChildren().TakeWhile(v => !ReferenceEquals(v, c)).Count() ?? 0;
            parts.Push(((c as Control)?.Name ?? c.GetType().Name) + "[" + index + "]");
        }
        return string.Join("/", parts);
    }

    private static string ItemKey(object item)
    {
        if (item is TaggingQueueRow row) return row.Key.ToString();
        foreach (string name in new[] { "Key", "AssetId", "Id", "AccessibleName" })
            if (item.GetType().GetProperty(name, Members) is { GetMethod: not null } property && property.GetIndexParameters().Length == 0 && property.GetValue(item) is { } value)
                return value.ToString() ?? item.GetType().Name;
        return item is string text ? text : item.GetType().Name;
    }

    private static IEnumerable<object> WorkTokens(IEnumerable<Control> controls)
    {
        foreach (Control control in controls.Where(c => c.GetType().Assembly == typeof(MainWindow).Assembly))
            foreach (object token in Inspect(control, Identity(control), 0)) yield return token;

        IEnumerable<object> Inspect(object owner, string path, int depth)
        {
            foreach (FieldInfo field in owner.GetType().GetFields(Members).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                object? value = field.GetValue(owner);
                string id = path + "/" + field.Name;
                if (value is Task task)
                    yield return new { token = id, kind = "task", pending = !task.IsCompleted, status = task.Status.ToString(), cancellation_requested = (bool?)null };
                else if (value is CancellationTokenSource cts)
                    yield return new { token = id, kind = "cancellation-source", pending = (bool?)null, status = "token-present", cancellation_requested = (bool?)cts.IsCancellationRequested };
                else if (value is bool busy && (field.Name.Contains("busy", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("pending", StringComparison.OrdinalIgnoreCase)))
                    yield return new { token = id, kind = "busy-flag", pending = busy, status = busy ? "pending" : "idle", cancellation_requested = (bool?)null };
                else if (depth == 0 && value != null && value is not Visual && value.GetType().Assembly == typeof(MainWindow).Assembly && !value.GetType().IsValueType && value is not Delegate)
                    foreach (object nested in Inspect(value, id, 1)) yield return nested;
            }
        }
    }
}
