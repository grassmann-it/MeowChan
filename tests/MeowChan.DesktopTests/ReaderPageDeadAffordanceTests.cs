using System.Text.Json;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// Package B of the dead-affordance audit (15.11.2026): the media sidebar's menu on every pointer right click (it was only the picture before, and the
// release of the click opened a second menu over the first), the middle click on the media a reader shows, the reader tag chips' menu and the thread
// row's tag filter. Source checks for the wiring (the page needs a window), plain checks for the pure parts.
public sealed class ReaderPageDeadAffordanceTests
{
    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.sln"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(RepositoryRoot(), relative));

    private const string Commands = "Desktop/Views/Archive/ArchivePage.MediaCommands.cs";

    // ---- the sidebar's right click ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ARightClickAnywhereInTheSidebarOpensTheMediaMenuAndOnlyAThumbnailKeepsItsOwn()
    {
        string commands = Source(Commands);
        // The press path no longer asks for the frame: header line, buttons and the card's edge count, a strip thumbnail selects itself first.
        Assert.DoesNotContain("ancestors.Any(control => ReferenceEquals(control, sidebar.FrameArea))", commands);
        Assert.DoesNotContain("keyboard && ReferenceEquals(control, _mediaSidebar)", commands);
        Assert.Contains("ancestors.Any(control => ReferenceEquals(control, sidebar.Strip))) return;", commands);
        Assert.Contains("!keyboard && ancestors.Any(control => ReferenceEquals(control, _mediaSidebar.Strip))", commands);
        Assert.Contains("ReferenceEquals(control, _mediaSidebar))", commands);
    }

    [Fact]
    public void TheReleaseOfTheClickIsConsumedByTheFlagThatTheReleaseEnds()
    {
        string commands = Source(Commands);
        // Avalonia 12 sends ContextRequested with the release, long after a Background tick: the flag must outlive the press and end after the release.
        Assert.Contains("private void OnSidebarPointerReleased(object? sender, PointerReleasedEventArgs e)", commands);
        Assert.Contains("e.InitialPressMouseButton != MouseButton.Right", commands);
        int press = commands.IndexOf("private void OnSidebarPointerPressed", StringComparison.Ordinal);
        int released = commands.IndexOf("private void OnSidebarPointerReleased", StringComparison.Ordinal);
        string pressBody = commands[press..released];
        Assert.DoesNotContain("Dispatcher.UIThread.Post", pressBody); // the press no longer drops the flag on a tick
        Assert.Contains("_sidebarPointerContextPending = true;", pressBody);
        string sidebar = Source("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs");
        Assert.Contains("sidebar.AddHandler(PointerReleasedEvent, OnSidebarPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true)", sidebar);
    }

    // ---- the middle click ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMiddleClickOnAPostMediumTheSidebarPictureAndTheMediaTableShowsTheFolder()
    {
        string keys = Source("Desktop/Views/Archive/ArchivePage.MouseKeyboard.cs");
        Assert.Contains("ArchiveMediaGrid.AddHandler(PointerPressedEvent, OnMediaTableMiddlePressed", keys);
        Assert.Contains("ArchiveReaderPane.AddHandler(PointerPressedEvent, OnPostMediumMiddlePressed", keys);
        Assert.Contains("ArchiveReaderPane.AddHandler(PointerReleasedEvent, OnPostMediumMiddleReleased", keys);
        Assert.Contains("MediaMenuSurface.ArchiveInline", keys);
        Assert.Contains("MediaMenuSurface.ArchiveTable", keys);
        // The one action stays the media menu's own "Show in folder", under its own availability.
        Assert.Contains("MediaFolderCommandId = \"media.show-in-folder\"", keys);
        // The large reader's frame is not under the page: the sidebar watches its own picture, and the pane's handler leaves the sidebar's subtree to it.
        string sidebar = Source("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs");
        Assert.Contains("sidebar.AddHandler(PointerPressedEvent, OnSidebarMiddlePressed, RoutingStrategies.Tunnel, handledEventsToo: true)", sidebar);
        Assert.Contains("sidebar.AddHandler(PointerReleasedEvent, OnSidebarMiddleReleased, RoutingStrategies.Tunnel)", sidebar);
        Assert.Contains("ReferenceEquals(node, _mediaSidebar))) return null;", keys);
    }

    [Fact]
    public void AMiddleClickIsAClickOnlyWhenPressAndReleaseMeetOnTheSameMedium()
    {
        MiddleClickTracker tracker = new();
        object first = new(), second = new();
        tracker.Pressed(first);
        Assert.True(tracker.Released(first));
        tracker.Pressed(first);
        Assert.False(tracker.Released(second)); // a drag over another medium is no click
        Assert.False(tracker.Released(first)); // and the press is used up
    }

    // ---- the tag bar chips and the thread row ----------------------------------------------------------------------------------------

    [Fact]
    public void EveryTagBarChipHasAMenuWithRemoveFilterCopyAndManage()
    {
        string tags = Source("Desktop/Views/Archive/ArchivePage.Tags.cs");
        Assert.Contains("WorkspaceChips.AttachMenu(chip, () => ComposeThreadTagChipMenu(tag.Name.Display, tagId)", tags);
        foreach (string entry in new[] { "reader.tag.remove", "reader.tag.filter", "reader.tag.copy-name", "reader.tag.manage" })
            Assert.Contains("\"" + entry + "\"", tags);
        // The filter runs the archive's own search path, after the large reader closed.
        Assert.Contains("if (_largeReader) CloseReaderRequested?.Invoke();", tags);
        Assert.Contains("await ApplySearchAsync();", tags);
        string commands = Source("Desktop/Views/Archive/ArchivePage.Commands.cs");
        Assert.Contains("if (ids.Length == 1) AddThreadTagFilterSubmenu(builder, lead.Summary.Id);", commands);
    }

    [Fact]
    public void TheNewMenusReuseTextsEveryLanguagePackAlreadyHas()
    {
        string[] keys = { "tags.remove", "tags.filter.includeHint", "tags.manage.open", "tags.filter.label", "menu.unavailable.target", "archive.command.busy" };
        string packs = Path.Combine(RepositoryRoot(), "Desktop", "Localization", "Packs");
        string[] files = Directory.GetFiles(packs, "*.json");
        Assert.Equal(10, files.Length); // plus the built-in English and German: the twelve languages
        foreach (string file in files)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = document.RootElement.GetProperty("strings");
            foreach (string key in keys)
                Assert.True(root.TryGetProperty(key, out _), Path.GetFileName(file) + " lacks " + key);
        }
    }
}
