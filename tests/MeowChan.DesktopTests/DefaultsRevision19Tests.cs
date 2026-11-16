using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Revision 19 (Tom's decision #125 of 15.11.2026, from the menu audit): frequent menu actions that had no key get one. Each new default is conflict-free
// in its contexts, yields to a binding the person saved for another command on the same key, leaves a binding saved for the command itself alone, and
// is what the menus and tooltips show (they read the effective bindings). Plain objects and a source scan; no window.
[Collection("Localization")]
public sealed class DefaultsRevision19Tests : IDisposable
{
    public DefaultsRevision19Tests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    /// <summary>The new defaults: command, gesture, display on Windows/Linux and on macOS, and a command of the same context that a profile could
    /// have bound to that key before revision 19 (it keeps the key; the new default is left unbound).</summary>
    public static readonly (string Id, string Gesture, string Display, string Mac, string Owner)[] NewDefaults =
    {
        ("tagging.mark-done", "D", "D", "D", "tagging.mark-open"),
        ("tagging.mark-skipped", "S", "S", "S", "tagging.clear-done"),
        ("tagging.show-in-media", "Alt+M", "Alt+M", "⌥M", "tagging.add"),
        ("thread.show-in-media", "Alt+M", "Alt+M", "⌥M", "archive.refresh"),
        ("post.hide", "H", "H", "H", "post.copy-text"),
        ("post.copy-link", "Primary+Shift+C", "Ctrl+Shift+C", "⇧⌘C", "post.copy-number"),
        ("media.copy-path", "Primary+Shift+C", "Ctrl+Shift+C", "⇧⌘C", "media.folders"),
        ("reader.expand", "Primary+Enter", "Ctrl+Enter", "⌘↩", "reader.quick-settings"),
        ("media.open-external", "Primary+Shift+O", "Ctrl+Shift+O", "⇧⌘O", "gallery.layout-menu"),
    };

    public static IEnumerable<object[]> Cases() =>
        from entry in NewDefaults from platform in Enum.GetValues<ShortcutPlatform>() select new object[] { entry.Id, platform };

    private static (string Id, string Gesture, string Display, string Mac, string Owner) Entry(string id) => NewDefaults.Single(entry => entry.Id == id);

    [Theory, MemberData(nameof(Platforms))]
    public void TheNewDefaultsAreActiveConflictFreeAndYielding(ShortcutPlatform platform)
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 19);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        Assert.Empty(bindings.Issues);
        foreach (var (id, gesture, display, mac, _) in NewDefaults)
        {
            Assert.Equal(gesture, CommandCatalog.Get(id).Shortcut);
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
            Assert.True(EffectiveShortcutBindings.YieldsOn(id, platform), id);
            Assert.Equal(platform == ShortcutPlatform.MacOS ? mac : display, bindings.Display(id));
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public void ASavedBindingOfAnotherCommandOnTheNewKeyWinsAndTheNewDefaultStaysUnbound(string id, ShortcutPlatform platform)
    {
        var (_, gesture, _, _, owner) = Entry(id);
        Assert.True(EffectiveShortcutBindings.ContextsOverlap(id, owner), id + " / " + owner);
        Assert.Empty(EffectiveShortcutBindings.Defaults(CommandCatalog.Get(owner), platform));
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set(owner, [ShortcutGesture.Parse(gesture)]);
        string before = section.Raw.GetRawText();
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
        // A default of one context is left unbound; one that also runs elsewhere (a path copied in the gallery and the tagging list too) gives way in the
        // owner's contexts only and keeps the key in the others (Opus review 15.11.).
        foreach (ShortcutGesture kept in bindings.For(id))
            Assert.All(EffectiveShortcutBindings.ExecutionContexts(owner).Where(EffectiveShortcutBindings.ConflictContexts(id).Contains),
                context => Assert.Contains(context, bindings.YieldedContexts(id, kept)));
        Assert.Equal(new[] { ShortcutGesture.Parse(gesture).Key }, bindings.For(owner).Select(g => g.Key));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == id && issue.OtherCommandId == owner && !issue.BlocksSave);
        // The saved profile itself is never rewritten.
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Theory, MemberData(nameof(Cases))]
    public void ABindingSavedForTheCommandItselfIsKeptAsItIs(string id, ShortcutPlatform platform)
    {
        EffectiveShortcutBindings own = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set(id, [ShortcutGesture.Parse("F9")]), platform);
        Assert.Equal(new[] { "F9" }, own.For(id).Select(g => g.Key));
        Assert.Empty(own.Issues);
        EffectiveShortcutBindings unbound = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set(id, []), platform);
        Assert.Empty(unbound.For(id));
        Assert.Empty(unbound.Issues);
    }

    [Fact]
    public void TheSingleLettersActOnlyInListsAndTheSingleKeySwitchTurnsThemOff()
    {
        // D and S are tagging-list keys, H a reader post key: never a context with a text field of its own.
        Assert.Equal(new[] { "tagging" }, CommandCatalog.Get("tagging.mark-done").ExecutionContexts);
        Assert.Equal(new[] { "tagging" }, CommandCatalog.Get("tagging.mark-skipped").ExecutionContexts);
        Assert.Equal(new[] { "reader" }, CommandCatalog.Get("post.hide").ExecutionContexts);
        EffectiveShortcutBindings off = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().WithSingleKeyShortcuts(false), ShortcutPlatform.Linux);
        foreach (string id in new[] { "tagging.mark-done", "tagging.mark-skipped", "post.hide" }) Assert.Empty(off.For(id));
        // The chords stay with the switch off.
        foreach (string id in new[] { "post.copy-link", "media.copy-path", "thread.show-in-media", "reader.expand", "media.open-external" })
            Assert.NotEmpty(off.For(id));
    }

    [Fact]
    public void EveryNewKeyHasARouteWhereItsMenuEntryShowsIt()
    {
        // The tagging list routes every command of its menu (TaggingCommands.All), so its three keys need no code of their own.
        foreach (string id in new[] { "tagging.mark-done", "tagging.mark-skipped", "tagging.show-in-media", "media.copy-path" })
            Assert.Contains(id, MeowChan.Desktop.TaggingCommands.All);
        Assert.Contains("CommandBindings.Matches(e, \"post.hide\")", Source("Desktop/Views/Archive/ArchivePage.PostFilter.cs"), StringComparison.Ordinal);
        Assert.Contains("OnPostCopyLinkKeyDown", Source("Desktop/Views/Archive/ArchivePage.PostFilter.cs"), StringComparison.Ordinal);
        Assert.Contains("CommandBindings.Matches(e, \"post.copy-link\")", Source("Desktop/Views/Archive/ArchivePage.PostBookmarks.cs"), StringComparison.Ordinal);
        Assert.Contains("MatchesArchiveCommand(e, \"thread.show-in-media\")", Source("Desktop/Views/Archive/ArchivePage.Selection.cs"), StringComparison.Ordinal);
        Assert.Contains("CommandBindings.Matches(e, \"thread.show-in-media\")", Source("Desktop/Shell/MainWindow.Selection.cs"), StringComparison.Ordinal);
        // The media page's MediaShortcutCommands list (one entry per line since the rating panel added the star keys).
        Assert.Contains("\"media.show-in-folder\", \"media.copy-path\"\n    };", Source("Desktop/Views/Media/MediaPage.cs"), StringComparison.Ordinal);
        string gallery = Source("Desktop/GalleryViewer.cs");
        Assert.Contains("CommandBindings.Matches(e, \"media.copy-path\")", gallery, StringComparison.Ordinal);
        Assert.Contains("CommandBindings.Matches(e, \"media.open-external\")", gallery, StringComparison.Ordinal);
        // The embedded reader's "Read" button carries reader.expand; the window routes a visible command button's key (RouteVisibleActions).
        Assert.Contains("CommandBindings.Apply(ArchiveReadPosts, \"reader.expand\"", Source("Desktop/Views/Archive/ArchivePage.axaml.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTooltipsNameTheNewKeys()
    {
        foreach (var (id, _, display, mac, _) in NewDefaults)
        {
            string shown = CommandCatalog.ShortcutDisplay(id, OperatingSystem.IsMacOS()) ?? "";
            Assert.Equal(OperatingSystem.IsMacOS() ? mac : display, shown);
            Assert.StartsWith(CommandCatalog.Label(id) + " · " + shown, CommandBindings.Tooltip(id), StringComparison.Ordinal);
        }
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));
}
