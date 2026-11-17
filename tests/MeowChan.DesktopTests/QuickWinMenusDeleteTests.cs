using System.Runtime.CompilerServices;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Quick-win package "menus, Delete, Esc" (interaction audit 1.1.0, V8, V9, V14): the pure parts (which media Delete may remove from the queue, the
// catalog contexts, the revision-20 yield) are tested on plain objects; the wiring of the pages (Diagnostics key, Booru cards, Reader strip, filmstrip tile)
// is GUI, so its source is the contract here, as in the other page tests. The Esc behaviour of the Media search field has its page tests in
// MediaTextBreakdownPageTests.
[Collection("Localization")]
public sealed class QuickWinMenusDeleteTests : IDisposable
{
    public QuickWinMenusDeleteTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Read(string relative, [CallerFilePath] string sourcePath = "") =>
        BooruViewSources.ReadAll(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private static readonly DateTimeOffset Day = DateTimeOffset.UnixEpoch;

    private static MediaLibraryEntry Entry(string thread, char hash, string? directory = "/dir", string? asset = "asset") =>
        new(new ResourceId("4chan", "g", thread), "T" + thread, 1, Day, Day, thread, long.Parse(thread), null, 0, "att-v1:" + new string(hash, 64),
            directory, asset == null ? null : asset + thread, new string(hash, 64), 1, "m" + thread, "original", "n" + thread + ".webm", SavedMediaKind.Video,
            MediaLibraryPresence.Present, false, false);

    // ---- V9: Delete on the media page ----------------------------------------------------------------------------------------

    [Fact]
    public void DeleteRemovesOnlyQueuedMediaAndNamesNothingElse()
    {
        MediaLibraryEntry queued = Entry("1", 'a'), other = Entry("2", 'b'), unsaved = Entry("3", 'c', directory: null, asset: null);
        TagQueueKey key = TagQueueKeys.Of(queued)!.Value;
        HashSet<TagQueueKey> queue = [key];
        Assert.Equal([key], TagQueueActions.RemovableKeys([other, queued, unsaved, queued], queue)); // once each, only queued
        Assert.Empty(TagQueueActions.RemovableKeys([other, unsaved], queue)); // not queued: Delete does nothing
        Assert.Empty(TagQueueActions.RemovableKeys([queued], new HashSet<TagQueueKey>())); // empty queue
        Assert.Empty(TagQueueActions.RemovableKeys([queued], null)); // unreadable queue
        Assert.Empty(TagQueueActions.RemovableKeys([], queue));
    }

    [Fact]
    public void TheSameCommandNowActsInTheMediaGridAndStaysYieldingFromRevisionTwenty()
    {
        CommandDefinition command = CommandCatalog.Get("tagging.remove");
        Assert.Equal("Delete", command.Shortcut);
        Assert.Contains("tagging-list-menu", command.Contexts);
        Assert.Contains("media-grid-keys", command.Contexts);
        Assert.Contains("media", command.ActiveContexts!);
        Assert.Contains("tagging", command.ActiveContexts!);
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 20);
        Assert.Contains("tagging.remove", EffectiveShortcutBindings.YieldingDefaultCommandIds);
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            Assert.True(EffectiveShortcutBindings.YieldsOn("tagging.remove", platform));
            Assert.Empty(EffectiveShortcutBindings.Build(null, platform).Issues);
            Assert.NotEmpty(EffectiveShortcutBindings.Build(null, platform).Display("tagging.remove"));
        }
    }

    [Fact]
    public void ASavedBindingOfAnotherMediaCommandOnDeleteWinsAndDeleteNeverRemovesFiles()
    {
        // A profile that bound Delete to another command of the media grid keeps it; the new default stays unbound.
        string other = CommandCatalog.All.Select(c => c.Id).First(id => id != "tagging.remove" && id.StartsWith("media.", StringComparison.Ordinal)
            && EffectiveShortcutBindings.ContextsOverlap(id, "tagging.remove"));
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set(other, [ShortcutGesture.Parse("Delete")]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Empty(bindings.Issues.Where(issue => issue.BlocksSave));
        Assert.Equal("Delete", bindings.Display(other));
        // The key handler removes queue entries and nothing else: no file API is in its path.
        string queue = Read("Desktop/Views/Media/MediaPage.Queue.cs");
        string actions = Read("Desktop/TagQueueActions.cs");
        Assert.Contains("RemovableKeys(", queue);
        Assert.Contains("RemoveFromTagQueue", queue);
        Assert.DoesNotContain("File.Delete", queue + actions);
        Assert.DoesNotContain("Directory.Delete", queue + actions);
        Assert.Contains("CommandBindings.Matches(e, \"tagging.remove\")", Read("Desktop/Views/Media/MediaPage.cs"));
    }

    // ---- V14: Diagnostics evaluates the rebindable context-menu command ---------------------------------------------------------

    [Fact]
    public void TheDiagnosticsTablesEvaluateTheRebindableContextMenuCommand()
    {
        CommandDefinition command = CommandCatalog.Get("menu.context-open");
        Assert.Contains("diagnostics-table", command.Contexts);
        Assert.Contains("diagnostics", command.ActiveContexts!);
        string keys = Read("Desktop/Views/Diagnostics/DiagnosticsPage.Interaction.cs");
        Assert.Contains("CommandBindings.Matches(e, \"menu.context-open\")", keys);
        Assert.Contains("OpenDiagnosticKeyboardMenu(", keys);
        Assert.Contains("void OpenDiagnosticKeyboardMenu(", Read("Desktop/Views/Diagnostics/DiagnosticsPage.ContextMenus.cs"));
    }

    // ---- V8: menus where there were none ---------------------------------------------------------------------------------------

    [Fact]
    public void BooruHitsAndWatchersHaveAContextMenuWhoseEntriesPressTheCardsOwnButtons()
    {
        string cards = Read("Desktop/Views/Booru/BooruWorkbenchView.CardMenus.cs");
        string view = Read("Desktop/Views/Booru/BooruWorkbenchView.cs");
        Assert.Contains("AttachCardMenu(", view);
        Assert.Contains("ComposeResultMenu(", view);
        Assert.Contains("ComposeWatcherMenu(", view);
        Assert.Contains("new ContextMenuHost(", cards);
        Assert.Contains("Press(", cards);
        // The same commands' icons and labels: nothing invented for the shared entries.
        Assert.Contains("\"thread.show-in-media\"", cards);
        Assert.Contains("\"post.open-source\"", cards);
        Assert.Contains("\"post.copy-link\"", cards);
        Assert.Contains("\"watch.check\"", cards);
        Assert.Contains("\"watch.remove\"", cards);
        // Removing a watcher is the last section.
        Assert.True(cards.IndexOf("MenuRemove, CommandCatalog", StringComparison.Ordinal) > cards.IndexOf("MenuCheck", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReaderSidebarStripAndTheFilmstripTileOpenTheMediaMenuOfTheirOwnMedium()
    {
        string archive = Read("Desktop/Views/Archive/ArchivePage.MediaCommands.cs");
        Assert.Contains("SidebarStripContext(", archive);
        string strip = Read("Desktop/Controls/GalleryFilmstrip.cs");
        Assert.Contains("TileContextRequested", strip);
        Assert.Contains("GalleryFilmstripPlan.TileAt(", strip);
        string viewer = Read("Desktop/GalleryViewer.Filmstrip.cs");
        Assert.Contains("OnFilmstripTileContextRequested", viewer);
        string menu = Read("Desktop/GalleryViewer.MediaMenu.cs");
        // A tile that is not the shown medium gets the menu without the favourite (it reads the shown medium's own state); its "Rate and tag…" opens
        // the popover on the filmstrip for that tile's medium.
        Assert.Contains("item.Action != MediaMenuAction.Favorite", menu);
        Assert.Contains("RateAndTagTileRequested?.Invoke(entry, _filmstrip)", menu);
    }
}
