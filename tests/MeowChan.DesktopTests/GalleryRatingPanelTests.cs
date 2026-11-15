using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using MeowChan.Desktop.Commands;
using MeowChan.Favorites;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// Review 15.11. (Tom's decision): the favourite badge toggles, the rating button and the rating modal are gone, the Post tab shows the stars
// and the tags, and the media menu rates through a submenu. The viewer is a reflection fixture (no window); the real controls and the
// source contracts are checked, native hit routing and live layout belong to the smokes.
[Collection("Localization")]
public sealed class GalleryRatingPanelTests : IDisposable
{
    private static readonly ResourceId Thread = new("source", "b", "42");

    public GalleryRatingPanelTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // ---- the favourite badge ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClickOnTheBadgeTogglesTheFavouriteAndTheShownStateStaysThePersistedOne(bool saved)
    {
        Fixture h = new();
        h.Viewer.SetFavorite(saved);
        List<GalleryEntry> toggles = new();
        h.Viewer.FavoriteToggleRequested += toggles.Add;
        foreach (ToggleButton badge in new[] { h.Managed, h.Native })
        {
            Click(badge);
            // The button flipped itself on the click; the viewer shows the persisted state again (the write decides).
            Assert.Equal(saved, badge.IsChecked);
        }
        Assert.Equal(2, toggles.Count);
    }

    [Fact]
    public void AClickWhileAWriteIsPendingIsIgnoredAndTheBadgeStaysEnabledForTheFocus()
    {
        Fixture h = new();
        h.Viewer.SetFavorite(true, pending: true);
        int toggles = 0;
        h.Viewer.FavoriteToggleRequested += _ => toggles++;
        Click(h.Managed);
        Assert.Equal(0, toggles);
        Assert.True(h.Managed.IsEnabled);
        Assert.True(h.Managed.IsChecked);
        Assert.Contains("…", AutomationProperties.GetName(h.Managed) ?? string.Empty);
    }

    [Fact]
    public void TheBadgeIsAToggleButtonSoSpaceAndEnterReachItAndTheShortcutGuardLeavesThemAlone()
    {
        // A Button is native input for Enter and Space (CommandBindings.IsNativeInput), so the gallery's own handler does not take them: the
        // click of the focused badge works by the keyboard exactly as by the pointer.
        Assert.IsAssignableFrom<Button>(GalleryViewer.CreateFavoriteIndicator("Badge"));
        string bindings = File.ReadAllText(Path.Combine(RepoRoot(), "Desktop", "Commands", "CommandBindings.cs"));
        Assert.Contains("Button", bindings);
        string viewer = File.ReadAllText(Path.Combine(RepoRoot(), "Desktop", "GalleryViewer.cs"));
        Assert.Contains("IsGalleryShortcutInput", viewer);
    }

    // ---- the media menu: "Rate and tag…" (one entry, no submenu) -----------------------------------------------------------------

    [Fact]
    public void RateAndTagIsOneEntryWithoutChildrenAndNoRatingStarsAreMenuEntries()
    {
        MediaMenuModel model = MediaMenuComposition.Compose(Facts());
        MediaMenuItemModel entry = RateAndTag(model);
        Assert.Equal("media.rating.edit", entry.CommandId);
        Assert.True(entry.Available);
        Assert.Null(entry.Checked);
        Assert.DoesNotContain(model.Items, item => item.CommandId is { } id && id.StartsWith("media.rating.set-", StringComparison.Ordinal));
        Assert.DoesNotContain(model.Items, item => item.CommandId is "media.rating.clear" or "media.tags.edit");
    }

    [Fact]
    public void ABlockedRatingStillOffersRateAndTagWhileTheTagsCanBeEdited()
    {
        MediaMenuItemModel entry = RateAndTag(MediaMenuComposition.Compose(Facts() with { RatingBlocked = "ratings damaged" }));
        Assert.True(entry.Available);
        MediaMenuItemModel neither = RateAndTag(MediaMenuComposition.Compose(Facts() with { RatingBlocked = "ratings damaged", TagsBlocked = "tags damaged" }));
        Assert.False(neither.Available);
        Assert.Equal("ratings damaged", neither.Reason);
    }

