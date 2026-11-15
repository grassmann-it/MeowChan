using System.Text.Json;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// "Clicking a medium" (sidebar by default, in the post, in the gallery): the stored choice and the migration of the old "play video in the reader"
/// switch, what a click and Enter do per choice, what may play or expand where, and the reader's video keys (routing, bindings, shortcut window).
/// The views themselves are checked in the reader-inline-video and reader-media-sidebar smokes.
/// </summary>
[Collection("Localization")]
public sealed class ReaderMediaClickTests : IDisposable
{
    public ReaderMediaClickTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ReaderInlineVideoOptions OptionsOf(ReaderMediaClickMode mode)
    {
        Config config = new();
        config.ChooseReaderMediaClick(mode);
        return ReaderInlineVideoOptions.From(config);
    }

    private static ArchiveAttachmentRow Row(string name, bool resolvable = true, bool deleted = false, bool sized = true)
    {
        ArchiveAttachmentRow row = new(new CapturedAttachment(0, "1", OriginalFileName: name, IsDeleted: deleted));
        if (resolvable) row = row with { ResolveEntry = _ => Task.FromResult<MeowChan.Gallery.GalleryEntry?>(null) };
        if (sized) row = row with { LoadSized = (_, _, _) => Task.FromResult<Avalonia.Media.Imaging.Bitmap?>(null) };
        return row;
    }

    // ---- the stored choice and its migration -------------------------------------------------------------------------------------

    [Fact]
    public void ANewConfigClicksIntoTheSidebarAndStoresNoChoiceYet()
    {
        Config config = new();
        Assert.Equal(string.Empty, config.ReaderMediaClick);
        Assert.Equal(ReaderMediaClickMode.Sidebar, config.EffectiveReaderMediaClick);
        Assert.False(config.ReaderInlineVideo);
    }

    [Theory]
    [InlineData("{\"ReaderInlineVideo\":true}", "Post")]        // the old switch on: "Open in the post"
    [InlineData("{\"ReaderInlineVideo\":false}", "Sidebar")]    // the old switch off: the default
    [InlineData("{}", "Sidebar")]
    [InlineData("{\"ReaderMediaClick\":\"Gallery\",\"ReaderInlineVideo\":true}", "Gallery")] // a chosen value wins over the old flag
    [InlineData("{\"ReaderInlineVideo\":true,\"ReaderMediaClick\":\"Sidebar\"}", "Sidebar")]   // whatever the key order
    [InlineData("{\"ReaderMediaClick\":\"post\"}", "Post")]     // case does not matter
    [InlineData("{\"ReaderMediaClick\":\"Nonsense\",\"ReaderInlineVideo\":true}", "Post")] // unknown: not chosen, the old flag decides
    [InlineData("{\"ReaderMediaClick\":7}", "Sidebar")]         // a wrong type is read as not chosen
    public void AnOldConfigIsReadAsTheMatchingChoice(string json, string expectedName)
    {
        ReaderMediaClickMode expected = Enum.Parse<ReaderMediaClickMode>(expectedName);
        Config loaded = JsonSerializer.Deserialize<Config>(json)!;
        Assert.Equal(expected, loaded.EffectiveReaderMediaClick);
        Assert.Equal(expected, ReaderInlineVideoOptions.From(loaded).Mode);
    }

