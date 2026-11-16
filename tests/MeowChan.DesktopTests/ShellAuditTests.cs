using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Shell/global audit (16.11.): the global layer's dead entries. Each test fails on the code before the fix; plain objects, no window.
[Collection("Media shortcut routing")]
public sealed class ShellAuditTests
{
    private static KeyEventArgs Press(object source, Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        new() { Source = source, Key = key, KeyModifiers = modifiers, RoutedEvent = InputElement.KeyDownEvent };

    private static string Source(params string[] relative)
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "MeowChan.sln")) && !Directory.Exists(Path.Combine(dir, "Desktop", "Shell")))
            dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine([dir!, .. relative]));
    }

    // Tray: "Add URLs from clipboard" was always enabled; before a restore or while an import runs it did nothing.
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void TheTrayClipboardEntryIsEnabledOnlyWhereTheImportCanRun(bool restored, bool importing, bool busy, bool expected) =>
        Assert.Equal(expected, TrayMenuModel.ClipboardEnabled(restored, importing, busy));

    [Fact]
    public void TheTrayRefreshAppliesTheClipboardState() =>
        Assert.Contains("TrayMenuModel.ClipboardEnabled(", Source("Desktop", "Shell", "MainWindow.axaml.cs"), StringComparison.Ordinal);

    // Page navigation (Ctrl+1..8, Ctrl+Shift+letter, pause) is the window's own: a text field swallowed it.
    [Fact]
    public void PageNavigationChordsReachTheWindowFromATextFieldButTypedLettersDoNot()
    {
        OwnedUiDispatcher.Run(() =>
        {
            ShortcutService.Publish(new ShortcutBindingsSection());
            var box = new TextBox();
            foreach (string id in MainPageOrder.CommandIds)
            {
                Assert.True(CommandBindings.IsShellNavigationCommand(id), id);
                ShortcutGesture gesture = Assert.Single(ShortcutService.Current.For(id).Take(1));
                var resolved = gesture.Resolve(ShortcutService.Current.Platform);
                var e = Press(box, resolved.Key, resolved.KeyModifiers);
                if (CommandBindings.IsTextSafeChord(e)) Assert.True(CommandBindings.Matches(e, id), id);
                else Assert.False(CommandBindings.Matches(e, id), id);
            }
            // A bare letter and Shift+letter stay text even for a navigation command.
            Assert.False(CommandBindings.IsTextSafeChord(Press(box, Key.A)));
            Assert.False(CommandBindings.IsTextSafeChord(Press(box, Key.A, KeyModifiers.Shift)));
            Assert.False(CommandBindings.IsTextSafeChord(Press(box, Key.A, KeyModifiers.Alt)));
            Assert.True(CommandBindings.IsTextSafeChord(Press(box, Key.D3, KeyModifiers.Control)));
            Assert.True(CommandBindings.IsTextSafeChord(Press(box, Key.F5)));
            Assert.False(CommandBindings.IsShellNavigationCommand("archive.search"));
        });
    }

    // Downloads lists: a saved binding of an archive command on the key of "Open context menu" (which also runs in the downloads lists) made the default
    // yield in the archive only; the list asked without a context and so counted it as yielded everywhere. "Check now" is no example here: its
    // default changed only in the macOS table, so on Windows and Linux it keeps the symmetric conflict rule and does not yield per context.
    [Fact]
    public void TheDownloadListsKeepADefaultThatASavedArchiveCommandTookOnlyFromTheArchive()
    {
        OwnedUiDispatcher.Run(() =>
        {
            try
            {
                ShortcutGesture check = ShortcutService.Current.For("menu.context-open")[0];
                ShortcutService.Publish(new ShortcutBindingsSection().Set("archive.refresh", [check]));
                var resolved = check.Resolve(ShortcutService.Current.Platform);
                var e = Press(new Border(), resolved.Key, resolved.KeyModifiers);
                Assert.False(CommandBindings.Matches(e, "menu.context-open"));
                Assert.True(MainWindow.DownloadsMatches(e, "menu.context-open"));
            }
            finally { ShortcutService.Publish(new ShortcutBindingsSection()); }
        });
    }

    [Fact]
    public void TheDownloadListHandlerAsksInItsOwnContextOnly()
    {
        string text = Source("Desktop", "Shell", "MainWindow.Selection.cs");
        int start = text.IndexOf("private async void OnDownloadListKeyDown", StringComparison.Ordinal);
        int end = text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        string handler = text[start..end];
        Assert.DoesNotContain("CommandBindings.Matches(", handler, StringComparison.Ordinal);
        Assert.Contains("DownloadsMatches(e, \"watch.check\")", handler, StringComparison.Ordinal);
    }

    // "Show new posts automatically" has no handler on the Downloads lists: its key was a dead entry in the help and the conflict check there.
    [Fact]
    public void FollowLiveIsAReaderCommandOnly()
    {
        Assert.Equal(new[] { "reader" }, EffectiveShortcutBindings.ExecutionContexts("watch.follow-live"));
    }

    // "Thread tags" (the reader's key) claimed the reader without a handler on the reader pane.
    [Fact]
    public void TheReaderPaneRegistersTheThreadTagsKey()
    {
        string text = Source("Desktop", "Shell", "MainWindow.Shortcuts.cs");
        Assert.Contains("new CommandRegistration(\"thread.tags\", readerPane", text, StringComparison.Ordinal);
    }

    // Keys assigned to commands that only a menu item or a window command carries ran nowhere; the shell now runs them.
    [Fact]
    public void TheShellRunsTheKeysOfCommandsThatOnlyAMenuItemOrTheWindowCarries()
    {
        string text = Source("Desktop", "Shell", "MainWindow.Fullscreen.cs");
        Assert.Contains("TryRunUnroutedShellCommand(e)", text, StringComparison.Ordinal);
        string shortcuts = Source("Desktop", "Shell", "MainWindow.Shortcuts.cs");
        foreach (string id in new[] { "window.maximize", "window.close", "app.show" })
            Assert.Contains($"\"{id}\"", shortcuts, StringComparison.Ordinal);
    }

    // "Close" (About) is a catalog entry without any control or handler: a key bound to it ran nothing. ("Back to image" in the gallery
    // is wired by GalleryViewer.EntryPoints and stays available.)
    [Theory]
    [InlineData("about.close")]
    public void CommandsWithoutAnyControlOrHandlerAreListedAsUnavailable(string id)
    {
        CommandDefinition command = CommandCatalog.Get(id);
        Assert.False(command.IsImplemented);
        Assert.Equal(CommandAvailability.Unavailable, command.Availability);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set(id, [ShortcutGesture.Parse("Primary+Shift+J")]));
        Assert.Empty(bindings.For(id));
        Assert.Contains(bindings.Issues, issue => issue.CommandId == id && issue.Reason?.Code == "notImplemented");
    }
}
