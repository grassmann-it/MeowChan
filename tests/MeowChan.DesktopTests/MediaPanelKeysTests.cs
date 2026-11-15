using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Review P2 of the Media page's media panel (1.1.0 split view): the video keys give way on the Media page instead of conflicting there (S-1), the panel's
// switch is a command with the reader sidebar's key (S-4), and Escape walks the page's levels with the panel after the focus mode (S-5).
[Collection("Localization")]
public sealed class MediaPanelKeysTests : IDisposable
{
    public MediaPanelKeysTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static bool Has(EffectiveShortcutBindings bindings, string id, ShortcutGesture gesture) =>
        bindings.For(id).Any(g => EffectiveShortcutBindings.Equivalent(g, gesture, bindings.Platform));

    // ---- S-1: a saved Media binding on a video key ----

    [Theory, MemberData(nameof(Platforms))]
    public void ASavedMediaBindingOnAVideoKeyLeavesTheKeyToTheGalleryAndTheReader(ShortcutPlatform platform)
    {
        // Every default gesture of the nine panel video keys, saved for a command that only runs on the Media page (media.focus-mode, an arrow of the panel).
        foreach ((string video, _) in ReaderVideoKeys.Commands)
            foreach (ShortcutGesture gesture in EffectiveShortcutBindings.Defaults(CommandCatalog.Get(video), platform))
                foreach (string owner in new[] { "media.focus-mode", "media.panel.next" })
                {
                    var section = new ShortcutBindingsSection().Set(owner, [gesture]);
                    EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
                    string what = video + " / " + owner + " / " + gesture.Display(platform);
                    Assert.True(Has(bindings, video, gesture), what);
                    Assert.True(Has(bindings, owner, gesture), what);
                    Assert.DoesNotContain(bindings.Issues, issue => issue.CommandId == video || issue.OtherCommandId == video);
                }
    }

    [Fact]
    public void SpaceSavedForTheFocusModeKeepsSpaceAsPlayPauseInTheGallery()
    {
        // The review's case: Space was free for a Media command before revision 18 and stays so; the gallery's (and the reader's) Space still plays.
        var section = new ShortcutBindingsSection().Set("media.focus-mode", [ShortcutGesture.Parse("Space")]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, ShortcutPlatform.Linux);
        var space = new KeyEventArgs { Key = Key.Space };
        Assert.True(bindings.Matches(space, "video.play-pause"));
        Assert.True(bindings.Matches(space, "media.focus-mode"));
        Assert.Empty(bindings.Issues);
        // On the Media page the page's own command has it: the panel does not take Space for its video.
        Assert.Equal(MediaPanelKeyRoute.None, MediaPanelKeys.Route(Key.Space, KeyModifiers.None, id => bindings.Matches(space, id),
            panelShown: true, videoOffered: true, nativeInput: false, fromPanel: false, canPrevious: true, canNext: true));
        // Without that binding Space plays the panel video (and so no longer marks a tile with Ctrl+arrows, Space while a video is shown: K-6).
        EffectiveShortcutBindings plain = EffectiveShortcutBindings.Build(null, ShortcutPlatform.Linux);
        Assert.Equal(new MediaPanelKeyRoute(MediaPanelKeyKind.Video, ReaderVideoAction.PlayPause), MediaPanelKeys.Route(Key.Space, KeyModifiers.None,
            id => plain.Matches(space, id), panelShown: true, videoOffered: true, nativeInput: false, fromPanel: false, canPrevious: true, canNext: true));
    }

    [Theory, MemberData(nameof(Platforms))]
    public void AGalleryOrReaderBindingStillMakesTheVideoKeyYield(ShortcutPlatform platform)
    {
        // The yield of a changed default is unchanged where the gesture really competes: the gallery (here the slideshow on M) and the reader.
        var section = new ShortcutBindingsSection().Set("gallery.slideshow-toggle", [ShortcutGesture.Parse("M")]);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(section, platform);
        Assert.False(Has(bindings, "video.mute", ShortcutGesture.Parse("M")));
        Assert.Contains(bindings.Issues, issue => issue.CommandId == "video.mute" && issue.OtherCommandId == "gallery.slideshow-toggle" && !issue.BlocksSave);
    }

