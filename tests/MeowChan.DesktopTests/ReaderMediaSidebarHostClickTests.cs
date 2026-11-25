using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderMediaSidebarHostClickTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void InfoClicksRecoverLegacyProfilesAndKeepBothHostActionRows(bool mediaPage, bool legacyOff) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            using var media = BooruComponentDispatcher.Wait(ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false));
            var thread = RateAndTagWindowFixture.View(media);
            BooruComponentDispatcher.Wait(RateAndTagWindowFixture.SeedAsync(profile.Path, thread));
            Config settings = new() { MediaSidebarInfo = legacyOff ? "Off" : "Auto",
                ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways, ReaderMediaSidebarMinimumWidth = 800 };
            var preferences = ReaderMediaPreferences.From(settings);
            Window window = new() { Width = 1600, Height = 850 };
            Task<ArchiveReader> OpenReader(CancellationToken token) => ArchiveReader.OpenAsync(profile.Path, token);
            if (mediaPage)
            {
                MediaPage page = new(OpenReader) { IsAvailable = true, SavePanelSetting = update => update(settings) };
                try
                {
                    page.ConfigurePanelInfo(preferences);
                    window.Content = page;
                    window.Show();
                    BooruComponentDispatcher.Wait(page.ActivateAsync());
                    page.Grid.SelectedIndex = 0;
                    if (!page.PageLayout.Panel) Click(window, page.PanelToggleButton);
                    BooruComponentDispatcher.WaitUntil(() => page.IsPanelShown && page.PanelRow != null, "selected Media tile");
                    BooruComponentDispatcher.Wait(page.PanelTask);
                    CheckFacts(window, Assert.IsType<ReaderMediaSidebar>(page.Panel), legacyOff);
                }
                finally { window.Close(); page.EndLifetime(); }
            }
            else
            {
                ArchivePage page = new(OpenReader, (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
                bool marked = false;
                List<GalleryEntry> writes = [];
                page.IsArchiveFavorite = _ => marked;
                page.ToggleArchiveFavoriteAsync = entry => { writes.Add(entry); marked = !marked; return Task.CompletedTask; };
                page.SaveReaderMediaSwitches = (update, _) => { update(settings); return Task.CompletedTask; };
                try
                {
                    page.ApplyReaderMediaPreferences(preferences);
                    window.Content = page;
                    window.Show();
                    BooruComponentDispatcher.Wait(page.LoadThreadAsync(thread.Summary.Id));
                    page.FindControl<ListBox>("ArchivePosts")!.SelectedIndex = 0;
                    BooruComponentDispatcher.Wait(page.MediaLoadTask);
                    BooruComponentDispatcher.Wait(page.MediaSidebarTask);
                    ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
                    Assert.True(page.IsMediaSidebarShown);
                    CheckFacts(window, sidebar, legacyOff);
                    Assert.True(sidebar.FavoriteButton.IsEffectivelyEnabled);
                    Click(window, sidebar.FavoriteButton);
                    Assert.Equal(media.Asset.AssetId, Assert.Single(writes).Asset.AssetId);
                    Assert.True(sidebar.FavoriteButton.IsChecked);
                }
                finally { window.Close(); page.EndLifetime(); }
            }
            Assert.Equal(!legacyOff, settings.SidebarFactsVisible);
        });

    [Fact]
    public void ReaderWithoutASavedCopyExplainsItsDisabledFavorite() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using var media = BooruComponentDispatcher.Wait(ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false));
        var thread = RateAndTagWindowFixture.View(media);
        using TempDirectory profile = new();
        BooruComponentDispatcher.Wait(RateAndTagWindowFixture.SeedAsync(profile.Path, thread));
        var empty = new MediaAssetSnapshot(media.Directory, MediaManifestStatus.Missing, null, null, []);
        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token), (_, _) => Task.CompletedTask,
            _ => { }, (_, _) => Task.FromResult(empty)) { IsAvailable = true };
        int writes = 0;
        page.ToggleArchiveFavoriteAsync = _ => { writes++; return Task.CompletedTask; };
        page.ApplyReaderMediaPreferences(ReaderMediaPreferences.From(new Config {
            ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways, ReaderMediaSidebarMinimumWidth = 800 }));
        Window window = new() { Width = 1600, Height = 850, Content = page };
        try
        {
            window.Show();
            BooruComponentDispatcher.Wait(page.LoadThreadAsync(thread.Summary.Id));
            page.FindControl<ListBox>("ArchivePosts")!.SelectedIndex = 0;
            BooruComponentDispatcher.Wait(page.MediaLoadTask);
            BooruComponentDispatcher.Wait(page.MediaSidebarTask);
            var sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
            Assert.True(sidebar.FavoriteButton.IsEffectivelyVisible);
            Assert.False(sidebar.FavoriteButton.IsEffectivelyEnabled);
            Assert.Equal(Texts.T("archive.notSaved"), ToolTip.GetTip(sidebar.FavoriteButton));
            Click(window, sidebar.FavoriteButton);
            Assert.Equal(0, writes);
        }
        finally { window.Close(); page.EndLifetime(); }
    });

    private static void CheckFacts(Window window, ReaderMediaSidebar sidebar, bool legacyOff)
    {
        window.UpdateLayout();
        var facts = SidePanelDesignTests.ById<MediaSidebarFacts>(sidebar, "ReaderMediaSidebarFacts");
        var tags = SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarTagsEmpty");
        var edit = SidePanelDesignTests.ById<Button>(sidebar, "ReaderMediaSidebarEditTags");
        void Expect(bool visible)
        {
            Assert.Equal(visible, facts.IsEffectivelyVisible);
            Assert.Equal(visible, sidebar.InfoToggle.IsChecked);
            Assert.True(sidebar.DecisionRow.StarButtons.All(star => star.IsEffectivelyVisible));
            Assert.True(tags.IsEffectivelyVisible);
            Assert.True(edit.IsEffectivelyVisible);
            Assert.True(sidebar.Details.TranslatePoint(default, sidebar)!.Value.Y >=
                sidebar.Card.TranslatePoint(new Point(0, sidebar.Card.Bounds.Height), sidebar)!.Value.Y);
            Assert.True(sidebar.Details.Bounds.Height >= (visible ? 100 : 40));
        }
        Expect(!legacyOff);
        double initial = sidebar.Details.Bounds.Height;
        Click(window, sidebar.InfoToggle);
        Expect(legacyOff);
        Assert.True(legacyOff ? sidebar.Details.Bounds.Height > initial : sidebar.Details.Bounds.Height < initial);
        Click(window, sidebar.InfoToggle);
        Expect(!legacyOff);
        Assert.Equal(initial, sidebar.Details.Bounds.Height);
    }

    private static void Click(Window window, Control button)
    {
        window.UpdateLayout();
        var point = button.TranslatePoint(new Rect(button.Bounds.Size).Center, window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
