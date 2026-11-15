using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Menu and icon audit of 15.11.2026 (Tom's review of 1.1.0): an entry that is no catalog command keeps the icon column of its menu
// filled, the same action has the same icon in every menu, and a menu names only shortcuts that work where it opens. Plain models and a
// source scan; no window.
[Collection("Localization")]
public sealed class MenuIconAuditTests : IDisposable
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");

    public MenuIconAuditTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void AnEntryWithoutACommandCarriesItsIconAndACommandBringsItsOwn()
    {
        ContextMenuModel children = new ContextMenuBuilder().Item("child", "Child", () => { }, icon: "plus").ToModel();
        ContextMenuModel model = new ContextMenuBuilder()
            .Command("post.hide", () => { })
            .Item("plain", "Plain", () => { })
            .Item("with-icon", "With icon", () => { }, icon: "eye-off")
            .Submenu("sub", "Sub", children, icon: "list")
            .ToModel();
        ContextMenuEntry[] entries = model.Entries.ToArray();
        Assert.Equal(new string?[] { null, null, "eye-off", "list" }, entries.Select(entry => entry.Icon));
        Assert.Equal("plus", Assert.Single(entries[3].Children!.SelectMany(section => section)).Icon);
    }

    [Fact]
    public void TheTaggingGroupSubmenuAndItsActionsHaveIcons()
    {
        TagQueueItem item = new(Thread, "/media/123", "asset-1", "1" + new string('a', 63), 101, "media-1", "original",
            "p1", null, null, null, false, null, "file-1.png", SavedMediaKind.Image, TagQueueState.Open, DateTimeOffset.UnixEpoch);
        TagQueueSnapshot queue = new(1, 0, Array.Empty<string>(), new[] { item }, TagQueueLoadState.Ready);
        TaggingQueueRow[] rows = TaggingQueueView.Rows(queue, TaggingQueueFilter.All).ToArray();
        TaggingCommandContext context = new(queue, TaggingQueueFilter.All, rows, rows[0], new(true, true, true, true, true, true, true, true, true));
        ContextMenuEntry group = TaggingQueueMenu.Compose(context, _ => () => { }).ToModel().Entries.Single(entry => entry.Id == TaggingCommands.GroupMenu);
        Assert.Equal(TaggingCommands.GroupMenuIcon, group.Icon);
        ContextMenuEntry[] children = group.Children!.SelectMany(section => section).ToArray();
        Assert.Equal(TaggingCommands.GroupNewIcon, children.Single(entry => entry.Id == TaggingCommands.GroupNew).Icon);
        Assert.Equal(TaggingCommands.GroupRemoveIcon, children.Single(entry => entry.Id == TaggingCommands.GroupRemove).Icon);
    }

    [Fact]
    public void TheProviderPostEntriesOfTheMediaMenuTakeThePostCommandIcons()
    {
        MediaMenuFacts facts = new(MediaMenuSurface.MediaPage, new(Thread, "dir", "asset", null, 0, null, "ab12", null))
        { Favorite = MediaMenuFavorite.Off, ProviderMedia = true, ProviderPostKnown = true };
        MediaMenuModel model = MediaMenuComposition.Compose(facts);
        Assert.Equal(CommandCatalog.Get("post.open-source").Icon, model.Items.Single(entry => entry.Action == MediaMenuAction.OpenProviderPost).Icon);
        Assert.Equal(CommandCatalog.Get("post.copy-link").Icon, model.Items.Single(entry => entry.Action == MediaMenuAction.CopyProviderPost).Icon);
        // Command entries get their icon from the command, never a second one.
        Assert.All(model.Items.Where(entry => entry.CommandId != null), entry => Assert.Null(entry.Icon));
    }

    [Fact]
    public void TheSameActionHasTheSameIconAndPairsArePairs()
    {
        // "Show in Media" of a thread and of a tagging entry.
        Assert.Equal(CommandCatalog.Get("thread.show-in-media").Icon, CommandCatalog.Get("tagging.show-in-media").Icon);
        // Seeking back and forward are mirror images, like the seek step.
        Assert.Equal(("rotate-ccw", "rotate-cw"), (CommandCatalog.Get("video.seek-back").Icon, CommandCatalog.Get("video.seek-forward").Icon));
        Assert.Equal(("rotate-ccw", "rotate-cw"), (CommandCatalog.Get("video.step-back").Icon, CommandCatalog.Get("video.step-forward").Icon));
        // Deleting (not removing from a list) is the circled cross everywhere.
        foreach (string id in new[] { "archive.delete-permanently", "reader-groups.delete", "archive.saved-searches.delete", "rules.delete", "rules.template-delete" })
            Assert.True(CommandCatalog.Get(id).Icon == "x-circle", id);
    }

    [Fact]
    public void TheTrayNamesOnlyShortcutsThatWorkFromTheWholeWindow()
    {
        // The tray's "Open folder" opens the download folder; Ctrl/Cmd+O opens the selected thread's folder. The clipboard key acts in the list only.
        Assert.Null(MeowChan.MainWindow.TrayShortcut("location.open-folder"));
        Assert.Null(MeowChan.MainWindow.TrayShortcut("watch.import-clipboard"));
        Assert.Equal(CommandCatalog.ShortcutDisplay("session.pause"), MeowChan.MainWindow.TrayShortcut("session.pause"));
        Assert.Equal(CommandCatalog.ShortcutDisplay("navigation.settings"), MeowChan.MainWindow.TrayShortcut("navigation.settings"));
    }

    [Fact]
    public void TheLabelsOfToggleCommandsUseSentenceCaseAfterTheSlash()
    {
        foreach (CommandDefinition command in CommandCatalog.All)
            Assert.False(Regex.IsMatch(command.LabelEn, " / (?!MeowChan)[A-Z][a-z]"), command.Id + ": " + command.LabelEn);
    }

    // The icons handed to the menu entries by name (icon: "…", the tagging group constants) are masks of the catalog.
    [Fact]
    public void EveryMenuEntryIconNamedInTheSourcesExists()
    {
        Regex named = new("\\bicon: \"([a-z][a-z0-9-]*)\"|Icon = \"([a-z][a-z0-9-]*)\";", RegexOptions.CultureInvariant);
        List<string> missing = [];
        int seen = 0;
        foreach (string path in Directory.EnumerateFiles(Root("Desktop"), "*.cs", SearchOption.AllDirectories))
            foreach (Match match in named.Matches(File.ReadAllText(path)))
            {
                seen++;
                string icon = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (!File.Exists(Root("Assets/icons/feather/masks/" + icon + ".png"))) missing.Add(Path.GetFileName(path) + ": " + icon);
            }
        Assert.True(seen >= 4, "The scan found only " + seen + " named menu icons.");
        Assert.True(missing.Count == 0, "Menu icons without a mask: " + string.Join("; ", missing));
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
