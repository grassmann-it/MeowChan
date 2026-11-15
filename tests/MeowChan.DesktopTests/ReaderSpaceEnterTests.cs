using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Space and Enter on the selected post's medium: one resolver picks the medium, one rule says what happens to it (ReaderMediaActivation, pure). Space is the video's
// play / pause with the same action as its fallback; Enter is the command "Open medium of the post".
public sealed class ReaderSpaceEnterTests
{
    private static ReaderPostMedium Image(bool available = true) => new(IsVideo: false, available);
    private static ReaderPostMedium Video(bool available = true, bool live = false, bool playing = false) => new(IsVideo: true, available, live, playing);
    private static ReaderInlineVideoOptions Mode(ReaderMediaClickMode mode)
    {
        Config config = new();
        config.ChooseReaderMediaClick(mode);
        return ReaderInlineVideoOptions.From(config);
    }

    // ---- which medium ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheFirstAvailableMediumIsTheLastResortAndAMissingOneIsSkipped()
    {
        ReaderPostMedium[] media = { Image(available: false), Video(), Image() };
        Assert.Equal(new ReaderMediumChoice(1, ReaderMediumPlace.None), ReaderMediaActivation.Choose(media, null, false, false, null));
        Assert.Null(ReaderMediaActivation.Choose(new[] { Image(false), Video(false) }, null, false, false, null)); // nothing to act on: the key stays unhandled
        Assert.Null(ReaderMediaActivation.Choose(Array.Empty<ReaderPostMedium>(), null, false, false, null));      // a post without media
    }

    [Fact]
    public void TheFocusedTileBeatsTheFirstAndTheSidebarsCurrentMediumBeatsTheFocusedTile()
    {
        ReaderPostMedium[] media = { Image(), Image(), Image() };
        Assert.Equal(2, ReaderMediaActivation.Choose(media, null, false, false, focusedIndex: 2)!.Value.Index);
        Assert.Equal(1, ReaderMediaActivation.Choose(media, sidebarIndex: 1, false, false, focusedIndex: 2)!.Value.Index); // Enter with the sidebar on medium 2 acts on medium 2
        // A sidebar medium that is not available gives way to the focus, then the first.
        ReaderPostMedium[] missing = { Image(), Image(false), Image() };
        Assert.Equal(2, ReaderMediaActivation.Choose(missing, sidebarIndex: 1, false, false, focusedIndex: 2)!.Value.Index);
        Assert.Equal(0, ReaderMediaActivation.Choose(missing, sidebarIndex: 1, false, false, focusedIndex: 1)!.Value.Index);
    }

    [Fact]
    public void ALivePlayerBeatsEverythingAndAPlayingOneBeatsAPausedOne()
    {
        ReaderPostMedium[] media = { Video(), Video(live: true), Video(live: true, playing: true) };
        Assert.Equal(new ReaderMediumChoice(2, ReaderMediumPlace.Post), ReaderMediaActivation.Choose(media, sidebarIndex: 0, false, false, focusedIndex: 0));
        ReaderPostMedium[] paused = { Video(), Video(live: true), Image() };
        Assert.Equal(new ReaderMediumChoice(1, ReaderMediumPlace.Post), ReaderMediaActivation.Choose(paused, sidebarIndex: 0, false, false, focusedIndex: 2));
        // The sidebar's own player, playing, beats a paused in-post player; paused it wins the tie.
        Assert.Equal(new ReaderMediumChoice(0, ReaderMediumPlace.Sidebar), ReaderMediaActivation.Choose(paused, sidebarIndex: 0, sidebarLive: true, sidebarPlaying: true, null));
        Assert.Equal(new ReaderMediumChoice(0, ReaderMediumPlace.Sidebar), ReaderMediaActivation.Choose(paused, sidebarIndex: 0, sidebarLive: true, sidebarPlaying: false, null));
    }

    [Fact]
    public void WhenTheSidebarIsIdleAndTheInPostCopyIsLiveTheInPostPlayerToggles()
    {
        ReaderPostMedium[] media = { Video(live: true, playing: true) };
        ReaderMediumChoice choice = ReaderMediaActivation.Choose(media, sidebarIndex: 0, sidebarLive: false, sidebarPlaying: false, null)!.Value;
        Assert.Equal(ReaderMediumPlace.Post, choice.Live);
        Assert.Equal(ReaderMediumOutcome.ToggleInPost,
            ReaderMediaActivation.Decide(choice, media[0], Mode(ReaderMediaClickMode.Sidebar), sidebarShown: true, sidebarShowsIt: true, canPlayInPost: true, canExpand: false, galleryWired: true));
        // The reverse case stays with the sidebar.
        ReaderMediumChoice sidebar = ReaderMediaActivation.Choose(new[] { Video() }, sidebarIndex: 0, sidebarLive: true, sidebarPlaying: true, null)!.Value;
        Assert.Equal(ReaderMediumOutcome.ToggleInSidebar,
            ReaderMediaActivation.Decide(sidebar, Video(), Mode(ReaderMediaClickMode.Sidebar), true, true, false, false, true));
    }

