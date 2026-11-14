using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The macOS menu bar model: generated from the catalog and the main-page order, no single-key gestures, one entry per context-dependent gesture.</summary>
[Collection("Localization")]
public sealed class MacMenuModelTests
{
    // Shortcut display strings are localized; the language is process-global, so run serially with the other language tests, in English.
    public MacMenuModelTests() => Texts.SetLanguage("en");

    private static readonly string[] Direct = { "navigation.about", "app.quit", "navigation.continue-reading", "workspace.view-cycle" };
    private static readonly EffectiveShortcutBindings Mac = EffectiveShortcutBindings.Build(null, ShortcutPlatform.MacOS);

    private static IEnumerable<MacMenuNode> Entries(IEnumerable<MacMenuNode> nodes) => nodes.SelectMany(node =>
        node.Kind == MacMenuKind.Submenu ? Entries(node.Items!) : node.Kind == MacMenuKind.Command ? new[] { node } : Array.Empty<MacMenuNode>());

    private static IEnumerable<MacMenuNode> AllEntries() => Entries(MacMenuModel.Build().SelectMany(menu => menu.Items));

    [Fact]
    public void TheBarHasTheSevenMenusInTheMacOrder()
    {
        Assert.Equal(new[] { "app", "file", "edit", "view", "go", "window", "help" }, MacMenuModel.Build().Select(menu => menu.Id));
        Assert.Null(MacMenuModel.Build()[0].TitleKey);
    }

    [Fact]
    public void EveryReferencedCommandExistsAndIsImplemented()
    {
        foreach (string id in MacMenuModel.CommandIds())
        {
            Assert.True(CommandCatalog.TryGet(id, out var command), id);
            Assert.True(command!.IsImplemented, id);
        }
    }

    [Fact]
    public void TheViewMenuListsTheMainPagesInFooterOrderWithTheirDigits()
    {
        var view = MacMenuModel.Build().Single(menu => menu.Id == "view").Items;
        // Tagging has no digit and so no gesture on macOS: it stays out of the native menu (footer and ⋯ menu carry it).
        Assert.Equal(MainPageOrder.NumberedCommandIds, view.Take(MainPageOrder.NumberedCommandIds.Count).Select(node => node.CommandId!));
        Assert.Equal("window.fullscreen", view[^1].CommandId);
        var digits = view.Take(MainPageOrder.NumberedCommandIds.Count).Select(node => MacMenuModel.Shortcut(Mac, node)).ToArray();
        // Booru takes the former Plugins slot 6 behind Media; Rules/Sources keep 4/5 and Status keeps Primary+7.
        Assert.Equal(new[] { "⌘1", "⌘2", "⌘3", "⌘6", "⌘4", "⌘5", "⌘7" }, digits);
        Assert.DoesNotContain(AllEntries(), node => node.Commands.Contains("navigation.plugins"));
    }

    [Fact]
    public void NoEntryAdvertisesAKeyWithoutCommandControlOrOption()
    {
        foreach (var platform in new[] { ShortcutPlatform.MacOS, ShortcutPlatform.Linux, ShortcutPlatform.Windows })
        {
            var bindings = EffectiveShortcutBindings.Build(null, platform);
            foreach (var node in AllEntries())
                foreach (string id in node.Commands)
                    Assert.All(bindings.For(id).Where(MacMenuModel.IsMenuGesture),
                        gesture => Assert.NotEmpty(gesture.Modifiers.Where(modifier => modifier != "Shift")));
            Assert.All(AllEntries(), node => Assert.DoesNotMatch(@"(^| / )(F\d+|Delete|Space|[A-Z])( \(Fn\))?($| / )", MacMenuModel.Shortcut(bindings, node) ?? ""));
        }
    }

    [Fact]
    public void EveryEntryHasAGestureOnMacExceptTheDirectOnes()
    {
        foreach (var node in AllEntries())
        {
            if (Direct.Contains(node.CommandId)) continue;
            Assert.NotNull(MacMenuModel.Gesture(Mac, node));
            Assert.False(string.IsNullOrEmpty(MacMenuModel.Shortcut(Mac, node)), node.CommandId);
        }
    }

    [Theory]
    [InlineData("input.focus-search", "⌘F")]
    [InlineData("reader.find-next", "⌘G")]
    [InlineData("reader.find-previous", "⇧⌘G")]
    [InlineData("media.return", "⌘←")]
    [InlineData("archive.previous-page", "⌥⌘← / ⌥⇞")]
    [InlineData("archive.next-page", "⌥⌘→ / ⌥⇟")]
    [InlineData("window.minimize", "⌘M")]
    [InlineData("viewer.shortcut-help", "⌘?")]
    [InlineData("window.fullscreen", "⌃⌘F")]
    [InlineData("navigation.settings", "⌘,")]
    public void MacShortcutTextOfKeyEntries(string id, string text) =>
        Assert.Equal(text, MacMenuModel.Shortcut(Mac, AllEntries().Single(node => node.CommandId == id)));

    [Fact]
    public void AContextDependentGestureIsOneEntryWhoseCommandsShareIt()
    {
        var resolving = AllEntries().Where(node => node.Resolves is { Count: > 1 }).ToArray();
        Assert.Equal(4, resolving.Length);
        foreach (var node in resolving)
        {
            var first = MacMenuModel.Gesture(Mac, node)!;
            foreach (string id in node.Resolves!)
                Assert.Contains(Mac.For(id), gesture => EffectiveShortcutBindings.Equivalent(gesture, first, ShortcutPlatform.MacOS));
        }
        // Each shared gesture appears once in the bar.
        var chords = AllEntries().Select(node => MacMenuModel.Gesture(Mac, node)).OfType<ShortcutGesture>()
            .Select(gesture => gesture.Display(ShortcutPlatform.MacOS)).ToArray();
        Assert.Equal(chords.Length, chords.Distinct().Count());
    }

    [Fact]
    public void TheMenuTitlesAreLocalizedInEveryBuiltInLanguage()
    {
        string[] keys = MacMenuModel.Build().Select(menu => menu.TitleKey).Concat(AllEntries().Select(node => node.TitleKey))
            .Concat(MacMenuModel.Build().SelectMany(menu => menu.Items).Where(node => node.Kind == MacMenuKind.Submenu).Select(node => node.TitleKey))
            .OfType<string>().Distinct().ToArray();
        Assert.NotEmpty(keys);
        foreach (string key in keys)
        {
            Assert.True(BuiltInCatalog.English.ContainsKey(key), key);
            Assert.True(BuiltInCatalog.German.ContainsKey(key), key);
        }
    }
}
