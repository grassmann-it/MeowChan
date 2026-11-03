using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Testing;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// Root hook: await SecondarySettingsLanguageRefreshChecks.RunAsync(mainWindow).
// Native shown windows, real controls, managed routed input and Texts.Changed;
// not an OS-keyboard, translation-quality or cross-platform acceptance claim.
internal static class SecondarySettingsLanguageRefreshChecks
{
    private static readonly string[] Languages = ["es", "fr", "ru", "de", "en"];
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private const string ActionId = "navigation.archive";

    internal static async Task RunAsync(MainWindow owner)
    {
        await RunGalleryPlaybackAsync(owner);
        await RunShortcutFrameAsync(owner);
        await RunShortcutCaptureAsync(owner);
        Console.WriteLine("SMOKE secondary settings language refresh: five live languages; gallery controls/popup/raw draft, shortcut frame/search/dirty draft, active capture/focus, unchanged pending F9 Apply and F10 continuation with complete modifiers passed. Managed input only.");
    }

    // Separate entry points let Root diagnose the three journeys independently.
    internal static Task RunGalleryPlaybackAsync(MainWindow owner) =>
        WithFixtureAsync(owner, "gallery", GalleryPlaybackAsync);
    internal static Task RunShortcutFrameAsync(MainWindow owner) =>
        WithFixtureAsync(owner, "shortcut-frame", ShortcutFrameAsync);
    internal static async Task RunShortcutCaptureAsync(MainWindow owner)
    {
        // Separate real windows: F9 must remain applyable WITHOUT another key;
        // the independent F10 journey still proves continued input after refresh.
        await WithFixtureAsync(owner, "shortcut-capture-pending", (window, config) =>
            ShortcutCaptureAsync(window, config, continueRecording: false));
        await WithFixtureAsync(owner, "shortcut-capture-continuation", (window, config) =>
            ShortcutCaptureAsync(window, config, continueRecording: true));
    }

