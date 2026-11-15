using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Modals plan KS1: the pure table of the shortcuts window and the keycaps.
[Collection("Localization")]
public sealed class ShortcutTableModelTests : IDisposable
{
    private readonly string _language = Texts.Language;
    public ShortcutTableModelTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    public static TheoryData<ShortcutPlatform> Platforms() => new() { ShortcutPlatform.Windows, ShortcutPlatform.Linux, ShortcutPlatform.MacOS };

    [Theory]
    [MemberData(nameof(Platforms))]
    public void EveryCatalogCommandIsInExactlyOneGroupInEveryContext(ShortcutPlatform platform)
    {
        foreach (string? area in new string?[] { null }.Concat(ShortcutGroups.Order))
        {
            ShortcutTableModel model = ShortcutTableModel.Build(new ShortcutBindingsSection(), platform, area);
            Assert.Equal(CommandCatalog.All.Select(c => c.Id), model.Rows.Select(r => r.CommandId));
            string[] listed = model.Query(new ShortcutTableQuery()).SelectMany(g => g.Rows).Select(r => r.CommandId).ToArray();
            Assert.Equal(CommandCatalog.All.Count, listed.Length);
            Assert.Equal(listed.Length, listed.Distinct(StringComparer.Ordinal).Count());
            Assert.All(model.Rows, row => Assert.Contains(row.Group, ShortcutGroups.Order));
            // Labels resolve for every group that shows.
            Assert.All(model.Query(new ShortcutTableQuery()), group => Assert.False(string.IsNullOrWhiteSpace(ShortcutGroups.Label(group.Key))));
        }
    }

    [Fact]
    public void GroupingIsDeterministicAndFollowsTheTableOrderWithTheCurrentAreaFirst()
    {
        var first = ShortcutTableModel.Build(new ShortcutBindingsSection(), ShortcutPlatform.Linux, ShortcutGroups.Reader);
        var second = ShortcutTableModel.Build(new ShortcutBindingsSection(), ShortcutPlatform.Linux, ShortcutGroups.Reader);
        Assert.Equal(first.Rows.Select(r => r.Group), second.Rows.Select(r => r.Group));
        IReadOnlyList<ShortcutTableGroup> groups = first.Query(new ShortcutTableQuery());
        Assert.Equal(ShortcutGroups.Reader, groups[0].Key);
        Assert.True(groups[0].IsCurrent);
        Assert.All(groups.Skip(1), group => Assert.False(group.IsCurrent));
        string[] expected = ShortcutGroups.OrderFor(ShortcutGroups.Reader).Where(key => groups.Any(g => g.Key == key)).ToArray();
        Assert.Equal(expected, groups.Select(g => g.Key));
        // The window's page groups are a fixed list in base order: the digits move with the footer, the groups of the window do not.
        Assert.Equal(ShortcutGroups.Pages, MainPageOrder.CommandIds.Where(id => id != "navigation.tagging").Select(ShortcutGroups.PageKey));
        Assert.DoesNotContain("tagging", ShortcutGroups.Pages); // its commands file under the application group (context "tagging")
        Assert.Equal(ShortcutGroups.Application, ShortcutGroups.Order[^1]);
    }

    [Fact]
    public void TheGalleryAreaPutsItsTagPanelDirectlyAfterIt()
    {
        string[] order = ShortcutGroups.OrderFor(ShortcutGroups.Gallery).ToArray();
        Assert.Equal(new[] { ShortcutGroups.Gallery, ShortcutGroups.TagPanel }, order.Take(2));
        Assert.Equal(ShortcutGroups.Order.Order(), order.Order());
        Assert.Equal(ShortcutGroups.Reader, ShortcutGroups.OrderFor(ShortcutGroups.Reader)[0]);
        Assert.Equal(ShortcutGroups.Order, ShortcutGroups.OrderFor(null));
    }

    [Fact]
    public void ACommandOfSeveralAreasListsUnderTheCurrentOneAndFixedGroupsKeepTheirCommands()
    {
        CommandDefinition rating = CommandCatalog.Get("media.rating.edit"); // media, reader, gallery
        Assert.Equal("media", ShortcutGroups.Primary(rating));
        Assert.Equal(ShortcutGroups.Reader, ShortcutGroups.For(rating, ShortcutGroups.Reader));
        Assert.Equal(ShortcutGroups.Gallery, ShortcutGroups.For(rating, ShortcutGroups.Gallery));
        Assert.Equal("media", ShortcutGroups.For(rating, "downloads"));
        Assert.Equal(ShortcutGroups.Application, ShortcutGroups.For(CommandCatalog.Get("navigation.archive"), "archive"));
        Assert.Equal(ShortcutGroups.Dialogs, ShortcutGroups.For(CommandCatalog.Get("reader-groups.create"), ShortcutGroups.Reader));
        foreach (string id in QuickTagCommands.All)
            Assert.Equal(ShortcutGroups.TagPanel, ShortcutGroups.For(CommandCatalog.Get(id), ShortcutGroups.Gallery));
        // The current area cannot be a collecting group.
        Assert.Equal("media", ShortcutGroups.For(rating, ShortcutGroups.Application));
    }

