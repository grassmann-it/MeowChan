using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

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
            page.ArchiveLayoutPreference = "Table";
            page.ApplyReaderMediaPreferences(ReaderMediaPreferences.From(new Config
            {
                ReaderMediaSidebar = true,
                ReaderMediaSidebarScope = Config.ReaderMediaSidebarScopeAlways
            }));
            Window window = new() { Content = page, Width = 1920, Height = 850 };
            try
            {
                window.Show();
                BooruComponentDispatcher.Wait(page.ActivateAsync());
                Settle(window);
                DataGrid table = page.FindControl<DataGrid>("ArchiveThreadTable")!;
                Control list = page.FindControl<Control>("ArchiveListPane")!;
                GridSplitter splitter = page.FindControl<GridSplitter>("ArchiveSplitter")!;
                SelectThread(first);

                double initialWidth = list.Bounds.Width;
                Point start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
                Point end = new(start.X - initialWidth + 460, start.Y);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                Settle(window);
                window.MouseUp(end, MouseButton.Left);
                Settle(window);

                double draggedWidth = list.Bounds.Width;
                Assert.InRange(draggedWidth, 459, 461);
                Assert.True(page.IsMediaSidebarShown);

                SelectThread(second);

                Assert.Equal(draggedWidth, list.Bounds.Width);
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

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
