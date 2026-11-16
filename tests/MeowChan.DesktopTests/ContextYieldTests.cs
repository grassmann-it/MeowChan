using System.Runtime.CompilerServices;
using Avalonia.Input;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Opus review 15.11. (finding 5): a changed default is per command, not per context. A saved binding of another command that collides with it in only
// some of the command's contexts (a saved archive command on F against favorites.toggle, which also runs in the gallery) used to silence the default
// everywhere. It now gives way only where the two meet; the default stays active in every other context, and the conflict check no longer rejects
// either of them. A saved binding in the shell (or in every context of the command) still takes the default away completely. Plain objects; no window.
public sealed class ContextYieldTests
{
    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static readonly string[] FavoriteContexts = { "archive", "gallery", "media", "reader" };

    private static KeyEventArgs Press(Key key, KeyModifiers modifiers = KeyModifiers.None) => new() { Key = key, KeyModifiers = modifiers };

    [Fact]
    public void TheFavoriteKeysRunInFourContextsAndTheSavedCommandsUsedBelowRunInOneEach()
    {
        foreach (string id in new[] { "favorites.add", "favorites.remove", "favorites.toggle" })
            Assert.Equal(FavoriteContexts, EffectiveShortcutBindings.ConflictContexts(id).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "archive" }, EffectiveShortcutBindings.ConflictContexts("archive.refresh"));
        Assert.Equal(new[] { "gallery" }, EffectiveShortcutBindings.ConflictContexts("gallery.close"));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void ASavedArchiveCommandOnFTakesFFromTheArchiveOnlyAndTheGalleryKeepsIt(ShortcutPlatform platform)
    {
        ShortcutBindingsSection section = new ShortcutBindingsSection().Set("archive.refresh", [ShortcutGesture.Parse("F")]);
        string before = section.Raw.GetRawText();
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
        ShortcutGesture f = Assert.Single(bindings.For("favorites.toggle"), gesture => gesture.Key == "F");
        Assert.Equal(new[] { "archive" }, bindings.YieldedContexts("favorites.toggle", f));
        KeyEventArgs key = Press(Key.F);
        foreach (string context in new[] { "gallery", "media", "reader" })
            Assert.True(bindings.Matches(key, "favorites.toggle", context), context);
        Assert.False(bindings.Matches(key, "favorites.toggle", "archive"));
        // A caller that does not say where it runs never overrides the saved binding.
        Assert.False(bindings.Matches(key, "favorites.toggle"));
        // The saved command keeps its key, nothing is rejected, and the profile is untouched.
        Assert.True(bindings.Matches(key, "archive.refresh"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave || issue.Reason?.Code == "conflictingActions");
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "favorites.toggle" && issue.OtherCommandId == "archive.refresh"
            && issue.Reason?.Code == "defaultYields" && !issue.BlocksSave);
        Assert.Equal(before, section.Raw.GetRawText());
    }

