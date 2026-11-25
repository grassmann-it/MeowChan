using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.WorkViews;
using Semi.Avalonia.DataGrid;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ArchiveExportOriginFocusTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EscapeReturnsToTheWorkItemForDirectAndOverflowedExports(bool results, bool overflow) =>
        BooruComponentDispatcher.Run(async () =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Application.Current!.Styles.Insert(1, new DataGridSemiTheme());
            using TempDirectory profile = new();
            using var media = await ArchiveAttachmentMatchingTests.Fixture.CreateAsync(mismatch: false);
            var thread = RateAndTagWindowFixture.View(media);
            await RateAndTagWindowFixture.SeedAsync(profile.Path, thread);
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile.Path, token), (_, _) => Task.CompletedTask,
                _ => { }, (_, _) => Task.FromResult(media.Snapshot))
            {
                IsAvailable = true, ArchiveColumnModePreference = WorkViewSetting.AdvancedName,
                LastExportDirectoryPreference = profile.Path
            };
            Window window = new() { Width = 1440, Height = 840, Content = page };
            try
            {
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                if (results) await page.ApplySearchAsync();
                else await page.LoadThreadAsync(thread.Summary.Id, preparedThread: thread);
                window.UpdateLayout();

                string barId = results ? "ArchiveResultActions" : "ReaderToolbar";
                var bar = Assert.Single(window.GetVisualDescendants().OfType<ActionToolBar>(), item => Id(item) == barId);
                string actionId = results ? "ArchiveResultsExport" : "ExportArchiveButton";
                var origin = Assert.Single(bar.Items.OfType<Button>(), item => Id(item) == actionId);
                bar.Width = overflow ? 100 : 3000;
                window.UpdateLayout();
                if (overflow)
                {
                    Assert.Contains(origin, bar.OverflowHost!.Children);
                    Assert.True(bar.More!.IsEffectivelyVisible);
                    Assert.False(bar.More.IsChecked == true);
                }
                else Assert.Contains(bar, origin.GetVisualAncestors());

                string listId = results ? "ArchiveThreads" : "ArchivePosts";
                var list = Assert.Single(window.GetVisualDescendants().OfType<ListBox>(), item => Id(item) == listId);
                Assert.True(list.ItemCount > 0);
                list.SelectedItem = results ? list.Items.Single() : list.Items.OfType<ArchivePostRow>().Single(post => post.Id == "44");
                await BooruTestSupport.WaitAsync(() => !page.IsBusy, "the selected thread preview");
                window.UpdateLayout();
                var row = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(list.SelectedItem!));
                Assert.True(row.Focus());

                if (results) await page.OpenExportForAllResultsAsync(origin);
                else await page.OpenExportForThreadAsync(thread.Summary.Id, origin);
                Assert.True(page.IsReaderDialogOpen);
                var destination = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), item => Id(item) == "ArchiveExportDestination");
                Assert.True(destination.IsKeyboardFocusWithin, $"Expected the export destination, focused {window.FocusManager!.GetFocusedElement()}.");
                Assert.False(list.IsKeyboardFocusWithin);
                RateAndTagWindowFixture.Escape(window);
                Assert.False(page.IsReaderDialogOpen);
                Assert.Same(row, window.FocusManager!.GetFocusedElement());
                Dispatcher.UIThread.RunJobs();
                Assert.Same(row, window.FocusManager.GetFocusedElement());
            }
            finally
            {
                page.EndLifetime();
                window.Close();
            }
        });

    private static string? Id(Control control) => ControlAutomationPeer.CreatePeerForElement(control).GetAutomationId();
}