    [Theory]
    [InlineData("Sidebar", false)]
    [InlineData("Post", true)]
    [InlineData("Gallery", false)]
    public void ChoosingKeepsTheOldFlagInStepAndSurvivesASaveAndLoad(string modeName, bool legacyFlag)
    {
        ReaderMediaClickMode mode = Enum.Parse<ReaderMediaClickMode>(modeName);
        Config config = new();
        config.ChooseReaderMediaClick(mode);
        Assert.Equal(legacyFlag, config.ReaderInlineVideo);
        Config reloaded = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(config))!;
        Assert.Equal(mode, reloaded.EffectiveReaderMediaClick);
        Assert.Equal(ReaderMediaClickRules.ToKey(mode), reloaded.ReaderMediaClick);
        Config clone = config.Clone(), copy = new(), settings = new();
        copy.CopyFrom(config);
        settings.CopySettingsFieldsFrom(config);
        foreach (Config other in new[] { clone, copy, settings }) Assert.Equal(mode, other.EffectiveReaderMediaClick);
    }

    [Fact]
    public void TheChoicesAreThreeKeysThatRoundTrip()
    {
        Assert.Equal(new[] { "Sidebar", "Post", "Gallery" }, ReaderMediaClickRules.Keys);
        foreach (ReaderMediaClickMode mode in Enum.GetValues<ReaderMediaClickMode>())
            Assert.Equal(mode, ReaderMediaClickRules.FromKey(ReaderMediaClickRules.ToKey(mode)));
        Assert.Equal(ReaderMediaClickMode.Sidebar, ReaderMediaClickRules.FromKey(null));
        Assert.Equal(string.Empty, ReaderMediaClickRules.Normalize("  "));
        Assert.Equal("Post", ReaderMediaClickRules.Normalize(" POST "));
    }

    [Fact]
    public void TheChoiceIsTheFirstReaderQuickSettingWithItsTextsInBothLanguages()
    {
        QuickSettingField field = ReaderQuickSettings.Fields[0];
        Assert.Equal(SettingsFieldId.ReaderMediaClick, field.Id);
        Assert.DoesNotContain(ReaderQuickSettings.Fields, other => other.Id == "ReaderInlineVideo");
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            foreach (string key in new[] { "settings.reader.mediaClick", "settings.reader.mediaClick.hint", "settings.reader.mediaClick.sidebar",
                         "settings.reader.mediaClick.post", "settings.reader.mediaClick.gallery", "reader.media.collapseHint" })
                Assert.NotEqual(key, Texts.T(key));
        }
        Texts.SetLanguage("de");
        Assert.Equal("In der Medienleiste zeigen", Texts.T("settings.reader.mediaClick.sidebar"));
        Assert.Equal("Im Post öffnen", Texts.T("settings.reader.mediaClick.post"));
        Assert.Equal("In der Galerie öffnen", Texts.T("settings.reader.mediaClick.gallery"));
    }

    // ---- what a click does per choice ----------------------------------------------------------------------------------------------

    [Theory]
    // mode, video plays in post, image expands, sidebar shown -> outcome
    [InlineData("Sidebar", true, false, true, "ShowInSidebar")]
    [InlineData("Sidebar", false, false, true, "ShowInSidebar")]
    [InlineData("Sidebar", false, false, false, "OpenGallery")] // sidebar closed: the gallery
    [InlineData("Post", true, false, true, "PlayInPost")]       // also with the sidebar open
    [InlineData("Post", false, true, true, "ExpandInPost")]
    [InlineData("Post", false, false, true, "OpenGallery")]     // audio, a missing file
    [InlineData("Gallery", true, true, true, "OpenGallery")]
    public void AClickGoesWhereTheChoiceSays(string modeName, bool video, bool image, bool sidebar, string expectedName) =>
        Assert.Equal(Enum.Parse<ReaderMediaClickRules.Outcome>(expectedName), ReaderMediaClickRules.Click(OptionsOf(Enum.Parse<ReaderMediaClickMode>(modeName)), video, image, sidebar));

    [Fact]
    public void WithoutAnyReaderConfigurationAClickOpensTheGallery()
    {
        // A list or page without the reader's controller: the default options send everything to the gallery.
        Assert.Equal(ReaderMediaClickRules.Outcome.OpenGallery, ReaderMediaClickRules.Click(default, false, false, true));
        Assert.Equal(ReaderMediaClickMode.Sidebar, ReaderMediaClickRules.Resolve(ReaderMediaClickMode.Sidebar, true));
        Assert.Equal(ReaderMediaClickMode.Gallery, ReaderMediaClickRules.Resolve(ReaderMediaClickMode.Sidebar, false));
        Assert.Equal(ReaderMediaClickMode.Post, ReaderMediaClickRules.Resolve(ReaderMediaClickMode.Post, false));
    }

    [Fact]
    public void VideosPlayInTheSidebarAndInThePostButImagesExpandOnlyInThePost()
    {
        ArchiveAttachmentRow video = Row("clip.webm"), image = Row("cat.jpg"), audio = Row("song.mp3");
        ReaderInlineVideoOptions sidebar = OptionsOf(ReaderMediaClickMode.Sidebar), post = OptionsOf(ReaderMediaClickMode.Post), gallery = OptionsOf(ReaderMediaClickMode.Gallery);
        Assert.True(ReaderInlineVideoRules.CanOffer(sidebar, video));       // the sidebar plays it
        Assert.False(ReaderInlineVideoRules.CanOfferInPost(sidebar, video)); // but not the post
        Assert.True(ReaderInlineVideoRules.CanOfferInPost(post, video));
        Assert.False(ReaderInlineVideoRules.CanOffer(gallery, video));
        Assert.False(ReaderInlineVideoRules.CanExpand(sidebar, image));
        Assert.False(ReaderInlineVideoRules.CanExpand(gallery, image));
        Assert.True(ReaderInlineVideoRules.CanExpand(post, image));
        Assert.False(ReaderInlineVideoRules.CanExpand(post, video));
        Assert.False(ReaderInlineVideoRules.CanExpand(post, audio));
        Assert.False(ReaderInlineVideoRules.CanExpand(post, Row("gone.jpg", deleted: true)));
        Assert.False(ReaderInlineVideoRules.CanExpand(post, Row("cat.jpg", sized: false)));
    }

    [Fact]
    public async Task LeavingThePostChoiceReleasesThePostPlayersAndAnnouncesTheChange()
    {
        ReaderInlineVideoController controller = new();
        int changes = 0;
        controller.OptionsChanged += () => changes++;
        await controller.ConfigureAsync(OptionsOf(ReaderMediaClickMode.Post));
        await controller.ConfigureAsync(OptionsOf(ReaderMediaClickMode.Post) with { MaxPlayers = 3 });
        Assert.Equal(1, changes); // a limit alone is not a change of choice
        await controller.ConfigureAsync(OptionsOf(ReaderMediaClickMode.Sidebar));
        await controller.ConfigureAsync(OptionsOf(ReaderMediaClickMode.Gallery));
        Assert.Equal(3, changes);
        Assert.False(controller.Options.Enabled);
    }

    // ---- the reader's video keys ----------------------------------------------------------------------------------------------------

    [Fact]
    public void EnterIsAFixedContentKeyAndSpaceComesFromTheBindings()
    {
        Assert.True(ReaderVideoKeys.IsEnterToggle(Key.Enter, KeyModifiers.None));
        Assert.False(ReaderVideoKeys.IsEnterToggle(Key.Enter, KeyModifiers.Control));
        Assert.False(ReaderVideoKeys.IsEnterToggle(Key.Space, KeyModifiers.None));
        // Both keys toggle once a slot has the key (Enter, Space), nothing else does.
        Assert.Equal(ReaderInlineVideoRules.KeyAction.Toggle, ReaderInlineVideoRules.Classify(Key.Enter, KeyModifiers.None));
        Assert.Equal(ReaderInlineVideoRules.KeyAction.Toggle, ReaderInlineVideoRules.Classify(Key.Space, KeyModifiers.None));
        Assert.Equal(ReaderInlineVideoRules.KeyAction.None, ReaderInlineVideoRules.Classify(Key.M, KeyModifiers.None));
    }

    [Fact]
    public void EveryVideoCommandIsAGalleryCommandThatNowAlsoRunsInTheReader()
    {
        foreach ((string id, ReaderVideoAction action) in ReaderVideoKeys.Commands)
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.NotEqual(ReaderVideoAction.None, action);
            Assert.True(command.Scope.HasFlag(CommandScope.Gallery) && command.Scope.HasFlag(CommandScope.Reader), id);
            Assert.Contains("reader", command.ExecutionContexts);
            Assert.Contains("gallery", command.ExecutionContexts);
            // Listed in the shortcut window under the reader when it is the current area, under the gallery otherwise.
            Assert.Equal(ShortcutGroups.Reader, ShortcutGroups.For(command, ShortcutGroups.Reader));
            Assert.Equal(ShortcutGroups.Gallery, ShortcutGroups.For(command, ShortcutGroups.Gallery));
            Assert.Equal(ShortcutGroups.Gallery, ShortcutGroups.Primary(command));
            Assert.Contains(ShortcutGroups.Reader, ShortcutGroups.Areas(command));
        }
        // Seeking with Ctrl+arrows is deliberately not extended: the arrows move between posts and threads in the reader.
        Assert.DoesNotContain(ReaderVideoKeys.Commands, entry => entry.Id is "video.seek-back" or "video.seek-forward");
    }

    [Fact]
    public void TheVideoKeysInTheReaderAddNoSavingConflictToTheDefaults()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build();
        Assert.DoesNotContain(bindings.Issues, issue => issue.BlocksSave && (ReaderVideoKeys.IsVideoCommand(issue.CommandId)
            || issue.OtherCommandId != null && ReaderVideoKeys.IsVideoCommand(issue.OtherCommandId)));
        // Space still plays and pauses (revision 17 re-resolves a saved copy of the old default).
        Assert.True(EffectiveShortcutBindings.DefaultsRevision >= 17);
        Assert.NotEmpty(bindings.Gestures["video.play-pause"]);
    }

    [Fact]
    public void ArrowsAndPageKeysStayWithThePostsEvenWithFocusInThePlayer()
    {
        foreach (Key key in new[] { Key.Up, Key.Down, Key.Left, Key.Right, Key.Home, Key.End, Key.PageUp, Key.PageDown })
            Assert.True(ReaderVideoKeys.IsNavigationKey(key), key.ToString());
        foreach (Key key in new[] { Key.Space, Key.Enter, Key.M, Key.L, Key.OemComma, Key.OemPeriod, Key.Back, Key.X, Key.C })
            Assert.False(ReaderVideoKeys.IsNavigationKey(key), key.ToString());
    }

    [Fact]
    public void ClassifyReadsTheCommandsOnlyFromTheMatchingBinding()
    {
        foreach ((string id, ReaderVideoAction action) in ReaderVideoKeys.Commands)
            Assert.Equal(action, ReaderVideoKeys.Classify(candidate => candidate == id));
        Assert.Equal(ReaderVideoAction.None, ReaderVideoKeys.Classify(_ => false));
        Assert.True(ReaderVideoKeys.IsVideoCommand("video.mute"));
        Assert.False(ReaderVideoKeys.IsVideoCommand("gallery.open"));
    }

    [Fact]
    public void AMutedChoiceInThisSessionBeatsTheGalleryDefaultAndAutoplayIsAlwaysMuted()
    {
        ReaderInlineVideoOptions options = OptionsOf(ReaderMediaClickMode.Post) with { StartMuted = false };
        Assert.False(ReaderInlineVideoRules.StartMuted(options, null, autoplay: false));
        Assert.True(ReaderInlineVideoRules.StartMuted(options, true, autoplay: false));
        Assert.False(ReaderInlineVideoRules.StartMuted(options with { StartMuted = true }, false, autoplay: false));
        Assert.True(ReaderInlineVideoRules.StartMuted(options with { StartMuted = true }, null, autoplay: false));
        Assert.True(ReaderInlineVideoRules.StartMuted(options, false, autoplay: true));
    }

    // ---- the menu ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePlaybackGroupOfTheMenuIsPlayMuteRepeatWithTheStateInTheLabels()
    {
        var idle = ReaderPlaybackMenu.Entries(live: false, playing: false, muted: false, loop: false);
        Assert.Equal(new[] { "video.play-pause", "video.mute", "video.loop" }, idle.Select(entry => entry.CommandId));
        Assert.Equal(new[] { true, false, false }, idle.Select(entry => entry.Available));
        Assert.Equal("Play", idle[0].Label);
        var playing = ReaderPlaybackMenu.Entries(live: true, playing: true, muted: true, loop: true);
        Assert.All(playing, entry => Assert.True(entry.Available));
        Assert.Equal(new[] { "Pause", "Unmute", "Repeat off" }, playing.Select(entry => entry.Label));
        Assert.Equal(new[] { ReaderVideoAction.PlayPause, ReaderVideoAction.Mute, ReaderVideoAction.Loop }, playing.Select(entry => entry.Action));
        Assert.All(playing, entry => Assert.True(ReaderVideoKeys.IsVideoCommand(entry.CommandId)));
    }

    // ---- the gallery's menu ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheGalleryMenuOffersThePlaybackGroupForAVideoAndThePostBookmarkForAPostMedium()
    {
        var video = ReaderPlaybackMenu.GalleryExtras.Playback(true, transportUsable: true, playing: true, muted: false, loop: true, hasAudio: true, loopChangeable: true);
        Assert.Equal(new[] { "video.play-pause", "video.mute", "video.loop" }, video.Select(entry => entry.CommandId));
        Assert.Equal(new[] { "Pause", "Mute", "Repeat off" }, video.Select(entry => entry.Label));
        Assert.All(video, entry => Assert.True(entry.Available));
        Assert.Equal(new[] { "video.play-pause", "video.mute", "video.loop", "post-bookmarks.edit-post" },
            ReaderPlaybackMenu.GalleryExtras.CommandIds(video, offersPostBookmark: true));
        // Every playback entry is one of the bindings' commands, so the menu shows the shortcuts the keys use.
        Assert.All(video, entry => Assert.True(ReaderVideoKeys.IsVideoCommand(entry.CommandId)));
    }

    [Fact]
    public void TheGalleryMenuHasNoPlaybackGroupForAPictureAndNoBookmarkWithoutAPost()
    {
        var picture = ReaderPlaybackMenu.GalleryExtras.Playback(false, transportUsable: true, playing: false, muted: false, loop: false, hasAudio: true, loopChangeable: true);
        Assert.Empty(picture);
        Assert.Equal(new[] { "post-bookmarks.edit-post" }, ReaderPlaybackMenu.GalleryExtras.CommandIds(picture, offersPostBookmark: ReaderPlaybackMenu.GalleryExtras.OffersPostBookmark(true)));
        Assert.Empty(ReaderPlaybackMenu.GalleryExtras.CommandIds(picture, offersPostBookmark: ReaderPlaybackMenu.GalleryExtras.OffersPostBookmark(false)));
    }

    [Fact]
    public void TheGalleryPlaybackEntriesAreDisabledWhereTheyCannotActNow()
    {
        var silent = ReaderPlaybackMenu.GalleryExtras.Playback(true, transportUsable: true, playing: false, muted: false, loop: false, hasAudio: false, loopChangeable: false);
        Assert.Equal(new[] { true, false, false }, silent.Select(entry => entry.Available));
        var busy = ReaderPlaybackMenu.GalleryExtras.Playback(true, transportUsable: false, playing: false, muted: false, loop: false, hasAudio: true, loopChangeable: true);
        Assert.All(busy, entry => Assert.False(entry.Available));
        Assert.Equal("Play", busy[0].Label);
    }
}
