using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

// Real, detached production controls + the production Texts.Changed callback.
// No native Window, OS input, profile writes or shortcut publication. Native focus,
// viewport and routed-key continuation remain covered by the separate smoke journey.
[Collection("Localization")]
public sealed class ShortcutEditorLiveLanguageStateTests : IDisposable
{
    private const string ActionId = "navigation.archive";
    private static readonly string[] Languages = ["es", "fr", "ru", "de", "en", "en"];
    private readonly string _language = Texts.Language;

    public ShortcutEditorLiveLanguageStateTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefreshKeepsCleanOrDirtyDraftSearchSelectionAndExistingControls(bool dirty)
    {
        using var fixture = new EditorFixture();
        ShortcutEditor editor = fixture.Editor;
        TextBox search = Get<TextBox>(editor, "_search");
        ListBox actions = Get<ListBox>(editor, "_actions");
        search.Text = ActionId;
        Select(editor, ActionId);
        if (dirty) Click(editor, "clearBinding");
        search.CaretIndex = 8;
        search.SelectionStart = 2;
        search.SelectionEnd = 8;
        var input = (search.Text, search.CaretIndex, search.SelectionStart, search.SelectionEnd);
        var draft = editor.Draft;
        string raw = draft.Raw.GetRawText();
        object selection = actions.SelectedItem!;
        object items = actions.ItemsSource!;
        Control[] tree = Controls(editor).ToArray();
        int selectionChanges = 0;
        actions.SelectionChanged += (_, _) => selectionChanges++;
        long revision = ShortcutService.Revision;
        var live = ShortcutService.Current;

        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Same(draft, editor.Draft);
            Assert.Equal(raw, editor.Draft.Raw.GetRawText());
            Assert.Equal(dirty, editor.IsDirty);
            Assert.Equal(input, (search.Text, search.CaretIndex, search.SelectionStart, search.SelectionEnd));
            Assert.Same(items, actions.ItemsSource);
            Assert.Same(selection, actions.SelectedItem);
            Assert.Single(actions.Items);
            Assert.Equal(ActionId, Get<string>(editor, "_selected"));
            Assert.Equal(0, selectionChanges);
            AssertSameControls(tree, Controls(editor).ToArray());
            AssertFrame(editor, dirty);
            Assert.False(ShortcutService.IsCapturing);
            Assert.Equal(revision, ShortcutService.Revision);
            Assert.Same(live, ShortcutService.Current);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RefreshRetainsTheSameActiveLeasePendingGestureAndModifierChoice(bool hasPending, bool keepExplicit)
    {
        using var fixture = new EditorFixture();
        ShortcutEditor editor = fixture.Editor;
        Select(editor, ActionId);
        Click(editor, "clearBinding");
        CheckBox explicitModifiers = Get<CheckBox>(editor, "_explicit");
        explicitModifiers.IsChecked = keepExplicit;
        Click(editor, "recordAdditional");
        KeyGestureInput capture = Get<KeyGestureInput>(editor, "_capture");
        var primary = EffectiveShortcutBindings.CurrentPlatform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Control;
        if (hasPending) capture.Gesture = new KeyGesture(Key.F9, primary | KeyModifiers.Shift);
        object lease = Get<object>(editor, "_captureLease");
        ShortcutGesture? pending = Get<ShortcutGesture?>(editor, "_pending");
        var gesture = capture.Gesture;
        string? pendingText = Get<TextBlock>(editor, "_pendingLabel").Text;
        var draft = editor.Draft;
        string raw = draft.Raw.GetRawText();
        ListBox actions = Get<ListBox>(editor, "_actions");
        object selection = actions.SelectedItem!;
        Control[] tree = Controls(editor).ToArray();
        long revision = ShortcutService.Revision;
        var live = ShortcutService.Current;

        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.True(ShortcutService.IsCapturing);
            Assert.True(capture.IsEnabled);
            Assert.Same(lease, Get<object>(editor, "_captureLease"));
            Assert.Same(pending, Get<ShortcutGesture?>(editor, "_pending"));
            Assert.Same(gesture, capture.Gesture);
            Assert.Equal(pendingText, Get<TextBlock>(editor, "_pendingLabel").Text);
            Assert.Equal((bool?)keepExplicit, explicitModifiers.IsChecked);
            Assert.Same(selection, actions.SelectedItem);
            Assert.Same(draft, editor.Draft);
            Assert.Equal(raw, editor.Draft.Raw.GetRawText());
            Assert.True(editor.IsDirty);
            AssertSameControls(tree, Controls(editor).ToArray());
            AssertFrame(editor, customUnbound: true);
        }

        // Continue through the real GestureProperty/ReadCapture path after refresh.
        // Only the explicit production Apply button is allowed to alter the draft.
        capture.Gesture = new KeyGesture(Key.F10, primary | KeyModifiers.Shift);
        Assert.True(ShortcutService.IsCapturing);
        Assert.Equal(raw, editor.Draft.Raw.GetRawText());
        Click(editor, "applyRecording");
        Assert.False(ShortcutService.IsCapturing);
        Assert.False(capture.IsEnabled);
        ShortcutGesture applied = Assert.Single(EffectiveShortcutBindings.Build(editor.Draft).RequestedGestures[ActionId]);
        Assert.Equal("F10", applied.Key);
        Assert.Contains(keepExplicit ? primary.ToString() : "Primary", applied.Modifiers);
        Assert.DoesNotContain(keepExplicit ? "Primary" : primary.ToString(), applied.Modifiers);
        Assert.Equal(revision, ShortcutService.Revision);
        Assert.Same(live, ShortcutService.Current);
        Assert.Equal("{\"future\":[1,2]}", editor.Draft.Raw.GetProperty("Overrides").GetProperty("future.command").GetRawText());
    }

