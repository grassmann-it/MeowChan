using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// W3 quick wins: files without a viewer, copy path, post menu copies, live-follow reasons, slideshow durations.
// Pure decisions only; the real controls are covered by the media-menu-surfaces smoke scenario.
[Collection("Localization")]
public sealed class GalleryMediaActionsTests : IDisposable
{
    private static readonly ResourceId Thread = new("4chan", "g", "123");
    private readonly string _previous = Texts.Language;

    public GalleryMediaActionsTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    [Theory]
    [InlineData("/x/doc.pdf", true)]
    [InlineData("/x/archive.ZIP", true)]
    [InlineData("/x/clip.swf", true)]
    [InlineData("/x/no-extension", true)]
    [InlineData("/x/setup.exe", false)]
    [InlineData("/x/SETUP.EXE", false)]
    [InlineData("C:\\x\\run.bat", false)]
    [InlineData("/x/script.sh", false)]
    [InlineData("/x/script.ps1", false)]
    [InlineData("/x/tool.jar", false)]
    [InlineData("/x/app.AppImage", false)]
    [InlineData("/x/shortcut.desktop", false)]
    [InlineData("/x/shortcut.lnk", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyFilesThatCannotRunAsAProgramAreHandedToTheDefaultProgram(string? path, bool allowed) =>
        Assert.Equal(allowed, MediaFileActions.CanOpenWithDefaultProgram(path));

    [Theory]
    [InlineData("en", "/x/doc.pdf", "PDF file")]
    [InlineData("de", "/x/doc.pdf", "PDF-Datei")]
    [InlineData("en", "/x/doc", "file")]
    [InlineData("de", "/x/doc", "Datei")]
    [InlineData("en", "/x/doc.verylongextension", "file")]
    [InlineData("en", "/x/doc.p d", "file")]
    public void TheTypeLabelNamesPlainExtensionsOnly(string language, string path, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, MediaFileActions.TypeLabel(path));
    }

    [Fact]
    public void TheCommandsOfThisPackageAreRealCatalogEntriesWithoutInventedShortcuts()
    {
        foreach (string id in new[] { "media.open-external", "media.copy-path", "post.copy-text", "post.copy-number", "post.copy-link" })
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.True(command.IsImplemented, id);
            Assert.Equal(CommandAvailability.Contextual, command.Availability);
            // The shortcut concept assigns keys; this package only adds the commands.
            Assert.Empty(EffectiveShortcutBindings.Build().For(id));
            Assert.Null(command.Shortcut);
        }
        Assert.Equal(new[] { "gallery" }, CommandCatalog.Get("media.open-external").ExecutionContexts);
        Assert.Equal(new[] { "reader" }, CommandCatalog.Get("post.copy-text").ExecutionContexts);
        // The remaining placeholder stays one.
        Assert.False(CommandCatalog.Get("post.open-source").IsImplemented);
    }

