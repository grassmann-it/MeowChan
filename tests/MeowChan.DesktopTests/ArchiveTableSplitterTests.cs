using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Desktop.WorkViews;
using MeowChan.Interfaces;
using Semi.Avalonia.DataGrid;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ArchiveTableSplitterTests
{
    [Fact]
    public async Task ChangingTableRowsKeepsTheDraggedWidthAndTheMediaSidebarVisible()
    {
        using ReaderArchiveTestFixture archive = new();
        ResourceId first = new("synthetic-A", "board-A", "1101");
        ResourceId second = new("synthetic-A", "board-A", "1102");
        await archive.CaptureAsync(first, "First thread", [new("1101", 0, PostFieldPresence.All, Html: "First post")]);
        await archive.CaptureAsync(second, "Second thread", [new("1102", 0, PostFieldPresence.All, Html: "Second post")]);

        archive.Run(page =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
            page.ArchiveColumnModePreference = WorkViewSetting.AdvancedName;
            page.ArchiveLayoutPreference = "Table";
            page.ApplyReaderMediaPreferences(ReaderMediaPreferences.From(new Config
            {
                ReaderMediaSidebar = true,
                ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways,
                ReaderMediaSidebarWidth = 560
            }));
            Window window = new() { Content = page, Width = 1920, Height = 850 };
            try
            {
                window.Show();
                BooruComponentDispatcher.Wait(page.ActivateAsync());
                Settle(window);
                DataGrid table = FindByAutomationId<DataGrid>(page, "ArchiveThreadTable");
                Grid list = FindByAutomationId<Grid>(page, "ArchiveListPane");
                GridSplitter splitter = FindByAutomationId<GridSplitter>(page, "ArchiveSplitter");
                SelectThread(first);
                Assert.False(page.IsMediaSidebarShown);

                double initialWidth = list.Bounds.Width;
                Point start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
                Point end = new(start.X - initialWidth + 460, start.Y);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                Settle(window);
                window.MouseUp(end, MouseButton.Left);
                Settle(window);

                double draggedWidth = list.Bounds.Width;
                Assert.InRange(draggedWidth, Config.ArchiveSplitWidthMinimum, initialWidth - 1);
                Assert.True(page.IsMediaSidebarShown);

                SelectThread(second);

                Assert.InRange(list.Bounds.Width, draggedWidth - 1, draggedWidth + 1);
                Assert.True(page.IsMediaSidebarShown);

                void SelectThread(ResourceId id)
                {
                    DataGridRow row = Assert.Single(table.GetVisualDescendants().OfType<DataGridRow>(),
                        item => item.DataContext is ArchiveThreadRow thread && thread.Summary.Id == id);
                    Point point = row.TranslatePoint(new Point(120, row.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    BooruComponentDispatcher.WaitUntil(() => page.CurrentThread?.Summary.Id == id, $"archive thread {id}");
                    Settle(window);
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void DraggingToTheDefaultWidthSurvivesSwitchingWorkViewsReloadAndSettingsTransfer() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
        using TempDirectory profile = new();
        Config initial = new()
        {
            DownloadPath = Path.Combine(profile.Path, "downloads"),
            FirstRunSetupVersion = 1,
            WorkspaceMode = WorkViewSetting.AdvancedName,
            ArchiveThreadLayout = "Table",
            WindowWidth = 1920,
            WindowHeight = 850
        };
        initial.GetLayoutProfile(WorkViewSetting.SimpleName).ArchiveThreadLayout = "Table";
        initial.SaveConfig(profile.Path);

        await WithArchiveAsync(profile.Path, async (window, session) =>
        {
            ArchivePage page = window.ArchivePage;
            Grid list = FindByAutomationId<Grid>(page, "ArchiveListPane");
            Assert.Equal(950, list.Bounds.Width);
            GridSplitter splitter = FindByAutomationId<GridSplitter>(page, "ArchiveSplitter");
            Grid split = FindByAutomationId<Grid>(page, "ArchiveSplit");
            double columnWidth = split.ColumnDefinitions[Grid.GetColumn(list)].ActualWidth;
            Point start = splitter.TranslatePoint(new Rect(splitter.Bounds.Size).Center, window)!.Value;
            Point end = new(start.X + 330 - columnWidth, start.Y);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Settle(window);
            window.MouseUp(end, MouseButton.Left);
            Settle(window);
            Assert.Equal(330, list.Bounds.Width);
            await session.FlushPreferencesAsync();

            ComboBox modes = FindByAutomationId<ComboBox>(window, "WorkspaceModeComboBox");
            await SelectWorkViewAsync(WorkView.Simple);
            Assert.Equal(650, list.Bounds.Width);
            await SelectWorkViewAsync(WorkView.Advanced);
            Assert.Equal(330, list.Bounds.Width);

            async Task SelectWorkViewAsync(WorkView view)
            {
                modes.SelectedItem = Assert.Single(modes.Items.OfType<ComboBoxItem>(), item => Equals(item.Tag, view.ToSetting()));
                await BooruTestSupport.WaitAsync(() => page.ReaderWorkView == view, $"archive work view {view}");
                Settle(window);
            }
        });

        await WithArchiveAsync(profile.Path, CheckSavedWidth);

        using TempDirectory imported = new();
        File.WriteAllText(Path.Combine(imported.Path, "settings.json"),
            SettingsTransfer.Export(Config.LoadConfig(profile.Path), "1.1.2").ToJsonString());
        await WithArchiveAsync(imported.Path, CheckSavedWidth);
    });

    private static async Task WithArchiveAsync(string profile, Func<MainWindow, DesktopSessionHost, Task> exercise)
    {
        await using DesktopSessionHost session = new(profile, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
        await using MainWindow window = new(session) { Classes = { MotionPolicy.OffClass } };
        window.Show();
        window.PlatformImpl!.Activated?.Invoke();
        await window.StartupTimingRestoreTask;
        BooruReviewTestPlatform.Resize(window, 1920);
        Button archive = FindByAutomationId<Button>(window, "ArchiveNavigationButton");
        Assert.True(archive.IsEffectivelyEnabled);
        archive.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await BooruTestSupport.WaitAsync(() => window.ArchivePage.IsEffectivelyVisible && !window.ArchivePage.IsBusy, "archive table ready");
        Settle(window);
        Assert.True(FindByAutomationId<DataGrid>(window.ArchivePage, "ArchiveThreadTable").IsEffectivelyVisible);
        await exercise(window, session);
    }

    private static Task CheckSavedWidth(MainWindow window, DesktopSessionHost session)
    {
        Assert.Equal(330, FindByAutomationId<Grid>(window.ArchivePage, "ArchiveListPane").Bounds.Width);
        return Task.CompletedTask;
    }

    private static T FindByAutomationId<T>(Control root, string automationId) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(),
            control => ControlAutomationPeer.CreatePeerForElement(control).GetAutomationId() == automationId);

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