    [Fact]
    public void EveryExecutionContextOfTheCatalogNamesAnArea()
    {
        string[] unknown = CommandCatalog.All.SelectMany(c => c.ExecutionContexts)
            .Where(context => ShortcutGroups.AreaOf(context) == null).Distinct().ToArray();
        Assert.Empty(unknown);
    }

    [Fact]
    public void QuickTagCommandsAreListedEditableAndShowTheirWaitingDefaults()
    {
        ShortcutTableModel model = ShortcutTableModel.Build(new ShortcutBindingsSection(), ShortcutPlatform.Linux);
        foreach (string id in QuickTagCommands.All)
        {
            ShortcutTableRow row = model.Rows.Single(r => r.CommandId == id);
            Assert.Equal(ShortcutGroups.TagPanel, row.Group);
            Assert.True(row.IsEditable);
            if (CommandCatalog.Get(id).IsAwaitingHandler)
            {
                Assert.Equal(ShortcutRowStatus.Unavailable, row.Status);
                Assert.True(row.IsBound, id + " lost its waiting default in the table.");
            }
        }
    }

    [Fact]
    public void TheTableHasOneFixedOrderWithoutColumnSorting()
    {
        // The window has no sortable columns: the query carries no sort and the order is the same on every call.
        Assert.DoesNotContain(typeof(ShortcutTableQuery).GetProperties(), p => p.Name.Contains("Sort") || p.Name == "Descending");
        ShortcutTableModel model = ShortcutTableModel.Build(new ShortcutBindingsSection(), ShortcutPlatform.Linux);
        var a = model.Query(new ShortcutTableQuery()).Select(g => g.Rows.Select(r => r.CommandId).ToArray()).ToArray();
        var b = model.Query(new ShortcutTableQuery()).Select(g => g.Rows.Select(r => r.CommandId).ToArray()).ToArray();
        Assert.Equal(a, b);
        // Catalog order inside a group (no preferred commands here), grouped or flat.
        Assert.All(model.Query(new ShortcutTableQuery()), group =>
            Assert.Equal(group.Rows.Select(r => r.CatalogIndex).Order(), group.Rows.Select(r => r.CatalogIndex)));
        IReadOnlyList<ShortcutTableRow> flat = model.Query(new ShortcutTableQuery(Grouped: false)).Single().Rows;
        Assert.Equal(flat.Select(r => r.CatalogIndex).Order(), flat.Select(r => r.CatalogIndex));
        // The current area leads, the groups follow in their fixed order.
        ShortcutTableModel inReader = ShortcutTableModel.Build(new ShortcutBindingsSection(), ShortcutPlatform.Linux, ShortcutGroups.Reader);
        Assert.Equal(ShortcutGroups.Reader, inReader.Query(new ShortcutTableQuery()).First().Key);
    }

    [Fact]
    public void PreferredCommandsOfTheOpeningContextComeFirstInTheDefaultOrder()
    {
        ShortcutTableModel model = ShortcutTableModel.Build(new ShortcutBindingsSection(), ShortcutPlatform.Linux, ShortcutGroups.Reader,
            preferred: new[] { "reader.next-thread", "reader.previous-post" });
        ShortcutTableGroup reader = model.Query(new ShortcutTableQuery()).First();
        Assert.Equal(new[] { "reader.next-thread", "reader.previous-post" }, reader.Rows.Take(2).Select(r => r.CommandId));
    }

    [Theory]
    [InlineData("ARCHIVE")]
    [InlineData("navigation.archive")]
    [InlineData("ctrl+2")]
    public void SearchIsCaseInsensitiveOverLabelIdAndShortcut(string text)
    {
        ShortcutTableModel model = ShortcutTableModel.Build(new ShortcutBindingsSection(), ShortcutPlatform.Linux);
        string[] hits = model.Query(new ShortcutTableQuery(text)).SelectMany(g => g.Rows).Select(r => r.CommandId).ToArray();
        Assert.Contains("navigation.archive", hits);
        Assert.All(hits, id => Assert.Contains(text.ToLowerInvariant(), model.Rows.Single(r => r.CommandId == id).SearchText));
    }

    [Fact]
    public void ConflictPairsAreSymmetricAndCounted()
    {
        // Primary+Shift+Y: a key no page letter (revision 15) or other default uses, so only the two saved bindings meet.
        var draft = new ShortcutBindingsSection().Set("navigation.archive", new[] { ShortcutGesture.Parse("Primary+Shift+Y") })
            .Set("navigation.rules", new[] { ShortcutGesture.Parse("Primary+Shift+Y") });
        ShortcutTableModel model = ShortcutTableModel.Build(draft, ShortcutPlatform.Linux);
        ShortcutTableRow archive = model.Rows.Single(r => r.CommandId == "navigation.archive");
        ShortcutTableRow rules = model.Rows.Single(r => r.CommandId == "navigation.rules");
        Assert.Contains(archive.Conflicts, c => c.OtherCommandId == "navigation.rules" && c.BlocksSave);
        Assert.Contains(rules.Conflicts, c => c.OtherCommandId == "navigation.archive" && c.BlocksSave);
        Assert.Single(archive.Conflicts);
        Assert.Equal(2, model.BlockingCount);
        // The table still shows the requested (rejected) gesture so the user sees what conflicts.
        Assert.Equal("Ctrl+Shift+Y", Assert.Single(archive.GestureTexts));
        string[] onlyConflicts = model.Query(new ShortcutTableQuery(ConflictsOnly: true)).SelectMany(g => g.Rows).Select(r => r.CommandId).ToArray();
        Assert.Equal(new[] { "navigation.archive", "navigation.rules" }, onlyConflicts.Order());
        Assert.Equal(0, ShortcutTableModel.Build(new ShortcutBindingsSection(), ShortcutPlatform.Linux).BlockingCount);
    }

