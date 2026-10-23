using Avalonia;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// Test-only handshake. Does not assign accessibility properties or bypass the native client.
internal static class NativeFeatureCheckpoint
{
    private static readonly HashSet<string> Seen = new();
    internal static async Task HoldAsync(string family, Control root, Control? expectedFocus, params Control[] targets)
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_URSA_NATIVE_SUITE") is not { Length: > 0 }) return;
        string id = family + "-" + Texts.Language;
        if (!Seen.Add(id)) return;
        string directory = Environment.GetEnvironmentVariable("MEOWCHAN_ACCESSIBILITY_EXCHANGE")
            ?? throw new InvalidOperationException("Native feature exchange is required.");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw new IOException("Missing absolute native exchange.");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var top = TopLevel.GetTopLevel(root) ?? throw new InvalidOperationException("Detached native checkpoint.");
        object Describe(Control control) => new
        {
            id = AutomationProperties.GetAutomationId(control) ?? control.Name,
            name = AutomationProperties.GetName(control),
            expectedHelp = AutomationProperties.GetHelpText(control),
            expectedText = control is TextBox text ? text.Text : null,
            type = control.GetType().Name,
            enabled = control.IsEffectivelyEnabled,
            focusable = control.Focusable,
            requireInitialFocus = ReferenceEquals(control, expectedFocus)
        };
        string path = Path.Combine(directory, "stage.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
        {
            id, family, language = Texts.Language, pid = Environment.ProcessId,
            actualTheme = top.ActualThemeVariant?.ToString(),
            initialFocus = top.FocusManager?.GetFocusedElement() is Control current ? Describe(current) : null,
            probes = targets.Select(Describe).ToArray(),
            scope = "native labels/roles/help, initial focus where required, native focus and one XTest Tab; no persisted mutation"
        }));
        File.Move(path + ".tmp", path, true);
        Console.WriteLine("URSA NATIVE STAGE " + id);
        DateTime deadline = DateTime.UtcNow.AddSeconds(55);
        while (!File.Exists(Path.Combine(directory, id + ".done")))
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Native observer did not acknowledge " + id);
            await Task.Delay(100);
        }
    }

    private static bool InViewport(Control control, bool requireFullBounds = true)
    {
        TopLevel? top = TopLevel.GetTopLevel(control);
        if (top == null || !control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0
            || control.TranslatePoint(default, top) is not Point position) return false;
        Rect visible = new(default(Point), top.Bounds.Size);
        foreach (Visual ancestor in control.GetVisualAncestors())
        {
            // The content presenter is the actual scrolling clip. Also honor
            // other clipping ancestors, including the ListBox and page host.
            if (ancestor.ClipToBounds || ancestor is ScrollViewer)
            {
                if (ancestor.TranslatePoint(default, top) is not Point origin) return false;
                visible = visible.Intersect(new Rect(origin, ancestor.Bounds.Size));
            }
            if (ReferenceEquals(ancestor, top)) break;
        }
        Rect bounds = new(position, control.Bounds.Size);
        Rect overlap = bounds.Intersect(visible);
        if (overlap.Width <= 0 || overlap.Height <= 0) return false;
        return !requireFullBounds || (bounds.Left >= visible.Left - 1 && bounds.Top >= visible.Top - 1
            && bounds.Right <= visible.Right + 1 && bounds.Bottom <= visible.Bottom + 1);
    }

    private static bool SelectedActionInViewport(ListBox actions) =>
        actions.SelectedItem is ListBoxItem { IsSelected: true } selected
        && selected.GetVisualAncestors().Contains(actions) && InViewport(selected);

    // Diagnostic only: preserve geometry/focus evidence and the original timeout.
    // Include every control ancestor so nested presenter/clip geometry is visible,
    // rather than inferring visibility from a ListBox's outer Bounds alone.
    private static void LogShortcutSurface(string phase, SettingsEditor settings, ShortcutEditor editor,
        TextBox search, ListBox actions, bool? focusAccepted = null)
    {
        try
        {
            TopLevel? top = TopLevel.GetTopLevel(settings);
            object Describe(Control control) => new
            {
                type = control.GetType().Name, control.Name,
                bounds = control.Bounds.ToString(),
                topOrigin = top == null ? null : control.TranslatePoint(default, top)?.ToString(),
                control.IsEffectivelyVisible, control.IsEffectivelyEnabled,
                control.IsFocused, control.IsKeyboardFocusWithin, control.Focusable,
                control.ClipToBounds, clip = control.Clip?.Bounds.ToString(),
                scrollOffset = (control as ScrollViewer)?.Offset.ToString(),
                scrollExtent = (control as ScrollViewer)?.Extent.ToString(),
                scrollViewport = (control as ScrollViewer)?.Viewport.ToString()
            };
            object WithAncestors(Control control) => new
            {
                control = Describe(control), inViewport = InViewport(control),
                hasVisibleArea = InViewport(control, requireFullBounds: false),
                ancestors = control.GetVisualAncestors().OfType<Control>().Select(Describe).ToArray()
            };
            Console.WriteLine("URSA NATIVE SHORTCUT DIAGNOSTIC " + JsonSerializer.Serialize(new
            {
                phase, language = Texts.Language, focusAccepted,
                sameTopLevel = ReferenceEquals(TopLevel.GetTopLevel(search), top),
                focused = top?.FocusManager?.GetFocusedElement() is Control current ? Describe(current) : null,
                heading = WithAncestors(editor.SectionHeading),
                search = WithAncestors(search), actions = WithAncestors(actions),
                selectedIndex = actions.SelectedIndex,
                selected = actions.SelectedItem is Control item ? WithAncestors(item) : null
            }));
        }
        catch (Exception diagnosticError)
        {
            Console.WriteLine("URSA NATIVE SHORTCUT DIAGNOSTIC ERROR " + diagnosticError);
        }
    }

    internal static async Task ShortcutsAsync(SettingsEditor settings)
    {
        // SettingsPageVisible is the selected-page enum, not a materialization
        // signal. Resolve the existing XAML instance, then use the real section
        // navigator only after its scroll host and editor are attached/measured.
        ShortcutEditor editor = settings.FindControl<ShortcutEditor>("ShortcutsEditor")
            ?? throw new InvalidOperationException("The production Settings shortcut editor is missing from its namescope.");
        bool dirty = editor.IsDirty;
        string raw = editor.Draft.Raw.GetRawText();
        await Program.WaitUntilAsync(() => settings.IsEffectivelyVisible && settings.CanOpenSectionNavigation
            && TopLevel.GetTopLevel(editor) != null && editor.IsEffectivelyVisible && editor.Bounds.Width > 0
            && settings.GetVisualDescendants().Contains(editor));
        settings.OpenSectionNavigation();
        if (!settings.CanGoToSection("settings.shortcuts"))
            throw new InvalidOperationException("The production shortcut section is unavailable in active Settings.");
        settings.GoToSection("settings.shortcuts");
        await Program.WaitUntilAsync(() => editor.SectionHeading.IsFocused && InViewport(editor.SectionHeading));
        ListBox actions = editor.GetVisualDescendants().OfType<ListBox>().Single();
        TextBox search = editor.GetVisualDescendants().OfType<TextBox>().First();
        LogShortcutSurface("section-ready", settings, editor, search, actions);
        actions.SelectedIndex = 0;
        search.BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        bool focusAccepted = search.Focus();
        LogShortcutSurface("focus-requested", settings, editor, search, actions, focusAccepted);
        try
        {
            // A 210-DIP scrollable ListBox cannot fit as a whole in the measured
            // 202-DIP Settings viewport. Require the real selected row instead,
            // fully inside both its list presenter and the outer Settings clip.
            await Program.WaitUntilAsync(() => search.IsFocused && InViewport(editor.SectionHeading) && InViewport(search)
                && InViewport(actions, requireFullBounds: false) && SelectedActionInViewport(actions)
                && ReferenceEquals(TopLevel.GetTopLevel(search), TopLevel.GetTopLevel(settings)));
        }
        catch (TimeoutException)
        {
            LogShortcutSurface("readiness-timeout", settings, editor, search, actions, focusAccepted);
            throw;
        }
        LogShortcutSurface("surface-ready", settings, editor, search, actions, focusAccepted);
        Console.WriteLine($"URSA NATIVE shortcut surface ready: language={Texts.Language}; "
            + $"editor={editor.Bounds}; search={search.Bounds}; actions={actions.Bounds}; focus={search.IsFocused}; real-section=settings.shortcuts");
        ListBoxItem returnTarget = (ListBoxItem)actions.SelectedItem!;
        await HoldAsync("shortcuts", editor, search, search, actions);
        LogShortcutSurface("observer-complete", settings, editor, search, actions);
        if (editor.IsDirty != dirty || editor.Draft.Raw.GetRawText() != raw || ShortcutService.IsCapturing)
            throw new InvalidOperationException("Native shortcut cancel changed draft or left capture active.");
        if (!ReferenceEquals(actions.SelectedItem, returnTarget) || !returnTarget.IsFocused
            || !ReferenceEquals(TopLevel.GetTopLevel(settings)?.FocusManager?.GetFocusedElement(), returnTarget)
            || !ReferenceEquals(TopLevel.GetTopLevel(returnTarget), TopLevel.GetTopLevel(settings))
            || !SelectedActionInViewport(actions))
            throw new InvalidOperationException("Native shortcut Escape did not restore focus and viewport to the original selected action.");
    }
}
