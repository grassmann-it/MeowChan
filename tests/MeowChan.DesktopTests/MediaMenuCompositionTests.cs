using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// AP-01c: the one media-action composition. Pure model tests; the real menus on every surface are covered by the
// media-menu-surfaces smoke scenario and the media page / gallery / archive smokes.
[Collection("Localization")]
public sealed class MediaMenuCompositionTests : IDisposable
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };
    private static readonly MediaMenuSurface[] All = Enum.GetValues<MediaMenuSurface>();
    private static readonly ResourceId Thread = new("source", "b", "42");
    private readonly string _previous = Texts.Language;

    public MediaMenuCompositionTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    private static MediaMenuTarget Target() => new(Thread, "dir", "asset", "43", 0, "rev", "ab12", null);

    private static MediaMenuFacts Full(MediaMenuSurface surface) => new(surface, Target())
    {
        Favorite = MediaMenuFavorite.Off,
        Posts = MediaMenuPostScope.Single
    };

    private static string[] Ids(MediaMenuModel model) => model.Items.Select(item => item.Id).ToArray();

    [Fact]
    public void EverySurfaceListsTheSameCommonActionsInTheSameOrder()
    {
        string[] expected = { "gallery.open", "favorites.toggle", "media.rating.edit", "media.tags.edit", "media.export-original",
            "media.show-in-folder", "media.details", "gallery.show-in-thread" };
        foreach (MediaMenuSurface surface in All.Where(item => item is not (MediaMenuSurface.MediaPage or MediaMenuSurface.Gallery or MediaMenuSurface.NativeVideoOverlay)))
            Assert.Equal(expected, Ids(MediaMenuComposition.Compose(Full(surface))));
        // The media page's open command is its own (Enter), the entry stays the same action in the same place.
        Assert.Equal(expected.Select(id => id == "gallery.open" ? "media.open" : id),
            Ids(MediaMenuComposition.Compose(Full(MediaMenuSurface.MediaPage))));
        // The gallery is the open gallery: everything else, in the same order.
        foreach (MediaMenuSurface surface in new[] { MediaMenuSurface.Gallery, MediaMenuSurface.NativeVideoOverlay })
            Assert.Equal(expected.Skip(1), Ids(MediaMenuComposition.Compose(Full(surface))));
        Assert.Equal(MediaMenuComposition.Order, Enum.GetValues<MediaMenuAction>());
    }

    [Fact]
    public void TheGalleryMenuOffersTheMediaTagAction()
    {
        MediaMenuItemModel tags = MediaMenuComposition.Compose(Full(MediaMenuSurface.Gallery)).Items
            .Single(item => item.Action == MediaMenuAction.Tags);
        Assert.Equal("media.tags.edit", tags.CommandId);
        Assert.True(tags.Available);
        Assert.Equal(CommandCatalog.Label("media.tags.edit"), tags.Label);
    }

    [Fact]
    public void LabelsAvailabilityAndReasonsAgreeOnEverySurfaceForTheSameFacts()
    {
        foreach (bool present in new[] { true, false })
            foreach (MediaMenuFavorite favorite in Enum.GetValues<MediaMenuFavorite>())
            {
                MediaMenuModel[] models = All.Select(surface => MediaMenuComposition.Compose(
                    Full(surface) with { Present = present, Favorite = favorite, TagsBlocked = present ? "blocked" : null })).ToArray();
                foreach (MediaMenuAction action in Enum.GetValues<MediaMenuAction>())
                {
                    MediaMenuItemModel[] shown = models.Select(model => model.Items.SingleOrDefault(item => item.Action == action))
                        .OfType<MediaMenuItemModel>().ToArray();
                    // Only the gallery and its overlay leave out "open"; provider actions need provider media.
                    Assert.Equal(action == MediaMenuAction.Open ? 4 : action is MediaMenuAction.OpenProviderPost or MediaMenuAction.CopyProviderPost ? 0 : All.Length,
                        shown.Length);
                    Assert.True(shown.Length == 0 || shown.Select(item => item.Label).Distinct().Count() == 1, action + " label");
                    Assert.True(shown.Length == 0 || shown.Select(item => item.Available).Distinct().Count() == 1, action + " availability");
                    Assert.True(shown.Length == 0 || shown.Select(item => item.Reason).Distinct().Count() == 1, action + " reason");
                    Assert.True(shown.Length == 0 || shown.Select(item => item.Checked).Distinct().Count() == 1, action + " check state");
                }
            }
    }

    [Fact]
    public void AMissingFileDisablesEveryFileActionWithTheSameReason()
    {
        foreach (MediaMenuSurface surface in All)
        {
            MediaMenuModel model = MediaMenuComposition.Compose(Full(surface) with { Present = false });
            foreach (MediaMenuItemModel item in model.Items.Where(item => item.Action is not (MediaMenuAction.ShowInThread)))
            {
                Assert.False(item.Available, surface + " " + item.Id);
                Assert.Equal(Texts.T("media.menu.reason.notSaved"), item.Reason);
            }
            // A post needs no saved file.
            Assert.True(model.Items.Single(item => item.Action == MediaMenuAction.ShowInThread).Available);
        }
    }

    [Fact]
    public void AFileSharedByTwoPostsNeverPicksOneForShowInThread()
    {
        foreach (MediaMenuSurface surface in All)
        {
            MediaMenuItemModel thread = MediaMenuComposition.Compose(Full(surface) with { Posts = MediaMenuPostScope.Several })
                .Items.Single(item => item.Action == MediaMenuAction.ShowInThread);
            Assert.False(thread.Available);
            Assert.Equal(Texts.T("media.menu.reason.severalPosts"), thread.Reason);
        }
        MediaMenuItemModel none = MediaMenuComposition.Compose(Full(MediaMenuSurface.ArchiveTiles) with { Posts = MediaMenuPostScope.None })
            .Items.Single(item => item.Action == MediaMenuAction.ShowInThread);
        Assert.False(none.Available);
        Assert.Equal(Texts.T("gallery.noPost"), none.Reason);
    }

    [Fact]
    public void TagReasonsAreOneSharedWordingForLooseBoundElsewhereAmbiguousAndProviderMedia()
    {
        string boundElsewhere = MediaMenuReasons.Tags(GalleryMediaTagUnavailable.NoPostVersion);
        Assert.Equal(Texts.T("tags.media.boundElsewhere"), boundElsewhere);
        Assert.Equal(Texts.T("tags.media.unsupported"), MediaMenuReasons.Tags(GalleryMediaTagUnavailable.InvalidIdentity));
        Assert.Equal(Texts.T("tags.media.unsupported"), MediaMenuReasons.Tags(GalleryMediaTagUnavailable.Mismatch));
        Assert.Equal(Texts.T("tags.media.ambiguous"), MediaMenuReasons.Tags(GalleryMediaTagUnavailable.NoPostVersion, ambiguous: true));
        Assert.Equal(Texts.T("booru.media.tags.unavailable"), MediaMenuReasons.Tags(GalleryMediaTagUnavailable.NoPostVersion, providerMedia: true, ambiguous: true));
        foreach (MediaMenuSurface surface in All)
        {
            MediaMenuItemModel tags = MediaMenuComposition.Compose(Full(surface) with { TagsBlocked = boundElsewhere })
                .Items.Single(item => item.Action == MediaMenuAction.Tags);
            Assert.False(tags.Available);
            Assert.Equal(boundElsewhere, tags.Reason);
        }
        Assert.True(MediaMenuComposition.Compose(Full(MediaMenuSurface.ArchiveTable) with { TagsBlocked = null })
            .Items.Single(item => item.Action == MediaMenuAction.Tags).Available);
    }

    [Fact]
    public void ProviderPostActionsExistOnlyForDanbooruMediaAndNeverGuessAnAddress()
    {
        foreach (MediaMenuSurface surface in All)
        {
            Assert.DoesNotContain(MediaMenuComposition.Compose(Full(surface)).Items, item => item.Action is MediaMenuAction.OpenProviderPost or MediaMenuAction.CopyProviderPost);
            MediaMenuModel known = MediaMenuComposition.Compose(Full(surface) with { ProviderMedia = true, ProviderPostKnown = true, Posts = MediaMenuPostScope.None });
            MediaMenuItemModel[] provider = known.Items.Where(item => item.Action is MediaMenuAction.OpenProviderPost or MediaMenuAction.CopyProviderPost).ToArray();
            Assert.Equal(new[] { "booru.media.openPost", "booru.media.copyPost" }, provider.Select(item => item.Id));
            Assert.All(provider, item => Assert.True(item.Available));
            Assert.Equal(new[] { Texts.T("booru.media.openPost"), Texts.T("booru.media.copyPost") }, provider.Select(item => item.Label));
            // Provider media is not a chan post: its post actions say so rather than doing nothing.
            Assert.False(known.Items.Single(item => item.Action == MediaMenuAction.ShowInThread).Available);
            MediaMenuModel unknown = MediaMenuComposition.Compose(Full(surface) with { ProviderMedia = true, ProviderPostKnown = false });
            Assert.All(unknown.Items.Where(item => item.Action is MediaMenuAction.OpenProviderPost or MediaMenuAction.CopyProviderPost), item =>
            {
                Assert.False(item.Available);
                Assert.Equal(Texts.T("media.menu.reason.providerPost"), item.Reason);
            });
        }
    }

    [Fact]
    public void SeveralSelectedItemsMakeSingleTargetActionsExplainThemselves()
    {
        MediaMenuModel model = MediaMenuComposition.Compose(Full(MediaMenuSurface.MediaPage) with { SeveralSelected = true, ProviderMedia = true, ProviderPostKnown = true });
        string reason = Texts.T("media.menu.reason.severalSelected");
        foreach (MediaMenuAction action in new[] { MediaMenuAction.Open, MediaMenuAction.ShowInThread, MediaMenuAction.OpenProviderPost, MediaMenuAction.CopyProviderPost })
        {
            MediaMenuItemModel item = model.Items.Single(entry => entry.Action == action);
            Assert.False(item.Available, action.ToString());
            Assert.Equal(reason, item.Reason);
        }
        // Actions on the clicked file stay on the clicked file.
        Assert.True(model.Items.Single(item => item.Action == MediaMenuAction.Favorite).Available);
    }

    [Fact]
    public void OpenSaysWhyAHiddenFileOrAMissingHostCannotBeOpened()
    {
        MediaMenuItemModel hidden = MediaMenuComposition.Compose(Full(MediaMenuSurface.ArchiveTiles) with { InGallerySequence = false })
            .Items.Single(item => item.Action == MediaMenuAction.Open);
        Assert.False(hidden.Available);
        Assert.Equal(Texts.T("media.menu.reason.notInSequence"), hidden.Reason);
        MediaMenuItemModel unwired = MediaMenuComposition.Compose(Full(MediaMenuSurface.MediaPage) with { GalleryWired = false })
            .Items.Single(item => item.Action == MediaMenuAction.Open);
        Assert.False(unwired.Available);
        Assert.Equal(Texts.T("media.menu.reason.unavailable"), unwired.Reason);
    }

    [Fact]
    public void FavoriteLabelAndCheckFollowTheStateAndAnUnknownStateIsUnavailable()
    {
        foreach (MediaMenuSurface surface in All)
        {
            MediaMenuItemModel on = MediaMenuComposition.Compose(Full(surface) with { Favorite = MediaMenuFavorite.On }).Items.Single(item => item.Action == MediaMenuAction.Favorite);
            MediaMenuItemModel off = MediaMenuComposition.Compose(Full(surface) with { Favorite = MediaMenuFavorite.Off }).Items.Single(item => item.Action == MediaMenuAction.Favorite);
            MediaMenuItemModel unknown = MediaMenuComposition.Compose(Full(surface) with { Favorite = MediaMenuFavorite.Unknown }).Items.Single(item => item.Action == MediaMenuAction.Favorite);
            Assert.Equal((Texts.T("viewer.favorite.remove"), true, true), (on.Label, on.Checked, on.Available));
            Assert.Equal((Texts.T("viewer.favorite.add"), false, true), (off.Label, off.Checked, off.Available));
            Assert.False(unknown.Available);
            Assert.Null(unknown.Checked);
            Assert.Equal(Texts.T("media.menu.reason.unavailable"), unknown.Reason);
        }
    }

    [Fact]
    public void ARatingErrorOfTheArchiveIsTheReasonVerbatim()
    {
        MediaMenuItemModel rating = MediaMenuComposition.Compose(Full(MediaMenuSurface.ArchiveTable) with { RatingBlocked = "ratings damaged" })
            .Items.Single(item => item.Action == MediaMenuAction.Rating);
        Assert.False(rating.Available);
        Assert.Equal("ratings damaged", rating.Reason);
    }

    [Fact]
    public void TheCommonActionsAreGroupedIntoThreeSectionsByTheirDeclaredGroup()
    {
        MediaMenuModel model = MediaMenuComposition.Compose(Full(MediaMenuSurface.MediaPage) with { ProviderMedia = true, ProviderPostKnown = true });
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 1, 1, 2, 2, 2 }, model.Items.Select(item => item.Group));
    }

    [Fact]
    public void ATargetFromTheMediaIndexIsNormalizedAndAFileTargetNamesNoPost()
    {
        MediaLibraryEntry indexed = new(Thread, "T", 1, DateTimeOffset.UnixEpoch, null, "43", 0, null, 0, "rev", "dir", "asset", "AB12", 5,
            "m", "original", "name.png", SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
        MediaMenuTarget target = MediaMenuTarget.Of(indexed);
        Assert.Equal(Target(), target with { LocalSlot = null });
        MediaMenuTarget file = target with { PostId = null, Position = null, AttachmentRevisionKey = null, LocalSlot = null };
        Assert.NotEqual(target, file);
        Assert.Null(file.PostId);
        MediaMenuTarget other = MediaMenuTarget.Of(indexed with { PostId = "44" });
        Assert.NotEqual(target, other);
        Assert.NotEqual(target, MediaMenuTarget.Of(indexed with { Sha256 = new string('0', 64) }));
    }

    [Fact]
    public async Task ASessionRefusesAfterInvalidationOrWhenItsGuardFails()
    {
        bool current = true;
        MediaMenuSession session = new(MediaMenuSurface.Gallery, Target(), () => current);
        int calls = 0;
        await session.RunAsync(() => { calls++; return Task.CompletedTask; });
        Assert.Equal(1, calls);
        current = false;
        Assert.False(session.IsCurrent);
        await session.RunAsync(() => { calls++; return Task.CompletedTask; });
        current = true;
        Assert.True(session.IsCurrent);
        int invalidated = 0;
        session.Invalidated += () => invalidated++;
        session.Invalidate();
        session.Invalidate();
        Assert.Equal(1, invalidated);
        Assert.False(session.IsCurrent);
        await session.RunAsync(() => { calls++; return Task.CompletedTask; });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EveryMenuTextExistsInAllFiveLanguagesWithItsPlaceholders()
    {
        Assert.Equal(MediaMenuCatalog.Entries.Length, MediaMenuCatalog.Entries.Select(entry => entry.Key).Distinct().Count());
        foreach ((string key, string english, string german) in MediaMenuCatalog.Entries)
        {
            Assert.StartsWith("media.menu.", key);
            Assert.Equal(german, BuiltInCatalog.German[key]);
            foreach (string language in Languages)
            {
                Texts.SetLanguage(language);
                string text = Texts.T(key);
                Assert.False(string.IsNullOrWhiteSpace(text), language + " " + key);
                Assert.NotEqual(key, text);
                Assert.Equal(Placeholders(english), Placeholders(text));
                if (language != "en") Assert.True(text != english, language + " is untranslated: " + key);
            }
        }
    }

    [Fact]
    public void EveryMenuKeyTheDesktopCodeUsesIsInTheCatalog()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, "../.."));
        System.Text.RegularExpressions.Regex literal = new("\"(media\\.menu\\.[A-Za-z0-9.]+)\"");
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            foreach (System.Text.RegularExpressions.Match match in literal.Matches(File.ReadAllText(file)))
                used.Add(match.Groups[1].Value);
        Assert.NotEmpty(used);
        foreach (string key in used.Where(key => key != "media.menu.unavailable" && key != "media.menu.note"))
            Assert.True(BuiltInCatalog.English.ContainsKey(key), "no text for " + key);
        foreach (string key in MediaMenuCatalog.Entries.Select(entry => entry.Key))
            Assert.True(used.Contains(key), "unused text " + key);
    }

    private static string[] Placeholders(string text) => System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+(:[^}]*)?\}")
        .Select(match => match.Value).Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
