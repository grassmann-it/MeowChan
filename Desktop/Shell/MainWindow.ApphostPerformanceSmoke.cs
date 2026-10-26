using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Diagnostics;

namespace MeowChan;

public partial class MainWindow
{
    private async Task<object> CheckWorkflowGalleryUnderLoadAsync(ApphostWorkflowSmoke fixture)
    {
        List<double> navigationMilliseconds = new();
        List<string> presentedPosts = new();
        using var diagnostics = PerformanceDiagnostics.BeginRun(Guid.NewGuid().ToString("N"),
            Environment.GetEnvironmentVariable("MEOWCHAN_PERF_SOURCE_HASH")
                ?? "assembly-mvid:" + typeof(App).Module.ModuleVersionId,
            ApphostWorkflowGalleryFixture.FixtureHash, capacity: 64);
        using UiResponsivenessProbe probe = new(action => Dispatcher.UIThread.Post(action),
            TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(5),
            report => Console.Error.WriteLine("APPHOST gallery-load unanswered UI: " + report.UnansweredMilliseconds));
        probe.Start();
        try
        {
            await OpenGalleryAsync(ApphostWorkflowGalleryFixture.Id);
            GalleryViewer viewer = (GalleryViewer)this.FindControl<ContentControl>("GalleryHost")!.Content!;
            Image image = viewer.GetGalleryVisualDescendants().OfType<Image>().Single(control => control.Name == "GalleryImage");
            await WaitWorkflowAsync(() => image.Source is Bitmap, "initial verified gallery image during download");
            for (int index = 0; index < 8; index++)
            {
                bool portrait = index % 2 == 0;
                Bitmap? previous = image.Source as Bitmap;
                long start = Stopwatch.GetTimestamp();
                Button move = viewer.GetGalleryVisualDescendants().OfType<Button>()
                    .Single(control => control.Name == (portrait ? "GalleryNext" : "GalleryPrevious"));
                ClickWorkflow(move);
                string expectedPost = portrait ? "9901" : "9900";
                await WaitWorkflowAsync(() => viewer.CaptureState().PostId == expectedPost
                    && image.Source is Bitmap current && !ReferenceEquals(current, previous)
                    && (portrait ? current.PixelSize.Height > current.PixelSize.Width
                        : current.PixelSize.Width > current.PixelSize.Height), "distinct decoded gallery target under active load");
                navigationMilliseconds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                presentedPosts.Add(expectedPost);
                RequireWorkflow(UrlList.Single().Active == 1 && fixture.Http.MediaRequests == 1
                    && fixture.Http.CancelledRequests == 0,
                    "Gallery navigation paused or lost the fixture's independently active download.");
                await Task.Delay(30);
            }
        }
        finally
        {
            CloseGallery();
            await GalleryCleanupTask;
        }
        UiResponsivenessSummary heartbeat = probe.Summarize();
        RequireWorkflow(heartbeat.Samples >= 8 && heartbeat.HardStalls == 0
            && heartbeat.OngoingSilenceMilliseconds < 1000
            && heartbeat.MaxGapMilliseconds < 1000, "The apphost did not answer UI pings during gallery/download load.");
        return new
        {
            fixture = "two own verified PNG targets with opposite aspect ratios; independently held HTTP response",
            scope = "decoded target identity and dimensions, not screen presentation timing; no USB or slow-commit claim",
            navigationMilliseconds, presentedPosts, heartbeat, diagnostics = diagnostics.Snapshot(),
            activeDownloadThroughout = true,
            diagnosticGapTriggerMilliseconds = 1000
        };
    }
}
