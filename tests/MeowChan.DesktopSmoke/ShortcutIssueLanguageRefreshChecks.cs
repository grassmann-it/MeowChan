using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Testing;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// Root hook (serial, authorized disposable desktop only):
// await ShortcutIssueLanguageRefreshChecks.RunAsync(mainWindow);
// Real shown SettingsWindow/controls and Texts.Changed; managed routed input,
// NOT OS keyboard injection, headless delegate invocation or translation-quality QA.
internal static class ShortcutIssueLanguageRefreshChecks
{
    private const string ActionId = "navigation.archive";
    private static readonly string[] Languages = ["en", "de", "es", "fr", "ru"];
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (OperatingSystem.IsLinux()
            ? Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1"
            : Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") != "1")
            throw new PlatformNotSupportedException("Shortcut issue smoke requires an authorized disposable desktop; never a personal display.");
        Require(owner.IsVisible && !ShortcutService.IsCapturing, "A shown idle owner without capture is required.");
        var templates = ReadTemplates(); // Compiled bundles only; never personal pack directories.
        // Shift+F10 already belongs to menu.context-open and overlaps shell actions.
        // Use an otherwise unassigned chord so this fixture has exactly one pair.
        var conflict = new ShortcutBindingsSection().Set(ActionId, [ShortcutGesture.Parse("Shift+F9")])
            .Set("navigation.rules", [ShortcutGesture.Parse("Shift+F9")]);
        await WithFixtureAsync(owner, "conflict", conflict, (window, editor) =>
            IssuesAsync(window, editor, templates, schema: false));
        using var document = JsonDocument.Parse("""{"SchemaVersion":99,"Overrides":{"future.action":{"opaque":[1,2],"raw":"{0} — 原"}}}""");
        await WithFixtureAsync(owner, "schema", new ShortcutBindingsSection(document.RootElement), (window, editor) =>
            IssuesAsync(window, editor, templates, schema: true));
        await WithFixtureAsync(owner, "pending-invalid", new ShortcutBindingsSection(), (window, editor) =>
            PendingAsync(window, editor, templates));
        // ADDITIONAL negative guards, separate windows AFTER all original 20 cells.
        // Do not replace or relax any of the original matrix assertions.
        await WithFixtureAsync(owner, "viewport-user-search", conflict, (window, editor) =>
            ViewportUserTakeoverAsync(window, editor, templates, startCapture: false));
        await WithFixtureAsync(owner, "viewport-user-capture", conflict, (window, editor) =>
            ViewportUserTakeoverAsync(window, editor, templates, startCapture: true));
        await WithFixtureAsync(owner, "viewport-pending-user-search", new ShortcutBindingsSection(), (window, editor) =>
            PendingUserTakeoverAsync(window, editor, templates));
        Console.WriteLine("SMOKE shortcut issue viewport user takeover: additional error-search/error-capture/pending-search guards passed.");
        Console.WriteLine("SMOKE shortcut issue refresh: 20 EN/DE/ES/FR/RU cells passed (conflict, retained schema, active invalid capture, rejected Apply). Managed input only; no language-quality/native-OS-keyboard claim.");
    }

    private static async Task IssuesAsync(SettingsWindow window, ShortcutEditor editor,
        IReadOnlyDictionary<string, Dictionary<string, string>> templates, bool schema)
    {
        var (search, actions) = SelectAction(editor);
        TextBlock error = Errors(editor);
        var snapshot = EffectiveShortcutBindings.Build(editor.Draft);
        ShortcutIssue issue = snapshot.Issues.Single();
        Require(issue.Reason?.Code == (schema ? "unsupportedSchema" : "conflictingActions")
            && issue.BlocksSave == !schema && error.Focusable == !schema,
            "Fixture must expose exactly the intended warning/conflict and BlocksSave contract.");
        if (schema)
        {
            Require(!editor.Draft.IsEditable && !ActionButton(editor, "resetAll").IsEffectivelyEnabled
                && !ActionButton(editor, "recordAdditional").IsEffectivelyEnabled, "Future schema must remain read-only.");
            Require(Model(snapshot) == Model(EffectiveShortcutBindings.Build(new ShortcutBindingsSection()), includeIssues: false),
                "Future schema did not retain the defaults-only effective/requested bindings.");
            // Warning is nonfocusable. Keep real search focus/caret/selection;
            // reveal the warning separately after the passive invariants.
            await FocusAsync(window, search);
            search.CaretIndex = 3; search.SelectionStart = 1; search.SelectionEnd = 3;
        }
        else
        {
            Require(new[] { issue.CommandId, issue.OtherCommandId }.Order().SequenceEqual(new[] { ActionId, "navigation.rules" }.Order())
                && snapshot.For(ActionId).Count == 0 && snapshot.For("navigation.rules").Count == 0
                && snapshot.RequestedGestures[ActionId].Count == 1 && snapshot.RequestedGestures["navigation.rules"].Count == 1,
                "Conflict must retain both requested bindings and disable both, without choosing an owner.");
            await FocusAsync(window, error);
        }
        var state = new EditorState(window, editor, search, actions);
        string scenario = schema ? "schema" : "conflict";
        state.Trace(window, editor, scenario + ":before-idle");
        await SettleAsync(window);
        state.Trace(window, editor, scenario + ":after-idle");
        state.AssertUnchanged(window, editor); // Negative control: idle must not drift either.
        foreach (string language in Languages)
        {
            state.Trace(window, editor, scenario + ":" + language + ":before-notification");
            Texts.SetLanguage(language);
            state.Trace(window, editor, scenario + ":" + language + ":after-notification");
            await SettleAsync(window);
            state.Trace(window, editor, scenario + ":" + language + ":after-settle");
            AssertCatalog(templates, language);
            state.AssertUnchanged(window, editor);
            Require(!ShortcutService.IsCapturing && error.Focusable == !schema, "Refresh changed capture/validation focusability.");
            AssertIssues(editor, error, snapshot.Issues, templates[language]);
            if (schema)
            {
                Require(!editor.Draft.IsEditable && !ActionButton(editor, "resetAll").IsEffectivelyEnabled
                    && !ActionButton(editor, "recordAdditional").IsEffectivelyEnabled, "Refresh made future schema editable.");
                // Scrolling is explicit presentation inspection AFTER passive
                // focus/draft checks, not a repair of a failed refresh assertion.
                error.BringIntoView();
                await SettleAsync(window);
                Require(Visible(window, error), "Schema warning is not visible in the native viewport.");
                await FocusAsync(window, search);
                state.AssertUnchanged(window, editor);
            }
            else Require(Visible(window, error), "Conflict message left the viewport on language refresh.");
            Console.WriteLine($"SMOKE shortcut issue {(schema ? "schema" : "conflict")} cell passed: {language}");
        }
    }

