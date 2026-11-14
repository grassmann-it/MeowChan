using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Views.Shortcuts;
using MeowChan.Desktop.Localization;
using MeowChan.Testing;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

// Requires Root's BuiltInCatalog integration of ShortcutIssueCatalog.Entries.
// No alternate test-only translator: missing integration must fail visibly.
[Collection("Localization")]
public sealed class ShortcutIssueLocalizationTests : IDisposable
{
    private const string ActionId = "navigation.archive";
    private readonly string _language = Texts.Language;
    public ShortcutIssueLocalizationTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    private static readonly (string Gesture, string Code)[] InvalidGestures =
    [
        ("Unicorn+K", "unknownModifier"), ("ImeProcessed", "imeKey"),
        ("Unicorn", "unknownKey"), ("Shift+Shift+K", "duplicateModifiers"),
        ("Primary+Control+K", "repeatedPlatformModifier"), ("Tab", "fixedNavigation"),
        ("Control+Alt+K", "altGrText"), ("Alt+F4", "reservedSystem")
    ];

    [Fact]
    public void AllSixteenProducedHostReasonsHaveDistinctCatalogKeysAndPreservePublicDiagnostics()
    {
        var reasons = new List<ShortcutIssueReason>();
        foreach (var (gestureText, code) in InvalidGestures)
        {
            var gesture = ShortcutGesture.Parse(gestureText);
            var reason = EffectiveShortcutBindings.InvalidIssueReason(gesture, ShortcutPlatform.Linux)!;
            Assert.Equal(code, reason.Code);
            Assert.Equal(EffectiveShortcutBindings.InvalidReason(gesture, ShortcutPlatform.Linux), reason.LegacyMessage);
            var issue = Assert.Single(EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set(ActionId, [gesture]),
                ShortcutPlatform.Linux).Issues.Where(i => i.CommandId == ActionId));
            Assert.Equal(reason.Code, issue.Reason!.Code);
            Assert.Equal(reason.Arguments, issue.Reason.Arguments);
            Assert.Equal(reason.LegacyMessage, issue.Message);
            Assert.True(issue.BlocksSave);
            reasons.Add(issue.Reason);
        }
        reasons.Add(EffectiveShortcutBindings.InvalidIssueReason(new("K", [], "!"), ShortcutPlatform.Linux)!);
        reasons.Add(ReasonFor(Read("""{"SchemaVersion":99,"Overrides":{"future":{}}}"""), "unsupportedSchema"));
        reasons.Add(ReasonFor(Read("""{"SchemaVersion":1,"Overrides":{"future.command":{"keep":true}}}"""), "unknownAction"));
        reasons.Add(ReasonFor(Read("""{"SchemaVersion":1,"Overrides":{"navigation.archive":{"Mode":"Replace","Gestures":[]}}}"""), "invalidMode"));
        var entries = Enumerable.Range(0, 512).ToDictionary(i => "future." + i, _ => (object)new { });
        entries.Add(ActionId, new { Mode = "Unbound", Gestures = Array.Empty<object>() });
        reasons.Add(ReasonFor(Read(JsonSerializer.Serialize(new { SchemaVersion = 1, Overrides = entries })), "limitsExceeded"));
        reasons.Add(ReasonFor(new ShortcutBindingsSection().Set("gallery.previous", [ShortcutGesture.Parse("Up")]), "defaultYields"));
        reasons.Add(ReasonFor(new ShortcutBindingsSection().Set("post.open-source", [ShortcutGesture.Parse("Shift+F10")]), "notImplemented"));
        reasons.Add(ReasonFor(ConflictDraft(), "conflictingActions"));