    [Fact]
    public void TheVideoKeysCompeteEverywhereTheyRunExceptOnTheMediaPage()
    {
        foreach ((string id, _) in ReaderVideoKeys.Commands)
        {
            Assert.Contains("media", CommandCatalog.Get(id).ExecutionContexts);
            Assert.Contains(EffectiveShortcutBindings.MediaPanelVideoKeys, CommandCatalog.Get(id).Contexts);
            Assert.Equal(new[] { "gallery", "reader" }, EffectiveShortcutBindings.ConflictContexts(id).Order(StringComparer.Ordinal));
            Assert.False(EffectiveShortcutBindings.ContextsOverlap(id, "media.focus-mode"), id);
            Assert.False(EffectiveShortcutBindings.ContextsOverlap(id, MediaPanelKeys.ToggleCommandId), id);
            Assert.True(EffectiveShortcutBindings.ContextsOverlap(id, "gallery.slideshow-toggle"), id);
        }
        // Only the panel's video keys carry the marker; every other Media command keeps its full contexts.
        Assert.All(CommandCatalog.All.Where(command => command.Contexts.Contains(EffectiveShortcutBindings.MediaPanelVideoKeys)),
            command => Assert.True(ReaderVideoKeys.IsVideoCommand(command.Id), command.Id));
        Assert.Equal(new[] { "media" }, EffectiveShortcutBindings.ConflictContexts("media.focus-mode"));
    }

    [Fact]
    public void ThePageClaimsAKeyThroughItsOwnCommandsButNotThroughTheVideoKeysOrThePanelArrows()
    {
        Assert.True(MediaPanelKeys.PageClaims(id => id == "media.focus-mode"));
        Assert.True(MediaPanelKeys.PageClaims(id => id == "media.select-all"));
        Assert.True(MediaPanelKeys.PageClaims(id => id == MediaPanelKeys.ToggleCommandId));
        Assert.True(MediaPanelKeys.PageClaims(id => id == "media.details"));
        Assert.False(MediaPanelKeys.PageClaims(id => ReaderVideoKeys.IsVideoCommand(id)));
        Assert.False(MediaPanelKeys.PageClaims(id => id is "media.panel.previous" or "media.panel.next"));
        Assert.False(MediaPanelKeys.PageClaims(id => id == "gallery.slideshow-toggle"));
        Assert.False(MediaPanelKeys.PageClaims(_ => false));
    }

    // ---- S-4: the panel's switch ----

