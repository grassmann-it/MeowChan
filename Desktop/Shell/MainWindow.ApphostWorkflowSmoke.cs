using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan;

public partial class MainWindow
{
    // This runs inside the actual published executable, not a separately built App/MainWindow.
    // Only the opt-in descriptor creates its isolated transport. Normal startup cannot select it.
    internal async Task CompleteApphostWorkflowSmokeAsync(ApphostWorkflowSmoke fixture)
    {
        List<string> checks = new();
        List<object> exports = new();
        object? externalDispatch = null;
        object? galleryLoad = null;
        bool completed = false;
        string? failure = null;
        string saved = StorageLayout.GetMediaPath(fixture.DownloadRoot, "4chan", "g", "123", "777", ".png");
        double renderScaling = RenderScaling;
        // macOS uses a thin apphost and symlinked managed payload. Record the actual
        // loaded assembly locations so the runner can bind their canonical files to
        // the complete extracted archive. Single-file publishes have empty locations.
#pragma warning disable IL3000 // Empty Location intentionally omits embedded assemblies; the runner binds their single-file apphost instead.
        var managedAssemblies = new[] { typeof(App).Assembly, typeof(FourChanImageBoard).Assembly }
            .Where(assembly => !string.IsNullOrEmpty(assembly.Location))
            .Select(assembly =>
            {
                using FileStream bytes = File.OpenRead(assembly.Location);
                return new { name = assembly.GetName().Name, path = assembly.Location,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
            }).ToArray();
#pragma warning restore IL3000
        void SaveEvidence() => File.WriteAllText(Path.Combine(fixture.Profile, "apphost-workflow.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, completed, failure, phase = fixture.Phase, checks, renderScaling,
            processId = Environment.ProcessId, workingDirectory = Environment.CurrentDirectory, profile = fixture.Profile,
            appBaseDirectory = AppContext.BaseDirectory, managedAssemblies,
            assemblyInformationalVersion = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            frameworkDescription = RuntimeInformation.FrameworkDescription, runtimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            transport = new { network = "blocked-by-terminal-fixture-handler", fixture.Http.MediaRequests,
                fixture.Http.CancelledRequests, fixture.Http.UnexpectedRequests },
            savedMediaSha256 = File.Exists(saved) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(saved))) : null,
            exports, externalDispatch, galleryLoad,
            scope = "actual extracted production app; injected offline HTTP responses; real native clipboard; native fallback state; intercepted export-folder target, no file-manager/browser/portal acceptance claim"
        }, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            await _restoreTask;
            RequireWorkflow(_restoreCompleted && _session.State == DesktopSessionState.Ready && IsVisible,
                "Packaged main window/session did not become ready.");
            renderScaling = RenderScaling;
            RequireWorkflow(double.IsFinite(renderScaling) && renderScaling > 0
                && (!OperatingSystem.IsLinux() || Math.Abs(renderScaling - fixture.ExpectedScale) < 0.01),
                "The requested isolated display scale was not observed.");
            if (fixture.Phase is "download" or "shutdown")
            {
                this.FindControl<TextBox>("UrlInput")!.Text = ApphostWorkflowSmoke.ThreadUrl;
                ClickWorkflow(this.FindControl<Button>("AddUrlButton")!);
                await WaitWorkflowAsync(() => UrlList.Count == 1 && UrlList[0].Active == 1 && fixture.Http.MediaRequests == 1,
                    "real Add/provider/queue admission");
                RequireWorkflow(!File.Exists(saved), "A blocked response published media before its bytes arrived.");
                if (fixture.Phase == "shutdown")
                {
                    await CloseWorkflowNormallyAsync();
                    RequireWorkflow(fixture.Http.CancelledRequests == 1 && !File.Exists(saved),
                        "Normal shutdown did not cancel the active transfer without publishing it.");
                    checks.Add("active-transfer-shutdown-disposed");
                }
                else
                {
                    galleryLoad = await CheckWorkflowGalleryUnderLoadAsync(fixture);
                    checks.Add("gallery-navigation-active-download");
                    await WaitWorkflowAsync(() => !RowCommandBusy && UrlList.Count == 1,
                        "complete uncovered-thread frame before watch selection");
                    ThreadRow row = UrlList[0];
                    this.FindControl<DataGrid>("ThreadsDataGrid")!.SelectedItem = row;
                    ClickWorkflow(this.FindControl<Button>("PauseButton")!);
                    await WaitWorkflowAsync(() => _session.IsPaused && row.Active == 0 && fixture.Http.CancelledRequests == 1
                        && this.FindControl<Button>("PauseButton")!.IsEnabled, "pause cancels active transfer");
                    RequireWorkflow(!File.Exists(saved), "Pausing an incomplete response published a file.");
                    checks.Add("download-paused");
                    ClickWorkflow(this.FindControl<Button>("PauseButton")!);
                    await WaitWorkflowAsync(() => !_session.IsPaused && row.Active == 1 && fixture.Http.MediaRequests == 2,
                        "resume admits the same transfer");
                    checks.Add("download-resumed");
                    fixture.Http.AllowMedia();
                    await WaitWorkflowAsync(() => row.CompletedMedia == 1 && row.Active == 0 && row.Queued == 0,
                        "verified download result");
                    RequireWorkflow(File.Exists(saved) && File.ReadAllBytes(saved).SequenceEqual(ApphostWorkflowSmoke.Payload)
                        && _session.ReceivedMediaBytes == ApphostWorkflowSmoke.Payload.Length, "Downloaded media bytes/telemetry differ.");
                    checks.Add("download-verified");

                    IClipboard clipboard = Clipboard ?? throw new InvalidOperationException("The packaged native clipboard is unavailable.");
                    await clipboard.SetTextAsync("meowchan-workflow-owned-sentinel");
                    DataGrid selectedThreads = this.FindControl<DataGrid>("ThreadsDataGrid")!;
                    // Clipboard access yields; wait for the same complete-frame command
                    // boundary as a user before preparing the selected-row context menu.
                    await WaitWorkflowAsync(() => !_sessionRowsApplying && selectedThreads.SelectedItems.Contains(row),
                        "complete selected-thread frame before native Copy command");
                    // Exercise the native keyboard path, including its pre-open menu preparation.
                    selectedThreads.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
                    MenuItem copyLinks = selectedThreads.ContextMenu!.Items.OfType<MenuItem>()
                        .Single(item => item.Tag as string == "selection.copy-links");
                    RequireWorkflow(copyLinks.IsEffectivelyEnabled, "The selected-thread Copy command is unavailable.");
                    copyLinks.Command!.Execute(null);
                    selectedThreads.ContextMenu.Close();
                    await WaitWorkflowAsync(async () => await clipboard.TryGetTextAsync() == ApphostWorkflowSmoke.ThreadUrl,
                        "native clipboard round trip through the Copy command");
                    checks.Add("native-clipboard-roundtrip");

                    // Exercise the production unavailable-tray branch with no fake tray/menu services.
                    DisposeTrayIcon();
                    MinimizeWindow(toTray: true);
                    await WaitWorkflowAsync(() => WindowState == WindowState.Minimized && IsVisible && ShowInTaskbar,
                        "unavailable tray retains the native taskbar route");
                    WindowState = WindowState.Normal; Show(); Activate();
                    await WaitWorkflowAsync(() => WindowState == WindowState.Normal && IsVisible && ShowInTaskbar,
                        "native restore after unavailable tray");
                    checks.Add("tray-fallback");
                    Width = 640; Height = 480;
                    await WaitWorkflowAsync(() => WorkflowRequestedSize(640, 480),
                        "compact native client bounds");
                    if (Screens.ScreenFromWindow(this) is { } screen && FrameSize is Size frame)
                        RequireWorkflow(frame.Width * RenderScaling <= screen.WorkingArea.Width + 1
                            && frame.Height * RenderScaling <= screen.WorkingArea.Height + 1,
                            "The compact native frame exceeds the screen working area.");
                    RequireWorkflow(WorkflowControlVisible(this.FindControl<Button>("AddUrlButton")!)
                        && WorkflowControlVisible(this.FindControl<Button>("PauseButton")!), "Compact primary commands leave the window.");
                    checks.Add("compact-window");
                    CaptureWorkflow(fixture.Profile, "compact");

                    ResourceId id = new("4chan", "g", "123");
                    await WaitWorkflowAsync(async () =>
                    {
                        await using ArchiveReader reader = await _session.OpenArchiveReaderAsync();
                        return (await reader.ReadThreadAsync(id))?.Summary.PostCount == 1;
                    }, "archived captured post");
                    ClickWorkflow(this.FindControl<Button>("ArchiveNavigationButton")!);
                    await ArchivePage.OpenExportForThreadAsync(id, this.FindControl<DataGrid>("ThreadsDataGrid"));
                    await WaitWorkflowAsync(() => ArchivePage.FindControl<Button>("ArchiveExportStart")!.IsEffectivelyEnabled,
                        "export dialog readiness");
                    string target = Path.Combine(fixture.Root, "export-one");
                    ArchivePage.FindControl<TextBox>("ArchiveExportDestination")!.Text = target;
                    ArchivePage.FindControl<CheckBox>("ArchiveExportMedia")!.IsChecked = true;
                    await RunWorkflowExportAsync(target, 1, exports);
                    string? handedOff = null;
                    ArchivePage.OpenExportFolder = directory => { handedOff = directory; return Task.CompletedTask; };
                    ArchivePage.FindControl<TextBox>("ArchiveExportDestination")!.Text = Path.Combine(fixture.Root, "not-the-result");
                    ClickWorkflow(ArchivePage.FindControl<Button>("ArchiveExportOpen")!);
                    await WaitWorkflowAsync(() => handedOff != null, "export folder command");
                    RequireWorkflow(ApphostWorkflowSmoke.SamePath(handedOff, target), "Open folder used draft text instead of its published result.");
                    externalDispatch = new { kind = "intercepted-export-command", target = handedOff,
                        nativeFileManagerOpened = false, nativeBrowserOpened = false };
                    checks.Add("external-handoff");
                    RequireWorkflow(WorkflowControlVisible(ArchivePage.FindControl<Button>("ArchiveExportStart")!)
                        && WorkflowControlVisible(ArchivePage.FindControl<Button>("ArchiveExportClose")!),
                        "Compact export actions leave the native client viewport.");
                    CaptureWorkflow(fixture.Profile, "export");
                    ClickWorkflow(ArchivePage.FindControl<Button>("ArchiveExportClose")!);
                    await ArchivePage.OpenExportForThreadAsync(id, null);
                    await WaitWorkflowAsync(() => ArchivePage.FindControl<RadioButton>("ArchiveExportUpdate")!.IsEffectivelyEnabled,
                        "persistent existing export lookup");
                    ArchivePage.FindControl<RadioButton>("ArchiveExportUpdate")!.IsChecked = true;
                    ArchivePage.FindControl<CheckBox>("ArchiveExportBackup")!.IsChecked = true;
                    await RunWorkflowExportAsync(target, 2, exports);
                    ClickWorkflow(ArchivePage.FindControl<Button>("ArchiveExportClose")!);
                    await ArchivePage.OpenExportForThreadAsync(id, null);
                    ArchivePage.FindControl<RadioButton>("ArchiveExportNew")!.IsChecked = true;
                    string secondTarget = Path.Combine(fixture.Root, "export-two");
                    ArchivePage.FindControl<TextBox>("ArchiveExportDestination")!.Text = secondTarget;
                    await RunWorkflowExportAsync(secondTarget, 1, exports);
                    ArchiveExportInspection old = await ArchiveExportMetadataFile.InspectAsync(target, id);
                    RequireWorkflow(old.Metadata.Generation == 2 && old.ModifiedPaths.Count == 0 && old.ExtraPaths.Count == 0,
                        "Creating a new export modified the preceding export.");
                    checks.Add("exports-created-updated-new");
                    ClickWorkflow(ArchivePage.FindControl<Button>("ArchiveExportClose")!);
                    await _session.SaveViewPreferencesAsync(config =>
                    {
                        config.GalleryPlaybackMode = "Slideshow"; config.GallerySlideshowSeconds = 1;
                        config.GalleryMediaFocus = true; config.GalleryOverlayControls = true;
                    });
                    RequireWorkflow(Config.LoadConfig(fixture.Profile).GalleryPlaybackMode == "Slideshow", "Playback choices were not durably saved.");
                    checks.Add("gallery-preferences-persisted");
                    await CloseWorkflowNormallyAsync();
                }
            }
            else if (fixture.Phase == "restore")
            {
                await WaitWorkflowAsync(() => UrlList.Count == 1 && UrlList[0].CompletedMedia == 1 && UrlList[0].Active == 0,
                    "fresh process watch/media restore");
                RequireWorkflow(fixture.Http.MediaRequests == 0 && _session.ReceivedMediaBytes == 0
                    && File.ReadAllBytes(saved).SequenceEqual(ApphostWorkflowSmoke.Payload), "Fresh restore downloaded or changed verified bytes.");
                checks.Add("restored-media-no-redownload");
                RequireWorkflow(_config.GalleryPlaybackMode == "Slideshow" && _config.GallerySlideshowSeconds == 1
                    && _config.GalleryMediaFocus && _config.GalleryOverlayControls && _gallery == null,
                    "Fresh process lost gallery preferences or opened a slideshow automatically.");
                await OpenGalleryAsync(new ResourceId("4chan", "g", "123"));
                GalleryViewer restoredGallery = _gallery ?? throw new InvalidOperationException("Restored gallery did not open.");
                await WaitWorkflowAsync(() => restoredGallery.GetGalleryVisualDescendants().OfType<Image>()
                    .Any(control => control.Name == "GalleryImage" && control.Source is Bitmap), "restored gallery image");
                var originalKey = restoredGallery.CaptureState().Key;
                await Task.Delay(1400);
                RequireWorkflow(MediaSmokeControl<ToggleButton>(restoredGallery, "GallerySlideshowToggle").IsChecked != true
                    && restoredGallery.CaptureState().Key == originalKey, "Restored slideshow started without a user action.");
                CloseGallery(); await GalleryCleanupTask;
                checks.Add("gallery-preferences-restored-no-autorun");
                // Verify export history survived an actual process boundary as well.
                await ArchivePage.OpenExportForThreadAsync(new ResourceId("4chan", "g", "123"), null);
                RequireWorkflow(ArchivePage.FindControl<RadioButton>("ArchiveExportUpdate")!.IsEnabled,
                    "Fresh process did not recover the preceding export history.");
                ClickWorkflow(ArchivePage.FindControl<Button>("ArchiveExportClose")!);
                await WaitWorkflowAsync(() => !RowCommandBusy && !_selectionCommandRunning && UrlList.Count == 1,
                    "complete restored-thread frame before Remove command");
                this.FindControl<DataGrid>("ThreadsDataGrid")!.SelectedItem = UrlList.Single();
                await RemoveDownloadSelectionAsync(false);
                await WaitWorkflowAsync(() => UrlList.Count == 0, "remove selected watch");
                RequireWorkflow(File.Exists(saved) && File.ReadAllBytes(saved).SequenceEqual(ApphostWorkflowSmoke.Payload),
                    "Remove watch deleted local media.");
                checks.Add("remove-keeps-local-media");
                await CloseWorkflowNormallyAsync();
            }
            else
            {
                RequireWorkflow(UrlList.Count == 0 && fixture.Http.MediaRequests == 0
                    && File.ReadAllBytes(saved).SequenceEqual(ApphostWorkflowSmoke.Payload), "Removed watch returned or local media changed after restart.");
                checks.Add("removed-watch-not-restored");
                await CloseWorkflowNormallyAsync();
            }
            RequireWorkflow(_allowClose && !IsVisible && _session.State == DesktopSessionState.Stopped,
                "Native close did not finish the production session lifetime.");
            checks.Add("normal-shutdown");
            using (ProfileLease lease = ProfileLease.Acquire(fixture.Profile)) { }
            checks.Add("lease-released");
            RequireWorkflow(fixture.Http.UnexpectedRequests == 0, "An unexpected URI attempted to leave the fixture contract.");
            completed = true;
        }
        catch (Exception exception) { failure = exception.ToString(); throw; }
        finally { SaveEvidence(); }
    }

    private async Task RunWorkflowExportAsync(string target, int generation, List<object> evidence)
    {
        await WaitWorkflowAsync(() => ArchivePage.FindControl<Button>("ArchiveExportStart")!.IsEnabled, "export command enabled");
        ClickWorkflow(ArchivePage.FindControl<Button>("ArchiveExportStart")!);
        await WaitWorkflowAsync(() => !ArchivePage.IsExportRunning && ArchivePage.LastExport?.Metadata?.Generation == generation
            && ApphostWorkflowSmoke.SamePath(ArchivePage.LastExport.Directory, target), "successful export generation " + generation);
        ArchiveHtmlExportResult result = ArchivePage.LastExport!;
        RequireWorkflow(result.PostCount == 1 && result.FileCount == 1 && result.Omissions.Count == 0,
            "The packaged export did not contain its captured post and verified image.");
        ArchiveExportInspection inspected = await ArchiveExportMetadataFile.InspectAsync(target, new ResourceId("4chan", "g", "123"));
        RequireWorkflow(inspected.ModifiedPaths.Count == 0 && inspected.ExtraPaths.Count == 0, "Published export inventory differs from its recorded bytes.");
        evidence.Add(new { target, generation, inspected.Metadata.ExportId, files = inspected.Metadata.Files });
    }
    private async Task CloseWorkflowNormallyAsync()
    {
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler handler = (_, _) => closed.TrySetResult();
        Closed += handler;
        try
        {
            // Public native window close enters the real Closing event and asynchronous cleanup path.
            Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { Closed -= handler; }
    }
    private bool WorkflowControlVisible(Control control)
    {
        Point? origin = control.TranslatePoint(new Point(), this);
        return control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 && origin is Point point
            && point.X >= -1 && point.Y >= -1 && point.X + control.Bounds.Width <= Bounds.Width + 1
            && point.Y + control.Bounds.Height <= Bounds.Height + 1;
    }
    private bool WorkflowRequestedSize(double width, double height)
    {
        Size client = Bounds.Size;
        Size frame = FrameSize ?? client;
        bool Matches(double actual, double requested, double minimum, double inset) =>
            Math.Abs(actual - requested) <= 1 || (OperatingSystem.IsWindows() && Math.Abs(requested - minimum) <= 0.01
                && actual >= requested && actual - requested <= Math.Max(0, inset) + 1);
        return Matches(client.Width, width, MinWidth, frame.Width - client.Width)
            && Matches(client.Height, height, MinHeight, frame.Height - client.Height);
    }
    private void CaptureWorkflow(string profile, string name)
    {
        using RenderTargetBitmap image = new(new PixelSize((int)Math.Ceiling(Bounds.Width * RenderScaling),
            (int)Math.Ceiling(Bounds.Height * RenderScaling)), new Vector(96 * RenderScaling, 96 * RenderScaling));
        image.Render(this); image.Save(Path.Combine(profile, "apphost-workflow-" + name + ".png"), PngBitmapEncoderOptions.Default);
    }
    private static void ClickWorkflow(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void RequireWorkflow(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static Task WaitWorkflowAsync(Func<bool> condition, string stage) => WaitWorkflowAsync(() => Task.FromResult(condition()), stage);
    private static async Task WaitWorkflowAsync(Func<Task<bool>> condition, string stage)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Packaged workflow timed out: " + stage);
            await Task.Delay(50);
        }
    }
}
