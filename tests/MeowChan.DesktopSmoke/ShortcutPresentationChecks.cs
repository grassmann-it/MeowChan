using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Views.Shortcuts;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// Presentation-only follow-up: the durable rebind/router suite runs separately.
// These are real product controls with in-process routed keys/clicks, not OS/AT input.
internal static class ShortcutPresentationChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Config original = window.SessionHost.Config.Clone();
        string originalLanguage = Texts.Language;
        Size originalSize = window.ClientSize;
        string? originalUrl = window.FindControl<TextBox>("UrlInput")!.Text;
        string output = Path.Combine(AppContext.BaseDirectory, "shortcut-presentation.jsonl");
        File.WriteAllText(output, string.Empty);
        int completed = 0;
        ShortcutEditor editor = CurrentEditor();
        string? previousDraft = null, previousPath = null;
        const string rawPath = "unfinished shortcut path — Ä/ß";
        const string rawUrl = "https://unsupported.invalid/shortcut-presentation-raw";
        try
        {
            // One real persisted rebind per matrix run, never one router suite per cell.
            Config remapped = original.Clone();
            remapped.ShortcutBindings = remapped.ShortcutBindings.Set("navigation.archive",
                new[] { ShortcutGesture.Parse("Primary+Shift+K") });
            long revision = ShortcutService.Revision;
            await window.SessionHost.ApplySettingsAsync(remapped);
            await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
            KeyGesture replacement = CommandBindings.Gesture("navigation.archive")!;
            Press(window, CommandBindings.Gesture("navigation.downloads")!);
            Require(Press(window, replacement).Handled && window.SelectedPage == MainWindow.WorkspacePage.Archive,
                "Presentation fixture's persisted replacement did not execute the real archive action.");
            Press(window, CommandBindings.Gesture("navigation.downloads")!);
            window.FindControl<TextBox>("UrlInput")!.Text = rawUrl;
            Write("fixture", new { remapped = "navigation.archive", gesture = CommandCatalog.ShortcutDisplay("navigation.archive"),
                input = "in-process real control events; no native or AT claim", priorRouterSuite = "separate evidence, not rerun per cell" });

            foreach (string theme in new[] { "Light", "Dark" })
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            foreach (string language in new[] { "en", "de" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                ThemeVariant expectedTheme = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
                if (window.SessionHost.Config.Theme != theme)
                {
                    Click(window.FindControl<Button>("ThemeToggleButton")!);
                    await Program.WaitUntilAsync(() => window.SessionHost.Config.Theme == theme && window.ActualThemeVariant == expectedTheme);
                }
                // Explicit presentation fixture change: saving a language setting would
                // commit/discard the draft under examination. The real localization event is used.
                Texts.SetLanguage(language);
                window.WindowState = WindowState.Normal;
                double width = mode == "Barebones" ? 640 : 760, height = mode == "Barebones" ? 480 : 500;
                window.Width = width; window.Height = height;
                await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - width) <= 1
                    && Math.Abs(window.ClientSize.Height - height) <= 1 && window.ActualThemeVariant == expectedTheme);
                await LayoutAsync();
                Require(Texts.Language == language && window.SessionHost.Config.WorkspaceMode == mode,
                    "The actual language/mode differs from the matrix cell.");
                editor = CurrentEditor();
                if (previousDraft != null)
                    Require(editor.Draft.Raw.GetRawText() == previousDraft && window.SettingsEditor.DownloadPath == previousPath,
                        "Appearance transition lost the previous shortcut/path draft before explicit fixture reset.");
                Require(ShortcutsWindow.For(window) == null, "The shortcuts window outlived its cell.");
                Require(window.FindControl<TextBox>("UrlInput")!.Text == rawUrl, "Appearance transition lost raw URL input.");
                // Explicit fixture reset between independent cells. No Save or live binding mutation.
                window.SettingsEditor.CancelChanges();
                window.SettingsEditor.DownloadPath = rawPath;
                string cell = $"{language}-{theme.ToLowerInvariant()}-{mode.ToLowerInvariant()}";
                Write("cell-start", new { cell, mode, language, theme, client = window.ClientSize.ToString(),
                    scale = window.RenderScaling, width, height, initialGesture = CommandCatalog.ShortcutDisplay("navigation.archive") });

                // Help is opened through the actual action (F1) and must show the current remap: the one shortcuts window (KS2).
                Press(window, CommandBindings.Gesture("viewer.shortcut-help")!);
                await Program.WaitUntilAsync(() => ShortcutsWindow.For(window) is { IsVisible: true } opened && opened.Editor.ActionList.IsEffectivelyVisible);
                ShortcutsWindow help = ShortcutsWindow.For(window)!;
                string helpText = help.PlainText;
                Require(helpText.Contains(CommandCatalog.Label("navigation.archive"), StringComparison.Ordinal)
                    && helpText.Contains(CommandCatalog.ShortcutDisplay("navigation.archive")!, StringComparison.Ordinal),
                    "Help does not show the translated action and its committed replacement.");
                ShortcutTableRow archiveRow = help.Editor.Model.Rows.Single(row => row.CommandId == "navigation.archive");
                Require(archiveRow.GestureTexts.Contains(CommandCatalog.ShortcutDisplay("navigation.archive")!),
                    "The shortcuts table does not show the committed replacement as keycaps.");
                Require(help.Editor.Context.Area == ShortcutGroups.Pages[0] && !help.Editor.CollapsedGroups.Contains(ShortcutGroups.Pages[0]),
                    "F1 on Downloads did not open the Downloads group expanded and on top.");
                await CaptureWindowAsync(help, cell, "help", help.Editor.SearchInput);
                Press(window, new KeyGesture(Key.Escape));
                await Program.WaitUntilAsync(() => ShortcutsWindow.For(window) == null);

                window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Program.WaitUntilAsync(() => window.SettingsPageVisible);
                editor = CurrentEditor();
                // Each settings category is its own page (029b4cd): the shortcut card only
                // materializes after its category is selected, so select it before waiting.
                await Program.WaitUntilAsync(() => window.SettingsEditor.CanOpenSectionNavigation);
                window.SettingsEditor.OpenSectionNavigation();
                Require(window.SettingsEditor.CanGoToSection("settings.shortcuts"), "The real shortcut section is unavailable.");
                window.SettingsEditor.GoToSection("settings.shortcuts");
                Button open = editor.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsShortcutsOpen");
                await Program.WaitUntilAsync(() => ReferenceEquals(CurrentEditor(), editor) && ReferenceEquals(TopLevel.GetTopLevel(editor), window)
                    && window.SettingsEditor.GetVisualDescendants().Contains(editor) && Materialized(editor) && Materialized(open));
                await Program.WaitUntilAsync(() => editor.SectionHeading.IsFocused && Visible(window, editor.SectionHeading));
                Click(open);
                await Program.WaitUntilAsync(() => ShortcutsWindow.For(window) is { IsVisible: true } opened && opened.Editor.ActionList.IsEffectivelyVisible);
                ShortcutsWindow shortcuts = ShortcutsWindow.For(window)!;
                ShortcutBindingsEditor table = shortcuts.Editor;
                Require(ReferenceEquals(table.Session, editor.Session), "The window does not edit the Settings draft.");
                TextBox search = table.SearchInput;
                ListBox actions = table.ActionList;
                string searchLabel = L("Search action, context or shortcut", "Aktion, Bereich oder Kürzel suchen");
                Require(search.PlaceholderText?.ToString() == searchLabel && AutomationProperties.GetName(search) == searchLabel,
                    "The materialized shortcut search label is stale.");
                Write("editor-ready", new { cell, section = window.SettingsEditor.SelectedSectionId,
                    headingFocused = editor.SectionHeading.IsFocused, window = shortcuts.Bounds.ToString(),
                    search = search.Bounds.ToString(), actions = actions.Bounds.ToString(), language = Texts.Language });
                search.Text = "navigation.archive";
                await Program.WaitUntilAsync(() => actions.Items.OfType<ListBoxItem>().Any(item => item.Tag as string == "navigation.archive"));
                actions.SelectedItem = actions.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "navigation.archive");
                await RevealFocusAsync(shortcuts, search);
                string rowText = AutomationProperties.GetName(actions.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "navigation.archive")) ?? "";
                Require(rowText.Contains(CommandCatalog.Label("navigation.archive"), StringComparison.Ordinal), "Search row kept an old language label.");
                Require(rowText.Contains(CommandCatalog.ShortcutDisplay("navigation.archive")!, StringComparison.Ordinal), "Editor row omitted the committed remap.");
                await CaptureWindowAsync(shortcuts, cell, "editor", search);
                string beforeCapture = editor.Draft.Raw.GetRawText();
                Button record = Action(table, "Record additional shortcut", "Weiteres Kürzel aufnehmen");
                await RevealFocusAsync(shortcuts, record); Click(record);
                KeyGestureInput capture = table.GetVisualDescendants().OfType<KeyGestureInput>().Single();
                capture.BringIntoView();
                await LayoutAsync();
                Require(ShortcutService.IsCapturing && capture.IsKeyboardFocusWithin, "Recording did not acquire real capture focus/suppression.");
                var input = shortcuts.FocusManager?.GetFocusedElement() as Control ?? throw new InvalidOperationException("Capture has no focused control.");
                KeyGesture conflict = CommandBindings.Gesture("navigation.rules")!;
                MainWindow.WorkspacePage page = window.SelectedPage;
                Press(input, conflict);
                await Program.WaitUntilAsync(() => capture.Gesture?.Key == conflict.Key && capture.Gesture.KeyModifiers == conflict.KeyModifiers);
                Require(window.SelectedPage == page && editor.Draft.Raw.GetRawText() == beforeCapture,
                    "Recording dispatched navigation or mutated the draft before Apply recording.");
                Require(AutomationProperties.GetName(capture) == L("Record shortcut", "Tastenkürzel aufnehmen"), "Capture accessibility label is stale.");
                await CaptureWindowAsync(shortcuts, cell, "capture", input);
                Button apply = Action(table, "Apply recording", "Aufnahme übernehmen");
                await RevealFocusAsync(shortcuts, apply); Click(apply);
                await Program.WaitUntilAsync(() => editor.IsDirty && !ShortcutService.IsCapturing);
                var issues = EffectiveShortcutBindings.Build(editor.Draft).Issues;
                Require(issues.Any(issue => issue.BlocksSave && (issue.CommandId == "navigation.archive" || issue.OtherCommandId == "navigation.archive")),
                    "Applying the recorded Rules gesture did not expose the real conflict.");
                Require(table.Model.Rows.Single(row => row.CommandId == "navigation.archive").HasBlockingConflict,
                    "The conflict row is not marked in the table.");
                string conflictRaw = editor.Draft.Raw.GetRawText();
                long beforeRejectedSave = ShortcutService.Revision;
                Button save = table.SaveButton;
                await RevealFocusAsync(shortcuts, save); Click(save);
                await LayoutAsync();
                Require(ShortcutService.Revision == beforeRejectedSave && CommandBindings.Gesture("navigation.archive")?.Key == Key.K
                    && editor.Draft.Raw.GetRawText() == conflictRaw && window.SettingsEditor.DownloadPath == rawPath,
                    "Conflict Save published a binding or lost the shortcut/path draft.");
                TextBlock error = table.ErrorText;
                Require(error.Text?.Contains(L("Conflicting actions", "Kollidierende Aktionen"), StringComparison.Ordinal) == true,
                    "Conflict error is absent from the shortcuts window.");
                await Program.WaitUntilAsync(() => ReferenceEquals(shortcuts.FocusManager?.GetFocusedElement(), error) && Visible(shortcuts, error));
                Program.CaptureWindow(shortcuts, $"smoke-shortcuts-{cell}-conflict.png");
                Write("scene", new { cell, scene = "conflict", language = Texts.Language, theme = shortcuts.ActualThemeVariant.ToString(),
                    scale = shortcuts.RenderScaling, client = shortcuts.ClientSize.ToString(), focus = error.GetType().Name,
                    conflicts = issues.Count, committed = CommandCatalog.ShortcutDisplay("navigation.archive"), rawDraftRetained = true,
                    card = editor.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "SettingsShortcutsErrors")?.Text });
                // The draft stays with Settings (same session) when the window closes without the question.
                shortcuts.CloseWithoutAsking();
                await Program.WaitUntilAsync(() => ShortcutsWindow.For(window) == null);
                Require(editor.Draft.Raw.GetRawText() == conflictRaw, "Closing the window dropped the shared Settings draft.");
                previousDraft = conflictRaw; previousPath = rawPath;
                Require(window.FindControl<TextBox>("UrlInput")!.Text == rawUrl, "Help/editor path lost raw URL input.");
                completed++;
                Write("cell-complete", new { cell, completed });
            }
            Require(completed == 12, "The presentation matrix did not complete all 12 cells.");
            Write("complete", new { completed, scenes = completed * 4, scale = window.RenderScaling });
            Console.WriteLine("SMOKE shortcut presentation: 12 EN/DE × Light/Dark × B/S/A minimum cells; real remap/F1 window/Settings-opened window/capture/conflict, visible focus and shared raw-draft preservation passed.");
        }
        finally
        {
            ShortcutsWindow.For(window)?.CloseWithoutAsking();
            window.SettingsEditor.CancelChanges();
            await window.SessionHost.ApplySettingsAsync(original);
            Texts.SetLanguage(originalLanguage);
            window.FindControl<TextBox>("UrlInput")!.Text = originalUrl;
            window.Width = originalSize.Width; window.Height = originalSize.Height;
        }

        ShortcutEditor CurrentEditor() => window.SettingsEditor.FindControl<ShortcutEditor>("ShortcutsEditor")
            ?? throw new InvalidOperationException("The production Settings shortcut editor is missing from its namescope.");
        bool Materialized(Control control) => control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && ReferenceEquals(TopLevel.GetTopLevel(control), window) && (ReferenceEquals(control, editor) || editor.GetVisualDescendants().Contains(control));
        static Button Action(Control owner, string en, string de) => owner.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == L(en, de));
        void Write(string kind, object data) => File.AppendAllText(output, JsonSerializer.Serialize(new { kind, data }, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }) + Environment.NewLine);
        async Task CaptureWindowAsync(Window shown, string cell, string scene, Control focus)
        {
            await LayoutAsync();
            focus.Focus();
            await Program.WaitUntilAsync(() => focus.IsKeyboardFocusWithin && Visible(shown, focus));
            Program.CaptureWindow(shown, $"smoke-shortcuts-{cell}-{scene}.png");
            Write("scene", new { cell, scene, language = Texts.Language, theme = shown.ActualThemeVariant.ToString(),
                scale = shown.RenderScaling, client = shown.ClientSize.ToString(), focus = focus.GetType().Name,
                name = AutomationProperties.GetName(focus), help = AutomationProperties.GetHelpText(focus) });
        }
        async Task RevealFocusAsync(Window shown, Control target)
        {
            target.BringIntoView(); await LayoutAsync(); target.Focus();
            await Program.WaitUntilAsync(() => target.IsKeyboardFocusWithin && Visible(shown, target));
        }
    }

    private static string L(string en, string de) => Texts.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? de : en;
    private static async Task LayoutAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static KeyEventArgs Press(Control source, KeyGesture gesture)
    {
        var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers };
        source.RaiseEvent(e); return e;
    }
    private static object Geometry(Window window, Control target) => new
    {
        type = target.GetType().Name, target.Name, target.Bounds,
        originInWindow = target.TranslatePoint(default, window), target.IsMeasureValid, target.IsArrangeValid,
        target.IsEffectivelyVisible, target.IsEffectivelyEnabled, target.IsKeyboardFocusWithin,
        sameOwner = ReferenceEquals(TopLevel.GetTopLevel(target), window), visibleGate = Visible(window, target),
        ancestors = target.GetVisualAncestors().OfType<Control>().Select(control => new
        {
            type = control.GetType().Name, control.Name, control.Bounds,
            originInWindow = control.TranslatePoint(default, window), control.ClipToBounds, clip = control.Clip?.Bounds,
            control.IsMeasureValid, control.IsArrangeValid,
            offset = control is ScrollViewer scroll ? (Vector?)scroll.Offset
                : control is ScrollContentPresenter presenter ? presenter.Offset : null,
            viewport = control is ScrollViewer viewer ? (Size?)viewer.Viewport
                : control is ScrollContentPresenter content ? content.Viewport : null,
            extent = control is ScrollViewer owner ? (Size?)owner.Extent
                : control is ScrollContentPresenter host ? host.Extent : null
        }).ToArray()
    };
    private static bool Visible(Window window, Control target)
    {
        if (!target.IsEffectivelyVisible || target.Bounds.Width <= 0 || target.Bounds.Height <= 0
            || target.TranslatePoint(default, window) is not Point point) return false;
        Rect bounds = new(point, target.Bounds.Size);
        Rect viewport = new(default(Point), window.ClientSize);
        foreach (ScrollViewer scroll in target.GetVisualAncestors().OfType<ScrollViewer>())
            if (scroll.TranslatePoint(default, window) is Point origin) viewport = viewport.Intersect(new Rect(origin, scroll.Bounds.Size));
        return bounds.Left >= viewport.Left - 1 && bounds.Top >= viewport.Top - 1
            && bounds.Right <= viewport.Right + 1 && bounds.Bottom <= viewport.Bottom + 1;
    }
    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
}