    [Fact]
    public void RateAndTagKeepsItsIdAndKeyAndIsNamedInBothLanguages()
    {
        Assert.Equal("Rate and tag…", CommandCatalog.Label("media.rating.edit"));
        Texts.SetLanguage("de");
        Assert.Equal("Bewerten und taggen…", CommandCatalog.Label("media.rating.edit"));
        Assert.Equal("R", CommandCatalog.Get("media.rating.edit").Shortcut);
        // The direct-rating keys stay commands (Shift+1 to 5, Shift+0), they are only no menu entries.
        Assert.Equal("Shift+3", CommandCatalog.Get("media.rating.set-3").Shortcut);
        Assert.Equal("Shift+0", CommandCatalog.Get("media.rating.clear").Shortcut);
    }

    [Fact]
    public void EveryPackNamesTheCombinedCommandAndNoPackKeepsTheOldRatingOnlyLabel()
    {
        foreach (string code in Texts.BundledPackCodes())
        {
            Texts.SetLanguage(code);
            Assert.NotEqual("Rate and tag…", CommandCatalog.Label("media.rating.edit"));
            Assert.NotEqual(CommandCatalog.Label("media.rating.clear"), CommandCatalog.Label("media.rating.edit"));
            Assert.EndsWith("…", CommandCatalog.Label("media.rating.edit"));
        }
        Texts.SetLanguage("en");
    }

    // ---- the Post tab ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePostRowShowsTheViewersRatingAndKeepsItsStarsEnabledWhileAWriteIsPending()
    {
        Fixture h = new();
        h.Rate(3, available: false, pending: true);
        GalleryDecisionRow row = h.PostRow();
        Assert.All(row.StarButtons, star => Assert.True(star.IsEnabled));
        Assert.Equal(new bool[] { false, false, true, false, false }, row.StarButtons.Select(star => star.IsChecked == true).ToArray());
        List<int?> requests = new();
        h.Viewer.RatingSetRequested += (_, value) => requests.Add(value);
        Click(row.StarButtons[3]);
        Assert.Empty(requests); // the click is ignored while the write runs; the focus is not taken away
        Assert.Equal(3, row.View.Rating);
        h.Rate(3, available: true, pending: false);
        Click(row.StarButtons[3]);
        Assert.Equal(new int?[] { 4 }, requests);
        Click(row.ClearButton);
        Assert.Equal(new int?[] { 4, null }, requests);
    }

    [Fact]
    public void ThePostRowIsDisabledWhenRatingsCannotBeUsedAndNotMerelyBusy()
    {
        Fixture h = new();
        h.Rate(null, available: false, pending: false);
        GalleryDecisionRow row = h.PostRow();
        Assert.All(row.StarButtons, star => Assert.False(star.IsEnabled));
        Assert.False(row.ClearButton.IsEnabled);
    }