        Assert.Equal(16, ShortcutIssueCatalog.Entries.Length);
        Assert.Equal(16, ShortcutIssueCatalog.Entries.Select(e => e.Key).Distinct().Count());
        Assert.Equal(ShortcutIssueCatalog.Entries.Select(e => e.Key).Order(),
            reasons.Select(r => ShortcutIssueCatalog.Prefix + r.Code).Order());
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            foreach (var reason in reasons)
            {
                var entry = Assert.Single(ShortcutIssueCatalog.Entries.Where(e => e.Key == ShortcutIssueCatalog.Prefix + reason.Code));
                string expected = string.Format(CultureInfo.GetCultureInfo(language), language == "en" ? entry.English : entry.German,
                    reason.Arguments.Cast<object?>().ToArray());
                Assert.Equal(expected, ShortcutIssueCatalog.Render(reason));
                Assert.DoesNotContain(ShortcutIssueCatalog.Prefix, ShortcutIssueCatalog.Render(reason));
            }
        }
    }

    [Fact]
    public void RawArgumentsAndExactFormatExceptionContractSurviveLanguageChanges()
    {
        const string raw = "Unicorn_{0}_Ä/原";
        var gesture = new ShortcutGesture("K", [raw]);
        var exception = Assert.Throws<FormatException>(() => gesture.Resolve(ShortcutPlatform.Linux));
        Assert.Equal("Unknown modifier: " + raw, exception.Message);
        var reason = EffectiveShortcutBindings.InvalidIssueReason(gesture, ShortcutPlatform.Linux)!;
        Assert.Equal("unknownModifier", reason.Code);
        Assert.Equal(raw, Assert.Single(reason.Arguments));
        foreach (string language in new[] { "en", "de", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal((language == "de" ? "Unbekannter Modifikator: " : "Unknown modifier: ") + raw, ShortcutIssueCatalog.Render(reason));
            Assert.Equal("Unknown modifier: " + raw, EffectiveShortcutBindings.InvalidReason(gesture, ShortcutPlatform.Linux));
        }
        string?[] mutable = [raw];
        var copied = new ShortcutIssueReason("unknownKey", "legacy", mutable);
        mutable[0] = "changed";
        Assert.Equal(raw, Assert.Single(copied.Arguments));
        Assert.Throws<NotSupportedException>(() => ((IList<string?>)copied.Arguments)[0] = "changed");
    }

    [Fact]
    public void FrameworkDiagnosticsAndCallerCreatedIssuesAreNotTranslatedOrParsedAsHostReasons()
    {
        var malformed = Read("""{"SchemaVersion":1,"Overrides":{"navigation.archive":{"Mode":"Replace"}}}""");
        var issue = Assert.Single(EffectiveShortcutBindings.Build(malformed).Issues.Where(i => i.CommandId == ActionId));
        Assert.True(issue.BlocksSave);
        Assert.Null(issue.Reason!.Code);
        Assert.Empty(issue.Reason.Arguments);
        var publicCopy = new ShortcutIssue(issue.CommandId, issue.Message, issue.OtherCommandId, issue.BlocksSave);
        Assert.Equal(publicCopy, issue); // Added provenance cannot change public record equality.
        Assert.Equal(publicCopy.GetHashCode(), issue.GetHashCode());
        var hostIssue = ShortcutIssue.FromReason(ActionId, ReasonFor(ConflictDraft(), "conflictingActions"));
        var changedMessage = hostIssue with { Message = "caller raw {0}" };
        Assert.Equal("caller raw {0}", ShortcutIssueCatalog.Render(changedMessage));
        var callerIssue = new ShortcutIssue("future.{0}", "Reserved system shortcut / Reserviertes Systemkürzel {0}");
        foreach (string language in new[] { "en", "de", "fr" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(issue.Message, ShortcutIssueCatalog.Render(issue));
            Assert.Equal(callerIssue.Message, ShortcutIssueCatalog.Render(callerIssue));
        }
    }

    [Fact]
    public void RetainedIssueRendersAgainWhileSchemaConflictOwnershipAndRawDraftStayUnchanged()
    {
        var draft = ConflictDraft();
        string raw = draft.Raw.GetRawText();
        var result = EffectiveShortcutBindings.Build(draft);
        var conflict = Assert.Single(result.Issues.Where(i => i.Reason?.Code == "conflictingActions"));
        var ownership = (conflict.CommandId, conflict.OtherCommandId, conflict.BlocksSave);
        Assert.True(conflict.BlocksSave);
        Assert.Empty(result.For(ActionId));
        Assert.Empty(result.For("navigation.rules"));
        Assert.Single(result.RequestedGestures[ActionId]);
        Assert.Single(result.RequestedGestures["navigation.rules"]);
        foreach (string language in new[] { "de", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(language == "de" ? "Kollidierende Aktionen" : "Conflicting actions", ShortcutIssueCatalog.Render(conflict));
            Assert.Equal(ownership, (conflict.CommandId, conflict.OtherCommandId, conflict.BlocksSave));
            Assert.Equal(raw, draft.Raw.GetRawText());
        }
        using var fixture = new EditorFixture();
        fixture.Editor.Load(Read("""{"SchemaVersion":99,"Overrides":{"future.action":{"opaque":[1,2]}}}"""));
        var schemaDraft = fixture.Editor.Draft;
        string schemaBytes = schemaDraft.Raw.GetRawText();
        foreach (string language in new[] { "de", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Contains(Texts.T("shortcuts.issue.unsupportedSchema"), Get<TextBlock>(fixture.Editor, "_errors").Text!);
            Assert.False(Get<TextBlock>(fixture.Editor, "_errors").Focusable);
            Assert.Equal(Texts.T("settings.shortcutsEditor.recordHelp"),
                AutomationProperties.GetHelpText(Get<KeyGestureInput>(fixture.Editor, "_capture")));
            Assert.Same(schemaDraft, fixture.Editor.Draft);
            Assert.Equal(schemaBytes, fixture.Editor.Draft.Raw.GetRawText());
            Assert.False(fixture.Editor.IsDirty);
            Assert.False(schemaDraft.IsEditable);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingInvalidCaptureRefreshesBeforeAndAfterApplyWithoutChangingEditorState(bool apply)
    {
        using var fixture = new EditorFixture();
        var editor = fixture.Editor;
        editor.Load(ConflictDraft());
        Select(editor, ActionId);
        Click(editor, "recordAdditional");
        var capture = Get<KeyGestureInput>(editor, "_capture");
        capture.Gesture = new KeyGesture(Key.F4, KeyModifiers.Alt);
        Assert.Equal("Reserved system shortcut", Get<TextBlock>(editor, "_pendingLabel").Text);
        if (apply) Click(editor, "applyRecording");
        var draft = editor.Draft;
        string raw = draft.Raw.GetRawText();
        var pending = Get<ShortcutGesture>(editor, "_pending");
        var lease = Get<IDisposable?>(editor, "_captureLease");
        var gesture = capture.Gesture;
        var actions = Get<ListBox>(editor, "_actions");
        var items = actions.ItemsSource;
        var selection = actions.SelectedItem;
        var detail = Get<StackPanel>(editor, "_detail").Children.ToArray();
        var current = ShortcutService.Current;
        long revision = ShortcutService.Revision;
        foreach (string language in new[] { "de", "en", "de" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(language == "de" ? "Reserviertes Systemkürzel" : "Reserved system shortcut", Get<TextBlock>(editor, "_pendingLabel").Text);
            Assert.Contains(language == "de" ? "Kollidierende Aktionen" : "Conflicting actions", Get<TextBlock>(editor, "_errors").Text!);
            Assert.Equal(Texts.T("settings.shortcutsEditor.issuesHelp") + Get<TextBlock>(editor, "_errors").Text,
                AutomationProperties.GetHelpText(capture));
            Assert.Contains(Texts.T("shortcuts.issue.conflictingActions"), AutomationProperties.GetHelpText(capture)!);
            Assert.Same(draft, editor.Draft);
            Assert.Equal(raw, editor.Draft.Raw.GetRawText());
            Assert.False(editor.IsDirty);
            Assert.Same(pending, Get<ShortcutGesture>(editor, "_pending"));
            Assert.Same(gesture, capture.Gesture);
            Assert.Same(lease, Get<IDisposable?>(editor, "_captureLease"));
            Assert.Equal(!apply, ShortcutService.IsCapturing);
            Assert.Equal(!apply, capture.IsEnabled);
            Assert.Same(items, actions.ItemsSource);
            Assert.Same(selection, actions.SelectedItem);
            Assert.Equal(ActionId, Get<string>(editor, "_selected"));
            Assert.Equal(detail.Length, Get<StackPanel>(editor, "_detail").Children.Count);
            for (int i = 0; i < detail.Length; i++) Assert.Same(detail[i], Get<StackPanel>(editor, "_detail").Children[i]);
            Assert.Equal(revision, ShortcutService.Revision);
            Assert.Same(current, ShortcutService.Current);
        }
    }

    [Fact]
    public void RecorderHelpReturnsToGeneralInstructionsAfterPriorIssuesAndStaysCorrectAcrossLanguages()
    {
        using var fixture = new EditorFixture();
        var editor = fixture.Editor;
        editor.Load(ConflictDraft());
        Select(editor, ActionId);
        var capture = Get<KeyGestureInput>(editor, "_capture");
        Texts.SetLanguage("de");
        Assert.Contains(Texts.T("shortcuts.issue.conflictingActions"), AutomationProperties.GetHelpText(capture)!);

        editor.Load(new ShortcutBindingsSection());
        Select(editor, ActionId);
        var draft = editor.Draft;
        string raw = draft.Raw.GetRawText();
        Assert.Empty(EffectiveShortcutBindings.Build(draft).Issues);
        Assert.Equal("", Get<TextBlock>(editor, "_errors").Text);
        Assert.Equal(Texts.T("settings.shortcutsEditor.recordHelp"), AutomationProperties.GetHelpText(capture));
        Click(editor, "recordAdditional");
        capture.Gesture = new KeyGesture(Key.F4, KeyModifiers.Alt);
        var pending = Get<ShortcutGesture>(editor, "_pending");
        var gesture = capture.Gesture;
        var lease = Get<IDisposable?>(editor, "_captureLease");
        Assert.NotNull(lease);
        var current = ShortcutService.Current;
        long revision = ShortcutService.Revision;
        foreach (string language in new[] { "en", "de", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Empty(EffectiveShortcutBindings.Build(editor.Draft).Issues);
            Assert.Equal("", Get<TextBlock>(editor, "_errors").Text);
            Assert.Equal(Texts.T("settings.shortcutsEditor.recordHelp"), AutomationProperties.GetHelpText(capture));
            Assert.Equal(Texts.T("shortcuts.issue.reservedSystem"), Get<TextBlock>(editor, "_pendingLabel").Text);
            Assert.Same(capture, Get<KeyGestureInput>(editor, "_capture"));
            Assert.Same(pending, Get<ShortcutGesture>(editor, "_pending"));
            Assert.Same(gesture, capture.Gesture);
            Assert.Same(lease, Get<IDisposable?>(editor, "_captureLease"));
            Assert.True(ShortcutService.IsCapturing);
            Assert.True(capture.IsEnabled);
            Assert.Same(draft, editor.Draft);
            Assert.Equal(raw, editor.Draft.Raw.GetRawText());
            Assert.False(editor.IsDirty);
            Assert.Equal(revision, ShortcutService.Revision);
            Assert.Same(current, ShortcutService.Current);
        }
    }

    [Theory]
    [InlineData("unknownAction")]
    [InlineData("defaultYields")]
    [InlineData("notImplemented")]
    public void AdvisoryWarningsRemainVisibleWithoutReplacingRecorderInstructions(string code)
    {
        using var fixture = new EditorFixture();
        var editor = fixture.Editor;
        editor.Load(ConflictDraft());
        Select(editor, ActionId);
        var capture = Get<KeyGestureInput>(editor, "_capture");
        Texts.SetLanguage("de");
        Assert.Contains(Texts.T("shortcuts.issue.conflictingActions"), AutomationProperties.GetHelpText(capture)!);

        var warningDraft = code switch
        {
            "unknownAction" => Read("""{"SchemaVersion":1,"Overrides":{"future.command":{"future":[1,2]}}}"""),
            "defaultYields" => new ShortcutBindingsSection().Set("gallery.previous", [ShortcutGesture.Parse("Up")]),
            "notImplemented" => new ShortcutBindingsSection().Set("post.open-source", [ShortcutGesture.Parse("Shift+F10")]),
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };
        editor.Load(warningDraft);
        Select(editor, ActionId);
        var draft = editor.Draft;
        string raw = draft.Raw.GetRawText();
        Click(editor, "recordAdditional");
        capture.Gesture = new KeyGesture(Key.F4, KeyModifiers.Alt);
        var pending = Get<ShortcutGesture>(editor, "_pending");
        var gesture = capture.Gesture;
        var lease = Get<IDisposable?>(editor, "_captureLease");
        Assert.NotNull(lease);
        var actions = Get<ListBox>(editor, "_actions");
        var items = actions.ItemsSource;
        var selection = actions.SelectedItem;
        var detail = Get<StackPanel>(editor, "_detail").Children.ToArray();
        var current = ShortcutService.Current;
        long revision = ShortcutService.Revision;
        foreach (string language in new[] { "es", "fr", "ru", "de", "en" })
        {
            Texts.SetLanguage(language);
            var issues = EffectiveShortcutBindings.Build(editor.Draft).Issues;
            Assert.NotEmpty(issues);
            Assert.All(issues, issue => Assert.False(issue.BlocksSave));
            Assert.Contains(issues, issue => issue.Reason?.Code == code);
            var errors = Get<TextBlock>(editor, "_errors");
            Assert.False(errors.Focusable);
            Assert.Contains(Texts.T("shortcuts.issue." + code), errors.Text!);
            Assert.Equal(Texts.T("settings.shortcutsEditor.recordHelp"), AutomationProperties.GetHelpText(capture));
            Assert.Equal(Texts.T("shortcuts.issue.reservedSystem"), Get<TextBlock>(editor, "_pendingLabel").Text);
            Assert.Same(capture, Get<KeyGestureInput>(editor, "_capture"));
            Assert.Same(pending, Get<ShortcutGesture>(editor, "_pending"));
            Assert.Same(gesture, capture.Gesture);
            Assert.Same(lease, Get<IDisposable?>(editor, "_captureLease"));
            Assert.True(ShortcutService.IsCapturing);
            Assert.True(capture.IsEnabled);
            Assert.Same(draft, editor.Draft);
            Assert.Equal(raw, editor.Draft.Raw.GetRawText());
            Assert.False(editor.IsDirty);
            Assert.Same(items, actions.ItemsSource);
            Assert.Same(selection, actions.SelectedItem);
            Assert.Equal(ActionId, Get<string>(editor, "_selected"));
            Assert.Equal(detail.Length, Get<StackPanel>(editor, "_detail").Children.Count);
            for (int i = 0; i < detail.Length; i++) Assert.Same(detail[i], Get<StackPanel>(editor, "_detail").Children[i]);
            Assert.Equal(revision, ShortcutService.Revision);
            Assert.Same(current, ShortcutService.Current);
        }

        // A real blocking error must still replace general help after advisory-only state.
        editor.Load(warningDraft.Set(ActionId, [ShortcutGesture.Parse("Shift+F9")])
            .Set("navigation.rules", [ShortcutGesture.Parse("Shift+F9")]));
        Select(editor, ActionId);
        Texts.SetLanguage("de");
        Assert.Contains(EffectiveShortcutBindings.Build(editor.Draft).Issues, issue => issue.BlocksSave);
        var mixedErrors = Get<TextBlock>(editor, "_errors");
        Assert.True(mixedErrors.Focusable);
        Assert.Contains(Texts.T("shortcuts.issue." + code), mixedErrors.Text!);
        Assert.Contains(Texts.T("shortcuts.issue.conflictingActions"), mixedErrors.Text!);
        Assert.Equal(Texts.T("settings.shortcutsEditor.issuesHelp") + mixedErrors.Text, AutomationProperties.GetHelpText(capture));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameLanguagePackReloadRerendersRetainedReasonsConflictAndRecorderHelp(bool apply)
    {
        // Synthetic isolated pack only, created by a future test run, never a personal pack.
        string directory = TemporaryWorkspacePaths.CreateDirectory("meowchan-shortcut-issues-");
        try
        {
            WritePack("A");
            Texts.InitializeWithoutBundlesForTests(directory);
            Assert.Empty(Texts.Warnings);
            Texts.SetLanguage("fr");
            using var fixture = new EditorFixture();
            var editor = fixture.Editor;
            editor.Load(ConflictDraft());
            Select(editor, ActionId);
            Click(editor, "recordAdditional");
            var capture = Get<KeyGestureInput>(editor, "_capture");
            capture.Gesture = new KeyGesture(Key.F4, KeyModifiers.Alt);
            if (apply) Click(editor, "applyRecording");
            var draft = editor.Draft;
            string raw = draft.Raw.GetRawText();
            var pending = Get<ShortcutGesture>(editor, "_pending");
            var issue = ReasonFor(ConflictDraft(), "conflictingActions");
            Assert.Equal("Conflict A", ShortcutIssueCatalog.Render(issue));
            Assert.Equal("Reserved A", Get<TextBlock>(editor, "_pendingLabel").Text);
            var lease = Get<IDisposable?>(editor, "_captureLease");
            var gesture = capture.Gesture;
            var actions = Get<ListBox>(editor, "_actions");
            var items = actions.ItemsSource;
            var selection = actions.SelectedItem;
            var detail = Get<StackPanel>(editor, "_detail").Children.ToArray();
            var current = ShortcutService.Current;
            long revision = ShortcutService.Revision;
            foreach (string version in new[] { "B", "C" })
            {
                WritePack(version);
                Texts.InitializeWithoutBundlesForTests(directory); // raises Changed, language stays fr
                Assert.Empty(Texts.Warnings);
                Assert.Equal("fr", Texts.Language);
                Assert.Equal("Conflict " + version, ShortcutIssueCatalog.Render(issue));
                var errors = Get<TextBlock>(editor, "_errors");
                Assert.Contains("Conflict " + version, errors.Text!);
                Assert.Equal("Issue help " + version + ": " + errors.Text, AutomationProperties.GetHelpText(capture));
                Assert.Contains("Conflict " + version, AutomationProperties.GetHelpText(capture)!);
                Assert.DoesNotContain("Recorder help " + version, AutomationProperties.GetHelpText(capture)!);
                Assert.Equal("Reserved " + version, Get<TextBlock>(editor, "_pendingLabel").Text);
                Assert.Same(capture, Get<KeyGestureInput>(editor, "_capture"));
                Assert.Same(draft, editor.Draft);
                Assert.Equal(raw, editor.Draft.Raw.GetRawText());
                Assert.Same(pending, Get<ShortcutGesture>(editor, "_pending"));
                Assert.Same(gesture, capture.Gesture);
                Assert.Same(lease, Get<IDisposable?>(editor, "_captureLease"));
                Assert.False(editor.IsDirty);
                Assert.Equal(!apply, ShortcutService.IsCapturing);
                Assert.Equal(!apply, capture.IsEnabled);
                Assert.Same(items, actions.ItemsSource);
                Assert.Same(selection, actions.SelectedItem);
                Assert.Equal(ActionId, Get<string>(editor, "_selected"));
                Assert.Equal(detail.Length, Get<StackPanel>(editor, "_detail").Children.Count);
                for (int i = 0; i < detail.Length; i++) Assert.Same(detail[i], Get<StackPanel>(editor, "_detail").Children[i]);
                Assert.Equal(revision, ShortcutService.Revision);
                Assert.Same(current, ShortcutService.Current);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }

        void WritePack(string version) => File.WriteAllText(Path.Combine(directory, "fr.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, code = "fr", nativeName = "Synthetic French",
            strings = new Dictionary<string, string>
            {
                ["shortcuts.issue.conflictingActions"] = "Conflict " + version,
                ["shortcuts.issue.reservedSystem"] = "Reserved " + version,
                ["settings.shortcutsEditor.issuesHelp"] = "Issue help " + version + ": ",
                ["settings.shortcutsEditor.recordHelp"] = "Recorder help " + version
            }
        }));
    }

    private static ShortcutBindingsSection ConflictDraft() => new ShortcutBindingsSection()
        .Set(ActionId, [ShortcutGesture.Parse("Shift+F9")])
        .Set("navigation.rules", [ShortcutGesture.Parse("Shift+F9")]);
    private static ShortcutBindingsSection Read(string json)
    { using var document = JsonDocument.Parse(json); return new(document.RootElement); }
    private static ShortcutIssueReason ReasonFor(ShortcutBindingsSection section, string code) => Assert.Single(
        EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux).Issues.Where(i => i.Reason?.Code == code)).Reason!;
    private static T Get<T>(ShortcutBindingsEditor editor, string field) =>
        (T)typeof(ShortcutBindingsEditor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
    private static void Select(ShortcutBindingsEditor editor, string id)
    {
        var list = Get<ListBox>(editor, "_actions");
        list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == id);
    }
    private static void Click(ShortcutBindingsEditor editor, string suffix) => Controls(editor).OfType<Button>()
        .Single(button => button.Content as string == Texts.T("settings.shortcutsEditor." + suffix))
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<Control> Controls(Control control)
    {
        yield return control;
        IEnumerable<Control> children = control switch
        {
            Panel panel => panel.Children,
            Decorator { Child: { } child } => [child],
            ContentControl { Content: Control content } => [content],
            _ => []
        };
        foreach (var child in children)
            foreach (var descendant in Controls(child)) yield return descendant;
    }
    private sealed class EditorFixture : IDisposable
    {
        public ShortcutBindingsEditor Editor { get; } = new();
        private readonly EventHandler _refresh;
        public EditorFixture()
        {
            Assert.False(ShortcutService.IsCapturing);
            _refresh = typeof(ShortcutBindingsEditor).GetMethod("OnLanguageChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler>(Editor);
            Texts.Changed += _refresh;
        }
        public void Dispose()
        {
            Texts.Changed -= _refresh;
            typeof(ShortcutBindingsEditor).GetMethod("StopCapture", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Editor, null);
        }
    }
}
