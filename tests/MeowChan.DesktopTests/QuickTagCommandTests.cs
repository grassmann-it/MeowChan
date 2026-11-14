using Avalonia.Input;
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
        Assert.Equal(16, QuickTagCommands.All.Count);
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

    [Fact]
    public void CommandsAwaitingTheirHandlerAreUnavailableInactiveAndRaiseNoIssue()
    {
        // TP2/TP3 add ids to QuickTagCommands.Wired; until then none is implemented, none matches a key, none is reported.
        Assert.Equal(QuickTagCommands.All.OrderBy(x => x, StringComparer.Ordinal), QuickTagCommands.AwaitingHandler.OrderBy(x => x, StringComparer.Ordinal));
        foreach (var platform in Enum.GetValues<ShortcutPlatform>())
        {
            var bindings = EffectiveShortcutBindings.Build(null, platform);
            Assert.Empty(bindings.Issues);
            foreach (string id in QuickTagCommands.AwaitingHandler)
            {
                var command = CommandCatalog.Get(id);
                Assert.False(command.IsImplemented, id);
                Assert.Equal(CommandAvailability.Unavailable, command.Availability);
                Assert.Empty(bindings.For(id));
                Assert.NotEmpty(bindings.RequestedGestures[id]);
            }
        }
        var linux = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        Assert.False(linux.Matches(new KeyEventArgs { Key = Key.Enter, KeyModifiers = KeyModifiers.Control }, QuickTagCommands.CommitNext));
        Assert.False(linux.Matches(new KeyEventArgs { Key = Key.Z, KeyModifiers = KeyModifiers.Control }, QuickTagCommands.Undo));
    }

    [Fact]
    public void ASavedBindingOnAQuickTagCommandIsRetainedAndReportedWhileItWaitsForItsHandler()
    {
        var section = new ShortcutBindingsSection().Set(QuickTagCommands.CommitNext, [ShortcutGesture.Parse("Primary+Shift+Enter")]);
        string before = section.Raw.GetRawText();
        var bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        Assert.Empty(bindings.For(QuickTagCommands.CommitNext));
        Assert.Contains(bindings.Issues, issue => issue.CommandId == QuickTagCommands.CommitNext && issue.Reason?.Code == "notImplemented" && !issue.BlocksSave);
        Assert.DoesNotContain(bindings.Issues, issue => issue.CommandId != QuickTagCommands.CommitNext);
        Assert.Equal(before, section.Raw.GetRawText());
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