    [Fact]
    public void AreaFilterAndAssignedOnlyNarrowTheTable()
    {
        var draft = new ShortcutBindingsSection().Set("navigation.archive", Array.Empty<ShortcutGesture>());
        ShortcutTableModel model = ShortcutTableModel.Build(draft, ShortcutPlatform.Linux);
        ShortcutTableGroup reader = Assert.Single(model.Query(new ShortcutTableQuery(Area: ShortcutGroups.Reader)));
        Assert.Equal(ShortcutGroups.Reader, reader.Key);
        Assert.Contains(reader.Rows, r => r.CommandId == "media.rating.edit"); // primary media, also runs in the reader
        Assert.All(reader.Rows, r => Assert.True(r.Group == ShortcutGroups.Reader || r.Areas.Contains(ShortcutGroups.Reader)));
        string[] assigned = model.Query(new ShortcutTableQuery(AssignedOnly: true)).SelectMany(g => g.Rows).Select(r => r.CommandId).ToArray();
        Assert.DoesNotContain("navigation.archive", assigned);
        Assert.All(assigned, id => Assert.True(model.Rows.Single(r => r.CommandId == id) is { IsBound: true, Status: not ShortcutRowStatus.Unavailable }));
        Assert.Equal(ShortcutRowStatus.Custom, model.Rows.Single(r => r.CommandId == "navigation.archive").Status);
        Assert.False(model.Rows.Single(r => r.CommandId == "navigation.archive").IsBound);
    }

    [Fact]
    public void UnderMoreIsTakenFromTheOpeningContext()
    {
        ShortcutTableModel model = ShortcutTableModel.Build(new ShortcutBindingsSection(), ShortcutPlatform.Linux,
            underMore: id => id == "navigation.statistics");
        Assert.True(model.Rows.Single(r => r.CommandId == "navigation.statistics").UnderMore);
        Assert.Single(model.Rows, r => r.UnderMore);
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux, "Primary+Shift+P", new[] { "Ctrl", "Shift", "P" })]
    [InlineData(ShortcutPlatform.Windows, "Primary+OemComma", new[] { "Ctrl", "," })]
    [InlineData(ShortcutPlatform.Windows, "Primary+OemPlus", new[] { "Ctrl", "+" })]
    [InlineData(ShortcutPlatform.MacOS, "Primary+Shift+P", new[] { "⇧", "⌘", "P" })]
    [InlineData(ShortcutPlatform.MacOS, "Alt+Left", new[] { "⌥", "←" })]
    public void KeycapsSplitTheGestureIntoOneCapPerKey(ShortcutPlatform platform, string gesture, string[] caps)
    {
        ShortcutGesture parsed = ShortcutGesture.Parse(gesture);
        Assert.Equal(caps, parsed.DisplayParts(platform));
        Assert.Equal(parsed.Display(platform), platform == ShortcutPlatform.MacOS ? string.Concat(caps) : string.Join("+", caps));
        var control = new KeyCapText(parsed, platform);
        Assert.Equal(caps.Length, control.Children.Count);
        Assert.All(control.Children, cap => Assert.Contains(KeyCapText.CapClass, cap.Classes));
        Assert.Equal(parsed.Display(platform), Avalonia.Automation.AutomationProperties.GetName(control));
    }

    [Fact]
    public void KeyNamesFollowTheLanguageAsEverywhereElse()
    {
        Texts.SetLanguage("de");
        Assert.Equal(new[] { "Strg", "Umschalt", "P" }, ShortcutGesture.Parse("Primary+Shift+P").DisplayParts(ShortcutPlatform.Linux));
        Assert.Equal("Strg+Umschalt+P", ShortcutGesture.Parse("Primary+Shift+P").Display(ShortcutPlatform.Linux));
        Assert.Equal(new[] { "⇧", "⌘", "P" }, ShortcutGesture.Parse("Primary+Shift+P").DisplayParts(ShortcutPlatform.MacOS));
    }

    [Fact]
    public void EveryDefaultGestureDisplaysIdenticallyFromItsParts()
    {
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
            foreach (CommandDefinition command in CommandCatalog.All)
                foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(command, platform))
                    Assert.Equal(gesture.Display(platform), platform == ShortcutPlatform.MacOS
                        ? string.Concat(gesture.DisplayParts(platform)) : string.Join("+", gesture.DisplayParts(platform)));
    }
}
