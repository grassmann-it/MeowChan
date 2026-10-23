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
                Require(window.FindControl<TextBox>("UrlInput")!.Text == rawUrl, "Appearance transition lost raw URL input.");
                // Explicit fixture reset between independent cells. No Save or live binding mutation.
                window.SettingsEditor.CancelChanges();
                window.SettingsEditor.DownloadPath = rawPath;
                string cell = $"{language}-{theme.ToLowerInvariant()}-{mode.ToLowerInvariant()}";
                Write("cell-start", new { cell, mode, language, theme, client = window.ClientSize.ToString(),
                    scale = window.RenderScaling, width, height, initialGesture = CommandCatalog.ShortcutDisplay("navigation.archive") });

                // Help is opened through the actual action and must show the current remap.
                Press(window, CommandBindings.Gesture("viewer.shortcut-help")!);
                Border help = window.FindControl<Border>("ShellHelpOverlay")!;
                await Program.WaitUntilAsync(() => help.IsVisible);
                string helpText = window.FindControl<TextBlock>("ShellHelpText")!.Text ?? string.Empty;
                Require(helpText.Contains(CommandCatalog.Label("navigation.archive"), StringComparison.Ordinal)
                    && helpText.Contains(CommandCatalog.ShortcutDisplay("navigation.archive")!, StringComparison.Ordinal),
                    "Help does not show the translated action and its committed replacement.");
                await CaptureAsync(cell, "help", window.FindControl<Button>("ShellHelpClose")!);
                Press(window, new KeyGesture(Key.Escape));
                Require(!help.IsVisible, "Help Escape failed to dismiss exactly the help surface.");

                window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                await Program.WaitUntilAsync(() => window.SettingsPageVisible);
                editor = CurrentEditor();
                // Page selection precedes visual materialization. Resolve the actual
                // content controls without using translated presentation text as identity.
                Panel content = editor.Content as Panel
                    ?? throw new InvalidOperationException("The production shortcut editor content is missing.");
                TextBox search = content.Children.OfType<TextBox>().Single();
                ListBox actions = content.Children.OfType<ListBox>().Single();
                await Program.WaitUntilAsync(() => window.SettingsEditor.CanOpenSectionNavigation
                    && ReferenceEquals(CurrentEditor(), editor) && ReferenceEquals(TopLevel.GetTopLevel(editor), window)
                    && window.SettingsEditor.GetVisualDescendants().Contains(editor)
                    && Materialized(editor) && Materialized(search) && Materialized(actions));
                window.SettingsEditor.OpenSectionNavigation();
                Require(window.SettingsEditor.CanGoToSection("settings.shortcuts"), "The real shortcut section is unavailable.");
                window.SettingsEditor.GoToSection("settings.shortcuts");
                await Program.WaitUntilAsync(() => editor.SectionHeading.IsFocused && Visible(window, editor.SectionHeading));
                string searchLabel = L("Search action, context or shortcut", "Aktion, Bereich oder Kürzel suchen");
                Require(search.PlaceholderText?.ToString() == searchLabel && AutomationProperties.GetName(search) == searchLabel,
                    "The materialized shortcut search label is stale.");
                Write("editor-ready", new { cell, section = window.SettingsEditor.SelectedSectionId,
                    headingFocused = editor.SectionHeading.IsFocused, editor = editor.Bounds.ToString(),
                    search = search.Bounds.ToString(), actions = actions.Bounds.ToString(), language = Texts.Language });
                search.Text = "navigation.archive";
                actions.SelectedItem = actions.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "navigation.archive");
                await RevealFocusAsync(search);
                Require(((TextBlock)actions.Items.OfType<ListBoxItem>().Single().Content!).Text!
                    .Contains(CommandCatalog.Label("navigation.archive"), StringComparison.Ordinal), "Search row kept an old language label.");
                Require(((TextBlock)actions.Items.OfType<ListBoxItem>().Single().Content!).Text!
                    .Contains(CommandCatalog.ShortcutDisplay("navigation.archive")!, StringComparison.Ordinal), "Editor row omitted the committed remap.");
                await CaptureAsync(cell, "editor", search);
                string beforeCapture = editor.Draft.Raw.GetRawText();
                Button record = Action("Record additional shortcut", "Weiteres Kürzel aufnehmen");
                await RevealFocusAsync(record); Click(record);
                KeyGestureInput capture = editor.GetVisualDescendants().OfType<KeyGestureInput>().Single();
                capture.BringIntoView();
                await LayoutAsync();
                Require(ShortcutService.IsCapturing && capture.IsKeyboardFocusWithin, "Recording did not acquire real capture focus/suppression.");
                var input = window.FocusManager?.GetFocusedElement() as Control ?? throw new InvalidOperationException("Capture has no focused control.");
                KeyGesture conflict = CommandBindings.Gesture("navigation.rules")!;
                MainWindow.WorkspacePage page = window.SelectedPage;
                Press(input, conflict);
                await Program.WaitUntilAsync(() => capture.Gesture?.Key == conflict.Key && capture.Gesture.KeyModifiers == conflict.KeyModifiers);
                Require(window.SelectedPage == page && editor.Draft.Raw.GetRawText() == beforeCapture,
                    "Recording dispatched navigation or mutated the draft before Apply recording.");
                Require(AutomationProperties.GetName(capture) == L("Record shortcut", "Tastenkürzel aufnehmen"), "Capture accessibility label is stale.");
                await CaptureAsync(cell, "capture", input);
                Button apply = Action("Apply recording", "Aufnahme übernehmen");
                await RevealFocusAsync(apply); Click(apply);
                await Program.WaitUntilAsync(() => editor.IsDirty && !ShortcutService.IsCapturing);
                var issues = EffectiveShortcutBindings.Build(editor.Draft).Issues;
                Require(issues.Any(issue => issue.BlocksSave && (issue.CommandId == "navigation.archive" || issue.OtherCommandId == "navigation.archive")),
                    "Applying the recorded Rules gesture did not expose the real conflict.");
                string conflictRaw = editor.Draft.Raw.GetRawText();
                long beforeRejectedSave = ShortcutService.Revision;
                Button save = window.SettingsEditor.FindControl<Button>("SaveSettingsButton")!;
                await RevealFocusAsync(save); Click(save);
                await Program.WaitUntilAsync(() => !window.SettingsEditor.IsBusy);
                await LayoutAsync();
                Require(ShortcutService.Revision == beforeRejectedSave && CommandBindings.Gesture("navigation.archive")?.Key == Key.K
                    && editor.Draft.Raw.GetRawText() == conflictRaw && window.SettingsEditor.DownloadPath == rawPath,
                    "Conflict Save published a binding or lost the shortcut/path draft.");
                TextBlock error = editor.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.IsEffectivelyVisible
                    && text.Text?.Contains("Kollidierende Aktionen", StringComparison.Ordinal) == true)
                    ?? throw new InvalidOperationException("Conflict error is absent from the real editor.");
                // BringIntoView changes offsets; a Loaded callback alone does not
                // establish the final arranged viewport. Observe the existing gate
                // without repairing focus, forcing layout or scrolling from the test.
                TraceConflictGeometry("before-wait");
                try
                {
                    await Program.WaitUntilAsync(() => ReferenceEquals(CurrentEditor(), editor)
                        && ReferenceEquals(TopLevel.GetTopLevel(error), window)
                        && ReferenceEquals(window.FocusManager?.GetFocusedElement(), error)
                        && editor.IsKeyboardFocusWithin && Visible(window, error));
                }
                catch (TimeoutException)
                {
                    TraceConflictGeometry("timeout");
                    try { Program.CaptureWindow(window, $"smoke-shortcuts-{cell}-conflict.png"); }
                    catch (Exception diagnosticError) { Console.WriteLine("SMOKE conflict timeout screenshot failed: " + diagnosticError.Message); }
                    throw;
                }
                TraceConflictGeometry("ready");
                Program.CaptureWindow(window, $"smoke-shortcuts-{cell}-conflict.png");
                Control focused = window.FocusManager?.GetFocusedElement() as Control ?? throw new InvalidOperationException("Conflict Save left no focus.");
                Require(editor.IsKeyboardFocusWithin && Visible(window, focused) && Visible(window, error),
                    "Conflict validation must expose the error and visible editor focus; actual focus: " + focused.GetType().Name);
                Write("scene", new { cell, scene = "conflict", language = Texts.Language, theme = window.ActualThemeVariant.ToString(),
                    scale = window.RenderScaling, client = window.ClientSize.ToString(), focus = focused.GetType().Name,
                    conflicts = issues.Count, committed = CommandCatalog.ShortcutDisplay("navigation.archive"), rawDraftRetained = true });
                void TraceConflictGeometry(string phase)
                {
                    try
                    {
                        Control? currentFocus = window.FocusManager?.GetFocusedElement() as Control;
                        Write("conflict-geometry", new
                        {
                            cell, phase, busy = window.SettingsEditor.IsBusy,
                            sameEditor = ReferenceEquals(CurrentEditor(), editor), editorFocused = editor.IsKeyboardFocusWithin,
                            focusIsError = ReferenceEquals(currentFocus, error), errorInEditor = editor.GetVisualDescendants().Contains(error),
                            editor = Geometry(window, editor), error = Geometry(window, error),
                            focus = currentFocus == null ? null : Geometry(window, currentFocus)
                        });
                    }
                    catch (Exception diagnosticError) { Console.WriteLine("SMOKE conflict geometry diagnostic failed: " + diagnosticError.Message); }
                }
                previousDraft = conflictRaw; previousPath = rawPath;
                Require(window.FindControl<TextBox>("UrlInput")!.Text == rawUrl, "Help/editor path lost raw URL input.");
                completed++;
                Write("cell-complete", new { cell, completed });
            }
            Require(completed == 12, "The presentation matrix did not complete all 12 cells.");
            Write("complete", new { completed, scenes = completed * 4, scale = window.RenderScaling });
            Console.WriteLine("SMOKE shortcut presentation: 12 EN/DE × Light/Dark × B/S/A minimum cells; real remap/help/editor/capture/conflict, visible focus and raw-draft preservation passed.");
        }
        finally
        {
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
        Button Action(string en, string de) => editor.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == L(en, de));
        void Write(string kind, object data) => File.AppendAllText(output, JsonSerializer.Serialize(new { kind, data }, new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }) + Environment.NewLine);
        async Task CaptureAsync(string cell, string scene, Control focus)
        {
            await LayoutAsync();
            Program.CaptureWindow(window, $"smoke-shortcuts-{cell}-{scene}.png");
            Require(focus.IsKeyboardFocusWithin && Visible(window, focus), scene + " lacks visible real keyboard focus.");
            Write("scene", new { cell, scene, language = Texts.Language, theme = window.ActualThemeVariant.ToString(),
                scale = window.RenderScaling, client = window.ClientSize.ToString(), focus = focus.GetType().Name,
                name = AutomationProperties.GetName(focus), help = AutomationProperties.GetHelpText(focus) });
        }
        async Task RevealFocusAsync(Control target)
        {
            target.BringIntoView(); await LayoutAsync(); target.Focus();
            await Program.WaitUntilAsync(() => target.IsKeyboardFocusWithin && Visible(window, target));
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
