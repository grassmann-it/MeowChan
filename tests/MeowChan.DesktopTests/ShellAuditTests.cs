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

    // S3 P1-1: Primary+, (⌘,) from the gallery or the large reader pressed the Settings menu entry inside the power block, which opened Settings behind
    // the overlay. The menu route leaves Settings and the URL field to the shell key handler's own branches, which close the overlay first.
    [Theory]
    [InlineData("navigation.settings")]
    [InlineData("input.focus-url")]
    public void TheMenuRouteLeavesSettingsAndTheUrlFieldToTheBranchesThatCloseTheOverlayFirst(string id)
    {
        Assert.False(MainWindow.IsUnroutedShellCommand(id));
        string shortcuts = Source("Desktop", "Shell", "MainWindow.Shortcuts.cs");
        int start = shortcuts.IndexOf("private bool TryRunUnroutedShellCommand", StringComparison.Ordinal);
        int end = shortcuts.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.Contains("IsUnroutedShellCommand(id)", shortcuts[start..end], StringComparison.Ordinal);
        // The branch that runs instead closes the gallery and the large reader before it navigates.
        string shell = Source("Desktop", "Shell", "MainWindow.Fullscreen.cs");
        int branch = shell.IndexOf($"CommandBindings.Matches(e, \"{id}\"))", shell.IndexOf("TryRunUnroutedShellCommand(e)", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(branch > 0, id);
        string body = shell[branch..shell.IndexOf("NavigateWorkspace(", branch, StringComparison.Ordinal)];
        Assert.Contains("CloseGallery(restoreOrigin: false)", body, StringComparison.Ordinal);
        Assert.Contains("CloseLargeReader(restoreOrigin: false)", body, StringComparison.Ordinal);
    }

    // S3 P2-2/P2-3: the menu route runs in the window's tunnel, before every page. Every command of the app menu is a shell command, so its key is
    // compared with every page's keys; a command of a page (or of the Settings page only) is never pressed through a menu entry from elsewhere.
    [Theory]
    [InlineData("app.quit")]
    [InlineData("navigation.about")]
    [InlineData("navigation.reading-history")]
    [InlineData("booru.open")]
    [InlineData("post-bookmarks.open-manager")]
    [InlineData("appearance.theme")]
    [InlineData("updates.check")]
    [InlineData("reader-groups.open-manager")]
    public void EveryAppMenuCommandIsAShellCommandTheMenuRouteMayRun(string id)
    {
        Assert.Contains("shell", EffectiveShortcutBindings.ExecutionContexts(id));
        Assert.True(MainWindow.IsUnroutedShellCommand(id), id);
    }

    [Theory]
    [InlineData("archive.refresh")]
    [InlineData("favorites.toggle")]
    [InlineData("plugins.refresh")]
    public void APageCommandIsNeverPressedThroughTheMenuRoute(string id)
    {
        Assert.DoesNotContain("shell", EffectiveShortcutBindings.ExecutionContexts(id));
        Assert.False(MainWindow.IsUnroutedShellCommand(id), id);
    }

    // S3 P2-2: "Change theme" on F was accepted (settings against gallery, media, reader and archive) and then toggled the theme from the gallery,
    // swallowing the favourite key. As a shell command the check sees the collision: the newer favourite default yields to the saved key, and two
    // saved keys conflict.
    [Fact]
    public void AThemeKeyOnFIsComparedWithThePageKeys()
    {
        ShortcutGesture f = ShortcutGesture.Parse("F");
        EffectiveShortcutBindings yielded = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("appearance.theme", [f]), ShortcutPlatform.Linux);
        Assert.Contains(yielded.Issues, issue => issue.CommandId == "favorites.toggle" && issue.OtherCommandId == "appearance.theme");
        Assert.DoesNotContain(yielded.For("favorites.toggle"), gesture => EffectiveShortcutBindings.Equivalent(gesture, f, ShortcutPlatform.Linux));
        EffectiveShortcutBindings both = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("appearance.theme", [f]).Set("favorites.toggle", [f]), ShortcutPlatform.Linux);
        Assert.Contains(both.Issues, issue => issue.Reason?.Code == "conflictingActions"
            && (issue.CommandId, issue.OtherCommandId) is ("appearance.theme", "favorites.toggle") or ("favorites.toggle", "appearance.theme"));
    }

    // S3 P2-3: the "Check for updates" entry was created without its command, so the menu route (which finds entries by their command) never found it.
    [Fact]
    public void TheCheckForUpdatesEntryCarriesItsCommand()
    {
        string updates = Source("Desktop", "Shell", "MainWindow.Updates.cs");
        Assert.Contains("CommandBindings.Apply(_checkUpdatesMenu, \"updates.check\"", updates, StringComparison.Ordinal);
        Assert.True(MainWindow.IsUnroutedShellCommand("updates.check"));
    }

    // S3 P2-1: the shell's page loop skipped the tagging page and left it to a router registration that refused text fields, the gallery and the
    // large reader. Every page chord, Tagging included, now comes out of the same route (ShellPageCommand), from a text field too.
    [Fact]
    public void EveryPageChordLeadsTheShellToItsPageFromATextFieldTaggingIncluded()
    {
        OwnedUiDispatcher.Run(() =>
        {
            ShortcutService.Publish(new ShortcutBindingsSection());
            var box = new TextBox();
            Assert.Contains("navigation.tagging", MainPageOrder.CommandIds);
            foreach (string id in MainPageOrder.CommandIds)
            foreach (ShortcutGesture gesture in ShortcutService.Current.For(id))
            {
                var resolved = gesture.Resolve(ShortcutService.Current.Platform);
                var e = Press(box, resolved.Key, resolved.KeyModifiers);
                if (!CommandBindings.IsTextSafeChord(e)) continue;
                Assert.Equal(id, MainWindow.ShellPageCommand(e, canOpenBooru: true));
                if (id == "navigation.booru") Assert.Null(MainWindow.ShellPageCommand(e, canOpenBooru: false));
            }
            Assert.Null(MainWindow.ShellPageCommand(Press(box, Key.A), canOpenBooru: true));
        });
        string shell = Source("Desktop", "Shell", "MainWindow.Fullscreen.cs");
        Assert.DoesNotContain("if (id == \"navigation.tagging\") continue;", shell, StringComparison.Ordinal);
        Assert.Contains("ShellPageCommand(e, CanOpenBooru)", shell, StringComparison.Ordinal);
        Assert.Contains("WorkspacePage.Tagging => ShowTaggingPage()", shell, StringComparison.Ordinal);
        // No second, narrower route is left behind for the tagging key.
        Assert.DoesNotContain("new CommandRegistration(\"navigation.tagging\"", Source("Desktop", "Shell", "MainWindow.TaggingPage.cs"), StringComparison.Ordinal);
    }

    // S3 P2-5: the window's own routes (its router and the visible actions) run in its bubble handler, after every page scope has closed. They now ask
    // in the context of the page the key comes from, so a default that gives way to a saved binding in another context only keeps working there.
    [Fact]
    public void TheWindowsRoutesAskInTheContextOfTheKeysPage()
    {
        const ShortcutPlatform linux = ShortcutPlatform.Linux;
        // "Thread tags" (archive and reader) after a saved reader command on its key: alive in the archive, gone in the reader.
        ShortcutGesture tags = EffectiveShortcutBindings.Build(null, linux).For("thread.tags")[0];
        EffectiveShortcutBindings readerSaved = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("watch.follow-live", [tags]), linux);
        KeyEventArgs tagsKey = Press(new Border(), tags.Resolve(linux).Key, tags.Resolve(linux).KeyModifiers);
        Assert.False(readerSaved.Matches(tagsKey, "thread.tags"));
        Assert.Equal("archive", MainWindow.ShellKeyContext(null, MainWindow.WorkspacePage.Archive, false, false, StatusSegment.Statistics));
        using (EffectiveShortcutBindings.EnterKeyContext(MainWindow.ShellKeyContext(null, MainWindow.WorkspacePage.Archive, false, false, StatusSegment.Statistics)!))
            Assert.True(readerSaved.Matches(tagsKey, "thread.tags"));
        using (EffectiveShortcutBindings.EnterKeyContext(MainWindow.ShellKeyContext(null, MainWindow.WorkspacePage.Downloads, true, false, StatusSegment.Statistics)!))
            Assert.False(readerSaved.Matches(tagsKey, "thread.tags")); // the large reader over any page
        // The page host's own answer comes first: the embedded reader pane inside the archive.
        Assert.Equal("reader", MainWindow.ShellKeyContext("reader", MainWindow.WorkspacePage.Archive, false, false, StatusSegment.Statistics));

        // "Edit tags" (media and gallery) after a saved gallery command on its key: alive on the Media page.
        ShortcutGesture edit = EffectiveShortcutBindings.Build(null, linux).For("media.tags.edit")[0];
        EffectiveShortcutBindings gallerySaved = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("gallery.close", [edit]), linux);
        KeyEventArgs editKey = Press(new Border(), edit.Resolve(linux).Key, edit.Resolve(linux).KeyModifiers);
        using (EffectiveShortcutBindings.EnterKeyContext(MainWindow.ShellKeyContext(null, MainWindow.WorkspacePage.Media, false, false, StatusSegment.Statistics)!))
            Assert.True(gallerySaved.Matches(editKey, "media.tags.edit"));
        using (EffectiveShortcutBindings.EnterKeyContext(MainWindow.ShellKeyContext(null, MainWindow.WorkspacePage.Media, false, true, StatusSegment.Statistics)!))
            Assert.False(gallerySaved.Matches(editKey, "media.tags.edit"));

        // macOS: "Check now" (⌥⌘R) on the Diagnostics retry button after a saved archive command on ⌥⌘R.
        ShortcutGesture check = EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS).For("watch.check")[0];
        EffectiveShortcutBindings archiveSaved = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("archive.refresh", [check]), ShortcutPlatform.MacOS);
        KeyEventArgs checkKey = Press(new Border(), check.Resolve(ShortcutPlatform.MacOS).Key, check.Resolve(ShortcutPlatform.MacOS).KeyModifiers);
        Assert.Equal("diagnostics", MainWindow.ShellKeyContext(null, MainWindow.WorkspacePage.Status, false, false, StatusSegment.Diagnostics));
        using (EffectiveShortcutBindings.EnterKeyContext("diagnostics"))
            Assert.True(archiveSaved.Matches(checkKey, "watch.check"));
        using (EffectiveShortcutBindings.EnterKeyContext("archive"))
            Assert.False(archiveSaved.Matches(checkKey, "watch.check"));

        string shortcuts = Source("Desktop", "Shell", "MainWindow.Shortcuts.cs");
        int start = shortcuts.IndexOf("private bool RouteWorkspaceShortcut(KeyEventArgs e)", StringComparison.Ordinal);
        int end = shortcuts.IndexOf("private bool RouteWorkspaceShortcutInContext", start, StringComparison.Ordinal);
        Assert.Contains("using (EffectiveShortcutBindings.EnterKeyContext(context)) return RouteWorkspaceShortcutInContext(e);", shortcuts[start..end], StringComparison.Ordinal);
        Assert.Contains("KeyContextHost.ContextOf(e)", shortcuts, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Downloads", "downloads")]
    [InlineData("Archive", "archive")]
    [InlineData("Media", "media")]
    [InlineData("Tagging", "tagging")]
    [InlineData("Booru", "booru")]
    [InlineData("Rules", "rules")]
    [InlineData("Sources", "sources")]
    [InlineData("Status", "statistics")]
    [InlineData("Settings", "settings")]
    public void AKeyFromOutsideEveryPageRunsInTheContextOfTheShownPage(string pageName, string context)
    {
        MainWindow.WorkspacePage page = Enum.Parse<MainWindow.WorkspacePage>(pageName);
        Assert.Equal(context, MainWindow.ShellKeyContext(null, page, false, false, StatusSegment.Statistics));
        Assert.Equal("reader", MainWindow.ShellKeyContext(null, page, true, false, StatusSegment.Statistics));
        Assert.Equal("gallery", MainWindow.ShellKeyContext(null, page, false, true, StatusSegment.Statistics));
        // Every name is a context some command runs in, so the yields recorded for it apply.
        Assert.Contains(CommandCatalog.All, command => command.ExecutionContexts.Contains(context));
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
