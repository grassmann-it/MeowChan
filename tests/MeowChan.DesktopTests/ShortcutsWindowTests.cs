using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Views.Shortcuts;

namespace MeowChan.DesktopTests;

// Modals plan KS2: the one shortcuts window. A Window needs a platform backend, so these drive its detached editor, its pure
// decisions and the wiring in the sources; the shown window is DesktopSmoke's shortcut-presentation run. The save handlers
// complete synchronously, so Save finishes inside the click on the test thread (Avalonia objects are thread-bound).
[Collection("Localization")]
public sealed class ShortcutsWindowTests : IDisposable
{
    private readonly string _language = Texts.Language;
    public ShortcutsWindowTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    private static ShortcutHelpContext ReaderContext() => new(ShortcutGroups.Reader,
        new[] { "reader.next-post", "navigation.archive", "session.pause" }, id => id == "navigation.statistics", CommandScope.Reader,
        new[] { "help.reader.mediaActivation" });

    [Fact]
    public void F1TogglesAndEachPageOpensOnItsOwnArea()
    {
        Assert.Equal(ShortcutsWindowToggle.Open, ShortcutsWindow.ToggleDecision(open: false));
        Assert.Equal(ShortcutsWindowToggle.Close, ShortcutsWindow.ToggleDecision(open: true));
        Assert.Equal(ShortcutGroups.Reader, ShortcutHelpContext.AreaFor("Archive", largeReaderOpen: true));
        foreach (string page in new[] { "Downloads", "Archive", "Media", "Rules", "Sources", "Plugins", "Diagnostics", "Statistics", "Settings" })
            Assert.Equal(page.ToLowerInvariant(), ShortcutHelpContext.AreaFor(page, largeReaderOpen: false));
        Assert.Null(ShortcutHelpContext.AreaFor("About", largeReaderOpen: false));
        Assert.False(ShortcutHelpContext.Settings.AssignedOnly);
        Assert.Null(ShortcutHelpContext.Settings.Area);
    }

    [Fact]
    public void TheF1ContextOpensItsAreaExpandedOnTopAndTheSettingsContextEverything()
    {
        var editor = new ShortcutBindingsEditor();
        editor.ApplyContext(ReaderContext());
        Assert.Equal(ShortcutGroups.Order.Where(g => g != ShortcutGroups.Reader).Order(), editor.CollapsedGroups.Order());
        ListBoxItem[] items = editor.ActionList.Items.OfType<ListBoxItem>().ToArray();
        Assert.IsNotType<string>(items[0].Tag); // the reader group header comes first
        Assert.Contains(Texts.T("shortcuts.group.current", Texts.T("shortcuts.group.reader")), Avalonia.Automation.AutomationProperties.GetName(items[0]));
        Assert.Equal("reader.next-post", items[1].Tag); // the context's preferred command leads its group
        Assert.True(((CheckBox)Field(editor, "_assignedOnly")).IsChecked);
        // Every visible command row belongs to the reader group; the others show only their headers.
        Assert.All(items.Where(i => i.Tag is string), i => Assert.Equal(ShortcutGroups.Reader,
            editor.Model.Rows.Single(r => r.CommandId == (string)i.Tag!).Group));

        editor.ApplyContext(ShortcutHelpContext.Settings);
        Assert.Empty(editor.CollapsedGroups);
        Assert.False(((CheckBox)Field(editor, "_assignedOnly")).IsChecked);
        Assert.Equal(CommandCatalog.All.Count, editor.ActionList.Items.OfType<ListBoxItem>().Count(i => i.Tag is string));
    }

    [Fact]
    public void ASearchShowsHitsInCollapsedGroupsAndTheEmptyStateSaysSo()
    {
        var editor = new ShortcutBindingsEditor();
        editor.ApplyContext(ReaderContext());
        editor.SearchInput.Text = "navigation.statistics";
        Assert.Contains(editor.ActionList.Items.OfType<ListBoxItem>(), i => i.Tag as string == "navigation.statistics");
        editor.SearchInput.Text = "no action is called like this";
        Assert.Empty(editor.ActionList.Items);
        Assert.True(((TextBlock)Field(editor, "_empty")).IsVisible);
    }

    [Fact]
    public void PlainTextIsTheFormerF1ListWithOneGeneralNote()
    {
        var editor = new ShortcutBindingsEditor();
        editor.ApplyContext(ReaderContext());
        string text = editor.PlainText;
        Assert.StartsWith(Texts.T("help.reader.mediaActivation"), text);
        Assert.Contains(Texts.T("help.scope.current"), text);
        Assert.Contains(Texts.T("help.scope.application"), text);
        Assert.Contains(CommandCatalog.Label("navigation.archive") + "   " + CommandCatalog.ShortcutDisplay("navigation.archive"), text);
        Assert.Equal(2, text.Split(Texts.T("help.scope.generalNote")).Length);
        Assert.DoesNotContain(CommandCatalog.ScopeHint("navigation.archive")!, text);
        Assert.EndsWith(Texts.T("help.escape"), text);
    }

