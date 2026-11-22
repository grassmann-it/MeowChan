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
        // Follow-ups: volume down stays in the volume family (no volume-1 mask; volume-x is the muted state), "Repeat video" is the repeat icon,
        // both "copy link" actions are the link icon, and the footer "More" is the same ⋯ as the other More menus.
        Assert.Equal("volume-2", CommandCatalog.Get("video.volume-down").Icon);
        Assert.Equal(CommandCatalog.Get("video.volume-up").Icon, CommandCatalog.Get("video.volume-down").Icon);
        Assert.Equal("repeat", CommandCatalog.Get("video.loop").Icon);
        Assert.Equal("link", CommandCatalog.Get("selection.copy-links").Icon);
        Assert.Equal(CommandCatalog.Get("post.copy-link").Icon, CommandCatalog.Get("selection.copy-links").Icon);
        Assert.Equal("more-horizontal", CommandCatalog.Get("navigation.tools-menu").Icon);
        // Decision #126: the rating family has its own half star; the favourite keeps the star (and star-filled when marked).
        foreach (string id in CommandCatalog.All.Select(command => command.Id).Where(id => id.StartsWith("media.rating.", StringComparison.Ordinal)))
            Assert.True(CommandCatalog.Get(id).Icon == "star-half", id);
        foreach (string id in new[] { "favorites.toggle", "favorites.add", "favorites.filter", "favorites.open", "thread.favorite-toggle" })
            Assert.True(CommandCatalog.Get(id).Icon == "star", id);
        Assert.Equal("star-filled", CommandCatalog.IconForState("favorites.toggle", "marked"));
        Assert.NotEqual(CommandCatalog.Get("favorites.toggle").Icon, CommandCatalog.Get("media.rating.edit").Icon);
        // Deleting (not removing from a list) is the circled cross everywhere.
        foreach (string id in new[] { "archive.delete-permanently", "reader-groups.delete", "archive.saved-searches.delete", "rules.delete", "rules.template-delete" })
            Assert.True(CommandCatalog.Get(id).Icon == "x-circle", id);
    }

    [Fact]
    public void ShowInPluginsNamesThePageAndNewGroupHasAMenuLabelWithAnEllipsis()
    {
        Texts.SetLanguage("en");
        Assert.Equal("Show in Plugins", CommandCatalog.Label("plugins.watches"));
        Assert.Equal("New group…", Texts.T("tagging.page.group.newMenu"));
        Assert.Equal("New group", Texts.T("tagging.page.group.new")); // the dialog title and the button name keep the plain label
        Texts.SetLanguage("de");
        Assert.Equal("In Plugins zeigen", CommandCatalog.Label("plugins.watches"));
        Assert.Equal("Neue Gruppe…", Texts.T("tagging.page.group.newMenu"));
        Assert.Equal("Neue Gruppe", Texts.T("tagging.page.group.new"));
        foreach (string code in Texts.BundledPackCodes())
        {
            Texts.SetLanguage(code);
            string plain = Texts.T("tagging.page.group.new");
            Assert.True(Texts.T("tagging.page.group.newMenu") == plain + "…", code + ": " + Texts.T("tagging.page.group.newMenu"));
            Assert.NotEqual("plugins.watches", Texts.T("plugins.watches"));
            Assert.NotEqual(Texts.T("command.navigation.tools-menu"), Texts.T("plugins.watches"));
        }
    }

    [Fact]
    public void TheNewGroupMenuEntriesUseTheMenuLabel()
    {
        TagQueueItem item = new(Thread, "/media/123", "asset-1", "1" + new string('a', 63), 101, "media-1", "original",
            "p1", null, null, null, false, null, "file-1.png", SavedMediaKind.Image, TagQueueState.Open, DateTimeOffset.UnixEpoch);
        TagQueueSnapshot queue = new(1, 0, Array.Empty<string>(), new[] { item }, TagQueueLoadState.Ready);
        TaggingQueueRow[] rows = TaggingQueueView.Rows(queue, TaggingQueueFilter.All).ToArray();
        TaggingCommandContext context = new(queue, TaggingQueueFilter.All, rows, rows[0], new(true, true, true, true, true, true, true, true, true));
        foreach (ContextMenuBuilder menu in new[] { TaggingQueueMenu.Compose(context, _ => () => { }),
                     TaggingQueueMenu.ComposeSelection(context, _ => () => { }) })
        {
            ContextMenuEntry group = Assert.Single(menu.ToModel().Entries, entry => entry.Id == TaggingCommands.GroupMenu);
            ContextMenuEntry create = Assert.Single(group.Children!.SelectMany(section => section), entry => entry.Id == TaggingCommands.GroupNew);
            Assert.Equal("New group…", create.Label);
            Assert.Equal(TaggingCommands.GroupNewIcon, create.Icon);
        }
        // The dialog title keeps the plain label, and Manage groups uses the menu label.
        string groups = File.ReadAllText(Root("Desktop/Views/Tagging/TaggingPage.Groups.cs"));
        Assert.Contains("GroupNameDialog.ShowAsync(this, Texts.T(\"tagging.page.group.new\")", groups, StringComparison.Ordinal);
        Assert.Contains(".Item(\"tagging.group.new\", Texts.T(\"tagging.page.group.newMenu\")", groups, StringComparison.Ordinal);
        Assert.DoesNotContain(".Item(\"tagging.group.new\", Texts.T(\"tagging.page.group.new\")", groups, StringComparison.Ordinal);
        foreach (string action in new[] { "CreateGroupDialogAsync", "RenameGroupDialogAsync", "DeleteGroupAsync" })
        {
            Assert.DoesNotContain($"_ = {action}()", groups, StringComparison.Ordinal);
            Assert.Contains($"_ = RunGroupAction({action})", groups, StringComparison.Ordinal);
        }
        Assert.Contains("ShowStatus(TagQueueActions.DescribeFailure(error))", groups, StringComparison.Ordinal);
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
