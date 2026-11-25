using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
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
            ResourceId leadId = thread.Summary.Id with { Thread = "51" };
            if (results)
            {
                var lead = new ArchivedThreadView(thread.Summary with
                {
                    Id = leadId, Url = "https://offline.invalid/thread/51", Title = "Second result"
                }, [new CapturedPost("51", 0, PostFieldPresence.All, Html: "Second result")]);
                await RateAndTagWindowFixture.SeedAsync(profile.Path, lead);
            }
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
                Control row;
                if (results)
                {
                    var primary = list.Items.OfType<ArchiveThreadRow>().Single(hit => hit.Summary.Id == thread.Summary.Id);
                    var lead = list.Items.OfType<ArchiveThreadRow>().Single(hit => hit.Summary.Id == leadId);
                    list.SelectedItem = primary;
                    await BooruTestSupport.WaitAsync(() => !page.IsBusy, "the primary result preview");
                    window.UpdateLayout();
                    row = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(lead));
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Point point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
                    var toggle = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
                    window.MouseDown(point, MouseButton.Left, toggle);
                    window.MouseUp(point, MouseButton.Left, toggle);
                    await BooruTestSupport.WaitAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == leadId,
                        "the added lead result preview");
                    Assert.Same(primary, list.SelectedItem);
                    AssertSelection();
                }
                else
                {
                    list.SelectedItem = list.Items.OfType<ArchivePostRow>().Single(post => post.Id == "44");
                    await BooruTestSupport.WaitAsync(() => !page.IsBusy, "the selected thread preview");
                    window.UpdateLayout();
                    row = Assert.IsAssignableFrom<Control>(list.ContainerFromItem(list.SelectedItem!));
                }
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
                if (results) AssertSelection();

                void AssertSelection() => Assert.True(new[] { thread.Summary.Id, leadId }.ToHashSet()
                    .SetEquals(list.SelectedItems!.Cast<ArchiveThreadRow>().Select(hit => hit.Summary.Id)));
            }
            finally
            {
                page.EndLifetime();
                window.Close();
            }
        });

    private static string? Id(Control control) => ControlAutomationPeer.CreatePeerForElement(control).GetAutomationId();
}