    [Theory, MemberData(nameof(Platforms))]
    public void ThePanelSwitchIsAMediaCommandWithTheReaderSidebarsKeyAndNoConflict(ShortcutPlatform platform)
    {
        CommandDefinition toggle = CommandCatalog.Get(MediaPanelKeys.ToggleCommandId);
        Assert.Equal(CommandScope.Media, toggle.Scope);
        Assert.Equal(new[] { "media" }, toggle.ExecutionContexts);
        Assert.True(toggle.IsImplemented);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        Assert.Equal(bindings.Display("reader.sidebar.toggle"), bindings.Display(MediaPanelKeys.ToggleCommandId));
        Assert.Equal(platform == ShortcutPlatform.MacOS ? "⇧⌘M" : "Ctrl+Shift+M", bindings.Display(MediaPanelKeys.ToggleCommandId));
        Assert.Empty(bindings.Issues);
        Assert.Contains(MediaPanelKeys.ToggleCommandId, EffectiveShortcutBindings.YieldingDefaultCommandIds);
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 18);
        // A binding saved before the switch existed keeps its key: the new default yields.
        var section = new ShortcutBindingsSection().Set("media.focus-mode", [ShortcutGesture.Parse("Primary+Shift+M")]);
        EffectiveShortcutBindings saved = EffectiveShortcutBindings.Build(section, platform);
        Assert.Empty(saved.For(MediaPanelKeys.ToggleCommandId));
        Assert.True(Has(saved, "media.focus-mode", ShortcutGesture.Parse("Primary+Shift+M")));
        Assert.DoesNotContain(saved.Issues, issue => issue.BlocksSave);
    }

    [Fact]
    public void ThePanelSwitchIsLabelledLikeItsLayoutRowInEveryLanguage()
    {
        Assert.Equal("Media sidebar", CommandCatalog.Label(MediaPanelKeys.ToggleCommandId));
        Assert.Equal(Texts.T("media.layout.panel"), CommandCatalog.Label(MediaPanelKeys.ToggleCommandId));
        Texts.SetLanguage("de");
        Assert.Equal("Medienleiste", CommandCatalog.Label(MediaPanelKeys.ToggleCommandId));
        foreach (string code in Texts.BundledPackCodes())
        {
            Texts.SetLanguage(code);
            Assert.Equal(Texts.T("media.layout.panel"), Texts.T("command.media.panel.toggle"));
        }
    }

    [Fact]
    public void TheShellRoutesTheSwitchToThePage()
    {
        string shortcuts = Source("Desktop/Shell/MainWindow.Shortcuts.cs");
        Assert.Contains("new CommandRegistration(MeowChan.Desktop.MediaPanelKeys.ToggleCommandId, media,", shortcuts);
        Assert.Contains("() => media.CanTogglePanel, media.TogglePanelByShortcut, Priority: 100));", shortcuts);
    }

    // ---- S-5: Escape ----

    [Theory]
    [InlineData(true, true, true, 3, "ExitFocus")]     // the focus mode ends first, also from the panel
    [InlineData(false, true, true, 3, "ReturnToGrid")] // then the panel hands the keyboard back; the question and the selection stay
    [InlineData(false, true, false, 0, "ReturnToGrid")]
    [InlineData(false, false, true, 3, "DismissExportAll")]
    [InlineData(false, false, false, 3, "ClearSelection")]
    [InlineData(false, false, false, 0, "None")]
    public void EscapeEndsTheFocusModeThenLeavesThePanelThenTheQuestionThenTheSelection(bool focus, bool panel, bool ask, int selected, string expected) =>
        Assert.Equal(Enum.Parse<MediaEscapeStep>(expected), MediaFocusMode.Escape(focus, panel, ask, selected));

    [Fact]
    public void EscapeFromThePanelWalksEveryLevelOnce()
    {
        bool focus = true, panel = true, ask = true; int selected = 4;
        List<MediaEscapeStep> steps = new();
        for (MediaEscapeStep step = MediaFocusMode.Escape(focus, panel, ask, selected); step != MediaEscapeStep.None; step = MediaFocusMode.Escape(focus, panel, ask, selected))
        {
            steps.Add(step);
            if (step == MediaEscapeStep.ExitFocus) focus = false;
            else if (step == MediaEscapeStep.ReturnToGrid) panel = false;
            else if (step == MediaEscapeStep.DismissExportAll) ask = false;
            else selected = 0;
        }
        Assert.Equal([MediaEscapeStep.ExitFocus, MediaEscapeStep.ReturnToGrid, MediaEscapeStep.DismissExportAll, MediaEscapeStep.ClearSelection], steps);
    }

    [Fact]
    public void ThePageHandlesThePanelsEscapeThroughTheLadder()
    {
        string selection = Source("Desktop/Views/Media/MediaPage.Selection.cs");
        Assert.Contains("AddHandler(KeyDownEvent, (_, e) => { if (!e.Handled) OnPanelEscape(e); }, Avalonia.Interactivity.RoutingStrategies.Bubble);", selection);
        Assert.Contains("MediaFocusMode.Escape(_focusMode, panelFocused: true, _exportAllAsk.IsVisible, _grid.Selection.Count) != MediaEscapeStep.ReturnToGrid", selection);
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));
}