    private static async Task PendingAsync(SettingsWindow window, ShortcutEditor editor,
        IReadOnlyDictionary<string, Dictionary<string, string>> templates)
    {
        var (search, actions) = SelectAction(editor);
        Click(ActionButton(editor, "clearBinding"));
        await Program.WaitUntilAsync(() => editor.IsDirty);
        Require(EffectiveShortcutBindings.Build(editor.Draft).RequestedGestures[ActionId].Count == 0,
            "Clear must create a real dirty unbound draft before capture.");
        await FocusAsync(window, ActionButton(editor, "recordAdditional"));
        Click(ActionButton(editor, "recordAdditional"));
        KeyGestureInput capture = editor.GetVisualDescendants().OfType<KeyGestureInput>().Single();
        TextBlock pending = PendingLabel(capture);
        capture.BringIntoView();
        await SettleAsync(window);
        await Program.WaitUntilAsync(() => ShortcutService.IsCapturing && capture.IsKeyboardFocusWithin && Visible(window, capture));
        Control focused = window.FocusManager?.GetFocusedElement() as Control
            ?? throw new InvalidOperationException("Real capture focus missing.");
        // Managed event only. Never inject Alt+F4 into the OS/native desktop.
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F4, KeyModifiers = KeyModifiers.Alt });
        await Program.WaitUntilAsync(() => capture.Gesture is { Key: Key.F4, KeyModifiers: KeyModifiers.Alt });
        Require(pending.Text == Expected(templates[Texts.Language], "reservedSystem"), "Capture did not produce the reserved-system reason.");
        pending.BringIntoView();
        await SettleAsync(window);
        Require(Visible(window, capture) && Visible(window, pending) && capture.IsKeyboardFocusWithin,
            "Invalid capture and pending reason must be simultaneously visible before the passive refresh.");
        KeyGesture originalGesture = capture.Gesture!;
        var activeState = new EditorState(window, editor, search, actions);
        await RefreshPendingAsync(activeState, active: true);

        // Apply only the original invalid recording: no recapture, replacement key,
        // checkbox toggle, Load, private-field write or draft reset after refresh.
        Button apply = ActionButton(editor, "applyRecording");
        Click(apply);
        await SettleAsync(window);
        Require(!ShortcutService.IsCapturing && !capture.IsEffectivelyEnabled
            && editor.Draft.Raw.GetRawText() == activeState.Raw && editor.IsDirty
            && ReferenceEquals(capture.Gesture, originalGesture), "Rejected Apply changed the draft/gesture or retained capture routing suppression.");
        await FocusAsync(window, apply); // New explicit setup for the independent post-Apply journey.
        pending.BringIntoView();
        await SettleAsync(window);
        Require(Visible(window, apply) && Visible(window, pending), "Rejected-Apply fixture is not visible.");
        var appliedState = new EditorState(window, editor, search, actions);
        await RefreshPendingAsync(appliedState, active: false);

        async Task RefreshPendingAsync(EditorState state, bool active)
        {
            string scenario = active ? "pending-active" : "pending-after-rejected-apply";
            state.Trace(window, editor, scenario + ":before-idle");
            await SettleAsync(window);
            state.Trace(window, editor, scenario + ":after-idle");
            state.AssertUnchanged(window, editor); // No language notification yet.
            foreach (string language in Languages)
            {
                state.Trace(window, editor, scenario + ":" + language + ":before-notification");
                Texts.SetLanguage(language);
                state.Trace(window, editor, scenario + ":" + language + ":after-notification");
                await SettleAsync(window);
                state.Trace(window, editor, scenario + ":" + language + ":after-settle");
                AssertCatalog(templates, language);
                state.AssertUnchanged(window, editor);
                Require(ReferenceEquals(editor.GetVisualDescendants().OfType<KeyGestureInput>().Single(), capture)
                    && ReferenceEquals(PendingLabel(capture), pending) && ReferenceEquals(capture.Gesture, originalGesture)
                    && ShortcutService.IsCapturing == active && capture.IsEffectivelyEnabled == active,
                    "Refresh replaced capture/pending controls, changed the invalid gesture or capture lease state.");
                // Passive split of the unchanged compound oracle; no raw text or extra geometry dump.
                Console.WriteLine("SMOKE shortcut pending gate: " + JsonSerializer.Serialize(new
                {
                    Language = language, Active = active,
                    TextMatches = pending.Text == Expected(templates[language], "reservedSystem"),
                    PendingVisible = Visible(window, pending), CaptureVisible = Visible(window, capture)
                }));
                Require(pending.Text == Expected(templates[language], "reservedSystem") && Visible(window, pending),
                    language + ": pending invalid reason stayed frozen or is not visible.");
                Require(AutomationProperties.GetName(capture) == T("settings.shortcutsEditor.record"),
                    language + ": live capture accessible name stayed stale.");
                if (active) Require(capture.IsKeyboardFocusWithin && Visible(window, capture), "Active invalid capture lost visible focus.");
                Console.WriteLine($"SMOKE shortcut issue pending {(active ? "active" : "after-rejected-apply")} cell passed: {language}");
            }
        }
    }

    // Real UI regression for a stale queued language-relayout reveal. The positive
    // 20-cell matrix above remains the prerequisite; this is not a lexical test.
    private static async Task ViewportUserTakeoverAsync(SettingsWindow window, ShortcutEditor editor,
        IReadOnlyDictionary<string, Dictionary<string, string>> templates, bool startCapture)
    {
        // Same FR -> RU boundary as the observed displacement; finish setup
        // before establishing error focus and the no-yield takeover boundary.
        Texts.SetLanguage("fr");
        await SettleAsync(window);
        var (search, actions) = SelectAction(editor);
        TextBlock error = Errors(editor);
        await FocusAsync(window, error);
        Require(error.IsFocused && Visible(window, error) && window.IsActive && !ShortcutService.IsCapturing,
            "Takeover fixture needs an already visible, focused validation error without capture.");
        var stateBefore = new EditorState(window, editor, search, actions);
        await SettleAsync(window);
        stateBefore.AssertUnchanged(window, editor);
        string raw = editor.Draft.Raw.GetRawText();
        bool dirty = editor.IsDirty;

        // No await/dispatcher pump until AFTER explicit user takeover and its
        // baseline. UpdateLayout is synchronous: the posted product callback must
        // remain pending while the actual control receives new user focus.
        Texts.SetLanguage("ru");
        window.UpdateLayout();
        Require(error.IsFocused && ReferenceEquals(window.FocusManager?.GetFocusedElement(), error),
            "Language notification itself unexpectedly changed validation focus before takeover.");
        KeyGestureInput capture = editor.GetVisualDescendants().OfType<KeyGestureInput>().Single();
        if (startCapture)
        {
            Button record = ActionButton(editor, "recordAdditional");
            record.BringIntoView();
            Require(record.Focus(), "Explicit user focus on Record was rejected.");
            Click(record);
            capture.BringIntoView();
            Require(capture.IsKeyboardFocusWithin && ShortcutService.IsCapturing, "Explicit user recording did not acquire capture.");
            Control captureFocus = window.FocusManager?.GetFocusedElement() as Control
                ?? throw new InvalidOperationException("User recording has no real focused control.");
            captureFocus.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F8, KeyModifiers = KeyModifiers.Shift });
            Require(capture.Gesture is { Key: Key.F8, KeyModifiers: KeyModifiers.Shift }, "Real user capture did not retain Shift+F8.");
        }
        else
        {
            search.BringIntoView();
            Require(search.Focus(), "Explicit user search focus was rejected.");
            search.CaretIndex = 3; search.SelectionStart = 1; search.SelectionEnd = 3;
        }
        window.UpdateLayout();
        Require(!error.IsFocused && !ReferenceEquals(window.FocusManager?.GetFocusedElement(), error),
            "Takeover must actually leave the previously focused error.");
        var userState = new EditorState(window, editor, search, actions);
        ScrollViewer[] scrolls = error.GetVisualAncestors().OfType<ScrollViewer>().ToArray();
        Vector[] offsets = scrolls.Select(scroll => scroll.Offset).ToArray();
        Require(scrolls.Length > 0, "Native takeover needs a real scroll viewport, not a vacuous offset check.");
        KeyGesture? pending = capture.Gesture;
        TextBlock pendingLabel = PendingLabel(capture);
        string? pendingText = pendingLabel.Text;

        // First yield after notification: now the product's queued work runs.
        // NO refocus, BringIntoView, recapture or input repair after this boundary.
        await SettleAsync(window);
        userState.AssertUnchanged(window, editor);
        AssertCatalog(templates, "ru");
        var currentScrolls = error.GetVisualAncestors().OfType<ScrollViewer>().ToArray();
        Require(currentScrolls.Length == scrolls.Length && currentScrolls.Select((scroll, index) =>
            ReferenceEquals(scroll, scrolls[index]) && scroll.Offset == offsets[index]).All(same => same),
            "Stale validation relayout reveal changed the user's scroll viewport after takeover.");
        Require(editor.Draft.Raw.GetRawText() == raw && editor.IsDirty == dirty
            && ShortcutService.IsCapturing == startCapture && capture.IsEffectivelyEnabled == startCapture
            && ReferenceEquals(capture.Gesture, pending) && pendingLabel.Text == pendingText,
            "Stale validation relayout reveal changed user capture/pending/draft state.");
        Require(!error.IsFocused && (startCapture ? capture.IsKeyboardFocusWithin : search.IsFocused),
            "Stale validation relayout reveal reclaimed user search/capture focus.");
        Console.WriteLine("SMOKE shortcut issue additional viewport user takeover passed: " + (startCapture ? "capture" : "search"));
    }

    // ADDITIONAL native negative guard for the active-invalid-pending reveal path.
    private static async Task PendingUserTakeoverAsync(SettingsWindow window, ShortcutEditor editor,
        IReadOnlyDictionary<string, Dictionary<string, string>> templates)
    {
        Texts.SetLanguage("fr");
        await SettleAsync(window);
        var (search, actions) = SelectAction(editor);
        Button record = ActionButton(editor, "recordAdditional");
        await FocusAsync(window, record);
        Click(record);
        KeyGestureInput capture = editor.GetVisualDescendants().OfType<KeyGestureInput>().Single();
        await Program.WaitUntilAsync(() => ShortcutService.IsCapturing && capture.IsKeyboardFocusWithin);
        Control focus = window.FocusManager?.GetFocusedElement() as Control
            ?? throw new InvalidOperationException("Pending takeover setup has no capture focus.");
        focus.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F4, KeyModifiers = KeyModifiers.Alt });
        TextBlock pending = PendingLabel(capture);
        pending.BringIntoView();
        await SettleAsync(window);
        Require(pending.Text == Expected(templates["fr"], "reservedSystem")
            && capture.Gesture is { Key: Key.F4, KeyModifiers: KeyModifiers.Alt }
            && Visible(window, pending) && Visible(window, capture) && capture.IsKeyboardFocusWithin
            && ShortcutService.IsCapturing, "Pending takeover requires a visible active invalid recording.");
        var before = new EditorState(window, editor, search, actions);
        await SettleAsync(window);
        before.AssertUnchanged(window, editor);
        KeyGesture? gesture = capture.Gesture;
        bool dirty = editor.IsDirty;

        // No yield until the new user's Search focus and scroll baseline exist.
        Texts.SetLanguage("ru");
        window.UpdateLayout();
        Require(ReferenceEquals(window.FocusManager?.GetFocusedElement(), focus) && ShortcutService.IsCapturing,
            "Pending notification unexpectedly changed capture ownership before takeover.");
        search.BringIntoView();
        Require(search.Focus(), "Pending takeover user Search focus was rejected.");
        search.CaretIndex = 3; search.SelectionStart = 1; search.SelectionEnd = 3;
        window.UpdateLayout();
        Require(!ShortcutService.IsCapturing && !capture.IsEffectivelyEnabled,
            "Leaving capture must release its lease before the queued reveal.");
        var userState = new EditorState(window, editor, search, actions);
        ScrollViewer[] scrolls = pending.GetVisualAncestors().OfType<ScrollViewer>().ToArray();
        Vector[] offsets = scrolls.Select(scroll => scroll.Offset).ToArray();
        Require(scrolls.Length > 0, "Pending takeover needs a real scroll viewport.");

        // No focus/scroll/input repair after dispatching the stale continuation.
        await SettleAsync(window);
        userState.AssertUnchanged(window, editor);
        AssertCatalog(templates, "ru");
        var currentScrolls = pending.GetVisualAncestors().OfType<ScrollViewer>().ToArray();
        Require(currentScrolls.Length == scrolls.Length && currentScrolls.Select((scroll, index) =>
            ReferenceEquals(scroll, scrolls[index]) && scroll.Offset == offsets[index]).All(same => same),
            "Stale pending reveal changed the user's Search scroll viewport.");
        Require(search.IsFocused && !capture.IsKeyboardFocusWithin && !ShortcutService.IsCapturing
            && !capture.IsEffectivelyEnabled && ReferenceEquals(capture.Gesture, gesture)
            && pending.Text == Expected(templates["ru"], "reservedSystem")
            && editor.Draft.Raw.GetRawText() == before.Raw && editor.IsDirty == dirty,
            "Stale pending reveal reclaimed capture or changed pending/draft state.");
        Console.WriteLine("SMOKE shortcut issue additional viewport pending-to-search takeover passed.");
    }

    private static async Task WithFixtureAsync(MainWindow owner, string scenario, ShortcutBindingsSection bindings,
        Func<SettingsWindow, ShortcutEditor, Task> journey)
    {
        Require(!ShortcutService.IsCapturing, "Fixture must not inherit a capture lease.");
        string language = Texts.Language;
        long revision = ShortcutService.Revision;
        EffectiveShortcutBindings live = ShortcutService.Current;
        var page = owner.SelectedPage;
        string profile = TemporaryWorkspacePaths.CreateDirectory("meowchan-shortcut-issue-" + scenario + "-");
        SettingsWindow? window = null;
        Task? dialog = null;
        try
        {
            Texts.SetLanguage("de"); // First matrix EN is also a real Changed event.
            Config config = Config.LoadConfig(profile);
            config.Language = "de";
            config.WorkspaceMode = "Advanced";
            config.DownloadPath = Path.Combine(profile, "synthetic downloads");
            config.AutomaticUpdateChecks = config.MonitorClipboardUrls = config.CloseToTray = config.MinimizeToTray = false;
            config.ShortcutBindings = bindings.Clone();
            config.SaveConfig(profile); // Future execution: newly owned synthetic profile only.
            string baseline = JsonSerializer.Serialize(config, ConfigJsonContext.Default.Config);
            string[] files = Files(profile);
            int commits = 0;
            window = new SettingsWindow(config, _ =>
            {
                commits++;
                throw new InvalidOperationException("Shortcut issue presentation helper must never commit settings.");
            });
            dialog = window.ShowDialog(owner);
            await Program.WaitUntilAsync(() => window.IsVisible);
            window.Activate();
            await Program.WaitUntilAsync(() => window.IsActive);
            await SettleAsync(window);
            window.Editor.OpenSectionNavigation();
            Require(window.Editor.CanGoToSection("settings.shortcuts"), "Real shortcut section unavailable.");
            window.Editor.GoToSection("settings.shortcuts");
            ShortcutEditor editor = window.Editor.FindControl<ShortcutEditor>("ShortcutsEditor")
                ?? throw new InvalidOperationException("Real ShortcutEditor missing.");
            await Program.WaitUntilAsync(() => editor.IsEffectivelyVisible && editor.SectionHeading.IsFocused
                && editor.Bounds.Width > 0 && ReferenceEquals(TopLevel.GetTopLevel(editor), window));
            await journey(window, editor);
            Require(commits == 0 && baseline == JsonSerializer.Serialize(config, ConfigJsonContext.Default.Config)
                && Files(profile).SequenceEqual(files), "Language/capture journey committed settings or changed fixture files.");
            Require(ShortcutService.Revision == revision && ReferenceEquals(ShortcutService.Current, live) && owner.SelectedPage == page,
                "Language/capture journey published bindings or dispatched owner navigation.");
        }
        finally
        {
            try
            {
                if (window?.IsVisible == true) window.Close();
                if (dialog != null) await dialog.WaitAsync(Deadline);
            }
            finally
            {
                Texts.SetLanguage(language);
                await SettleAsync();
                owner.Activate();
                Directory.Delete(profile, recursive: true); // Only this helper's newly created tree.
            }
        }
        Require(!ShortcutService.IsCapturing, "Closing the synthetic window leaked capture routing suppression.");
    }

    private sealed class EditorState
    {
        private readonly ShortcutBindingsSection _draft;
        private readonly bool _dirty;
        private readonly string _model, _settings;
        private readonly TextBox _search;
        private readonly ListBox _actions;
        private readonly object? _items, _selected;
        private readonly (string? Text, int Caret, int Start, int End) _input;
        private readonly Control _focus;
        private readonly Panel _detail;
        private readonly Control[] _children;
        private readonly KeyGestureInput _capture;
        private readonly TextBlock _error, _pendingLabel;
        private string _phase = "fixture-created";
        private readonly long _revision = ShortcutService.Revision;
        private readonly EffectiveShortcutBindings _live = ShortcutService.Current;
        internal string Raw { get; }
        internal EditorState(SettingsWindow window, ShortcutEditor editor, TextBox search, ListBox actions)
        {
            Dispatcher.UIThread.VerifyAccess();
            _draft = editor.Draft; Raw = _draft.Raw.GetRawText(); _dirty = editor.IsDirty;
            _model = Model(EffectiveShortcutBindings.Build(_draft), includeIssues: true);
            _settings = SettingsState(window.Editor);
            _search = search; _actions = actions; _items = actions.ItemsSource; _selected = actions.SelectedItem;
            _input = Input(search);
            _focus = window.FocusManager?.GetFocusedElement() as Control ?? throw new InvalidOperationException("Fixture has no real focus.");
            _capture = editor.GetVisualDescendants().OfType<KeyGestureInput>().Single();
            _detail = _capture.Parent as Panel ?? throw new InvalidOperationException("Real detail panel missing.");
            _error = Errors(editor);
            _pendingLabel = PendingLabel(_capture);
            _children = _detail.Children.ToArray();
            AssertUnchanged(window, editor);
        }
        internal void AssertUnchanged(SettingsWindow window, ShortcutEditor editor)
        {
            Dispatcher.UIThread.VerifyAccess();
            Require(ReferenceEquals(window.Editor.FindControl<ShortcutEditor>("ShortcutsEditor"), editor)
                && ReferenceEquals(editor.Draft, _draft) && editor.Draft.Raw.GetRawText() == Raw && editor.IsDirty == _dirty
                && Model(EffectiveShortcutBindings.Build(editor.Draft), includeIssues: true) == _model
                && SettingsState(window.Editor) == _settings, "Refresh changed editor/draft/schema/reasons/raw args/ownership/settings state.");
            Require(ReferenceEquals(_actions.ItemsSource, _items) && ReferenceEquals(_actions.SelectedItem, _selected)
                && (_selected as ListBoxItem)?.Tag as string == ActionId && Input(_search) == _input
                && _detail.Children.Count == _children.Length
                && _children.Select((child, i) => ReferenceEquals(child, _detail.Children[i])).All(same => same),
                "Refresh rebuilt rows/detail, changed selected command or search text/caret/selection.");
            if (!(window.IsActive && ReferenceEquals(window.FocusManager?.GetFocusedElement(), _focus)
                && _focus.IsFocused && Visible(window, _focus)))
                Trace(window, editor, _phase + ":focus-assertion-failed");
            Require(window.IsActive && ReferenceEquals(window.FocusManager?.GetFocusedElement(), _focus)
                && _focus.IsFocused && Visible(window, _focus), "Refresh lost/detached/stole visible native focus.");
            Require(ShortcutService.Revision == _revision && ReferenceEquals(ShortcutService.Current, _live), "Refresh published live bindings.");
        }

        // Passive bounded snapshots only: no focus, scroll, layout, dispatcher work,
        // subscriptions, retries, screenshots or catch-to-pass in diagnostics.
        internal void Trace(SettingsWindow window, ShortcutEditor editor, string phase)
        {
            Dispatcher.UIThread.VerifyAccess();
            _phase = phase;
            var current = window.FocusManager?.GetFocusedElement();
            Console.WriteLine("SMOKE shortcut issue focus geometry: " + JsonSerializer.Serialize(new
            {
                Phase = phase, Language = Texts.Language,
                Window = new { window.IsActive, window.IsVisible, window.IsEnabled,
                    Client = window.ClientSize.ToString(), Position = window.Position.ToString(),
                    Bounds = window.Bounds.ToString(), window.RenderScaling, State = window.WindowState.ToString() },
                // Exact terms of the unchanged compound assertion, separately readable.
                FocusGate = new { Active = window.IsActive, SameIdentity = ReferenceEquals(current, _focus),
                    ExpectedIsFocused = _focus.IsFocused, ExpectedVisible = Visible(window, _focus) },
                ExpectedFocus = Identity(_focus), ActualFocus = Identity(current),
                ExpectedIsError = ReferenceEquals(_focus, _error), ActualIsError = ReferenceEquals(current, _error),
                ActualWithinCapture = _capture.IsKeyboardFocusWithin,
                ExpectedGeometry = Geometry(window, _focus), ActualGeometry = Geometry(window, current as Control),
                Error = Geometry(window, _error), ErrorTextLength = _error.Text?.Length,
                Pending = Geometry(window, _pendingLabel), PendingTextLength = _pendingLabel.Text?.Length,
                Capture = Geometry(window, _capture), Capturing = ShortcutService.IsCapturing,
                Detail = Geometry(window, _detail), Editor = Geometry(window, editor),
                DetailLayout = _detail.Children.Select(child => new
                {
                    Identity = Identity(child), Bounds = child.Bounds.ToString(), DesiredSize = child.DesiredSize.ToString(),
                    child.IsMeasureValid, child.IsArrangeValid, TextLength = (child as TextBlock)?.Text?.Length
                }).ToArray(),
                DraftSame = ReferenceEquals(editor.Draft, _draft), DirtyUnchanged = editor.IsDirty == _dirty,
                SelectedSame = ReferenceEquals(_actions.SelectedItem, _selected),
                ItemsSame = ReferenceEquals(_actions.ItemsSource, _items),
                LiveSame = ReferenceEquals(ShortcutService.Current, _live), Revision = ShortcutService.Revision
            }));
        }
    }

    private static object? Identity(object? value) => value == null ? null : new
    {
        Type = value.GetType().FullName, Name = (value as Control)?.Name,
        // Process-local diagnostic token only. ReferenceEquals above is authoritative.
        RuntimeIdentity = RuntimeHelpers.GetHashCode(value)
    };

    private static object? Geometry(Window window, Control? target)
    {
        if (target == null) return null;
        Point? origin = target.TranslatePoint(default, window);
        Rect? bounds = origin is Point point ? new Rect(point, target.Bounds.Size) : null;
        Rect viewport = new(default(Point), window.ClientSize);
        var scrolls = target.GetVisualAncestors().OfType<ScrollViewer>().ToArray();
        // Same intersection used by Visible; do not change the oracle/tolerance.
        foreach (ScrollViewer scroll in scrolls)
            if (scroll.TranslatePoint(default, window) is Point scrollOrigin)
                viewport = viewport.Intersect(new Rect(scrollOrigin, scroll.Bounds.Size));
        return new
        {
            Identity = Identity(target), Attached = ReferenceEquals(TopLevel.GetTopLevel(target), window),
            target.IsVisible, target.IsEffectivelyVisible, target.IsEnabled, target.IsEffectivelyEnabled,
            target.IsFocused, target.IsKeyboardFocusWithin, target.Focusable,
            target.IsMeasureValid, target.IsArrangeValid,
            LocalBounds = target.Bounds.ToString(), DesiredSize = target.DesiredSize.ToString(),
            Origin = origin?.ToString(), WindowBounds = bounds?.ToString(),
            EffectiveViewport = viewport.ToString(), InViewport = Visible(window, target),
            // Individual comparisons distinguish clipping from focus loss. No relaxed threshold.
            Edges = bounds is Rect rectangle ? new
            {
                LeftFits = rectangle.Left >= viewport.Left - 1, TopFits = rectangle.Top >= viewport.Top - 1,
                RightFits = rectangle.Right <= viewport.Right + 1, BottomFits = rectangle.Bottom <= viewport.Bottom + 1
            } : null,
            ScrollAncestors = scrolls.Select(scroll => new
            {
                Identity = Identity(scroll), Bounds = scroll.Bounds.ToString(),
                Origin = scroll.TranslatePoint(default, window)?.ToString(),
                Viewport = scroll.Viewport.ToString(), Extent = scroll.Extent.ToString(), Offset = scroll.Offset.ToString(),
                scroll.ClipToBounds, scroll.IsMeasureValid, scroll.IsArrangeValid
            }).ToArray(),
            // Actual clipping presenters may be smaller than ScrollViewer.Bounds.
            // Log them as additional evidence; do not swap them into Visible here.
            ClippingAncestors = target.GetVisualAncestors().OfType<Control>().Where(control => control.ClipToBounds || control.Clip != null)
                .Select(control => new
                {
                    Identity = Identity(control), Bounds = control.Bounds.ToString(),
                    Origin = control.TranslatePoint(default, window)?.ToString(), control.ClipToBounds,
                    ClipBounds = control.Clip?.Bounds.ToString()
                }).ToArray()
        };
    }

    // Independent UI oracle: concrete compiled templates, NOT ShortcutIssueCatalog.Render.
    // Missing pack entries/EN fallback must fail, not quietly count as language coverage.
    private static Dictionary<string, Dictionary<string, string>> ReadTemplates()
    {
        var result = new Dictionary<string, Dictionary<string, string>>
        {
            ["en"] = ShortcutIssueCatalog.Entries.ToDictionary(entry => entry.Key, entry => entry.English),
            ["de"] = ShortcutIssueCatalog.Entries.ToDictionary(entry => entry.Key, entry => entry.German)
        };
        foreach (string language in Languages)
        {
            Require(Texts.Languages.Any(info => info.Code == language), "Bundled language missing: " + language);
            if (language is "en" or "de") continue;
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream("MeowChan.Desktop.Localization.Packs." + language + ".json")
                ?? throw new InvalidOperationException("Compiled language resource missing: " + language);
            using var document = JsonDocument.Parse(stream);
            JsonElement strings = document.RootElement.GetProperty("strings");
            result[language] = ShortcutIssueCatalog.Entries.ToDictionary(entry => entry.Key,
                entry => strings.GetProperty(entry.Key).GetString() ?? throw new InvalidOperationException("Null issue translation: " + entry.Key));
        }
        foreach (var pack in result.Where(pair => pair.Key != "en"))
        foreach (var entry in pack.Value)
            Require(!string.IsNullOrWhiteSpace(entry.Value) && entry.Value != result["en"][entry.Key],
                pack.Key + ": missing/native-language issue template: " + entry.Key);
        return result;
    }
    private static void AssertCatalog(IReadOnlyDictionary<string, Dictionary<string, string>> templates, string language)
    {
        Require(Texts.Language == language, "Requested language was not selected.");
        foreach (var entry in templates[language])
            Require(T(entry.Key) == entry.Value, language + ": missing/stale catalog integration: " + entry.Key);
    }
    private static void AssertIssues(ShortcutEditor editor, TextBlock error, IReadOnlyList<ShortcutIssue> issues,
        IReadOnlyDictionary<string, string> templates)
    {
        string expected = string.Join("\n", issues.Select(issue =>
            (CommandCatalog.TryGet(issue.CommandId, out var command) ? command!.Label : issue.CommandId)
            + (issue.OtherCommandId == null ? "" : " ↔ " + CommandCatalog.Label(issue.OtherCommandId)) + ": "
            + Expected(templates, issue.Reason?.Code ?? throw new InvalidOperationException("Host fixture lost its reason code."),
                issue.Reason!.Arguments.Cast<object?>().ToArray())));
        Require(ReferenceEquals(Errors(editor), error) && error.IsEffectivelyVisible && error.Text == expected,
            Texts.Language + ": real issue TextBlock is missing/stale (including action labels).");
    }
    private static string Expected(IReadOnlyDictionary<string, string> templates, string code, params object?[] args) =>
        string.Format(CultureInfo.GetCultureInfo(Texts.Language), templates[ShortcutIssueCatalog.Prefix + code], args);
    private static string Model(EffectiveShortcutBindings snapshot, bool includeIssues = false) => JsonSerializer.Serialize(new
    {
        snapshot.Platform, snapshot.Gestures, snapshot.RequestedGestures,
        Issues = includeIssues ? snapshot.Issues.Select(issue => new
        {
            issue.CommandId, issue.OtherCommandId, issue.Message, issue.BlocksSave,
            Code = issue.Reason?.Code, Args = issue.Reason?.Arguments
        }).ToArray() : null
    });
    private static string SettingsState(SettingsEditor editor) => JsonSerializer.Serialize(new
    {
        editor.DownloadPath, Language = editor.SelectedLanguage?.Code, Theme = editor.SelectedTheme?.Value,
        Workspace = editor.SelectedWorkspaceMode?.Value, editor.SelectedSectionId
    });
    private static (string? Text, int Caret, int Start, int End) Input(TextBox input) =>
        (input.Text, input.CaretIndex, input.SelectionStart, input.SelectionEnd);
    private static (TextBox Search, ListBox Actions) SelectAction(ShortcutEditor editor)
    {
        Panel root = (Panel)editor.Content!;
        TextBox search = root.Children.OfType<TextBox>().Single();
        ListBox actions = root.Children.OfType<ListBox>().Single();
        search.Text = ActionId;
        actions.SelectedItem = actions.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == ActionId);
        return (search, actions);
    }
    private static TextBlock Errors(ShortcutEditor editor) => ((Panel)editor.Content!).Children.OfType<TextBlock>()
        .Single(text => text.Classes.Contains("workspace-error"));
    private static TextBlock PendingLabel(KeyGestureInput capture)
    {
        Panel detail = capture.Parent as Panel ?? throw new InvalidOperationException("Capture panel missing.");
        int index = detail.Children.IndexOf(capture);
        return index >= 0 && index + 2 < detail.Children.Count && detail.Children[index + 1] is CheckBox
            && detail.Children[index + 2] is TextBlock label ? label : throw new InvalidOperationException("Pending label missing.");
    }
    private static Button ActionButton(ShortcutEditor editor, string suffix) => editor.GetVisualDescendants().OfType<Button>()
        .Single(button => button.Content as string == T("settings.shortcutsEditor." + suffix));
    private static void Click(Button button)
    {
        Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "Cannot click unavailable real shortcut button.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task FocusAsync(Window window, Control target)
    {
        target.BringIntoView();
        await SettleAsync(window);
        Require(target.Focus(), "Explicit fixture focus rejected.");
        await Program.WaitUntilAsync(() => target.IsKeyboardFocusWithin && Visible(window, target));
    }
    private static bool Visible(Window window, Control target)
    {
        if (!ReferenceEquals(TopLevel.GetTopLevel(target), window) || !target.IsEffectivelyVisible
            || target.Bounds.Width <= 0 || target.Bounds.Height <= 0 || target.TranslatePoint(default, window) is not Point origin) return false;
        Rect bounds = new(origin, target.Bounds.Size), viewport = new(default(Point), window.ClientSize);
        foreach (ScrollViewer scroll in target.GetVisualAncestors().OfType<ScrollViewer>())
            if (scroll.TranslatePoint(default, window) is Point point) viewport = viewport.Intersect(new Rect(point, scroll.Bounds.Size));
        return bounds.Left >= viewport.Left - 1 && bounds.Top >= viewport.Top - 1
            && bounds.Right <= viewport.Right + 1 && bounds.Bottom <= viewport.Bottom + 1;
    }
    private static string T(string key)
    {
        string value = Texts.T(key);
        Require(!string.IsNullOrWhiteSpace(value) && value != key, "Unresolved catalog key: " + key);
        return value;
    }
    private static string[] Files(string profile) => Directory.GetFiles(profile, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetRelativePath(profile, path) + ":"
            + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
    private static async Task SettleAsync(Window? window = null)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window?.UpdateLayout(), DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
