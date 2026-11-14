using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

/// <summary>A new or changed default must never silence a gesture the user saved, and the list that
/// guarantees it must not fall behind the default table.</summary>
public sealed class YieldingDefaultsTests
{
    // The default table as of revision 1 (before the shortcut rework), as plain strings. A command whose
    // current default has a gesture outside this table must be listed as a yielding default.
    private static readonly Dictionary<string, string> RevisionOneShortcuts = new(StringComparer.Ordinal)
    {
        ["navigation.downloads"] = "Primary+1", ["navigation.archive"] = "Primary+2", ["navigation.media"] = "Primary+6",
        ["navigation.rules"] = "Primary+3", ["navigation.sources"] = "Primary+4", ["navigation.diagnostics"] = "Primary+5",
        ["navigation.settings"] = "Primary+,", ["session.pause"] = "Primary+Shift+P", ["input.focus-url"] = "Primary+L",
        ["watch.import-clipboard"] = "Primary+V", ["selection.select-all"] = "Primary+A", ["thread.read"] = "Enter",
        ["gallery.open"] = "Shift+Enter", ["media.open"] = "Enter", ["thread.details"] = "Alt+Enter", ["watch.check"] = "F5",
        ["watch.retry-media"] = "Shift+F5", ["rules.edit-scope"] = "Primary+R", ["board.filters"] = "Primary+Shift+R",
        ["location.open-folder"] = "Primary+O", ["location.open-source"] = "Primary+Shift+O", ["selection.copy-links"] = "Primary+C",
        ["watch.remove"] = "Delete", ["archive.delete-permanently"] = "Shift+Delete", ["archive.previous-page"] = "Alt+PageUp",
        ["archive.next-page"] = "Alt+PageDown", ["archive.select-page"] = "Primary+A", ["input.focus-download-search"] = "Primary+F",
        ["reader.previous-thread"] = "Left", ["reader.next-thread"] = "Right", ["reader.previous-post"] = "Up", ["reader.find"] = "Primary+F",
        ["reader.next-post"] = "Down", ["media.return"] = "Alt+Left", ["gallery.previous"] = "Left", ["gallery.next"] = "Right",
        ["gallery.fit"] = "0", ["gallery.actual-size"] = "1", ["gallery.zoom-in"] = "Plus", ["gallery.zoom-out"] = "Minus",
        ["gallery.context-toggle"] = "P", ["gallery.show-in-thread"] = "T", ["export.open-html-dialog"] = "Primary+E",
        ["window.fullscreen"] = "F11", ["viewer.shortcut-help"] = "F1", ["input.focus-search"] = "Primary+F",
        ["input.focus-media-search"] = "Primary+F", ["favorites.toggle"] = "F", ["favorites.add"] = "M", ["favorites.remove"] = "U",
        ["video.play-pause"] = "Space", ["video.volume-up"] = "Up", ["video.volume-down"] = "Down", ["menu.context-open"] = "Shift+F10",
    };
    private static readonly (string Id, string Gesture)[] RevisionOneExtras =
    {
        ("gallery.fit", "NumPad0"), ("gallery.actual-size", "NumPad1"), ("gallery.zoom-in", "Add"),
        ("gallery.zoom-in", "Shift+OemPlus"), ("gallery.zoom-out", "Subtract"),
    };

    private static string Identity(ShortcutGesture gesture) =>
        string.Join("+", gesture.Modifiers.OrderBy(m => m, StringComparer.Ordinal)) + "|" + gesture.Key + "|" + gesture.Symbol;