    private static async Task GalleryPlaybackAsync(SettingsWindow window, Config config)
    {
        SettingsEditor editor = window.Editor;
        await SettingsSectionChecks.OpenAsync(editor, "settings.reading-media");
        ComboBox mode = Need<ComboBox>(editor, "GalleryPlaybackModeInput");
        mode.SelectedItem = mode.Items.OfType<SettingsChoice>().Single(choice => choice.Value == "Slideshow");
        Avalonia.Controls.NumericUpDown seconds = Need<Avalonia.Controls.NumericUpDown>(editor, "GallerySlideshowSecondsInput");
        await Program.WaitUntilAsync(() => editor.IsGallerySlideshow && seconds.IsEffectivelyVisible);
        // Semi nests PART_TextBox as PART_Spinner.Content. Applying only the
        // NumericUpDown template does not yet put that content in the visual tree;
        // the newly revealed row and nested presenters need their layout pass.
        // This is fixture setup BEFORE any raw draft/preservation snapshot exists.
        seconds.BringIntoView();
        seconds.ApplyTemplate();
        await SettleAsync(window);
        await Program.WaitUntilAsync(() => seconds.GetTemplateDescendants().OfType<TextBox>()
            .Any(input => input.Name == "PART_TextBox" && input.IsEffectivelyVisible
                && input.Bounds.Width > 0 && input.Bounds.Height > 0
                && ReferenceEquals(TopLevel.GetTopLevel(input), window)));
        TextBox raw = seconds.GetTemplateDescendants().OfType<TextBox>().Single(input => input.Name == "PART_TextBox");
        await FocusAsync(window, raw);
        raw.Text = "17 unfinished raw";
        await Program.WaitUntilAsync(() => seconds.Text == raw.Text && raw.Text == "17 unfinished raw");
        CheckBox mediaFocus = Need<CheckBox>(editor, "GalleryMediaFocusInput");
        CheckBox overlay = Need<CheckBox>(editor, "GalleryOverlayControlsInput");
        CheckBox postContext = Need<CheckBox>(editor, "GalleryOverlayPostContextInput");
        mediaFocus.IsChecked = !config.GalleryMediaFocus;
        overlay.IsChecked = !config.GalleryOverlayControls;
        postContext.IsChecked = !config.GalleryOverlayPostContext;
        editor.DownloadPath = "unfinished synthetic path — ü";
        editor.SelectedLanguage = editor.LanguageChoices.Single(choice => choice.Code == "de");
        await SettleAsync(window);
        SelectText(raw);
        InputState input = InputState.Read(raw);
        string draft = DraftState(editor);
        using var trace = new GalleryRefreshTrace(window, editor, mode, seconds, raw);
        trace.Capture("fixture-captured");
        // Negative control: do not silently rebase an unstable TextBox snapshot.
        // Any drift here predates Texts.SetLanguage and is a fixture/lifecycle issue,
        // not evidence that the language notification lost the user's draft.
        await SettleAsync(window);
        trace.Capture("fixture-idle-without-language-change");
        trace.ReportMismatch(draft, input);
        Require(DraftState(editor) == draft && InputState.Read(raw) == input,
            "gallery fixture semantic/raw draft, caret or selection changed BEFORE any language notification.");
        Require(ReferenceEquals(window.FocusManager?.GetFocusedElement(), raw) && raw.IsFocused && Visible(window, raw),
            "gallery fixture lost visible numeric input focus BEFORE any language notification.");

        foreach (string language in Languages)
        {
            // No focus, selection, draft reset, editor reload or BringIntoView
            // between notification and preservation assertions.
            trace.ResetEvents();
            trace.Capture(language + ":before-notification");
            Texts.SetLanguage(language);
            trace.Capture(language + ":after-notification");
            await SettleAsync(window);
            trace.Capture(language + ":after-settle");
            trace.ReportMismatch(draft, input);
            Require(Texts.Language == language && ReferenceEquals(window.Editor, editor), language + ": gallery editor was replaced.");
            Require(DraftState(editor) == draft && InputState.Read(raw) == input,
                language + ": gallery semantic/raw draft, caret or selection changed.");
            Require(ReferenceEquals(window.FocusManager?.GetFocusedElement(), raw) && raw.IsFocused && Visible(window, raw),
                language + ": gallery language refresh stole visible numeric input focus.");
            Require(editor.SelectedSectionId == "settings.reading-media", language + ": gallery category changed.");
            AssertText(Need<TextBlock>(editor, "SettingsGalleryPlaybackHeading"), "settings.galleryPlayback.title");
            AssertText(Need<TextBlock>(editor, "GalleryPlaybackModeFieldLabel"), "settings.galleryPlayback.mode");
            AssertText(Need<TextBlock>(editor, "GallerySlideshowSecondsFieldLabel"), "settings.galleryPlayback.imageSeconds");
            AssertAccessible(mode, "settings.galleryPlayback.mode", "settings.galleryPlayback.modeHelp");
            AssertAccessible(seconds, "settings.galleryPlayback.imageSeconds", "settings.galleryPlayback.imageSecondsHelp");
            AssertCheckBox(mediaFocus, "settings.galleryPlayback.mediaFocus");
            AssertCheckBox(overlay, "settings.galleryPlayback.overlayControls", "settings.galleryPlayback.overlayHelp");
            AssertCheckBox(postContext, "settings.galleryPlayback.overlayPostContext", "settings.galleryPlayback.overlayHelp");
            Control page = Need<Control>(editor, "SettingsReadingMediaPage");
            foreach (string key in new[] { "profileHelp", "modeHelp", "imageSecondsHelp", "overlayHelp" })
                Require(page.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible
                    && text.Text == T("settings.galleryPlayback." + key)), language + ": visible gallery help missing: " + key);
            Require(mode.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible
                && text.Text == T("settings.galleryPlayback.slideshow")), language + ": selected mode template retained an old label.");

