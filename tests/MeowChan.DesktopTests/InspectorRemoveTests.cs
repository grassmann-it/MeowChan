using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class InspectorRemoveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InspectorNamesAndStopsBothSelectedWatches(bool boards) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruComponentDispatcher.Wait(ExerciseAsync());

        async Task ExerciseAsync()
        {
            using TempDirectory profile = new();
            new Config
            {
                DownloadPath = Path.Combine(profile.Path, "Downloads"), DownloadsPaused = true,
                FirstRunSetupVersion = 1, WorkspaceMode = "Advanced", Language = "en"
            }.SaveConfig(profile.Path);
            await using DesktopSessionHost session = new(profile.Path, [new FourChanImageBoard()],
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await using MainWindow window = new(session) { Width = 1440, Height = 800, Classes = { MotionPolicy.OffClass } };
            window.Show();
            await window.StartupTimingRestoreTask;
            Assert.True(session.IsPaused);

            string[] paths = boards ? ["g/", "a/", "v/"] : ["g/thread/101", "g/thread/102", "g/thread/103"];
            List<long> ids = [];
            foreach (string path in paths)
                ids.Add(Assert.IsType<long>((await session.AddAsync("https://boards.4chan.org/" + path)).WatchId));

            window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = boards ? 1 : 0;
            DataGrid grid = window.FindControl<DataGrid>(boards ? "BoardsDataGrid" : "ThreadsDataGrid")!;
            await BooruTestSupport.WaitAsync(() => !window.SessionRowsApplying && grid.CollectionView.Cast<object>().Count() == 3,
                "all watches shown in Downloads");
            Dictionary<long, object> rows = grid.CollectionView.Cast<object>()
                .ToDictionary(row => boards ? ((BoardRow)row).WatchId : ((ThreadRow)row).WatchId);
            grid.SelectedItem = rows[ids[0]];
            window.FindControl<Button>("DetailsToggleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Button remove = window.FindControl<Button>("InspectorRemoveButton")!;
            AssertLabel("Stop watching");

            grid.SelectedItems.Add(rows[ids[1]]);
            AssertLabel("Stop watching (2)");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await BooruTestSupport.WaitAsync(() => RemainingIds().Length == 1, "the inspector to stop the selected watches");
            Assert.Equal(new[] { ids[2] }, RemainingIds());

            void AssertLabel(string expected)
            {
                window.UpdateLayout();
                Assert.True(remove.IsEffectivelyVisible && remove.IsEffectivelyEnabled);
                TextBlock label = Assert.Single(remove.GetVisualDescendants().OfType<TextBlock>());
                Assert.Equal(expected, label.Text);
                Assert.Equal(expected, AutomationProperties.GetName(remove));
            }

            long[] RemainingIds() => boards ? session.GetView().Boards.Select(row => row.WatchId).ToArray()
                : session.GetView().Threads.Select(row => row.WatchId).ToArray();
        }
    });
}