    // ---- what happens ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Gallery", true, false, false, "OpenGallery")]
    [InlineData("Gallery", true, true, true, "OpenGallery")]
    [InlineData("Post", true, false, true, "ExpandInPost")]       // Space on an image under the post setting expands it
    [InlineData("Post", true, false, false, "OpenGallery")]       // an image that cannot expand opens the gallery
    [InlineData("Sidebar", true, false, false, "ShowInSidebar")]
    [InlineData("Sidebar", false, false, false, "OpenGallery")]  // sidebar closed: the gallery
    [InlineData("Gallery", false, false, false, "OpenGallery")]
    public void AnImageDoesWhatAClickDoesUnderEachSetting(string mode, bool sidebarShown, bool sidebarShowsIt, bool canExpand, string expected)
    {
        ReaderMediumChoice choice = new(0, ReaderMediumPlace.None);
        Assert.Equal(Enum.Parse<ReaderMediumOutcome>(expected), ReaderMediaActivation.Decide(choice, Image(), Mode(Enum.Parse<ReaderMediaClickMode>(mode)), sidebarShown, sidebarShowsIt, canPlayInPost: false, canExpand, galleryWired: true));
    }

    [Fact]
    public void AnImageTheSidebarAlreadyShowsOpensTheGalleryAndWithoutAGalleryNothingHappens()
    {
        ReaderMediumChoice choice = new(0, ReaderMediumPlace.None);
        Assert.Equal(ReaderMediumOutcome.OpenGallery, ReaderMediaActivation.Decide(choice, Image(), Mode(ReaderMediaClickMode.Sidebar), true, sidebarShowsIt: true, false, false, true));
        Assert.Equal(ReaderMediumOutcome.None, ReaderMediaActivation.Decide(choice, Image(), Mode(ReaderMediaClickMode.Sidebar), true, true, false, false, galleryWired: false));
        Assert.Equal(ReaderMediumOutcome.None, ReaderMediaActivation.Decide(choice, Image(), Mode(ReaderMediaClickMode.Gallery), true, false, false, false, galleryWired: false));
    }

    [Theory]
    [InlineData("Post", true, true, "PlayInPost")]
    [InlineData("Post", true, false, "OpenGallery")]  // a video that cannot play in its post (limit) goes to the gallery
    [InlineData("Sidebar", true, false, "ShowInSidebar")]
    [InlineData("Sidebar", false, false, "OpenGallery")]
    [InlineData("Gallery", true, true, "OpenGallery")]
    public void AVideoWithoutAPlayerStartsWhereTheSettingSays(string modeName, bool sidebarShown, bool canPlayInPost, string expected)
    {
        ReaderMediaClickMode mode = Enum.Parse<ReaderMediaClickMode>(modeName);
        ReaderMediumChoice choice = new(0, ReaderMediumPlace.None);
        Assert.Equal(Enum.Parse<ReaderMediumOutcome>(expected), ReaderMediaActivation.Decide(choice, Video(), Mode(mode), sidebarShown, sidebarShowsIt: false, canPlayInPost, canExpand: false, galleryWired: true));
        // A video the sidebar shows (idle) starts there, never the gallery.
        if (mode == ReaderMediaClickMode.Sidebar && sidebarShown)
            Assert.Equal(ReaderMediumOutcome.ShowInSidebar, ReaderMediaActivation.Decide(choice, Video(), Mode(mode), true, sidebarShowsIt: true, false, false, true));
    }

    [Fact]
    public void AMediumThatIsNotAvailableDoesNothingForEitherKey()
    {
        ReaderMediumChoice choice = new(0, ReaderMediumPlace.None);
        foreach (ReaderMediaClickMode mode in Enum.GetValues<ReaderMediaClickMode>())
            Assert.Equal(ReaderMediumOutcome.None, ReaderMediaActivation.Decide(choice, Image(available: false), Mode(mode), true, false, true, true, true));
    }

    [Fact]
    public void ARepeatOfAHeldKeyDoesNothingUntilItIsReleasedOrWhileTheGalleryOpens()
    {
        HashSet<Key> held = new();
        Assert.False(ReaderMediaActivation.IsRepeat(held, Key.Space, activationInFlight: false)); // the first press acts
        held.Add(Key.Space);
        Assert.True(ReaderMediaActivation.IsRepeat(held, Key.Space, false));                       // the repeat does not: one toggle for a held key
        Assert.False(ReaderMediaActivation.IsRepeat(held, Key.Enter, false));
        Assert.True(ReaderMediaActivation.IsRepeat(held, Key.Enter, activationInFlight: true));
        held.Remove(Key.Space);                                                                    // the release
        Assert.False(ReaderMediaActivation.IsRepeat(held, Key.Space, false));
    }

