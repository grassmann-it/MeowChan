using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class TaggingPagePanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownArrowShowsTheNextMediumInThePanel(bool grid) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        Texts.Initialize();
        Texts.SetLanguage("en");
        new Config { DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1 }.SaveConfig(profile.Path);
        BooruComponentDispatcher.Wait(ExerciseAsync());

        async Task ExerciseAsync()
        {
            await using DesktopSessionHost session = new(profile.Path, [],
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await session.StartAsync();
            await session.EnqueueTagQueueAsync(Enumerable.Range(1, 12).Select(Item).ToArray());
            using TaggingPage page = new(session);
            if (grid) page.RestoreViewMode(TaggingViewMode.Grid);
            Window window = new() { Width = 1400, Height = 900, Content = page, Classes = { MotionPolicy.OffClass } };
            window.Show();
            try
            {
                await page.ActivateAsync();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                ReaderMediaSidebar panel = Assert.Single(page.GetVisualDescendants().OfType<ReaderMediaSidebar>());
                Assert.True(panel.IsEffectivelyVisible);
                ListBox list = page.ShownList;
                ListBoxItem first = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
                Assert.True(first.Focus());
                Assert.Equal("file-1.png", panel.Title.Text);

                window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                ListBoxItem focused = Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement());
                int index = list.IndexFromContainer(focused);
                Assert.True(index > 0);
                Assert.Equal($"file-{index + 1}.png", panel.Title.Text);
                Assert.Equal($"{index + 1} / 12", panel.PositionPill.Bare);
                Assert.InRange(panel.Bounds.Width, 359.5, 360.5);
                Grid listArea = page.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "TaggingListArea");
                double panelTop = panel.TranslatePoint(default, page)!.Value.Y;
                Assert.InRange(panelTop - listArea.TranslatePoint(default, page)!.Value.Y, -0.5, 0.5);
                Assert.InRange(panelTop + panel.Bounds.Height - page.Bounds.Height, -0.5, 0.5);
                Assert.All(panel.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible),
                    button => Assert.False(button.Focusable, button.Name));

                ToggleButton toggle = page.GetVisualDescendants().OfType<ToggleButton>().Single(control => control.Name == "TaggingPanelToggle");
                GridSplitter handle = page.GetVisualDescendants().OfType<GridSplitter>().Single(control => control.Name == "TaggingPanelHandle");
                BooruReviewTestPlatform.Resize(window, 900);
                Assert.False(panel.IsEffectivelyVisible);
                Assert.False(handle.IsEffectivelyVisible);
                Assert.True(toggle.IsEffectivelyEnabled);
                Assert.True(toggle.IsChecked);
                Assert.Contains(Texts.T("tagging.page.panel.narrow"), Assert.IsType<string>(ToolTip.GetTip(toggle)));

                BooruReviewTestPlatform.Resize(window, 1400);
                Assert.True(panel.IsEffectivelyVisible);
                Rect header = page.Header.Bounds;
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Assert.False(panel.IsEffectivelyVisible);
                Assert.Equal(header, page.Header.Bounds);
            }
            finally { window.Close(); }
        }
    });

    private static TagQueueItem Item(int number) => new(new ResourceId("4chan", "g", "123"), "/tagging/fixture",
        "asset-" + number, number.ToString("x64"), number, "media-" + number, "original", "1", null, null, null,
        false, null, "file-" + number + ".png", SavedMediaKind.Image, TagQueueState.Open, DateTimeOffset.UnixEpoch.AddSeconds(number));
}