    private static IEnumerable<string> RevisionOne(string id)
    {
        if (RevisionOneShortcuts.TryGetValue(id, out string? text)) yield return Identity(ShortcutGesture.Parse(text));
        foreach (var extra in RevisionOneExtras.Where(e => e.Id == id)) yield return Identity(ShortcutGesture.Parse(extra.Gesture));
        if (id == "viewer.shortcut-help") yield return Identity(new("OemQuestion", new[] { "Shift" }, "?"));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void EveryCommandWhoseDefaultGainedAGestureIsListedAsYielding(ShortcutPlatform platform)
    {
        var yielding = EffectiveShortcutBindings.YieldingDefaultCommandIds.ToHashSet(StringComparer.Ordinal);
        var unlisted = new List<string>();
        foreach (var command in CommandCatalog.All)
        {
            var before = RevisionOne(command.Id).ToHashSet(StringComparer.Ordinal);
            if (EffectiveShortcutBindings.Defaults(command, platform).Any(g => !before.Contains(Identity(g))) && !yielding.Contains(command.Id))
                unlisted.Add(command.Id);
        }
        Assert.True(unlisted.Count == 0, "Defaults changed without a yielding entry: " + string.Join(", ", unlisted));
    }

    [Fact]
    public void TheYieldingListNamesOnlyKnownCommandsWithinTheCurrentRevision()
    {
        foreach (string id in EffectiveShortcutBindings.YieldingDefaultCommandIds) Assert.True(CommandCatalog.TryGet(id, out _), id);
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 2);
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void AnyYieldingDefaultGivesWayToASavedBindingOnItsKeyWithoutBlockingOrRewriting(ShortcutPlatform platform)
    {
        foreach (string id in EffectiveShortcutBindings.YieldingDefaultCommandIds.Where(id => EffectiveShortcutBindings.YieldsOn(id, platform)))
        {
            var command = CommandCatalog.Get(id);
            foreach (var gesture in EffectiveShortcutBindings.Defaults(command, platform))
            {
                // About has no default of its own and, being a shell command, overlaps every non-modal context.
                const string owner = "navigation.about";
                // A gesture another stable default already owns where the saved binding applies is an ordinary conflict, not a yield.
                if (CommandCatalog.All.Any(c => c.Id != id && !EffectiveShortcutBindings.YieldsOn(c.Id, platform)
                    && EffectiveShortcutBindings.ContextsOverlap(owner, c.Id)
                    && EffectiveShortcutBindings.Defaults(c, platform).Any(g => EffectiveShortcutBindings.Equivalent(g, gesture, platform)))) continue;
                var section = new ShortcutBindingsSection().Set(owner, [gesture]);
                string before = section.Raw.GetRawText();
                var bindings = EffectiveShortcutBindings.Build(section, platform);
                Assert.DoesNotContain(bindings.For(id), g => EffectiveShortcutBindings.Equivalent(g, gesture, platform));
                Assert.Equal(1, bindings.For(owner).Count);
                Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
                Assert.Contains(bindings.Issues, issue => issue.CommandId == id && issue.OtherCommandId == owner && !issue.BlocksSave);
                Assert.Equal(before, section.Raw.GetRawText());
            }
        }
    }

    [Fact]
    public void AFrozenOverrideThatKeptAnOldDefaultAlongsideItsOwnGestureStillWinsAgainstTheNewDefault()
    {
        // "Add another shortcut" stored the whole list, old default included, as an explicit Replace.
        // Entries are never removed from the yielding list, so it also names commands whose default was taken away later
        // (Diagnostics and Statistics became segments of Status without a digit); they have no new default to yield.
        foreach (string id in EffectiveShortcutBindings.YieldingDefaultCommandIds.Where(id => id.StartsWith("navigation.", StringComparison.Ordinal)
            && EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id)).Count == 1))
        {
            // Every main page yields since the footer digits (revision 11), so the saved binding sits on an application command outside the pages.
            var other = CommandCatalog.Get("session.pause");
            var yieldingGesture = EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id)).Single();
            var section = new ShortcutBindingsSection().Set(other.Id, [ShortcutGesture.Parse("Primary+9"), yieldingGesture]);
            var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
            Assert.Equal(2, bindings.For(other.Id).Count);
            Assert.Empty(bindings.For(id));
            Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        }
    }

    [Fact]
    public void TwoExplicitBindingsOnTheSameKeyStillBlockAndNothingYieldsBetweenThem()
    {
        var section = new ShortcutBindingsSection()
            .Set("navigation.rules", [ShortcutGesture.Parse("Primary+9")])
            .Set("navigation.sources", [ShortcutGesture.Parse("Primary+9")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Contains(bindings.Issues, issue => issue.BlocksSave && issue.CommandId.StartsWith("navigation.", StringComparison.Ordinal));
        Assert.Empty(bindings.For("navigation.rules"));
        Assert.Empty(bindings.For("navigation.sources"));
    }

    [Fact]
    public void OnlyBlockingIssuesAndAnUnsupportedSchemaWarnAtStartup()
    {
        // Slot 6 is free after the Sources/Plugins merge, so use Media's actual default to exercise a yield.
        var section = new ShortcutBindingsSection().Set("navigation.archive", [ShortcutGesture.Parse("Primary+3")]);
        string before = section.Raw.GetRawText();
        var yields = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Contains(yields.Issues, issue => issue.CommandId == "navigation.media" && issue.OtherCommandId == "navigation.archive");
        Assert.Empty(yields.For("navigation.media"));
        Assert.Single(yields.For("navigation.archive"));
        Assert.Equal(before, section.Raw.GetRawText());
        Assert.DoesNotContain(yields.Issues, EffectiveShortcutBindings.NeedsStartupWarning);

        var unknown = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("no.such.action", [ShortcutGesture.Parse("Primary+9")]), ShortcutPlatform.Linux);
        Assert.NotEmpty(unknown.Issues);
        Assert.DoesNotContain(unknown.Issues, EffectiveShortcutBindings.NeedsStartupWarning);

        var conflict = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set("navigation.rules", [ShortcutGesture.Parse("Primary+9")]).Set("navigation.sources", [ShortcutGesture.Parse("Primary+9")]), ShortcutPlatform.Linux);
        Assert.Contains(conflict.Issues, EffectiveShortcutBindings.NeedsStartupWarning);

        using var document = System.Text.Json.JsonDocument.Parse("""{"SchemaVersion":7,"Overrides":{}}""");
        var future = EffectiveShortcutBindings.Build(new ShortcutBindingsSection(document.RootElement), ShortcutPlatform.Linux);
        Assert.Contains(future.Issues, EffectiveShortcutBindings.NeedsStartupWarning);
        Assert.Empty(EffectiveShortcutBindings.Build(platform: ShortcutPlatform.Linux).Issues);
    }
}
