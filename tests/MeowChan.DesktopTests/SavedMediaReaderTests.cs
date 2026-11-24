using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Storage;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using Semi.Avalonia.DataGrid;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class SavedMediaReaderTests
{
    private static readonly ResourceId ThreadId = new("fixture", "g", "42");
    private static readonly string[] FooterActions = ["ArchiveEditRatingButton", "PreviewArchiveMediaButton", "ArchiveManageRatingsButton"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrowKeysMakeTheOpenSidebarFollowTheExactRevision(bool grid) => Run((page, window) =>
    {
        page.MediaTilesPreference = grid;
        Layout(window);
        ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
        DataGrid table = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
        ArchiveAssetRow[] rows = (grid ? tiles.ItemsSource : table.ItemsSource)!.Cast<ArchiveAssetRow>().ToArray();
        Assert.Equal(rows[0].Name, rows[1].Name);

        if (grid)
        {
            tiles.SelectedIndex = 0;
            Layout(window);
            Assert.True(((Control)tiles.ContainerFromIndex(0)!).Focus(NavigationMethod.Tab));
        }
        else SelectTableRow(window, table, rows[0]);
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        Assert.True(page.IsMediaSidebarShown);
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[0].Version, StringComparison.Ordinal)
            && sidebar.Load.ShownSource == new PixelSize(17, 11));
        window.KeyPressQwerty(grid ? PhysicalKey.ArrowRight : PhysicalKey.ArrowDown, RawInputModifiers.None);
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[1].Version, StringComparison.Ordinal)
            && sidebar.Load.ShownSource == new PixelSize(19, 13));
        Assert.Equal(1, grid ? tiles.SelectedIndex : table.SelectedIndex);
        window.KeyPressQwerty(grid ? PhysicalKey.ArrowLeft : PhysicalKey.ArrowUp, RawInputModifiers.None);
        WaitUntil(() => sidebar.HeaderLine.Contains(rows[0].Version, StringComparison.Ordinal)
            && sidebar.Load.ShownSource == new PixelSize(17, 11));
        Assert.Equal(0, grid ? tiles.SelectedIndex : table.SelectedIndex);

        if (!grid)
        {
            DataGridRow row = table.GetVisualDescendants().OfType<DataGridRow>()
                .Single(item => ReferenceEquals(item.DataContext, rows[0]));
            Border background = row.GetVisualDescendants().OfType<Border>().Single(item => item.Name == "BackgroundBorder");
            Assert.Equal(new Thickness(3, 1, 1, 1), background.BorderThickness);
            Visual[] outlines = row.GetVisualDescendants().Where(item => item is Control { Name: "CurrencyVisual" or "FocusVisual" }).ToArray();
            Assert.NotEmpty(outlines);
            Assert.All(outlines, outline => Assert.Equal(0, outline.Opacity));
        }
    });

    [Theory]
    [InlineData("de", false)]
    [InlineData("fr", true)]
    [InlineData("ru", false)]
    public void FooterSharesALineAndTheFavoriteToggleKeepsItsLabel(string language, bool grid) => Run((page, window) =>
    {
        page.ArchiveColumnModePreference = WorkViewSetting.AdvancedName;
        window.SetRenderScaling(1.5);
        Assert.Equal(1.5, window.RenderScaling);
        Texts.SetLanguage(language);
        BooruComponentDispatcher.Wait(page.ReaderLocalizationTask);
        page.MediaTilesPreference = grid;
        Layout(window);
        ToggleButton favorites = page.FindControl<ToggleButton>("ArchiveFavoritesFilter")!;
        Assert.Equal(Texts.T("archive.favoritesOnly"), favorites.Content);
        IToggleProvider toggle = Assert.IsAssignableFrom<IToggleProvider>(ControlAutomationPeer.CreatePeerForElement(favorites));
        Assert.Equal(ToggleState.Off, toggle.ToggleState);
        CommandBindings.InvokeAction(favorites);
        Layout(window);
        Assert.Equal(Texts.T("archive.favoritesOnly"), favorites.Content);
        Assert.Equal(ToggleState.On, toggle.ToggleState);
        Assert.True(Assert.Single(ShownRows(page, grid)).Asset.IsCurrent);
        toggle.Toggle();
        Layout(window);
        Assert.Equal(Texts.T("archive.favoritesOnly"), favorites.Content);
        Assert.Equal(ToggleState.Off, toggle.ToggleState);
        Assert.Equal(2, ShownRows(page, grid).Length);

        TextBlock total = page.FindControl<TextBlock>("ArchiveThreadMediaTotal")!;
        Assert.True(total.IsEffectivelyVisible);
        Assert.Equal(Texts.T("thread.media.total", 1), total.Text);
        foreach (string name in FooterActions)
        {
            Button action = page.FindControl<Button>(name)!;
            Assert.True(action.IsEffectivelyVisible);
            SameFooterLine(action);
            Assert.True(total.TranslatePoint(new Point(total.Bounds.Width, 0), window)!.Value.X
                <= action.TranslatePoint(default, window)!.Value.X);
        }
        BooruReviewTestPlatform.Resize(window, 760);
        Layout(window);
        SameFooterLine(page.FindControl<ActionToolBar>("ReaderMediaActions")!);

        void SameFooterLine(Control action)
        {
            double CenterY(Control control) => control.TranslatePoint(new Point(0, control.Bounds.Height / 2), window)!.Value.Y;
            Assert.InRange(Math.Abs(CenterY(total) - CenterY(action)), 0, 1);
        }

        page.ReaderNavigationMode = Config.ReaderNavigationModeAlways;
        Assert.False(page.ReaderNavigationShown);
        page.ReaderNavigationMode = Config.ReaderNavigationModeAuto;
        page.NotifyReaderPointerMoved(new Point(100, 100));
        Assert.False(page.ReaderNavigationShown);
        Assert.False(page.FindControl<Control>("ReaderNavigation")!.IsVisible);
        page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 0;
        page.ReaderNavigationMode = Config.ReaderNavigationModeAlways;
        Assert.True(page.ReaderNavigationShown);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothViewsCopyTheFullHashOfTheSelectedRevision(bool grid) => Run((page, window) =>
    {
        page.MediaTilesPreference = grid;
        Layout(window);
        ArchiveAssetRow row = ShownRows(page, grid).Single(asset => !asset.Asset.IsCurrent);
        if (grid)
        {
            ListBox tiles = page.FindControl<ListBox>("ArchiveMediaTiles")!;
            tiles.SelectedItem = row;
            Assert.True(((Control)tiles.ContainerFromItem(row)!).Focus(NavigationMethod.Tab));
        }
        else SelectTableRow(window, page.FindControl<DataGrid>("ArchiveMediaGrid")!, row);
        window.KeyPress(Key.F10, RawInputModifiers.Shift, PhysicalKey.F10, null);
        Layout(window);
        MenuItem copy = Assert.IsType<MenuItem>(page.ArchiveMediaMenu!.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Tag, "archive.media.copySha256")));
        Assert.Equal(Texts.T("archive.media.copySha256"), copy.Header);
        Assert.True(copy.Command!.CanExecute(null));
        copy.Command.Execute(null);
        WaitUntil(() => BooruComponentDispatcher.Wait(window.Clipboard!.TryGetTextAsync()) == row.Asset.Sha256);
        Assert.Equal(64, row.Asset.Sha256.Length);
    });

    [Fact]
    public void SavedColumnsKeepTheirOrderAndVisibilityAcrossAViewSwitch() => Run((page, window) =>
    {
        page.ArchiveColumnOrderPreference = new Dictionary<string, int> { ["ArchiveMediaGrid.version"] = 0, ["ArchiveMediaGrid.name"] = 1 };
        page.ArchiveColumnVisibilityPreference = new Dictionary<string, bool> { ["ArchiveMediaGrid.rating"] = false };
        page.ArchiveColumnWidthsPreference = new Dictionary<string, double> { ["ArchiveMediaGrid.1"] = 70, ["ArchiveMediaGrid.version"] = 100 };
        page.MediaTilesPreference = false;
        Layout(window);
        DataGrid table = page.FindControl<DataGrid>("ArchiveMediaGrid")!;
        Assert.Equal(0, table.Columns[2].DisplayIndex);
        Assert.False(table.Columns[3].IsVisible);
        Assert.Equal(88, table.Columns[1].Width.Value);
        Assert.Equal(128, table.Columns[2].Width.Value);
        ArchiveAssetRow row = ShownRows(page, false).Single(asset => asset.Asset.IsCurrent);
        DataGridRow visual = table.GetVisualDescendants().OfType<DataGridRow>()
            .Single(item => ReferenceEquals(item.DataContext, row));
        foreach (string text in new[] { row.Size, row.Version })
        {
            TextBlock cell = visual.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Text == text);
            Assert.Equal(TextWrapping.NoWrap, cell.TextWrapping);
            Assert.True(cell.Bounds.Height > 0);
        }
        Assert.StartsWith("Current", row.Version);
        Assert.StartsWith("Earlier", ShownRows(page, false).Single(asset => !asset.Asset.IsCurrent).Version);
        Assert.Contains("\u00A0KiB", row.TileSummary);
        Assert.Equal(Texts.T("media.fact.byteSize", row.Asset.Length, SizeText.Format(row.Asset.Length)), row.ExactSize);
        page.MediaTilesPreference = true;
        page.MediaTilesPreference = false;
        Assert.Equal(0, table.Columns[2].DisplayIndex);
        Assert.False(table.Columns[3].IsVisible);
    });

    [Fact]
    public void TheMediaPagePanelAlsoFollowsGridArrows() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        MediaPage page = new(_ => throw new InvalidOperationException("Use the fixture snapshot")) { IsAvailable = true };
        Window window = new() { Width = 1440, Height = 840, Content = page };
        try
        {
            MediaLibraryEntry[] entries = [MediaTagCountsTests.Entry(1), MediaTagCountsTests.Entry(2)];
            typeof(MediaPage).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(page, new MediaLibrarySnapshot(entries, [], [], complete: true));
            page.ResetFilters();
            page.ApplyLayout(page.PageLayout with { Panel = true }, save: false);
            window.Show();
            Layout(window);
            Assert.True(page.IsPanelShown);
            MediaLibraryTileRow[] rows = page.Grid.ItemsSource!.Cast<MediaLibraryTileRow>().ToArray();
            Assert.Equal(2, rows.Length);
            page.Grid.SelectedIndex = 0;
            Assert.True(((Control)page.Grid.ContainerFromIndex(0)!).Focus(NavigationMethod.Tab));
            WaitUntil(() => ReferenceEquals(rows[0], page.PanelRow));

            window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
            WaitUntil(() => ReferenceEquals(rows[1], page.PanelRow));
            Assert.Equal(1, page.Grid.SelectedIndex);

            window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
            WaitUntil(() => ReferenceEquals(rows[0], page.PanelRow));
            Assert.Equal(0, page.Grid.SelectedIndex);
        }
        finally
        {
            page.EndLifetime();
            window.Close();
        }
    });

    private static void SelectTableRow(Window window, DataGrid table, ArchiveAssetRow row)
    {
        DataGridRow visual = table.GetVisualDescendants().OfType<DataGridRow>()
            .Single(item => ReferenceEquals(item.DataContext, row));
        Point position = visual.TranslatePoint(new Point(16, visual.Bounds.Height / 2), window)!.Value;
        window.MouseDown(position, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(position, MouseButton.Left, RawInputModifiers.None);
        Assert.Same(row, table.SelectedItem);
        Assert.True(table.IsKeyboardFocusWithin);
    }

    private static ArchiveAssetRow[] ShownRows(ArchivePage page, bool grid) =>
        (grid ? page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource : page.FindControl<DataGrid>("ArchiveMediaGrid")!.ItemsSource)!
        .Cast<ArchiveAssetRow>().ToArray();

    private static void Run(Action<ArchivePage, Window> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
        using TempDirectory temp = new();
        string filename = "same.png";
        MediaAssetReference current = WriteRevision("a", true,
            "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAE0lEQVR4nGNgmMlAMhrVM6oHiADlWm/EglqV/gAAAABJRU5ErkJggg==");
        MediaAssetReference earlier = WriteRevision("b", false,
            "iVBORw0KGgoAAAANSUhEUgAAABMAAAANCAIAAADE7sJwAAAAFklEQVR4nGMwdgklDzGM6hzVOQR1AgDPR8TVSHrz+wAAAABJRU5ErkJggg==");

        MediaAssetReference WriteRevision(string id, bool isCurrent, string png)
        {
            byte[] bytes = Convert.FromBase64String(png);
            Array.Resize(ref bytes, 4096);
            string path = Path.Combine(temp.Path, id + ".png");
            File.WriteAllBytes(path, bytes);
            return new(id, "image", "original", id + ".png", path, bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "https://offline.invalid/" + id, filename, isCurrent);
        }
        MediaAssetSnapshot manifest = new(temp.Path, MediaManifestStatus.Available, 1, "fixture", [current, earlier]);
        ArchiveThreadSummary summary = new(ThreadId, "https://offline.invalid/g/thread/42", "Fixture", temp.Path,
            ThreadSourceState.Active, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 1);
        ArchivedThreadView thread = new(summary, [new CapturedPost("42", 42, PostFieldPresence.All,
            Attachments: [new CapturedAttachment(0, "image", OriginalFileName: filename)])], [temp.Path]);
        ArchivePage page = new(_ => throw new InvalidOperationException("The prepared thread should be used"),
            (_, _) => Task.CompletedTask, _ => { }, (_, _) => Task.FromResult(manifest))
        {
            IsAvailable = true,
            IsArchiveFavorite = entry => entry.Asset.IsCurrent
        };
        Window window = new() { Width = 1440, Height = 840 };
        try
        {
            BooruComponentDispatcher.Wait(page.LoadThreadAsync(ThreadId, preparedThread: thread));
            BooruComponentDispatcher.Wait(page.MediaLoadTask);
            window.Content = page.DetachReader();
            window.Show();
            Layout(window);
            page.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 1;
            Layout(window);
            test(page, window);
        }
        finally
        {
            page.EndLifetime();
            window.Close();
        }
    });

    private static void Layout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void WaitUntil(Func<bool> condition)
    {
        long started = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - started < 5000)
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(1);
        }
        Assert.True(condition());
    }
}