    // ---- bindings ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMediumCommandIsAnEnterDefaultThatYieldsAndSpaceStaysThePlayPauseKey()
    {
        CommandDefinition command = CommandCatalog.Get(ArchivePage.ActivateMediumCommandId);
        Assert.Equal("Enter", command.Shortcut);
        Assert.Equal(CommandScope.Reader, command.Scope);
        Assert.Contains("reader-media", command.Contexts);
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 20);
        Assert.Contains(ArchivePage.ActivateMediumCommandId, EffectiveShortcutBindings.YieldingDefaultCommandIds);
        Assert.Equal("Space", CommandCatalog.Get("video.play-pause").Shortcut); // no second command on Space
        EffectiveShortcutBindings defaults = EffectiveShortcutBindings.Build();
        Assert.True(defaults.Matches(new KeyEventArgs { Key = Key.Enter }, ArchivePage.ActivateMediumCommandId));
        Assert.True(defaults.Matches(new KeyEventArgs { Key = Key.Space }, "video.play-pause"));
        Assert.False(defaults.Matches(new KeyEventArgs { Key = Key.Space }, ArchivePage.ActivateMediumCommandId));
    }

    [Fact]
    public void ASavedReaderBindingOnEnterWinsAndOneOnSpaceKeepsTheGalleryPlayPause()
    {
        KeyEventArgs enter = new() { Key = Key.Enter }, space = new() { Key = Key.Space };
        EffectiveShortcutBindings onEnter = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("thread.read", Array.Empty<ShortcutGesture>())
            .Set("reader.next-post", [ShortcutGesture.Parse("Enter")]));
        Assert.False(onEnter.Matches(enter, ArchivePage.ActivateMediumCommandId)); // the new default gave way, the saved binding runs
        Assert.True(onEnter.Matches(enter, "reader.next-post"));
        EffectiveShortcutBindings onSpace = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("reader.next-post", [ShortcutGesture.Parse("Space")]));
        Assert.True(ArchivePage.ClaimedByOtherReaderCommand(space, onSpace));
        Assert.True(onSpace.Matches(space, "video.play-pause")); // the gallery (and the media page) keep Space
        Assert.Equal(ReaderVideoAction.None, ReaderVideoKeys.ClassifyUnclaimed(id => onSpace.Matches(space, id))); // in the reader the video key gives way
    }

    [Fact]
    public void RebindingPlayPauseMovesItInTheReaderToo()
    {
        KeyEventArgs p = new() { Key = Key.P }, space = new() { Key = Key.Space };
        EffectiveShortcutBindings moved = EffectiveShortcutBindings.Build(new ShortcutBindingsSection().Set("video.play-pause", [ShortcutGesture.Parse("P")]));
        Assert.Equal(ReaderVideoAction.PlayPause, ReaderVideoKeys.ClassifyUnclaimed(id => moved.Matches(p, id)));
        Assert.Equal(ReaderVideoAction.None, ReaderVideoKeys.ClassifyUnclaimed(id => moved.Matches(space, id)));
        // Space itself stays the fixed content key of a medium (what it always did: it opens the medium) unless a reader or shell command has it.
        Assert.True(ReaderMediaActivation.IsMediaKey(Key.Space, KeyModifiers.None, false, ArchivePage.ClaimedByOtherReaderCommand(space, moved)));
    }

    [Fact]
    public void TheCommandHasALabelInAllTwelveLanguages()
    {
        const string key = "command.reader.media.activate";
        Assert.False(string.IsNullOrWhiteSpace(MeowChan.Desktop.Localization.BuiltInCatalog.English[key]));
        Assert.False(string.IsNullOrWhiteSpace(MeowChan.Desktop.Localization.BuiltInCatalog.German[key]));
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.sln"))) directory = Path.GetDirectoryName(directory);
        string[] packs = Directory.GetFiles(Path.Combine(directory!, "Desktop/Localization/Packs"), "*.json");
        Assert.Equal(10, packs.Length);
        foreach (string file in packs)
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            Assert.True(doc.RootElement.GetProperty("strings").TryGetProperty(key, out var value) && !string.IsNullOrWhiteSpace(value.GetString()), file);
        }
    }

    // ---- where the keys are taken ------------------------------------------------------------------------------------------------

    [Fact]
    public void ATextBoxAButtonAndSelectedTextKeepTheKeysAndOnlyThePostListGivesThemToTheMedium()
    {
        ListBox posts = new();
        Assert.True(ArchivePage.ReaderMediaBelongsToNativeControl(new TextBox(), posts));
        Assert.True(ArchivePage.ReaderMediaBelongsToNativeControl(new Button(), posts));
        Assert.True(ArchivePage.ReaderMediaBelongsToNativeControl(new Slider(), posts));
        Assert.False(ArchivePage.ReaderMediaBelongsToNativeControl(posts, posts)); // the post list itself: the medium keys apply
    }
}