    [Fact]
    public void AFailedWriteIsSaidInAnAssertiveLiveRegionUnderTheRowAndClearsWithTheNextSuccess()
    {
        Fixture h = new();
        h.Rate(2, available: true, pending: false);
        h.PostRow();
        TextBlock error = h.PostError;
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(error));
        Assert.False(error.IsVisible);
        SetField(h.Viewer, "_ratingError", "disk full");
        h.Viewer.RefreshPostDecision();
        Assert.True(error.IsVisible);
        Assert.Contains("disk full", error.Text);
        Assert.StartsWith(Texts.T("media.rating.error.save"), error.Text);
        SetField(h.Viewer, "_ratingError", null);
        h.Viewer.RefreshPostDecision();
        Assert.False(error.IsVisible);
        Assert.True(string.IsNullOrEmpty(error.Text));
    }

    [Fact]
    public void ThePostRowStarsAreOneGroupForAScreenReader()
    {
        Fixture h = new();
        h.Rate(4, available: true, pending: false);
        GalleryDecisionRow row = h.PostRow();
        Assert.Equal(AutomationControlType.Group, AutomationProperties.GetControlTypeOverride(row.RatingGroup));
        Assert.Equal(MediaRatingText.Label, AutomationProperties.GetName(row.RatingGroup));
        Assert.Contains("4", AutomationProperties.GetHelpText(row.RatingGroup));
    }

    [Fact]
    public void ThePostTagsSectionListsTheTagsAsChipButtonsThatOpenTheTagsSegmentAndOneLinkForTheKeyboard()
    {
        Fixture h = new();
        SetField(h.Viewer, "_tagNames", new[] { "alpha", "beta", "gamma" });
        StackPanel section = (StackPanel)BuildTags(h.Viewer, out List<(string Name, string Text)> commands);
        WrapPanel chips = Assert.IsType<WrapPanel>(section.Children.OfType<WrapPanel>().Single());
        Assert.Equal(3, chips.Children.Count);
        // Dead-affordance review 15.11.: a chip is a real, enabled button (it opens the Tags segment); the one link is the keyboard's way, so the
        // chips stay out of the Tab order.
        Assert.All(chips.Children, chip =>
        {
            Button button = Assert.IsType<Button>(chip);
            Assert.True(button.IsEnabled);
            Assert.False(button.Focusable);
            Assert.Equal(Texts.T("gallery.post.tags.edit"), ToolTip.GetTip(button));
            Click(button); // the viewer of this fixture has no open panel: nothing to open, nothing may throw
        });
        Assert.Equal(new[] { ("GalleryPostTagsEdit", Texts.T("gallery.post.tags.edit")) }, commands.Select(item => (item.Name, item.Text)).ToArray());
        Assert.Contains("alpha, beta, gamma", AutomationProperties.GetName(chips));
    }

    [Fact]
    public void ManyTagsEndInOneMoreChipAndNoTagsSayTheyAreEmpty()
    {
        Fixture h = new();
        string[] many = Enumerable.Range(1, GalleryViewer.PostTagChipLimit + 5).Select(number => "t" + number).ToArray();
        SetField(h.Viewer, "_tagNames", many);
        StackPanel section = (StackPanel)BuildTags(h.Viewer, out _);
        WrapPanel chips = section.Children.OfType<WrapPanel>().Single();
        Assert.Equal(GalleryViewer.PostTagChipLimit + 1, chips.Children.Count);
        Assert.Equal("GalleryPostTagsMore", ((Control)chips.Children[^1]).Name);
        Assert.IsType<Button>(chips.Children[^1]);
        SetField(h.Viewer, "_tagNames", Array.Empty<string>());
        section = (StackPanel)BuildTags(h.Viewer, out _);
        Assert.Contains(section.Children, child => child.Name == "GalleryPostTagsEmpty");
        Assert.DoesNotContain(section.Children, child => child is WrapPanel);
    }

    [Fact]
    public void UnknownTagsShowTheReasonAndOfferNoLink()
    {
        Fixture h = new();
        SetField(h.Viewer, "_tagNames", null);
        SetField(h.Viewer, "_tagsUnavailableReason", "The tag store is damaged.");
        StackPanel section = (StackPanel)BuildTags(h.Viewer, out List<(string Name, string Text)> commands);
        Assert.Empty(commands);
        TextBlock reason = Assert.IsType<TextBlock>(section.Children.Single(child => child.Name == "GalleryPostTagsReason"));
        Assert.Equal("The tag store is damaged.", reason.Text);
    }

    [Fact]
    public void AChipOrTheMoreChipOpensTheTagsSegmentLikeTheLink()
    {
        string post = Source("Desktop", "GalleryViewer.PostPanel.cs");
        Assert.Single(post.Split("GallerySidePanelSegment.Tags, focusContent: true")[1..]); // one action, shared by the link and both kinds of chip
        Assert.Contains("\"GalleryPostTagsEdit\", Texts.T(\"gallery.post.tags.edit\"), openTags)", post);
        Assert.Contains("chip.Click += (_, _) => open();", post);
        Assert.Contains("TagChip(name, more: false, openTags)", post);
        Assert.Contains("more: true, openTags)", post);
    }

    // ---- R and the deletions ---------------------------------------------------------------------------------------------------

    [Fact]
    public void RMovesToTheTagsSegmentWithItsStarsFocusedAndTheMenusHaveNoRatingMenuAnyMore()
    {
        string viewer = Source("Desktop", "GalleryViewer.cs"), post = Source("Desktop", "GalleryViewer.PostPanel.cs");
        Assert.Contains("RequestRateAndTag, Priority: 200", viewer);
        // The Tags segment opens first (when it can), then its row's stars take the focus; a medium without Tags segment gets the Post segment's stars.
        Assert.Contains("if (!_tagPanelOpen && CanOpenTagPanel) RequestTags();", post);
        Assert.Contains("if (_tagPanelOpen && _decisionRow is { } tagsRow) return _contextOpen && tagsRow.FocusRating();", post);
        Assert.Contains("return _postDecision is { } row && row.FocusRating();", post);
        Assert.DoesNotContain("BuildRatingMenu", post + Source("Desktop", "MediaActions", "MediaMenuBuilder.cs"));
        Assert.DoesNotContain("ShowRatingMenu", viewer + post);
        Fixture h = new(); // no rating available: R has nothing to focus
        Assert.False(h.Viewer.FocusRatingStars());
        Assert.False(h.Viewer.CanRateAndTag);
    }

    [Fact]
    public void TheMediaPageAndTheArchiveOpenTheCombinedPopoverForRAndTheButton()
    {
        string page = Source("Desktop", "Views", "Media", "MediaPage.cs");
        Assert.Contains("OpenRateAndTagAsync(row.Entry, row,", page);
        Assert.DoesNotContain("OpenRatingMenu", page + Source("Desktop", "Views", "Archive", "ArchivePage.Ratings.cs") + Source("Desktop", "Views", "Archive", "ArchivePage.RatingCommands.cs"));
        Assert.Contains("media.rating.set-", page + Source("Desktop", "Views", "Archive", "ArchivePage.RatingCommands.cs"));
        Assert.Contains("OpenRateAndTagForFocusAsync", Source("Desktop", "Views", "Archive", "ArchivePage.RatingCommands.cs"));
        Assert.Contains("OpenRateAndTagForFocusAsync", Source("Desktop", "Views", "Archive", "ArchivePage.Ratings.cs"));
        Assert.Contains("RatingMenuAnchor", Source("Desktop", "Views", "Archive", "ArchivePage.Ratings.cs"));
    }

    [Fact]
    public void TheInfoLineRatingAndTheTileRatingCaptionAreKeyboardReachableControlsThatOpenRateAndTag()
    {
        // Dead-affordance audit 15.11.: the passive "Rating: n" texts open the same surface as R.
        TextBlock status = new();
        int opened = 0;
        GalleryViewer.MakeRatingStatusInteractive(status, () => opened++);
        Assert.True(status.Focusable);
        Assert.True(status.IsTabStop);
        Assert.Equal(AutomationControlType.Button, AutomationProperties.GetControlTypeOverride(status));
        status.Text = "Rating: 4 of 5";
        Assert.Equal("Rating: 4 of 5", AutomationProperties.GetName(status));
        Assert.Contains("R", (string)ToolTip.GetTip(status)!);
        Assert.Contains("MakeRatingStatusInteractive(_ratingStatus, RequestRateAndTag)", Source("Desktop", "GalleryViewer.InfoLine.cs"));
        Assert.Equal(0, opened);

        string tile = Source("Desktop", "ArchiveMediaTileView.cs");
        Assert.Contains("caption.Focusable = true;", tile);
        Assert.Contains("caption.IsTabStop = true;", tile);
        Assert.Contains("AutomationControlType.Button", tile);
        Assert.Contains("Key.Enter or Avalonia.Input.Key.Space", tile);
        Assert.Contains("RateAndTagRequestedEvent", Source("Desktop", "Views", "Archive", "ArchivePage.MediaCommands.cs"));
    }

    [Fact]
    public void NoRatingDialogPlaybackHoldOrToolbarRatingButtonRemains()
    {
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "Desktop", "MediaRatingDialog.cs")));
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "Desktop", "GalleryDialogPlaybackHold.cs")));
        Assert.DoesNotContain("gallery.toolbar.rating", Source("Desktop", "WorkViews", "WorkViewPolicy.Gallery.cs"));
        Assert.DoesNotContain("gallery.toolbar.rating", Source("Desktop", "GalleryChromePlan.cs"));
    }

    // ---- support ---------------------------------------------------------------------------------------------------------------------

    private static MediaMenuFacts Facts() => new(MediaMenuSurface.Gallery, new MediaMenuTarget(Thread, "dir", "asset", "43", 0, "rev", "ab12", null))
    {
        Favorite = MediaMenuFavorite.Off,
        Posts = MediaMenuPostScope.Single
    };

    private static MediaMenuItemModel RateAndTag(MediaMenuModel model) => model.Items.Single(item => item.Action == MediaMenuAction.RateAndTag);

    private static void Click(Button button) =>
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);

    private static Control BuildTags(GalleryViewer viewer, out List<(string Name, string Text)> commands)
    {
        List<(string Name, string Text)> seen = new();
        Func<Panel, string, string, Action, Button> command = (parent, name, text, action) =>
        {
            Button button = new() { Name = name, Content = text };
            parent.Children.Add(button);
            seen.Add((name, text));
            Assert.NotNull(action);
            return button;
        };
        object result = typeof(GalleryViewer).GetMethod("BuildPostTagsSection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewer, new object[] { command })!;
        commands = seen;
        return (Control)result;
    }

    private static void SetField(GalleryViewer viewer, string name, object? value) =>
        typeof(GalleryViewer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(viewer, value);

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    private sealed class Fixture
    {
        internal GalleryViewer Viewer { get; } = (GalleryViewer)RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        internal ToggleButton Managed { get; } = GalleryViewer.CreateFavoriteIndicator("GalleryFavoriteIndicator");
        internal ToggleButton Native { get; } = GalleryViewer.CreateFavoriteIndicator("GalleryNativeFavoriteIndicator");
        internal TextBlock PostError { get; } = new() { Name = "GalleryPostRatingError" };

        internal Fixture()
        {
            SetField(Viewer, "_favorite", new ToggleButton());
            SetField(Viewer, "_favoriteIndicator", Managed);
            SetField(Viewer, "_nativeFavoriteIndicator", Native);
            SetField(Viewer, "_visualRoot", new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") });
            SetField(Viewer, "_nativeVideoOverlay", new Grid());
            SetField(Viewer, "_postRatingError", PostError);
            SetField(Viewer, "_interactionAvailable", true);
            SetField(Viewer, "_entry", Entry());
            typeof(GalleryViewer).GetMethod("InitializeFavoriteIndicators", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Viewer, null);
        }

        internal void Rate(int? value, bool available, bool pending)
        {
            SetField(Viewer, "_ratingValue", value);
            SetField(Viewer, "_ratingAvailable", available);
            SetField(Viewer, "_ratingPending", pending);
            Viewer.SetFavorite(false);
            Viewer.RefreshPostDecision();
        }

        internal GalleryDecisionRow PostRow()
        {
            typeof(GalleryViewer).GetMethod("PostDecisionSection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Viewer, null);
            return Viewer.PostDecisionRow!;
        }
    }

    private static GalleryEntry Entry()
    {
        var asset = new MediaAssetReference("asset", "image", "original", "fixture.png", "fixture.png", 7,
            new string('a', 64), "https://fixture.invalid/fixture.png", "fixture.png", true);
        var manifest = new MediaAssetSnapshot("fixture", MediaManifestStatus.Available, 1, "generation", [asset]);
        FavoriteAssetKey key = new(Thread, "image", "original", new string('a', 64));
        return new(new(key.Thread, manifest.Directory, asset.AssetId), manifest, asset, null, null, false, false);
    }
}