    [Fact]
    public void AltGrWarningIsRelocalizedWithoutResurrectingTheRejectedPendingGesture()
    {
        using var fixture = new EditorFixture();
        ShortcutEditor editor = fixture.Editor;
        Select(editor, ActionId);
        Click(editor, "recordAdditional");
        KeyGestureInput capture = Get<KeyGestureInput>(editor, "_capture");
        capture.Gesture = new KeyGesture(Key.F9, KeyModifiers.Shift);
        object lease = Get<object>(editor, "_captureLease");
        capture.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = Key.Q,
            KeyModifiers = KeyModifiers.Control | KeyModifiers.Alt
        });
        Assert.Null(Get<ShortcutGesture?>(editor, "_pending"));
        string raw = editor.Draft.Raw.GetRawText();
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T("settings.shortcutsEditor.altGrText"), Get<TextBlock>(editor, "_pendingLabel").Text);
            Assert.Null(Get<ShortcutGesture?>(editor, "_pending"));
            Assert.Same(lease, Get<object>(editor, "_captureLease"));
            Assert.True(ShortcutService.IsCapturing);
            Assert.Equal(raw, editor.Draft.Raw.GetRawText());
        }
        Click(editor, "applyRecording");
        Assert.Equal(raw, editor.Draft.Raw.GetRawText());
        Assert.False(ShortcutService.IsCapturing);
    }

    [Fact]
    public void GestureLimitMessageRetainsPendingStateAndTracksTheLiveLanguage()
    {
        using var fixture = new EditorFixture();
        ShortcutEditor editor = fixture.Editor;
        editor.Load(editor.Draft.Set(ActionId, Enumerable.Range(1, 8)
            .Select(number => ShortcutGesture.Parse("Shift+F" + number)).ToArray()));
        Select(editor, ActionId);
        Click(editor, "recordAdditional");
        Get<KeyGestureInput>(editor, "_capture").Gesture = new KeyGesture(Key.F10, KeyModifiers.Shift);
        Click(editor, "applyRecording");
        var pending = Get<ShortcutGesture>(editor, "_pending");
        var draft = editor.Draft;
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T("settings.shortcutsEditor.gestureLimit"), Get<TextBlock>(editor, "_pendingLabel").Text);
            Assert.Same(pending, Get<ShortcutGesture>(editor, "_pending"));
            Assert.Same(draft, editor.Draft);
            Assert.False(ShortcutService.IsCapturing);
            Assert.False(editor.IsDirty);
        }
        Click(editor, "cancelRecording");
        Texts.SetLanguage("fr");
        Assert.Equal("", Get<TextBlock>(editor, "_pendingLabel").Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyAndTakeOverRetainTheirDistinctDraftOnlyConflictSemantics(bool takeOver)
    {
        using var fixture = new EditorFixture();
        ShortcutEditor editor = fixture.Editor;
        editor.Load(editor.Draft.Set(ActionId, Array.Empty<ShortcutGesture>())
            .Set("navigation.rules", new[] { ShortcutGesture.Parse("Shift+F10") }));
        Select(editor, ActionId);
        Click(editor, "recordAdditional");
        Get<KeyGestureInput>(editor, "_capture").Gesture = new KeyGesture(Key.F10, KeyModifiers.Shift);
        Texts.SetLanguage("ru");
        long revision = ShortcutService.Revision;
        var live = ShortcutService.Current;
        Click(editor, takeOver ? "takeOver" : "applyRecording");
        var snapshot = EffectiveShortcutBindings.Build(editor.Draft);
        Assert.Single(snapshot.RequestedGestures[ActionId]);
        Assert.Equal(takeOver ? 0 : 1, snapshot.RequestedGestures["navigation.rules"].Count);
        Assert.Equal(!takeOver, snapshot.Issues.Any(issue => issue.BlocksSave &&
            (issue.CommandId == ActionId || issue.OtherCommandId == ActionId)));
        Assert.True(editor.IsDirty);
        Assert.False(ShortcutService.IsCapturing);
        Assert.Equal(revision, ShortcutService.Revision);
        Assert.Same(live, ShortcutService.Current);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("selection")]
    [InlineData("load")]
    [InlineData("tab")]
    [InlineData("escape")]
    public void RealLifecycleActionsStillReleaseCaptureAfterLanguageRefresh(string action)
    {
        using var fixture = new EditorFixture();
        ShortcutEditor editor = fixture.Editor;
        Select(editor, ActionId);
        Click(editor, "recordAdditional");
        Get<KeyGestureInput>(editor, "_capture").Gesture = new KeyGesture(Key.F9, KeyModifiers.Shift);
        Texts.SetLanguage("fr");
        Assert.True(ShortcutService.IsCapturing);
        string raw = editor.Draft.Raw.GetRawText();
        switch (action)
        {
            case "cancel": Click(editor, "cancelRecording"); break;
            case "selection": Select(editor, "navigation.rules"); break;
            case "load": editor.Load(editor.Draft); break;
            default:
                Get<KeyGestureInput>(editor, "_capture").RaiseEvent(new KeyEventArgs
                { RoutedEvent = InputElement.KeyDownEvent, Key = action == "tab" ? Key.Tab : Key.Escape });
                break;
        }
        Assert.False(ShortcutService.IsCapturing);
        Assert.False(Get<KeyGestureInput>(editor, "_capture").IsEnabled);
        Assert.Equal(raw, editor.Draft.Raw.GetRawText());
        Texts.SetLanguage("de");
        Assert.False(ShortcutService.IsCapturing);
        Assert.False(Get<KeyGestureInput>(editor, "_capture").IsEnabled);
    }

    [Fact]
    public void UnsupportedSchemaRemainsReadOnlyAndVerbatimOnRefresh()
    {
        using var fixture = new EditorFixture();
        using var document = JsonDocument.Parse("""{"SchemaVersion":99,"Overrides":{"future.command":{"future":[1,2]}}}""");
        ShortcutEditor editor = fixture.Editor;
        editor.Load(new ShortcutBindingsSection(document.RootElement));
        Select(editor, ActionId);
        var draft = editor.Draft;
        string raw = draft.Raw.GetRawText();
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Same(draft, editor.Draft);
            Assert.Equal(raw, editor.Draft.Raw.GetRawText());
            Assert.False(editor.IsDirty);
            Assert.False(Get<StackPanel>(editor, "_detail").IsEnabled);
            Assert.False(Get<Button>(editor, "_reset").IsEnabled);
            Assert.False(ShortcutService.IsCapturing);
        }
    }

    private static void AssertFrame(ShortcutEditor editor, bool customUnbound)
    {
        Assert.Equal(Texts.T("shortcuts.title"), ((TextBlock)editor.SectionHeading).Text);
        Assert.Equal(Texts.T("settings.shortcutsEditor.search"), Get<TextBox>(editor, "_search").PlaceholderText);
        Assert.Equal(Texts.T("settings.shortcutsEditor.search"), AutomationProperties.GetName(Get<TextBox>(editor, "_search")));
        ListBox actions = Get<ListBox>(editor, "_actions");
        Assert.Equal(Texts.T("settings.shortcutsEditor.actions"), AutomationProperties.GetName(actions));
        Panel root = (Panel)editor.Content!;
        Assert.Contains(root.Children.OfType<TextBlock>(), text => text.Text == Texts.T("settings.shortcutsEditor.hint"));
        Assert.Equal(new[] { "action", "context", "effectiveBinding" }.Select(key => Texts.T("settings.shortcutsEditor." + key)),
            root.Children.OfType<Grid>().Single().Children.OfType<TextBlock>().Select(text => text.Text));
        foreach (string key in new[] { "resetAll", "recordAdditional", "clearBinding", "restoreDefault", "applyRecording", "takeOver", "cancelRecording" })
        {
            Button button = FindButton(editor, key);
            Assert.Equal(Texts.T("settings.shortcutsEditor." + key), AutomationProperties.GetName(button));
        }
        Assert.Equal(Texts.T("settings.shortcutsEditor.keepExplicit"), Get<CheckBox>(editor, "_explicit").Content);
        Assert.Equal(Texts.T("settings.shortcutsEditor.record"), AutomationProperties.GetName(Get<KeyGestureInput>(editor, "_capture")));
        Assert.Equal(Texts.T("settings.shortcutsEditor.recordHelp"), AutomationProperties.GetHelpText(Get<KeyGestureInput>(editor, "_capture")));
        Assert.Equal(CommandCatalog.Label(ActionId), ((TextBlock)Get<StackPanel>(editor, "_detail").Children[0]).Text);
        var row = (ListBoxItem)actions.SelectedItem!;
        Assert.Contains(Controls((Control)row.Content!).OfType<TextBlock>(), text => text.Text == CommandCatalog.Label(ActionId));
        Assert.Contains(CommandCatalog.Label(ActionId), AutomationProperties.GetName(row));
        if (customUnbound)
            foreach (string key in new[] { "custom", "unbound" })
            {
                Assert.Contains(Controls((Control)row.Content!).OfType<TextBlock>(), text => text.Text == Texts.T("settings.shortcutsEditor." + key));
                Assert.Contains(Texts.T("settings.shortcutsEditor." + key), AutomationProperties.GetName(row));
            }
    }

    private static void AssertSameControls(Control[] expected, Control[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Same(expected[i], actual[i]);
    }
    private static IEnumerable<Control> Controls(Control control)
    {
        yield return control;
        IEnumerable<Control> children = control switch
        {
            Panel panel => panel.Children,
            Decorator { Child: { } child } => new[] { child },
            ContentControl { Content: Control content } => new[] { content },
            _ => Array.Empty<Control>()
        };
        foreach (Control child in children)
            foreach (Control descendant in Controls(child)) yield return descendant;
    }
    private static void Select(ShortcutEditor editor, string id)
    {
        ListBox list = Get<ListBox>(editor, "_actions");
        list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == id);
    }
    private static Button FindButton(ShortcutEditor editor, string suffix) => Controls(editor).OfType<Button>()
        .Single(button => button.Content as string == Texts.T("settings.shortcutsEditor." + suffix));
    private static void Click(ShortcutEditor editor, string suffix) => FindButton(editor, suffix).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Get<T>(ShortcutEditor editor, string field) =>
        (T)typeof(ShortcutEditor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;

    private sealed class EditorFixture : IDisposable
    {
        public ShortcutEditor Editor { get; } = new();
        private readonly EventHandler _refresh;
        public EditorFixture()
        {
            Assert.False(ShortcutService.IsCapturing);
            using var document = JsonDocument.Parse("""{"SchemaVersion":1,"Overrides":{"future.command":{"future":[1,2]}}}""");
            Editor.Load(new ShortcutBindingsSection(document.RootElement));
            // Detached controls do not subscribe through AttachedToVisualTree.
            // Subscribe the exact production callback, not a duplicate refresh model.
            _refresh = typeof(ShortcutEditor).GetMethod("OnLanguageChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler>(Editor);
            Texts.Changed += _refresh;
        }
        public void Dispose()
        {
            Texts.Changed -= _refresh;
            typeof(ShortcutEditor).GetMethod("StopCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Editor, null);
        }
    }
}