    [Theory]
    [InlineData("en", "media.open-external", "Open with default program")]
    [InlineData("de", "media.open-external", "Mit Standardprogramm öffnen")]
    [InlineData("en", "media.copy-path", "Copy file path")]
    [InlineData("de", "media.copy-path", "Dateipfad kopieren")]
    [InlineData("en", "post.copy-text", "Copy post text")]
    [InlineData("de", "post.copy-text", "Post-Text kopieren")]
    [InlineData("en", "post.copy-number", "Copy post number")]
    [InlineData("de", "post.copy-number", "Post-Nummer kopieren")]
    [InlineData("de", "post.copy-link", "Post-Link kopieren")]
    public void CommandLabelsComeFromTheCatalog(string language, string id, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, CommandCatalog.Label(id));
    }

    [Fact]
    public void CopyPathIsOneSharedActionOnEverySurfaceThatNeedsASavedFile()
    {
        foreach (MediaMenuSurface surface in Enum.GetValues<MediaMenuSurface>())
        {
            MediaMenuFacts facts = new(surface, new MediaMenuTarget(Thread, "dir", "asset", "43", 0, "rev", "ab12", null));
            MediaMenuItemModel path = MediaMenuComposition.Compose(facts).Items.Single(item => item.Action == MediaMenuAction.CopyPath);
            Assert.Equal("media.copy-path", path.CommandId);
            Assert.True(path.Available);
            Assert.Equal(CommandCatalog.Label("media.copy-path"), path.Label);

            MediaMenuItemModel missing = MediaMenuComposition.Compose(facts with { Present = false }).Items.Single(item => item.Action == MediaMenuAction.CopyPath);
            Assert.False(missing.Available);
            Assert.Equal(Texts.T("media.menu.reason.notSaved"), missing.Reason);

            MediaMenuItemModel unwired = MediaMenuComposition.Compose(facts with { CopyPathWired = false }).Items.Single(item => item.Action == MediaMenuAction.CopyPath);
            Assert.False(unwired.Available);
            Assert.Equal(Texts.T("media.menu.reason.unavailable"), unwired.Reason);
        }
    }

    [Fact]
    public void CopyPathSitsBetweenShowInFolderAndDetailsInTheFileSection()
    {
        MediaMenuModel model = MediaMenuComposition.Compose(new(MediaMenuSurface.Gallery, null));
        string[] ids = model.Items.Select(item => item.Id).ToArray();
        int folder = Array.IndexOf(ids, "media.show-in-folder");
        Assert.Equal("media.copy-path", ids[folder + 1]);
        Assert.Equal("media.details", ids[folder + 2]);
        Assert.Equal(model.Items[folder].Group, model.Items[folder + 1].Group);
    }

    [Fact]
    public void ThePostNumberIsTheQuoteAndTheTextStartsWithTheSubject()
    {
        Assert.Equal(">>123", PostMenuText.Number("123"));
        ArchivePostRow plain = new("1", "#1", "", "Body line", Array.Empty<ArchiveAttachmentRow>());
        Assert.Equal("Body line", PostMenuText.Text(plain));
        ArchivePostRow subject = new("1", "#1", " Subject ", "Body line", Array.Empty<ArchiveAttachmentRow>());
        Assert.Equal("Subject" + Environment.NewLine + "Body line", PostMenuText.Text(subject));
    }

    [Theory]
    [InlineData("4chan", "https://boards.4chan.org/g/thread/123", "456", "https://boards.4chan.org/g/thread/123#p456")]
    [InlineData("4chan", "https://boards.4channel.org/g/thread/123/slug?x=1#p9", "456", "https://boards.4channel.org/g/thread/123/slug#p456")]
    [InlineData("4chan", "https://boards.4chan.org/g/thread/123", "45a", null)]
    [InlineData("4chan", "https://boards.4chan.org/g/thread/123", "", null)]
    [InlineData("4chan", "https://evil.example/g/thread/123", "456", null)]
    [InlineData("4chan", "ftp://boards.4chan.org/g/thread/123", "456", null)]
    [InlineData("4chan", "not a url", "456", null)]
    [InlineData("4chan", null, "456", null)]
    [InlineData("wizchan", "https://wizchan.org/demo/res/123.html", "456", null)]
    public void ThePostLinkExistsOnlyWhereTheAddressSchemeIsKnown(string source, string? threadUrl, string post, string? expected) =>
        Assert.Equal(expected, PostMenuText.Link(new ResourceId(source, "g", "123"), threadUrl, post));

    private static ArchiveThreadSummary Summary(bool capture, ThreadSourceState state) =>
        new(Thread, "https://boards.4chan.org/g/thread/123", "t", "dir", state, capture, DateTimeOffset.UnixEpoch, null, 1, 1);

    [Fact]
    public void TheLiveFollowTipNamesTheOneMissingCondition()
    {
        Assert.Null(ArchivePage.FollowLiveReason(null, false));
        Assert.Null(ArchivePage.FollowLiveReason(Summary(true, ThreadSourceState.Active), true));
        Assert.Equal(Texts.T("reader.live.reason.notWatched"), ArchivePage.FollowLiveReason(Summary(true, ThreadSourceState.Active), false));
        Assert.Equal(Texts.T("reader.live.reason.captureOff"), ArchivePage.FollowLiveReason(Summary(false, ThreadSourceState.Active), true));
        Assert.Equal(Texts.T("reader.live.reason.sourceArchived"), ArchivePage.FollowLiveReason(Summary(true, ThreadSourceState.Archived), true));
        Assert.Equal(Texts.T("reader.live.reason.sourceGone"), ArchivePage.FollowLiveReason(Summary(true, ThreadSourceState.NotFound), true));
        // The first missing condition wins, in the order a user can act on them.
        Assert.Equal(Texts.T("reader.live.reason.notWatched"), ArchivePage.FollowLiveReason(Summary(false, ThreadSourceState.Archived), false));
        Assert.Equal(Texts.T("reader.live.reason.captureOff"), ArchivePage.FollowLiveReason(Summary(false, ThreadSourceState.Archived), true));
    }

    [Theory]
    [InlineData(5, new[] { 3, 5, 10, 30 })]
    [InlineData(3, new[] { 3, 5, 10, 30 })]
    [InlineData(7, new[] { 3, 5, 7, 10, 30 })]
    [InlineData(120, new[] { 3, 5, 10, 30, 120 })]
    [InlineData(1, new[] { 1, 3, 5, 10, 30 })]
    public void TheSlideshowDurationsAreTheUsualOnesPlusTheSavedValue(int saved, int[] expected) =>
        Assert.Equal(expected, GalleryViewer.SlideshowSecondChoices(saved));

    [Fact]
    public void TheCompactExportLabelNamesTheObject()
    {
        Assert.Equal("Original…", GalleryViewer.CompactLabelFor("GalleryExportOriginal"));
        Texts.SetLanguage("de");
        Assert.Equal("Original …", GalleryViewer.CompactLabelFor("GalleryExportOriginal"));
    }

    [Fact]
    public void TheNewTextsExistInEveryBundledLanguageWithTheirPlaceholders()
    {
        string[] keys = { "gallery.fileOnly.body", "gallery.fileOnly.type", "gallery.fileOnly.typeUnknown", "gallery.fileOnly.blocked",
            "gallery.fileOnly.missing", "media.copyPath.done", "media.copyPath.failed", "post.copy.textDone", "post.copy.numberDone",
            "post.copy.linkDone", "post.copy.failed", "archive.threadFavorite.loading", "reader.live.reason.notWatched",
            "reader.live.reason.captureOff", "reader.live.reason.sourceArchived", "reader.live.reason.sourceGone",
            "gallery.slideshow.seconds", "gallery.slideshow.secondsLabel" };
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            foreach (string key in keys) Assert.NotEqual(key, Texts.T(key));
            Assert.Contains("{0}", Texts.T("gallery.fileOnly.body"));
            Assert.Contains("{1}", Texts.T("gallery.fileOnly.body"));
            Assert.Contains("{0}", Texts.T("gallery.fileOnly.type"));
            Assert.False(string.IsNullOrWhiteSpace(Texts.T("gallery.slideshow.seconds", 5)));
        }
    }
}