            // Explicit popup action AFTER preservation checks: inspect actual
            // generated ComboBoxItems, not only the CLR SettingsChoice collection.
            await FocusAsync(window, mode);
            mode.IsDropDownOpen = true;
            try
            {
                // Inspect this ComboBox's named template popup, then traverse its
                // Child: an open native PopupRoot is not a descendant of the window.
                Popup? popup = null;
                await Program.WaitUntilAsync(() =>
                {
                    popup = mode.GetTemplateDescendants().OfType<Popup>().SingleOrDefault(part => part.Name == "PART_Popup");
                    return popup?.IsOpen == true && popup.Child is { Bounds.Width: > 0, Bounds.Height: > 0 };
                });
                await SettleAsync(window);
                ComboBoxItem[] items = popup!.Child!.GetVisualDescendants().OfType<ComboBoxItem>()
                    .Where(item => item.IsEffectivelyVisible).ToArray();
                Require(items.Length == 3, language + ": mode popup did not materialize all three choices.");
                string[] labels = items.Select(item => string.Join("", item.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible).Select(text => text.Text))).ToArray();
                Require(labels.SequenceEqual(new[] { "manual", "repeat", "slideshow" }.Select(key => T("settings.galleryPlayback." + key))),
                    language + ": actual mode popup contains missing/stale translations: " + string.Join(" | ", labels));
            }
            finally { mode.IsDropDownOpen = false; }
            await SettleAsync(window);
            Require(DraftState(editor) == draft, language + ": opening/closing the translated popup mutated the draft.");
            // New explicit setup for the NEXT passive transition, not a repair
            // of failed refresh focus (asserted above before popup interaction).
            await FocusAsync(window, raw);
            SelectText(raw);
            Require(InputState.Read(raw) == input, language + ": popup roundtrip changed raw input.");
            Console.WriteLine("SMOKE secondary gallery language cell passed: " + language);
        }
    }

    private static async Task ShortcutFrameAsync(SettingsWindow window, Config config)
    {
        ShortcutEditor editor = await OpenShortcutsAsync(window);
        (TextBox search, ListBox actions) = SelectAction(editor);
        // Make a real unsaved edit through a production button, not Load/Set.
        Click(ActionButton(editor, "clearBinding"));
        await Program.WaitUntilAsync(() => editor.IsDirty && editor.Draft.HasOverride(ActionId));
        Require(EffectiveShortcutBindings.Build(editor.Draft).RequestedGestures[ActionId].Count == 0,
            "Clear binding did not establish the real dirty/unbound fixture.");
        await FocusAsync(window, search);
        SelectText(search);
        InputState input = InputState.Read(search);
        string draft = editor.Draft.Raw.GetRawText();
        string settingsDraft = DraftState(window.Editor);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            await SettleAsync(window);
            AssertShortcutFrame(editor, actions, customUnbound: true);
            Require(ReferenceEquals(Need<ShortcutEditor>(window.Editor, "ShortcutsEditor"), editor)
                && editor.IsDirty && editor.Draft.Raw.GetRawText() == draft && DraftState(window.Editor) == settingsDraft,
                language + ": shortcut/settings dirty draft was replaced or reset.");
            Require(InputState.Read(search) == input && search.Text == ActionId
                && actions.Items.OfType<ListBoxItem>().Count() == 1
                && (actions.SelectedItem as ListBoxItem)?.Tag as string == ActionId,
                language + ": shortcut search/caret/selection or selected command identity changed.");
            Require(ReferenceEquals(window.FocusManager?.GetFocusedElement(), search) && search.IsFocused && Visible(window, search),
                language + ": shortcut frame refresh detached/refocused the live search input.");
            Require(!ShortcutService.IsCapturing, language + ": frame-only refresh invented a capture lease.");
            Console.WriteLine("SMOKE secondary shortcut frame language cell passed: " + language);
        }
    }

    private static async Task ShortcutCaptureAsync(SettingsWindow window, Config config, bool continueRecording)
    {
        Require(!ShortcutService.IsCapturing, "Capture fixture requires no pre-existing capture lease.");
        ShortcutEditor editor = await OpenShortcutsAsync(window);
        var (_, actions) = SelectAction(editor);
        Click(ActionButton(editor, "clearBinding"));
        await Program.WaitUntilAsync(() => editor.IsDirty);
        Button record = ActionButton(editor, "recordAdditional");
        await FocusAsync(window, record);
        Click(record);
        KeyGestureInput capture = editor.GetVisualDescendants().OfType<KeyGestureInput>().Single();
        TextBlock pendingLabel = CapturePendingLabel(capture);
        Require(editor.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked != true,
            "Capture fixture expects portable Primary-modifier recording, not explicit modifiers.");
        capture.BringIntoView();
        await SettleAsync(window);
        await Program.WaitUntilAsync(() => ShortcutService.IsCapturing && capture.IsKeyboardFocusWithin && Visible(window, capture));
        Control captureFocus = window.FocusManager?.GetFocusedElement() as Control
            ?? throw new InvalidOperationException("Capture acquired no real focused control.");
        var pending = new KeyGesture(Key.F9, KeyModifiers.Control | KeyModifiers.Shift);
        Press(captureFocus, pending);
        await Program.WaitUntilAsync(() => SameGesture(capture.Gesture, pending));
        ShortcutGesture pendingBinding = RecordedControlShift(Key.F9);
        AssertPendingLabel(capture, pendingLabel, pendingBinding);
        string draft = editor.Draft.Raw.GetRawText();
        string settingsDraft = DraftState(window.Editor);
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            await SettleAsync(window);
            AssertShortcutFrame(editor, actions, customUnbound: true);
            Require(editor.Draft.Raw.GetRawText() == draft && editor.IsDirty && DraftState(window.Editor) == settingsDraft,
                language + ": pending capture was applied/discarded during language refresh.");
            Require(ReferenceEquals(capture, editor.GetVisualDescendants().OfType<KeyGestureInput>().Single())
                && capture.IsEffectivelyEnabled && SameGesture(capture.Gesture, pending) && ShortcutService.IsCapturing,
                language + ": refresh replaced/disabled the capture, lost its pending gesture or released routing suppression.");
            Require(ReferenceEquals(window.FocusManager?.GetFocusedElement(), captureFocus)
                && capture.IsKeyboardFocusWithin && Visible(window, capture),
                language + ": active capture focus was lost/stolen on language refresh.");
            Require(AutomationProperties.GetName(capture) == T("settings.shortcutsEditor.record")
                && AutomationProperties.GetHelpText(capture) == T("settings.shortcutsEditor.recordHelp"),
                language + ": live capture accessible label/help remained stale.");
            AssertPendingLabel(capture, pendingLabel, pendingBinding);
            Console.WriteLine("SMOKE secondary shortcut capture language cell passed: " + language
                + (continueRecording ? " (F10 continuation journey)" : " (unchanged F9 apply journey)"));
        }
        KeyGesture expected = pending;
        if (continueRecording)
        {
            // The same real focused capture must still accept input after the
            // entire language sequence. This is NOT the proof of F9 applyability.
            expected = new KeyGesture(Key.F10, KeyModifiers.Control | KeyModifiers.Shift);
            Press(captureFocus, expected);
            await Program.WaitUntilAsync(() => SameGesture(capture.Gesture, expected));
            Require(editor.Draft.Raw.GetRawText() == draft && ShortcutService.IsCapturing, "Post-refresh recording prematurely changed bindings.");
            AssertPendingLabel(capture, pendingLabel, RecordedControlShift(Key.F10));
        }
        else
        {
            // No Press, checkbox toggle, recapture or private-field access after
            // the language changes: only Apply may consume the original pending F9.
            Require(SameGesture(capture.Gesture, pending) && editor.Draft.Raw.GetRawText() == draft
                && ShortcutService.IsCapturing, "Unchanged post-refresh F9 is no longer pending.");
        }
        Click(ActionButton(editor, "applyRecording"));
        await Program.WaitUntilAsync(() => !ShortcutService.IsCapturing && editor.Draft.Raw.GetRawText() != draft);
        AssertAppliedRecording(editor, expected);
        Console.WriteLine("SMOKE secondary shortcut capture applied: " + expected.Key
            + "; complete Control+Shift modifiers; " + (continueRecording ? "post-refresh continuation" : "unchanged pre-refresh pending"));
    }

    private static ShortcutGesture RecordedControlShift(Key key) => new(key.ToString(),
        EffectiveShortcutBindings.CurrentPlatform == ShortcutPlatform.MacOS
            ? new[] { "Control", "Shift" } : new[] { "Primary", "Shift" });

    private static TextBlock CapturePendingLabel(KeyGestureInput capture)
    {
        // ShortcutEditor.RefreshDetail adds capture, explicit-modifier checkbox,
        // then the real pending TextBlock. No private field/reflection surrogate.
        Panel detail = capture.Parent as Panel ?? throw new InvalidOperationException("Capture detail panel missing.");
        int index = detail.Children.IndexOf(capture);
        return index >= 0 && index + 2 < detail.Children.Count
            && detail.Children[index + 1] is CheckBox && detail.Children[index + 2] is TextBlock pending
            ? pending : throw new InvalidOperationException("Actual capture pending label missing.");
    }

    private static void AssertPendingLabel(KeyGestureInput capture, TextBlock label, ShortcutGesture expected) =>
        Require(ReferenceEquals(CapturePendingLabel(capture), label) && label.IsEffectivelyVisible
            && label.Text == expected.Display(EffectiveShortcutBindings.CurrentPlatform),
            Texts.Language + ": actual pending capture label was replaced, cleared or changed: " + label.Text);

    private static void AssertAppliedRecording(ShortcutEditor editor, KeyGesture expected)
    {
        EffectiveShortcutBindings snapshot = EffectiveShortcutBindings.Build(editor.Draft);
        IReadOnlyList<ShortcutGesture> requested = snapshot.RequestedGestures[ActionId];
        IReadOnlyList<ShortcutGesture> effective = snapshot.For(ActionId);
        Require(editor.IsDirty && requested.Count == 1 && effective.Count == 1
            && SameGesture(requested[0].Resolve(snapshot.Platform), expected)
            && SameGesture(effective[0].Resolve(snapshot.Platform), expected),
            "Explicit Apply recording lost the complete " + expected.Key + "/" + expected.KeyModifiers
                + " gesture; requested=" + JsonSerializer.Serialize(requested) + "; effective=" + JsonSerializer.Serialize(effective));
    }

    private static async Task<ShortcutEditor> OpenShortcutsAsync(SettingsWindow window)
    {
        SettingsEditor settings = window.Editor;
        await SettleAsync(window);
        settings.OpenSectionNavigation();
        Require(settings.CanGoToSection("settings.shortcuts"), "Shortcut section unavailable through public navigation.");
        settings.GoToSection("settings.shortcuts");
        ShortcutEditor editor = Need<ShortcutEditor>(settings, "ShortcutsEditor");
        await Program.WaitUntilAsync(() => editor.IsEffectivelyVisible && editor.SectionHeading.IsFocused
            && editor.Bounds.Width > 0 && ReferenceEquals(TopLevel.GetTopLevel(editor), window));
        return editor;
    }

    private static (TextBox Search, ListBox Actions) SelectAction(ShortcutEditor editor)
    {
        Panel content = editor.Content as Panel ?? throw new InvalidOperationException("Shortcut root panel missing.");
        TextBox search = content.Children.OfType<TextBox>().Single();
        ListBox actions = content.Children.OfType<ListBox>().Single();
        search.Text = ActionId; // Stable ID, never translated labels as identity.
        actions.SelectedItem = actions.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == ActionId);
        return (search, actions);
    }

    private static void AssertShortcutFrame(ShortcutEditor editor, ListBox actions, bool customUnbound)
    {
        Panel root = editor.Content as Panel ?? throw new InvalidOperationException("Shortcut root panel missing.");
        AssertText((TextBlock)editor.SectionHeading, "shortcuts.title");
        Require(root.Children.OfType<TextBlock>().Any(text => text.Text == T("settings.shortcutsEditor.hint")), "Stale shortcut editor hint.");
        TextBox search = root.Children.OfType<TextBox>().Single();
        Require(search.PlaceholderText == T("settings.shortcutsEditor.search")
            && AutomationProperties.GetName(search) == T("settings.shortcutsEditor.search")
            && AutomationProperties.GetName(actions) == T("settings.shortcutsEditor.actions"), "Stale shortcut search/actions accessible frame labels.");
        Grid header = root.Children.OfType<Grid>().Single();
        Require(header.Children.OfType<TextBlock>().Select(text => text.Text).SequenceEqual(
            new[] { "action", "context", "effectiveBinding" }.Select(key => T("settings.shortcutsEditor." + key))),
            "Stale shortcut table headings.");
        foreach (string suffix in new[] { "resetAll", "recordAdditional", "clearBinding", "restoreDefault", "applyRecording", "takeOver", "cancelRecording" })
        {
            Button button = ActionButton(editor, suffix);
            Require(button.IsEffectivelyVisible && AutomationProperties.GetName(button) == T("settings.shortcutsEditor." + suffix),
                "Missing/stale shortcut button/accessibility: " + suffix);
        }
        CheckBox explicitModifiers = editor.GetVisualDescendants().OfType<CheckBox>().Single();
        Require(explicitModifiers.Content as string == T("settings.shortcutsEditor.keepExplicit"), "Stale explicit-modifier label.");
        ListBoxItem row = actions.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == ActionId);
        Require(ReferenceEquals(actions.SelectedItem, row), "Shortcut selected row identity was lost.");
        Control rowContent = row.Content as Control ?? throw new InvalidOperationException("Shortcut row content missing.");
        Require(rowContent.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == CommandCatalog.Label(ActionId)),
            "Materialized shortcut row kept an old command label.");
        if (customUnbound)
            foreach (string key in new[] { "custom", "unbound" })
                Require(rowContent.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == T("settings.shortcutsEditor." + key))
                    && AutomationProperties.GetName(row)?.Contains(T("settings.shortcutsEditor." + key), StringComparison.Ordinal) == true,
                    "Materialized shortcut row lost localized state: " + key);
    }

    private static async Task WithFixtureAsync(MainWindow owner, string scenario, Func<SettingsWindow, Config, Task> journey)
    {
        Dispatcher.UIThread.VerifyAccess();
        foreach (string code in Languages)
            Require(Texts.Languages.Any(language => language.Code == code), "Bundled language unavailable: " + code);
        Require(!ShortcutService.IsCapturing, "Secondary settings helper must run without an existing capture.");
        string originalLanguage = Texts.Language;
        long revision = ShortcutService.Revision;
        EffectiveShortcutBindings live = ShortcutService.Current;
        MainWindow.WorkspacePage page = owner.SelectedPage;
        string profile = TemporaryWorkspacePaths.CreateDirectory("meowchan-secondary-settings-" + scenario + "-");
        SettingsWindow? window = null;
        Task? dialog = null;
        try
        {
            Texts.SetLanguage("en");
            Config config = Config.LoadConfig(profile);
            config.Language = "en";
            config.WorkspaceMode = "Advanced";
            config.DownloadPath = Path.Combine(profile, "synthetic downloads");
            config.GalleryPlaybackMode = "Manual";
            config.AutomaticUpdateChecks = config.MonitorClipboardUrls = config.CloseToTray = config.MinimizeToTray = false;
            config.SaveConfig(profile); // Only initial data in this newly owned tree.
            string baseline = JsonSerializer.Serialize(config, ConfigJsonContext.Default.Config);
            string[] files = Files(profile);
            int applies = 0;
            window = new SettingsWindow(config, _ =>
            {
                applies++;
                throw new InvalidOperationException("Presentation-only journey unexpectedly reached Apply settings.");
            });
            dialog = window.ShowDialog(owner);
            await Program.WaitUntilAsync(() => window.IsVisible);
            window.Activate();
            await Program.WaitUntilAsync(() => window.IsActive);
            await journey(window, config);
            Require(applies == 0 && JsonSerializer.Serialize(config, ConfigJsonContext.Default.Config) == baseline
                && Files(profile).SequenceEqual(files), scenario + ": language/input journey committed settings or wrote fixture files.");
            Require(ShortcutService.Revision == revision && ReferenceEquals(ShortcutService.Current, live) && owner.SelectedPage == page,
                scenario + ": language/input journey published bindings or dispatched owner navigation.");
        }
        finally
        {
            // Close first: detaching the real editor must release its capture.
            // Do not reset/load the draft or synthesize a replacement capture lease.
            try
            {
                if (window?.IsVisible == true) window.Close();
                if (dialog != null) await dialog.WaitAsync(Deadline);
            }
            finally
            {
                Texts.SetLanguage(originalLanguage);
                await SettleAsync();
                owner.Activate();
                Directory.Delete(profile, recursive: true); // This helper's new synthetic tree only.
            }
        }
        Require(!ShortcutService.IsCapturing, scenario + ": closing the synthetic editor leaked routing suppression.");
    }

    private static string DraftState(SettingsEditor editor) => JsonSerializer.Serialize(new
    {
        editor.DownloadPath, editor.GallerySlideshowSeconds, editor.GalleryMediaFocus,
        editor.GalleryOverlayControls, editor.GalleryOverlayPostContext,
        Mode = editor.SelectedGalleryPlaybackMode?.Value, Language = editor.SelectedLanguage?.Code,
        Theme = editor.SelectedTheme?.Value, Workspace = editor.SelectedWorkspaceMode?.Value,
        Number = Need<Avalonia.Controls.NumericUpDown>(editor, "GallerySlideshowSecondsInput").Text
    });
    // Read-only, bounded diagnostics. No focus/text/selection/Value writes, no
    // dispatcher work, and subscriptions are removed even when an assertion fails.
    private sealed class GalleryRefreshTrace : IDisposable
    {
        private readonly Window _window;
        private readonly SettingsEditor _editor;
        private readonly ComboBox _mode;
        private readonly Avalonia.Controls.NumericUpDown _seconds;
        private readonly TextBox _raw;
        private readonly List<object> _events = new();
        private string _phase = "fixture-captured";
        private int _omitted;

        internal GalleryRefreshTrace(Window window, SettingsEditor editor, ComboBox mode,
            Avalonia.Controls.NumericUpDown seconds, TextBox raw)
        {
            _window = window; _editor = editor; _mode = mode; _seconds = seconds; _raw = raw;
            _editor.PropertyChanged += OnDraftChanged;
            _mode.PropertyChanged += OnControlChanged;
            _seconds.PropertyChanged += OnControlChanged;
            _raw.PropertyChanged += OnControlChanged;
        }

        internal void Capture(string phase)
        {
            _phase = phase;
            Console.WriteLine("SMOKE secondary gallery state: " + JsonSerializer.Serialize(new { Phase = phase, State = Snapshot() }));
        }

        internal void ResetEvents() { _events.Clear(); _omitted = 0; }

        internal void ReportMismatch(string expectedDraft, InputState expectedInput)
        {
            string actualDraft = DraftState(_editor);
            InputState actualInput = InputState.Read(_raw);
            if (actualDraft == expectedDraft && actualInput == expectedInput) return;
            Console.WriteLine("SMOKE secondary gallery state MISMATCH: " + JsonSerializer.Serialize(new
            {
                Phase = _phase,
                DraftMatches = actualDraft == expectedDraft, RawTextMatches = actualInput.Text == expectedInput.Text,
                SelectionMatches = actualInput.Start == expectedInput.Start && actualInput.End == expectedInput.End,
                CaretMatches = actualInput.Caret == expectedInput.Caret,
                ExpectedDraft = JsonSerializer.Deserialize<JsonElement>(expectedDraft), ExpectedInput = expectedInput,
                Actual = Snapshot(), Events = _events, OmittedEvents = _omitted
            }));
        }

        private object Snapshot() => new
        {
            UiLanguage = Texts.Language, Draft = JsonSerializer.Deserialize<JsonElement>(DraftState(_editor)),
            Input = InputState.Read(_raw), NumericValue = _seconds.Value, NumericText = _seconds.Text,
            ModeControl = (_mode.SelectedItem as SettingsChoice)?.Value, _editor.IsGallerySlideshow,
            FocusedIsRaw = ReferenceEquals(_window.FocusManager?.GetFocusedElement(), _raw), _raw.IsFocused,
            FocusedControl = FocusName(), RawVisible = _raw.IsEffectivelyVisible,
            NumericVisible = _seconds.IsEffectivelyVisible, RawInViewport = Visible(_window, _raw),
            RawAttachedToWindow = ReferenceEquals(TopLevel.GetTopLevel(_raw), _window),
            SameTemplateTextBox = _seconds.GetTemplateDescendants().OfType<TextBox>().Any(part => ReferenceEquals(part, _raw))
        };

        private string? FocusName() => _window.FocusManager?.GetFocusedElement() is Control focus
            ? focus.GetType().Name + "#" + focus.Name : null;

        private void OnDraftChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(SettingsEditor.SelectedGalleryPlaybackMode) or nameof(SettingsEditor.IsGallerySlideshow)
                or nameof(SettingsEditor.GallerySlideshowSeconds) or nameof(SettingsEditor.SelectedLanguage))) return;
            Record(new
            {
                Phase = _phase, Owner = "draft", Property = e.PropertyName,
                Mode = _editor.SelectedGalleryPlaybackMode?.Value, _editor.IsGallerySlideshow,
                Seconds = _editor.GallerySlideshowSeconds, Language = _editor.SelectedLanguage?.Code,
                Input = InputState.Read(_raw), NumericText = _seconds.Text, FocusedControl = FocusName()
            });
        }

        private void OnControlChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name is not ("SelectedItem" or "ItemsSource" or "Text" or "Value" or "CaretIndex"
                or "SelectionStart" or "SelectionEnd" or "IsFocused" or "IsVisible")) return;
            Record(new
            {
                Phase = _phase, Owner = ReferenceEquals(sender, _raw) ? "raw" : ReferenceEquals(sender, _mode) ? "mode" : "numeric",
                Property = e.Property.Name, Before = e.OldValue?.ToString(), After = e.NewValue?.ToString(),
                Input = InputState.Read(_raw), NumericText = _seconds.Text, FocusedControl = FocusName()
            });
        }

        private void Record(object change)
        {
            if (_events.Count < 96) _events.Add(change);
            else _omitted++;
        }

        public void Dispose()
        {
            _editor.PropertyChanged -= OnDraftChanged;
            _mode.PropertyChanged -= OnControlChanged;
            _seconds.PropertyChanged -= OnControlChanged;
            _raw.PropertyChanged -= OnControlChanged;
        }
    }

    private readonly record struct InputState(string? Text, int Start, int End, int Caret)
    {
        internal static InputState Read(TextBox input) => new(input.Text, input.SelectionStart, input.SelectionEnd, input.CaretIndex);
    }
    private static void SelectText(TextBox input)
    {
        Require(input.IsFocused && input.Text is { Length: > 3 }, "Text selection requires an already-focused nonempty fixture input.");
        input.CaretIndex = 3; input.SelectionStart = 1; input.SelectionEnd = 3;
    }
    private static async Task FocusAsync(Window window, Control target)
    {
        target.BringIntoView();
        await SettleAsync(window);
        Require(target.Focus(), "Explicit fixture focus rejected: " + target.GetType().Name);
        await Program.WaitUntilAsync(() => target.IsKeyboardFocusWithin && Visible(window, target));
    }
    private static bool Visible(Window window, Control target)
    {
        if (!target.IsEffectivelyVisible || target.Bounds.Width <= 0 || target.Bounds.Height <= 0
            || target.TranslatePoint(default, window) is not Point origin) return false;
        Rect bounds = new(origin, target.Bounds.Size);
        Rect viewport = new(default(Point), window.ClientSize);
        foreach (ScrollViewer scroll in target.GetVisualAncestors().OfType<ScrollViewer>())
            if (scroll.TranslatePoint(default, window) is Point point) viewport = viewport.Intersect(new Rect(point, scroll.Bounds.Size));
        return bounds.Left >= viewport.Left - 1 && bounds.Top >= viewport.Top - 1
            && bounds.Right <= viewport.Right + 1 && bounds.Bottom <= viewport.Bottom + 1;
    }
    private static bool SameGesture(KeyGesture? actual, KeyGesture expected) => actual?.Key == expected.Key && actual.KeyModifiers == expected.KeyModifiers;
    private static void Press(Control source, KeyGesture gesture) => source.RaiseEvent(new KeyEventArgs
        { RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers });
    private static Button ActionButton(ShortcutEditor editor, string suffix) => editor.GetVisualDescendants().OfType<Button>()
        .Single(button => button.Content as string == T("settings.shortcutsEditor." + suffix));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static string T(string key)
    {
        string value = Texts.T(key);
        Require(!string.IsNullOrWhiteSpace(value) && value != key, "Unresolved fixture catalog key: " + key);
        return value;
    }
    private static void AssertText(TextBlock text, string key) => Require(text.IsEffectivelyVisible && text.Text == T(key), "Missing/stale real TextBlock: " + key);
    private static void AssertAccessible(Control control, string key, string? help = null) => Require(
        AutomationProperties.GetName(control) == T(key) && (help == null || AutomationProperties.GetHelpText(control) == T(help)),
        "Stale accessible gallery label/help: " + key);
    private static void AssertCheckBox(CheckBox control, string key, string? help = null)
    {
        AssertAccessible(control, key, help);
        Require(control.IsEffectivelyVisible && control.Content is TextBlock text && text.Text == T(key), "Stale visible gallery checkbox: " + key);
    }
    private static string[] Files(string profile) => Directory.GetFiles(profile, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(profile, path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
    private static async Task SettleAsync(Window? window = null)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window?.UpdateLayout(), DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static TControl Need<TControl>(Control owner, string name) where TControl : Control => owner.FindControl<TControl>(name)
        ?? throw new InvalidOperationException("Missing actual Settings control: " + name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