    [Fact]
    public void SaveCommitsTheSharedDraftOnceAndAConflictNeverReachesTheHost()
    {
        var session = new ShortcutDraftSession(new ShortcutBindingsSection());
        var editor = new ShortcutBindingsEditor(session);
        List<ShortcutBindingsSection> commits = new();
        editor.SaveHandler = draft => { commits.Add(draft); return Task.CompletedTask; };
        Assert.False(editor.SaveButton.IsEnabled); // nothing to save
        session.Clear("navigation.archive");
        Assert.True(editor.SaveButton.IsEnabled);
        ShortcutBindingsSection expected = session.Draft;
        editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Same(expected, Assert.Single(commits));
        Assert.False(session.IsDirty);
        Assert.Equal(Texts.T("shortcuts.window.saved"), editor.StatusText.Text);

        session.Set("navigation.archive", new[] { ShortcutGesture.Parse("Primary+Shift+K") });
        session.Set("navigation.rules", new[] { ShortcutGesture.Parse("Primary+Shift+K") });
        editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Single(commits);
        Assert.True(session.IsDirty);
        Assert.Contains(Texts.T("shortcuts.issue.conflictingActions"), editor.ErrorText.Text);
        Assert.True(editor.Model.Rows.Single(r => r.CommandId == "navigation.rules").HasBlockingConflict);
    }

    [Fact]
    public void AFailedSaveKeepsTheDraftAndSaysWhy()
    {
        var session = new ShortcutDraftSession(new ShortcutBindingsSection());
        var editor = new ShortcutBindingsEditor(session) { SaveHandler = _ => throw new InvalidOperationException("busy") };
        session.Clear("navigation.archive");
        editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(session.IsDirty);
        Assert.Equal(Texts.T("shortcuts.window.saveFailed", "busy"), editor.StatusText.Text);
        Assert.False(editor.IsSaving);
    }

    [Fact]
    public void ClosingWithChangesAsksAndEscapeKeepsEditing()
    {
        var session = new ShortcutDraftSession(new ShortcutBindingsSection());
        var editor = new ShortcutBindingsEditor(session);
        session.Clear("navigation.archive");
        List<bool> decisions = new();
        editor.AskBeforeClose(decisions.Add);
        Assert.True(editor.IsAskingBeforeClose);
        Assert.True(editor.TryCancelLocal()); // Escape: keep editing
        Assert.Equal(new[] { false }, decisions);
        Assert.True(session.IsDirty);
        editor.AskBeforeClose(decisions.Add);
        Button discard = ((Border)Field(editor, "_closePrompt")).DescendantsOfType<Button>()
            .Single(b => (string?)b.Content == Texts.T("shortcuts.window.discard"));
        discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(new[] { false, true }, decisions);
        Assert.False(session.IsDirty);
        Assert.False(editor.TryCancelLocal());
    }

    [Fact]
    public void TheSettingsCardAndTheWindowEditTheSameDraft()
    {
        var card = new ShortcutEditor();
        card.Load(new ShortcutBindingsSection());
        var editor = new ShortcutBindingsEditor(card.Session);
        int edits = 0;
        card.DraftEdited += (_, _) => edits++;
        editor.Session.Clear("navigation.archive");
        Assert.Equal(1, edits);
        Assert.True(card.IsDirty);
        Assert.Same(editor.Draft, card.Draft);
        Assert.Contains(Texts.T("shortcuts.card.custom", 1), TextsOf(card));
        card.Load(new ShortcutBindingsSection());
        Assert.False(editor.IsDirty);
        Assert.Equal(1, edits); // a load is not an edit
        editor.Detach();
        object? items = editor.ActionList.ItemsSource;
        card.Session.Clear("navigation.rules");
        Assert.Same(items, editor.ActionList.ItemsSource); // a closed window no longer follows the draft
    }

    [Fact]
    public void F1TheMacHelpMenuAndSettingsShareOneWindowAndOneCommitPath()
    {
        string fullscreen = Source("Desktop/Shell/MainWindow.Fullscreen.cs");
        string show = Between(fullscreen, "private void ShowShellHelp(", "private void OnShortcutsWindowClosed(");
        Assert.Contains("ShortcutsWindow.Open(this, SettingsEditor, context", show);
        Assert.Contains("_gallery.ShortcutHelp() : ShellHelpContext()", show);
        Assert.Contains("ShortcutsWindow.IsPassive(window)", fullscreen);
        Assert.Contains("case \"viewer.shortcut-help\": ShowShellHelp(); return;", Source("Desktop/Shell/MainWindow.MacMenu.cs"));
        string settings = Source("Desktop/Views/Settings/SettingsEditor.axaml.cs");
        Assert.Contains("ShortcutBindingsCommit.ApplyTo(candidate, ShortcutsEditor.Draft);", settings);
        Assert.Contains("await ShortcutBindingsCommit.PersistAsync(candidate, _applySettings);", settings);
        Assert.Contains("await ShortcutBindingsCommit.CommitAsync(_config, draft, _applySettings);", settings);
        Assert.Contains("ShortcutsEditor.Host = this;", settings);
        string window = Source("Desktop/Views/Shortcuts/ShortcutsWindow.cs");
        Assert.Contains("DialogWindow.Configure(this, DialogSize.L, modal: false);", window);
        Assert.Contains("DialogEscape.Attach(this, Editor.TryCancelLocal, () => Editor.IsSaving);", window);
        Assert.Contains("e.CloseReason != WindowCloseReason.WindowClosing", window);
    }

