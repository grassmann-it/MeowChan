using Avalonia.Input;
using MeowChan.Desktop;
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
        "gallery.fit", "gallery.actual-size", "gallery.zoom-in", "gallery.zoom-out", "gallery.filmstrip-toggle",
        "media.details", "media.rating.edit", "viewer.shortcut-help",
        // The media page's focus mode: Shift+F (the F alone is the favourite); a typed capital in a text field is left to the field.
        "media.focus-mode",
        // Gallery video (key scheme of revision 13): M mute, S frame snapshot, [ ] speed, A the A–B loop, Shift+. the next frame, "," and "."
        // seek by the step setting, L repeats the video, D the slideshow. Revision 14: X slower and C faster next to [ and ] (AltGr on DE/FR).
        "video.mute", "video.snapshot", "video.speed-down", "video.speed-up", "video.ab-loop", "video.frame-step",
        "video.step-back", "video.step-forward", "video.loop",
        // Revision 18: the same video keys also act on the Media page's panel video (scope Media added, no new gesture, so no new entry here).
        // PT6: Shift+1 to Shift+5 rate, Shift+0 removes the rating (switched off with the other single keys).
        "media.rating.set-1", "media.rating.set-2", "media.rating.set-3", "media.rating.set-4", "media.rating.set-5", "media.rating.clear",
        // Revision 24: the colour labels on the bare digits 6 to 9.
        "media.label.red", "media.label.yellow", "media.label.green", "media.label.blue",
        // Revision 24: Fit's Primary+0 is no single character, the bare digits of the rating are listed above.
        // PT4: K switches the card mode of the tagging mode, 1 to 5 choose its cards (only inside the mode; the cards stay clickable).
        "tags.queue.cards", "tags.queue.card-1", "tags.queue.card-2", "tags.queue.card-3", "tags.queue.card-4", "tags.queue.card-5",
        // Gallery picture and video: Shift+R turns the shown medium by 90°.
        "gallery.rotate",
        // PT7: Q puts the open medium in the tag queue (media/gallery contexts, revision 9).
        "media.queue.add",
        // G1: Shift+T switches the gallery between View and Tag (gallery context only).
        "gallery.mode-toggle",
        // Revision 13: Shift+H hides the tools, Shift+O switches the controls overlay (Shift+P shows the post, Shift+L the filmstrip).
        "gallery.tools-toggle", "gallery.overlay-toggle",
        // Revision 13 with GL3's layout rows registered: Shift+I shows or hides the info line, Shift+N the hint row.
        "gallery.layout.info", "gallery.layout.hints",
        // Revision 19 (decision #125): D and S mark the selected tagging entries done or skipped (the tagging page's queue list only), H hides the
        // selected post in the reader (its post list; a text field keeps the letter, CommandBindings.Matches).
        "tagging.mark-done", "tagging.mark-skipped", "tags.queue.skip", "post.hide"
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
    public void OnlyReaderExpandUsesTheExactNonTextCtrlAltEnterChord(ShortcutPlatform platform)
    {
        foreach (var command in CommandCatalog.All)
            foreach (var gesture in EffectiveShortcutBindings.Defaults(command, platform))
                if (gesture.Modifiers.Contains("Alt") && gesture.Modifiers.Any(m => m is "Primary" or "Control" or "Ctrl"))
                {
                    Assert.Equal("reader.expand", command.Id);
                    Assert.Equal(Key.Enter, gesture.Resolve(platform).Key);
                    Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, gesture.Resolve(platform).KeyModifiers);
                }
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
        foreach (string id in new[] { "navigation.media", "navigation.booru", "navigation.plugins", "thread.favorite-toggle", "thread.tags", "media.tags.edit",
            "media.select-all", "reader.find-next", "reader.find-previous", "gallery.slideshow-toggle", "media.details", "media.rating.edit",
            "media.export-original", "media.show-in-folder", "reader.history-back", "gallery.return", "pagination.previous", "pagination.next",
            "window.minimize", "menu.context-open", "video.mute", "video.seek-back", "video.seek-forward", "video.seek-back-far",
            "video.seek-forward-far", "video.speed-down", "video.speed-up", "video.speed-reset", "video.snapshot",
            "video.ab-loop", "video.frame-step", "gallery.rotate", "gallery.mode-toggle",
            // Revisions 12, 17 and 18: the video keys that the reader (17) and the Media page's panel (18) share with the gallery.
            "video.play-pause", "video.loop", "video.step-back", "video.step-forward",
            "gallery.context-toggle", "gallery.tools-toggle", "gallery.overlay-toggle", "gallery.layout.info", "gallery.layout.hints" })
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
    }

    [Theory]
    [InlineData("tags.queue.skip", "Space", "S", "gallery-tagging")]
    [InlineData("video.speed-reset", "Back", "Shift+Back", "gallery")]
    [InlineData("video.speed-reset", "Back", "Shift+Back", "reader")]
    [InlineData("video.speed-reset", "Back", "Shift+Back", "media")]
    [InlineData("video.snapshot", "S", "Shift+S", "gallery")]
    [InlineData("gallery.slideshow-toggle", "D", "Shift+D", "gallery")]
    [InlineData("tags.queue.done-next", "Enter", "Primary+Enter", "gallery-tagging")]
    [InlineData("reader.expand", "Primary+Enter", "Primary+Alt+Enter", "reader")]
    [InlineData("video.volume-up", "Up", "Shift+Up", "gallery")]
    [InlineData("video.volume-down", "Down", "Shift+Down", "gallery")]
    [InlineData("video.seek-back", "Primary+Left", "Alt+Left", "gallery")]
    [InlineData("video.seek-forward", "Primary+Right", "Alt+Right", "gallery")]
    public void Ledger17MovedGesturesResolveAndTheirOldGesturesDoNot(string id, string oldKey, string newKey, string context)
    {
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            var bindings = EffectiveShortcutBindings.Build(platform: platform);
            var expected = ShortcutGesture.Parse(newKey).Resolve(platform);
            var old = ShortcutGesture.Parse(oldKey).Resolve(platform);
            Assert.True(bindings.Matches(new KeyEventArgs { Key = expected.Key, KeyModifiers = expected.KeyModifiers }, id, context), id);
            Assert.False(bindings.Matches(new KeyEventArgs { Key = old.Key, KeyModifiers = old.KeyModifiers }, id, context), id);
            Assert.True(EffectiveShortcutBindings.YieldsOn(id, platform), id);
        }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void Ledger17SMeansSkipInBothTaggingSurfaces(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.S }, "tagging.mark-skipped", "tagging"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.S }, "tags.queue.skip", "gallery-tagging"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Space }, "video.play-pause", "gallery"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Back }, "tags.queue.back", "gallery-tagging"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.D }, "tagging.mark-done", "tagging"));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void Ledger17PrimaryEnterMeansCommitAndAdvanceInBothTaggingContexts(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        var key = ShortcutGesture.Parse("Primary+Enter").Resolve(platform);
        var press = new KeyEventArgs { Key = key.Key, KeyModifiers = key.KeyModifiers };
        Assert.True(bindings.Matches(press, "tags.queue.done-next", "gallery-tagging"));
        Assert.True(bindings.Matches(press, "tags.quick.commit-next", "gallery"));
        // UX-LEDGER-16 owns open/activate behavior. Pin only their existing Enter defaults here.
        foreach (string id in new[] { "tagging.continue-here", "thread.read", "media.open", "reader.media.activate" })
            Assert.Equal("Enter", CommandCatalog.Get(id).Shortcut);
    }

    [Theory, MemberData(nameof(Platforms))]
    public void Ledger17UnmodifiedArrowsRemainReaderPostNavigation(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Up }, "reader.previous-post", "reader"));
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Down }, "reader.next-post", "reader"));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void Ledger17FarSeekingAndSidebarArrowsStayDistinct(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        foreach ((string id, string gesture, string context) in new[]
        {
            ("video.seek-back-far", "Primary+Shift+Left", "gallery"),
            ("video.seek-forward-far", "Primary+Shift+Right", "gallery"),
            ("reader.sidebar.previous-media", "Alt+Shift+Left", "reader"),
            ("reader.sidebar.next-media", "Alt+Shift+Right", "reader")
        })
        {
            var key = ShortcutGesture.Parse(gesture).Resolve(platform);
            Assert.True(bindings.Matches(new KeyEventArgs { Key = key.Key, KeyModifiers = key.KeyModifiers }, id, context));
            Assert.Single(bindings.For(id));
        }
        if (platform == ShortcutPlatform.MacOS)
            foreach (string id in new[] { "media.return", "reader.history-back", "gallery.return" })
                Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.Left, KeyModifiers = KeyModifiers.Meta }, id, "reader"));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void SharedBindingsHaveExactlyTheirIntendedOwners(ShortcutPlatform platform)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        foreach ((string gesture, string[] owners) in new[]
        {
            ("S", new[] { "tagging.mark-skipped", "tags.queue.skip" }),
            ("Primary+Enter", new[] { "tags.queue.done-next", "tags.quick.commit-next" }),
            ("Enter", new[] { "booru.search.submit", "gallery.open", "reader.media.activate", "tagging.continue-here", "thread.read" }),
            ("Shift+Enter", new[] { "booru.search.alternate", "gallery.open" })
        })
        {
            var key = ShortcutGesture.Parse(gesture).Resolve(platform);
            Assert.Equal(owners, CommandCatalog.All.Where(command => bindings.Matches(
                new KeyEventArgs { Key = key.Key, KeyModifiers = key.KeyModifiers }, command.Id))
                .Select(command => command.Id).Order(StringComparer.Ordinal));
        }
    }

    /// <summary>The nine video commands the gallery shares with the reader and the Media page's panel (revision 18).</summary>
    private static readonly string[] MediaPanelVideoCommands =
    {
        "video.play-pause", "video.mute", "video.loop", "video.step-back", "video.step-forward",
        "video.speed-down", "video.speed-up", "video.speed-reset", "video.frame-step"
    };

    [Theory, MemberData(nameof(Platforms))]
    public void Revision18ScopesTheVideoKeysToTheMediaPageAndAddsThePanelArrowsWithoutAKey(ShortcutPlatform platform)
    {
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 18);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        // Exactly the reader's video keys, each also a Media command with its own media context; their defaults keep yielding to saved bindings.
        Assert.Equal(MediaPanelVideoCommands.Order(StringComparer.Ordinal), ReaderVideoKeys.Commands.Select(entry => entry.Id).Order(StringComparer.Ordinal));
        foreach (string id in MediaPanelVideoCommands)
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.True(command.Scope.HasFlag(CommandScope.Gallery) && command.Scope.HasFlag(CommandScope.Reader) && command.Scope.HasFlag(CommandScope.Media), id);
            Assert.Contains("media", command.ExecutionContexts);
            Assert.Contains("media-video-keys", command.Contexts);
            Assert.Contains(id, EffectiveShortcutBindings.YieldingDefaultCommandIds);
            Assert.NotEmpty(bindings.For(id));
        }
        // Seeking with Ctrl+arrows stays a gallery key: on the Media page the arrows move the grid's selection.
        foreach (string id in new[] { "video.seek-back", "video.seek-forward", "video.seek-back-far", "video.seek-forward-far" })
            Assert.False(CommandCatalog.Get(id).Scope.HasFlag(CommandScope.Media), id);
        // The panel shows exactly one medium: it has no previous/next commands (the grid's own arrow keys move the selection).
        Assert.DoesNotContain(CommandCatalog.All, command => command.Id is "media.panel.previous" or "media.panel.next");
        Assert.DoesNotContain(bindings.Issues, issue => MediaPanelVideoCommands.Contains(issue.CommandId)
            || issue.OtherCommandId != null && MediaPanelVideoCommands.Contains(issue.OtherCommandId));
    }
}
