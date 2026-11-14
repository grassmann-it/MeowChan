using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>TP6: the quick-tag commands (gallery tag panel, tag popover) are registered with defaults, conflict free on every platform,
/// and stay inactive and silent until their handlers are wired.</summary>
[Collection("Localization")]
public sealed class QuickTagCommandTests
{
    public QuickTagCommandTests() => Texts.SetLanguage("en");

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static string Show(string id, ShortcutPlatform platform) =>
        string.Join(" / ", EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), platform).Select(g => g.Display(platform)));

    [Fact]
    public void EveryQuickTagIdIsACatalogCommandWithLabelsAndNothingElseUsesTheNamespace()
    {
        Assert.Equal(17, QuickTagCommands.All.Count);
        Assert.Equal(QuickTagCommands.All.Count, QuickTagCommands.All.Distinct().Count());
        foreach (string id in QuickTagCommands.All)
        {
            Assert.True(CommandCatalog.TryGet(id, out CommandDefinition? command), id);
            Assert.False(string.IsNullOrWhiteSpace(command!.LabelEn) || string.IsNullOrWhiteSpace(command.LabelDe), id);
            Assert.Equal(command.LabelEn, BuiltInCatalog.English[command.LabelKey]);
            Assert.Equal(command.LabelDe, BuiltInCatalog.German[command.LabelKey]);
        }
        Assert.Equal(QuickTagCommands.All.OrderBy(x => x, StringComparer.Ordinal),
            CommandCatalog.All.Select(c => c.Id).Where(id => id.StartsWith("tags.quick.", StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal("tags.quick.suggestion-9", QuickTagCommands.Suggestion(9));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuickTagCommands.Suggestion(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuickTagCommands.Suggestion(10));
    }

    [Fact]
    public void TheDefaultGesturesFollowThePlanOnWindowsAndLinux()
    {
        foreach (var platform in new[] { ShortcutPlatform.Windows, ShortcutPlatform.Linux })
        {
            Assert.Equal("PageDown", Show(QuickTagCommands.Next, platform));
            Assert.Equal("PageUp", Show(QuickTagCommands.Previous, platform));
            Assert.Equal("Ctrl+Enter", Show(QuickTagCommands.CommitNext, platform));
            Assert.Equal("Shift+PageDown", Show(QuickTagCommands.MarkNext, platform));
            Assert.Equal("Shift+PageUp", Show(QuickTagCommands.MarkPrevious, platform));
            Assert.Equal("Ctrl+T", Show(QuickTagCommands.ThreadSection, platform));
            Assert.Equal("Ctrl+Z", Show(QuickTagCommands.Undo, platform));
            for (int n = 1; n <= QuickTagCommands.SuggestionCount; n++)
                Assert.Equal("Alt+" + n, Show(QuickTagCommands.Suggestion(n), platform));
        }
    }

    [Fact]
    public void OnMacTheArrowsAreTheFirstGestureAndPageKeysOnlyExtras()
    {
        // ⌘1-9 would collide with the page navigation (⌘1-7, shell context), so the digits stay on ⌥ on every platform.
        Assert.Equal("⌘↓ / ⇟", Show(QuickTagCommands.Next, ShortcutPlatform.MacOS));
        Assert.Equal("⌘↑ / ⇞", Show(QuickTagCommands.Previous, ShortcutPlatform.MacOS));
        Assert.Equal("⇧⌘↓ / ⇧⇟", Show(QuickTagCommands.MarkNext, ShortcutPlatform.MacOS));
        Assert.Equal("⇧⌘↑ / ⇧⇞", Show(QuickTagCommands.MarkPrevious, ShortcutPlatform.MacOS));
        Assert.Equal("⌘↩", Show(QuickTagCommands.CommitNext, ShortcutPlatform.MacOS));
        Assert.Equal("⌘T", Show(QuickTagCommands.ThreadSection, ShortcutPlatform.MacOS));
        Assert.Equal("⌘Z", Show(QuickTagCommands.Undo, ShortcutPlatform.MacOS));
        Assert.Equal("⌥3", Show(QuickTagCommands.Suggestion(3), ShortcutPlatform.MacOS));
        foreach (string id in QuickTagCommands.All)
            Assert.False(EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), ShortcutPlatform.MacOS)
                .All(g => g.Key is "PageUp" or "PageDown"), id);
    }

    [Theory, MemberData(nameof(Platforms))]
    public void NoQuickTagDefaultSharesAGestureWithACommandThatActsInTheSameContext(ShortcutPlatform platform)
    {
        // The effective table cannot show this while the commands are inactive, so compare the plain defaults.
        foreach (string id in QuickTagCommands.All)
            foreach (var gesture in EffectiveShortcutBindings.Defaults(CommandCatalog.Get(id), platform))
            {
                Assert.Null(EffectiveShortcutBindings.InvalidReason(gesture, platform));
                Assert.False(EffectiveShortcutBindings.IsSingleCharacterKey(gesture), id + " is a bare character key");
                var clashes = CommandCatalog.All.Where(other => other.Id != id && EffectiveShortcutBindings.ContextsOverlap(id, other.Id)
                    && EffectiveShortcutBindings.Defaults(other, platform).Any(g => EffectiveShortcutBindings.Equivalent(g, gesture, platform)))
                    .Select(other => other.Id).ToArray();
                Assert.True(clashes.Length == 0, $"{id} {gesture.Display(platform)} on {platform} clashes with {string.Join(", ", clashes)}");
            }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void EveryQuickTagDefaultYieldsToASavedBindingOfAnotherCommand(ShortcutPlatform platform)
    {
        foreach (string id in QuickTagCommands.All)
        {
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
            Assert.True(EffectiveShortcutBindings.YieldsOn(id, platform), id);
        }
    }

    [Fact]
    public void ThreadTagsKeepCtrlTInTheArchiveAndTheReaderWhileTheGalleryPanelUsesItForTheThreadSection()
    {
        Assert.Equal(new[] { "gallery" }, CommandCatalog.Get(QuickTagCommands.ThreadSection).ExecutionContexts);
        Assert.Equal(new[] { "archive", "reader" }, CommandCatalog.Get("thread.tags").ExecutionContexts);
        Assert.False(EffectiveShortcutBindings.ContextsOverlap(QuickTagCommands.ThreadSection, "thread.tags"));
        Assert.True(EffectiveShortcutBindings.Equivalent(ShortcutGesture.Parse("Primary+T"), ShortcutGesture.Parse("Primary+T"), ShortcutPlatform.Linux));
    }

    [Fact]
    public void MediaTagsEditIsAGalleryCommandInTheCatalogAndTheQuickTagScopesAreWhereTheSurfacesAre()
    {
        Assert.True(CommandCatalog.Get("media.tags.edit").Scope.HasFlag(CommandScope.Gallery));
        Assert.Equal("Ctrl+Shift+T", Show("media.tags.edit", ShortcutPlatform.Linux));
        foreach (string id in new[] { QuickTagCommands.Next, QuickTagCommands.Previous, QuickTagCommands.CommitNext, QuickTagCommands.MarkNext,
            QuickTagCommands.MarkPrevious, QuickTagCommands.ThreadSection })
        {
            Assert.Equal(CommandScope.Gallery, CommandCatalog.Get(id).Scope);
            Assert.Equal(new[] { "gallery" }, CommandCatalog.Get(id).ExecutionContexts);
        }
        foreach (string id in new[] { QuickTagCommands.Undo }.Concat(Enumerable.Range(1, 9).Select(QuickTagCommands.Suggestion)))
            Assert.Equal(new[] { "gallery", "media", "reader", "archive" }, CommandCatalog.Get(id).ExecutionContexts);
    }

    private static readonly string[] PanelCommands =
    {
        QuickTagCommands.Next, QuickTagCommands.Previous, QuickTagCommands.CommitNext, QuickTagCommands.MarkNext,
        QuickTagCommands.MarkPrevious, QuickTagCommands.ThreadSection
    };

    [Fact]
    public void EveryQuickTagCommandIsWiredNowAndTheMarkingKeysAreActiveWithoutIssues()
    {
        // TP2 wired the gallery panel (paging, commit-and-next, thread section, undo, nine toggles), TP4 the marking: nothing awaits a handler.
        Assert.Empty(QuickTagCommands.AwaitingHandler);
        Assert.Equal(QuickTagCommands.All.OrderBy(x => x, StringComparer.Ordinal), QuickTagCommands.WiredIds.OrderBy(x => x, StringComparer.Ordinal));
        foreach (var platform in Enum.GetValues<ShortcutPlatform>())
        {
            var bindings = EffectiveShortcutBindings.Build(null, platform);
            Assert.Empty(bindings.Issues);
            foreach (string id in QuickTagCommands.All)
            {
                Assert.True(CommandCatalog.Get(id).IsImplemented, id);
                Assert.False(CommandCatalog.Get(id).IsAwaitingHandler, id);
                Assert.NotEmpty(bindings.For(id));
            }
        }
        var linux = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        Assert.True(linux.Matches(new KeyEventArgs { Key = Key.Enter, KeyModifiers = KeyModifiers.Control }, QuickTagCommands.CommitNext));
        Assert.True(linux.Matches(new KeyEventArgs { Key = Key.Z, KeyModifiers = KeyModifiers.Control }, QuickTagCommands.Undo));
        Assert.True(linux.Matches(new KeyEventArgs { Key = Key.PageDown, KeyModifiers = KeyModifiers.Shift }, QuickTagCommands.MarkNext));
        Assert.True(linux.Matches(new KeyEventArgs { Key = Key.PageUp, KeyModifiers = KeyModifiers.Shift }, QuickTagCommands.MarkPrevious));
    }

    [Fact]
    public void UndoAndTheNineSuggestionTogglesAreWiredByThePopoverAndActiveWithTheirDefaults()
    {
        string[] wired = new[] { QuickTagCommands.Undo, QuickTagCommands.StampApply }.Concat(Enumerable.Range(1, QuickTagCommands.SuggestionCount).Select(QuickTagCommands.Suggestion)).ToArray();
        Assert.Equal(QuickTagCommands.All.Count - PanelCommands.Length, wired.Length);
        foreach (string id in wired)
        {
            Assert.DoesNotContain(id, QuickTagCommands.AwaitingHandler);
            Assert.True(CommandCatalog.Get(id).IsImplemented, id);
        }
        var linux = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        Assert.True(linux.Matches(new KeyEventArgs { Key = Key.Z, KeyModifiers = KeyModifiers.Control }, QuickTagCommands.Undo));
        for (int n = 1; n <= QuickTagCommands.SuggestionCount; n++)
            Assert.True(linux.Matches(new KeyEventArgs { Key = Key.D0 + n, KeyModifiers = KeyModifiers.Alt }, QuickTagCommands.Suggestion(n)), n.ToString());
        Assert.False(linux.Matches(new KeyEventArgs { Key = Key.D1, KeyModifiers = KeyModifiers.Control }, QuickTagCommands.Suggestion(1)));
    }

    [Fact]
    public void AnIdIsWiredOnlyWhenItIsInTheWiredListSoTheMarkingWasInactiveUntilItsHandlerExisted()
    {
        // The rule behind AwaitingHandler: registered ids without a handler stay awaiting, wired ones do not.
        string[] withoutMarking = QuickTagCommands.WiredIds.Where(id => id != QuickTagCommands.MarkNext && id != QuickTagCommands.MarkPrevious).ToArray();
        IReadOnlySet<string> awaiting = QuickTagCommands.Unwired(QuickTagCommands.All, withoutMarking);
        Assert.Equal(new[] { QuickTagCommands.MarkNext, QuickTagCommands.MarkPrevious }.OrderBy(x => x, StringComparer.Ordinal), awaiting.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Empty(QuickTagCommands.Unwired(QuickTagCommands.All, QuickTagCommands.All));
        Assert.Equal(QuickTagCommands.All.Count, QuickTagCommands.Unwired(QuickTagCommands.All, Array.Empty<string>()).Count);
        // Every wired id has a gallery panel handler and every handled id is wired: a handler and its registration cannot drift apart.
        Assert.Equal(GalleryTagPanelCommands.Handled.OrderBy(x => x, StringComparer.Ordinal), QuickTagCommands.WiredIds.OrderBy(x => x, StringComparer.Ordinal));
        foreach (string id in QuickTagCommands.WiredIds) Assert.NotNull(GalleryTagPanelCommands.RouteOf(id));
    }

    [Fact]
    public void ASavedBindingOnAMarkingCommandIsNowActiveAndRaisesNoIssue()
    {
        var section = new ShortcutBindingsSection().Set(QuickTagCommands.MarkNext, [ShortcutGesture.Parse("Primary+Shift+Enter")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+Shift+Enter", Assert.Single(bindings.For(QuickTagCommands.MarkNext)).Display(ShortcutPlatform.Linux));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Enter, KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift }, QuickTagCommands.MarkNext));
        Assert.False(bindings.Matches(new KeyEventArgs { Key = Key.PageDown, KeyModifiers = KeyModifiers.Shift }, QuickTagCommands.MarkNext));
        Assert.DoesNotContain(bindings.Issues, issue => issue.Reason?.Code == "notImplemented");
    }

    [Fact]
    public void ASavedBindingOfAnotherCommandOnAQuickTagKeyIsNeverSilencedByTheNewDefault()
    {
        var section = new ShortcutBindingsSection()
            .Set("navigation.archive", [ShortcutGesture.Parse("Primary+Enter")])
            .Set("navigation.statistics", [ShortcutGesture.Parse("Alt+D1")]);
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Equal("Ctrl+Enter", bindings.Display("navigation.archive"));
        Assert.Equal("Alt+1", bindings.Display("navigation.statistics"));
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave);
        Assert.Contains(bindings.Issues, issue => issue.CommandId == QuickTagCommands.CommitNext && issue.OtherCommandId == "navigation.archive"
            && issue.Reason?.Code == "defaultYields");
        // The shared-surface commands yield the same way, and a profile without those bindings is untouched.
        Assert.Contains(bindings.Issues, issue => issue.CommandId == QuickTagCommands.Suggestion(1) && issue.OtherCommandId == "navigation.statistics");
        var plain = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("navigation.archive", [ShortcutGesture.Parse("Primary+9")]), ShortcutPlatform.Linux);
        Assert.Empty(plain.Issues.Where(issue => issue.BlocksSave));
    }

    [Fact]
    public void TheDefaultsRevisionCoversTheQuickTagCommands()
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 6);
    }
}
