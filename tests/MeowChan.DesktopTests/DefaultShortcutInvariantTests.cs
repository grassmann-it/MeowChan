using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

/// <summary>Invariants of the whole default table, on every platform: no conflicts, valid gestures, single keys only from a fixed list.</summary>
public sealed class DefaultShortcutInvariantTests
{
    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    /// <summary>Bare character keys that may be a default: only inside lists, tiles and the gallery, never in text. Each has a menu or button
    /// counterpart, is listed in F1 for its page, can be rebound and is turned off by the single-key switch.</summary>
    private static readonly string[] SingleKeyCommands =
    {
        "favorites.toggle", "favorites.add", "favorites.remove",
        "gallery.context-toggle", "gallery.show-in-thread", "gallery.slideshow-toggle",
        "gallery.fit", "gallery.actual-size", "gallery.zoom-in", "gallery.zoom-out",
        "media.details", "media.rating.edit", "viewer.shortcut-help",
        // Gallery video: Shift+M mute, Shift+S frame snapshot, [ ] and Shift+, / Shift+. speed, A the A–B loop, "." the next frame.
        "video.mute", "video.snapshot", "video.speed-down", "video.speed-up", "video.ab-loop", "video.frame-step"
    };

    [Theory, MemberData(nameof(Platforms))]
    public void TheDefaultTableRaisesNoIssue(ShortcutPlatform platform) =>
        Assert.Empty(EffectiveShortcutBindings.Build(null, platform).Issues);

    [Theory, MemberData(nameof(Platforms))]
    public void EveryDefaultGestureIsValidOnItsPlatform(ShortcutPlatform platform)
    {
        foreach (var command in CommandCatalog.All)
            foreach (var gesture in EffectiveShortcutBindings.Defaults(command, platform))
                Assert.True(EffectiveShortcutBindings.InvalidReason(gesture, platform) == null,
                    command.Id + " " + gesture.Display(platform) + ": " + EffectiveShortcutBindings.InvalidReason(gesture, platform));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.Linux)]
    public void NoDefaultUsesCtrlWithAltBecauseThatIsAltGrOnManyKeyboardLayouts(ShortcutPlatform platform)
    {
        foreach (var command in CommandCatalog.All)
            foreach (var gesture in EffectiveShortcutBindings.Defaults(command, platform))
                Assert.False(gesture.Modifiers.Contains("Alt") && gesture.Modifiers.Any(m => m is "Primary" or "Control" or "Ctrl"),
                    command.Id + " " + gesture.Display(platform));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void SingleCharacterDefaultsComeOnlyFromTheFixedList(ShortcutPlatform platform)
    {
        var found = CommandCatalog.All.Where(command => EffectiveShortcutBindings.Defaults(command, platform)
            .Any(EffectiveShortcutBindings.IsSingleCharacterKey)).Select(command => command.Id).ToArray();
        Assert.Empty(found.Except(SingleKeyCommands));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void TurningSingleKeysOffLeavesEveryFixedListCommandWithoutABareCharacter(ShortcutPlatform platform)
    {
        var off = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().WithSingleKeyShortcuts(false), platform);
        Assert.Empty(off.Issues);
        foreach (var command in CommandCatalog.All)
            Assert.DoesNotContain(off.For(command.Id), EffectiveShortcutBindings.IsSingleCharacterKey);
    }

    [Theory, MemberData(nameof(Platforms))]
    public void EveryCommandWithADefaultIsImplementedAndHasALabelInBothLanguages(ShortcutPlatform platform)
    {
        // Quick-tag commands are registered ahead of their handlers (QuickTagCommands.AwaitingHandler); QuickTagCommandTests pins that list.
        foreach (var command in CommandCatalog.All.Where(command => EffectiveShortcutBindings.Defaults(command, platform).Count > 0))
        {
            Assert.True(command.IsImplemented || command.IsAwaitingHandler, command.Id);
            Assert.False(string.IsNullOrWhiteSpace(command.LabelEn) || string.IsNullOrWhiteSpace(command.LabelDe), command.Id);
        }
    }

    [Fact]
    public void EveryChangedDefaultOfTheCurrentRevisionYieldsToSavedBindings()
    {
        // A command whose default differs between platforms or was added after profiles exist must be in the yielding table.
        foreach (string id in new[] { "navigation.media", "navigation.plugins", "thread.favorite-toggle", "thread.tags", "media.tags.edit",
            "media.select-all", "reader.find-next", "reader.find-previous", "gallery.slideshow-toggle", "media.details", "media.rating.edit",
            "media.export-original", "media.show-in-folder", "reader.history-back", "gallery.return", "pagination.previous", "pagination.next",
            "window.minimize", "menu.context-open", "video.mute", "video.seek-back", "video.seek-forward", "video.seek-back-far",
            "video.seek-forward-far", "video.speed-down", "video.speed-up", "video.speed-reset", "video.snapshot",
            "video.ab-loop", "video.frame-step" })
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
    }
}