    [Theory, MemberData(nameof(Platforms))]
    public void ASavedArchiveCommandOnUOrMKeepsTheOtherKeysAndTheGalleryToo(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings removeSaved = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("archive.refresh", [ShortcutGesture.Parse("U")]), platform);
        Assert.True(removeSaved.Matches(Press(Key.U), "favorites.remove", "gallery"));
        Assert.False(removeSaved.Matches(Press(Key.U), "favorites.remove", "archive"));
        Assert.True(removeSaved.Matches(Press(Key.F), "favorites.toggle", "archive")); // F was not saved: it works in the archive too
        EffectiveShortcutBindings addSaved = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("archive.refresh", [ShortcutGesture.Parse("M")]), platform);
        Assert.True(addSaved.Matches(Press(Key.M), "favorites.add", "gallery"));
        Assert.False(addSaved.Matches(Press(Key.M), "favorites.add", "archive"));
        Assert.False(addSaved.Matches(Press(Key.B), "favorites.add", "archive")); // revision 21: B is no key of "Add favorite" any more
        // The other command that has M by default (mute moved to Shift+M) is not involved.
        Assert.True(addSaved.Matches(Press(Key.M, KeyModifiers.Shift), "video.mute", "gallery"));
    }

    [Fact]
    public void SavedCommandsInTwoContextsTakeTheKeyFromBothAndTheRestKeepsIt()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set("archive.refresh", [ShortcutGesture.Parse("F")]).Set("gallery.close", [ShortcutGesture.Parse("F")]), ShortcutPlatform.Linux);
        ShortcutGesture f = Assert.Single(bindings.For("favorites.toggle"), gesture => gesture.Key == "F");
        Assert.Equal(new[] { "archive", "gallery" }, bindings.YieldedContexts("favorites.toggle", f).Order(StringComparer.Ordinal));
        Assert.True(bindings.Matches(Press(Key.F), "favorites.toggle", "reader"));
        Assert.True(bindings.Matches(Press(Key.F), "favorites.toggle", "media"));
        Assert.False(bindings.Matches(Press(Key.F), "favorites.toggle", "gallery"));
        Assert.False(bindings.Matches(Press(Key.F), "favorites.toggle", "archive"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }

    [Fact]
    public void ASavedShellCommandOrOneInEveryContextStillTakesTheDefaultAwayCompletely()
    {
        // A page of the footer (shell) is reachable from every context: no context is left where the default would not collide.
        EffectiveShortcutBindings shell = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("navigation.archive", [ShortcutGesture.Parse("F")]), ShortcutPlatform.Linux);
        Assert.Empty(shell.For("favorites.toggle"));
        Assert.Contains(shell.Issues, issue => issue.CommandId == "favorites.toggle" && issue.Reason?.Code == "defaultYields");
        Assert.False(shell.Matches(Press(Key.F), "favorites.toggle", "gallery"));
        // Saved commands in all four contexts leave nothing either.
        EffectiveShortcutBindings all = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set("archive.refresh", [ShortcutGesture.Parse("F")]).Set("gallery.close", [ShortcutGesture.Parse("F")])
            .Set("media.folders", [ShortcutGesture.Parse("F")]).Set("reader.quick-settings", [ShortcutGesture.Parse("F")]), ShortcutPlatform.Linux);
        Assert.Empty(all.For("favorites.toggle"));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void ASavedVideoKeyKeepsItsKeyEverywhereItWorksEvenWhereItGivesWayToThePagesCommands(ShortcutPlatform platform)
    {
        // The video keys do not conflict in the reader and on the Media page (they route after those pages' commands), but a saved one still works
        // there: a default that kept the key in those contexts would take it, so the default gives way in every context the saved key runs in.
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("video.mute", [ShortcutGesture.Parse("M")]), platform);
        foreach (string context in FavoriteContexts.Where(context => EffectiveShortcutBindings.ExecutionContexts("video.mute").Contains(context)))
            Assert.False(bindings.Matches(Press(Key.M), "favorites.add", context), context);
        Assert.False(bindings.Matches(Press(Key.M), "favorites.add", "reader"));
        Assert.False(bindings.Matches(Press(Key.B), "favorites.add", "gallery")); // revision 21: B is free
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
    }

    [Fact]
    public void TwoExplicitBindingsInTheSameContextStillConflictSymmetrically()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection()
            .Set("archive.refresh", [ShortcutGesture.Parse("F9")]).Set("archive.select-page", [ShortcutGesture.Parse("F9")]), ShortcutPlatform.Linux);
        Assert.Empty(bindings.For("archive.refresh"));
        Assert.Empty(bindings.For("archive.select-page"));
        Assert.Contains(bindings.Issues, issue => issue.Reason?.Code == "conflictingActions" && issue.BlocksSave);
    }

    [Fact]
    public void TheKeyContextScopeRestoresTheOuterOneAndPlainMatchesFollowsIt()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("archive.refresh", [ShortcutGesture.Parse("F")]), ShortcutPlatform.Linux);
        KeyEventArgs key = Press(Key.F);
        Assert.False(bindings.Matches(key, "favorites.toggle"));
        using (EffectiveShortcutBindings.EnterKeyContext("gallery"))
        {
            Assert.True(bindings.Matches(key, "favorites.toggle"));
            using (EffectiveShortcutBindings.EnterKeyContext("archive")) Assert.False(bindings.Matches(key, "favorites.toggle"));
            Assert.True(bindings.Matches(key, "favorites.toggle"));
        }
        Assert.False(bindings.Matches(key, "favorites.toggle"));
    }

    [Fact]
    public void TheGalleryHandlesEveryKeyInItsOwnContext() =>
        Assert.Contains("EffectiveShortcutBindings.EnterKeyContext(\"gallery\")", Source("Desktop/GalleryViewer.cs"));

    /// <summary>Every default of revisions 17 to 20 that runs in several contexts, against a saved binding of one command in each single context it
    /// shares: the key is gone in exactly that context, kept in the others, never rejected, and a context without a saved binding is untouched.</summary>
    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void EveryMultiContextDefaultGivesWayOnlyWhereTheSavedBindingCollides(ShortcutPlatform platform)
    {
        string[] audited =
        {
            // revision 17, 18 and 20 (the video keys and the favourites, mute) and the tile and font sizes of 16 and 20
            "video.play-pause", "video.mute", "video.loop", "video.speed-down", "video.speed-up", "video.step-back", "video.step-forward",
            "favorites.add", "favorites.remove", "favorites.toggle",
            "media.tile-size.larger", "media.tile-size.smaller", "media.tile-size.reset",
            // revision 19
            "media.copy-path", "media.open-external", "tagging.remove",
        };
        EffectiveShortcutBindings none = EffectiveShortcutBindings.Build(null, platform);
        foreach (string id in audited)
        {
            IReadOnlyList<string> own = EffectiveShortcutBindings.ConflictContexts(id);
            if (own.Count < 2 || own.Contains("shell")) continue;
            foreach (ShortcutGesture gesture in none.For(id))
            foreach (string context in own)
            {
                string? owner = CommandCatalog.All.Where(command => command.IsImplemented && command.Id != id
                        && EffectiveShortcutBindings.ConflictContexts(command.Id) is { Count: 1 } single && single[0] == context
                        && EffectiveShortcutBindings.Defaults(command, platform).Count == 0
                        && !EffectiveShortcutBindings.YieldsOn(command.Id, platform))
                    .Select(command => command.Id).FirstOrDefault();
                if (owner == null) continue;
                EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set(owner, [gesture]), platform);
                string what = id + " " + gesture.Display(platform) + " vs " + owner + " in " + context;
                Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave || issue.Reason?.Code == "conflictingActions");
                IReadOnlyList<string> yielded = bindings.For(id).Any(g => EffectiveShortcutBindings.Equivalent(g, gesture, platform))
                    ? bindings.YieldedContexts(id, gesture) : own;
                Assert.Contains(context, yielded);
                Assert.True(own.Where(other => other != context && !yielded.Contains(other)).All(other => bindings.Matches(KeyFor(gesture, platform), id, other)), what);
                Assert.False(bindings.Matches(KeyFor(gesture, platform), id, context), what);
                Assert.True(bindings.Matches(KeyFor(gesture, platform), owner), what);
            }
        }
    }

    private static KeyEventArgs KeyFor(ShortcutGesture gesture, ShortcutPlatform platform)
    {
        KeyGesture resolved = gesture.Resolve(platform);
        return new KeyEventArgs { Key = resolved.Key, KeyModifiers = resolved.KeyModifiers };
    }

    private static string Source(string relative, [CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", relative)));
}