    [Fact]
    public void TheGalleryContextOpensTheGalleryGroupThenTheTagPanelAndKeepsTheWayBackNote()
    {
        var context = new ShortcutHelpContext(ShortcutGroups.Gallery, new[] { "gallery.next", "gallery.previous" },
            NoteKeys: new[] { "viewer.help.escape" }, ExpandedAreas: new[] { ShortcutGroups.TagPanel });
        var editor = new ShortcutBindingsEditor();
        editor.ApplyContext(context);
        Assert.Equal(ShortcutGroups.Order.Where(g => g is not (ShortcutGroups.Gallery or ShortcutGroups.TagPanel)).Order(),
            editor.CollapsedGroups.Order());
        string[] groups = editor.Model.Query(new ShortcutTableQuery()).Select(g => g.Key).ToArray();
        Assert.Equal(new[] { ShortcutGroups.Gallery, ShortcutGroups.TagPanel }, groups.Take(2));
        ListBoxItem[] items = editor.ActionList.Items.OfType<ListBoxItem>().ToArray();
        Assert.Equal("gallery.next", items[1].Tag); // the first preferred command leads the gallery group
        // The tag panel's commands wait for their handlers (QuickTagCommands) and "Assigned only" hides them until then.
        Assert.All(items.Where(i => i.Tag is string), i => Assert.Contains(editor.Model.Rows.Single(r => r.CommandId == (string)i.Tag!).Group,
            new[] { ShortcutGroups.Gallery, ShortcutGroups.TagPanel }));
        // The note of the former gallery help is no shortcut and stays reachable in the window.
        Assert.Contains(Texts.T("viewer.help.escape"), editor.PlainText);
        Assert.Contains(Texts.T("viewer.help.escape"), ((TextBlock)Field(editor, "_notes")).Text!);
    }

    [Fact]
    public void TheGalleryAsksTheOwnerForTheWindowAndNeitherItNorTheMediaMenuTreatsItAsABlockingDialog()
    {
        string gallery = Source("Desktop/GalleryViewer.cs");
        Assert.Contains("{ RequestShortcutHelp(); e.Handled = true; return; }", gallery);
        Assert.Contains("\"GalleryHelp\", Texts.T(\"viewer.help\"), RequestShortcutHelp, overflow: true);", gallery);
        Assert.Contains("ShortcutGroups.Gallery, preferred", gallery);
        Assert.Contains("ExpandedAreas: new[] { ShortcutGroups.TagPanel }", gallery);
        Assert.Contains("\"viewer.help.escape.media\" : \"viewer.help.escape\"", gallery);
        Assert.Equal(1, CountOf(gallery, "ShortcutsWindow.IsPassive(window)")); // CanRouteGalleryShortcut
        Assert.Equal(1, CountOf(Source("Desktop/GalleryViewer.Overlay.cs"), "ShortcutsWindow.IsPassive(window)")); // HasGalleryDialog
        Assert.Contains("ShortcutsWindow.IsPassive(window)", Source("Desktop/MediaActions/MediaMenuBuilder.cs"));
        Assert.Contains("ShortcutsWindow.IsPassive(window)", Source("Desktop/Shell/MainWindow.Media.cs"));
        Assert.Contains("_gallery.ShortcutHelpRequested += context =>", Source("Desktop/Shell/MainWindow.Gallery.cs"));
    }

    private static int CountOf(string text, string needle) => (text.Length - text.Replace(needle, "", StringComparison.Ordinal).Length) / needle.Length;

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(target)!;

    private static IEnumerable<string> TextsOf(Control root) => root.DescendantsOfType<TextBlock>().Select(t => t.Text ?? "");

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return text[from..to];
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string caller = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../..", relative)));
}

internal static class LogicalTreeTestExtensions
{
    internal static IEnumerable<T> DescendantsOfType<T>(this Control root) where T : Control
    {
        IEnumerable<Control> children = root switch
        {
            Panel panel => panel.Children,
            Decorator { Child: { } child } => new[] { child },
            ContentControl { Content: Control content } => new[] { content },
            _ => Array.Empty<Control>()
        };
        foreach (Control child in children)
        {
            if (child is T match) yield return match;
            foreach (T descendant in child.DescendantsOfType<T>()) yield return descendant;
        }
    }
}
