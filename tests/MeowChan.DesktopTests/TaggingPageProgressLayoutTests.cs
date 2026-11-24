using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
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
public sealed class TaggingPageProgressLayoutTests
{
    private static readonly double[] Widths = [760, 1440];
    private static readonly string[] ProgressKeys =
        ["tagging.page.progress.results", "tagging.page.progress", "tagging.page.progressName", "tagging.page.percent"];

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void TheHostedProgressFitsAndFollowsTheLanguage(double scale) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(ReaderFonts.ManagerOptions());
        using TempDirectory profile = new();
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1, WorkspaceMode = "Advanced"
        }.SaveConfig(profile.Path);
        BooruComponentDispatcher.Wait(ExerciseAsync());

        async Task ExerciseAsync()
        {
            await using DesktopSessionHost session = new(profile.Path, [],
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await session.StartAsync();
            TagQueueItem[] items = Enumerable.Range(1, 201).Select(Item).ToArray();
            await session.EnqueueTagQueueAsync(items);
            await session.SetTagQueueStatesAsync([KeyValuePair.Create(items[0].Key, TagQueueState.Done),
                KeyValuePair.Create(items[1].Key, TagQueueState.Skipped)]);
            await using MainWindow window = new(session) { Height = 640, Classes = { MotionPolicy.OffClass } };
            window.Show();
            window.SetRenderScaling(scale);
            Assert.Equal(scale, window.RenderScaling);
            Button footer = window.FindControl<Button>("TaggingNavigationButton")!;
            await BooruTestSupport.WaitAsync(() => footer.IsEffectivelyEnabled, "Tagging navigation is enabled");
            footer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await BooruTestSupport.WaitAsync(() => window.TaggingPage is { IsBusy: false, Queue.Count: 201 }, "Tagging queue is loaded");

            TaggingPage page = window.TaggingPage!;
            TaggingHeader header = page.Header;
            ProgressBar bar = header.GetVisualDescendants().OfType<ProgressBar>().Single();
            TextBlock count = page.ProgressText;
            string[] languages = ["en", "de", .. Texts.BundledPackCodes()];
            Assert.Equal(12, languages.Distinct().Count());

            foreach (double width in Widths)
            {
                BooruReviewTestPlatform.Resize(window, width);
                Assert.InRange(window.Bounds.Width, width - 1 / scale, width + 1 / scale);
                page.StartButton.Focus(NavigationMethod.Tab);
                Assert.True(page.StartButton.IsFocused);
                ToolTip.SetIsOpen(bar, true);
                Assert.True(ToolTip.GetIsOpen(bar));
                foreach (string language in languages)
                {
                    ReaderFonts.Apply(Application.Current!, language);
                    Texts.SetLanguage(language);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();

                    Assert.Equal(language, Texts.Language);
                    foreach (string key in ProgressKeys)
                        Assert.False(string.IsNullOrWhiteSpace(Texts.Own(key)), language + " · " + key);
                    string label = Texts.Count("tagging.page.progress.results", 201, 2, 201);
                    string breakdown = Texts.T("tagging.page.progress", 199, 1, 1);
                    string percent = Texts.T("tagging.page.percent", 0d);
                    string name = Texts.T("tagging.page.progressName");
                    Assert.Equal(label, count.Text);
                    Assert.Equal(name + " · " + label, AutomationProperties.GetName(count));
                    Assert.Equal(breakdown, AutomationProperties.GetHelpText(count));
                    Assert.Equal(breakdown, ToolTip.GetTip(count));
                    Assert.Equal(name, AutomationProperties.GetName(bar));
                    Assert.Equal(percent + " · " + breakdown, ToolTip.GetTip(bar));
                    Assert.Equal(label + " · " + percent + " · " + breakdown, AutomationProperties.GetHelpText(bar));
                    Assert.Equal(200d / 201, bar.Value, 8);
                    Popup popup = Assert.Single(window.OpenedPopups, popup => popup.Child is ToolTip);
                    Assert.Equal(percent + " · " + breakdown, Assert.IsType<ToolTip>(popup.Child).Content);

                    CheckGeometry(header, bar, count, page.StartButton, $"{language}/{width}/{scale}");
                    if (width == 760)
                    {
                        // Exercise the widest numeric format in the arranged header without persisting a 50,000-row queue.
                        count.Text = Texts.Count("tagging.page.progress.results", Config.TagQueueLimitMaximum,
                            Config.TagQueueLimitMaximum, Config.TagQueueLimitMaximum);
                        window.UpdateLayout();
                        CheckGeometry(header, bar, count, page.StartButton, $"{language}/{width}/{scale} (queue limit)");
                        count.Text = label;
                    }
                }
                ToolTip.SetIsOpen(bar, false);
            }
        }
    });

    private static void CheckGeometry(TaggingHeader header, ProgressBar bar, TextBlock count, Button startButton, string stage)
    {
        Rect track = BoundsIn(bar, header);
        Rect number = BoundsIn(count, header);
        Rect start = BoundsIn(startButton, header);
        Rect available = new Rect(header.Bounds.Size).Inflate(1);
        Assert.True(bar.IsEffectivelyVisible && count.IsEffectivelyVisible, stage);
        Assert.InRange(track.Height, 3, 5);
        Assert.True(track.Width >= 63 && available.Contains(track), $"{stage} · {track}");
        Assert.True(available.Contains(number), $"{stage} · {number}");
        Assert.InRange(number.X - track.Right, 3, 5);
        Assert.InRange(track.X - start.Right, 7, 9);
        Assert.InRange(Math.Abs(number.Center.Y - track.Center.Y), 0, 1);
        Assert.InRange(count.TextLayout.WidthIncludingTrailingWhitespace, 1, number.Width + 1);
        Assert.False(Assert.Single(count.TextLayout.TextLines).HasCollapsed, stage);
    }

    private static Rect BoundsIn(Control control, Control ancestor) =>
        new(control.TranslatePoint(default, ancestor)!.Value, control.Bounds.Size);

    private static TagQueueItem Item(int number) => new(new ResourceId("4chan", "g", "123"), "/tagging/fixture",
        "asset-" + number, number.ToString("x64"), number, "media-" + number, "original", "1", null, null, null,
        false, null, "file-" + number + ".png", SavedMediaKind.Image, TagQueueState.Open, DateTimeOffset.UnixEpoch.AddSeconds(number));
}
