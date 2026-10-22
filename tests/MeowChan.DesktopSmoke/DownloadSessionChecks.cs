using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Boards.FourChan;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopSmoke;

/// <summary>The normal application with a caller-owned offline transport, selected only by the smoke executable.</summary>
internal sealed class DownloadSessionSmokeApp : App
{
    private readonly HttpClient _client;
    internal DownloadSessionTransport Transport { get; } = new DownloadSessionTransport();

    public DownloadSessionSmokeApp() => _client = new HttpClient(Transport);

    protected override MainWindow CreateMainWindow()
    {
        DesktopSessionHost session = new DesktopSessionHost(Config.GetConfigDirectory(),
            new IImageBoard[] { new FourChanImageBoard(_client) });
        return new MainWindow(session);
    }

    internal MainWindow CreateRestoredWindow() => CreateMainWindow();
    internal void DisposeFixture()
    {
        Transport.AllowMedia();
        _client.Dispose();
    }
}

internal static class DownloadSessionChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync(MainWindow initial, IClassicDesktopStyleApplicationLifetime desktop)
    {
        DownloadSessionSmokeApp app = Application.Current as DownloadSessionSmokeApp
            ?? throw new InvalidOperationException("The offline download fixture application was not selected.");
        MainWindow current = initial;
        int geometryChanges = 0;
        void TraceGeometryChange(object? sender, AvaloniaPropertyChangedEventArgs change)
        {
            if (change.Property != Window.WidthProperty && change.Property != Window.HeightProperty
                && change.Property != Window.BoundsProperty && change.Property != Window.FrameSizeProperty
                && change.Property != Window.WindowStateProperty) return;
            if (++geometryChanges <= 32 && sender is MainWindow window)
                TraceLayout(window, $"native/property change {geometryChanges}: {change.Property.Name}");
        }
        void TraceSizeChange(object? sender, SizeChangedEventArgs change)
        {
            if (++geometryChanges <= 32 && sender is MainWindow window)
                TraceLayout(window, $"native size change {geometryChanges}: {change.PreviousSize} to {change.NewSize}");
        }
        initial.PropertyChanged += TraceGeometryChange;
        initial.SizeChanged += TraceSizeChange;
        string profile = Config.GetConfigDirectory();
        string downloadRoot = Config.LoadConfig(profile).DownloadPath;
        string savedFile = StorageLayout.GetMediaPath(downloadRoot, "4chan", "g", "123", "777", ".png");
        try
        {
            Require(Config.LoadConfig(profile).WorkspaceMode == "Barebones"
                && (initial.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectedItem as ComboBoxItem)?.Tag as string == "Barebones",
                "The new download profile did not start in Barebones view.");
            // Exercise the existing Simple-to-Barebones transition explicitly.
            TraceLayout(initial, "before Simple mode");
            await PresentationChecks.SwitchModeAsync(initial, "Simple");
            TraceLayout(initial, "after Simple mode");
            TextBox input = initial.FindControl<TextBox>("UrlInput")!;
            Button add = initial.FindControl<Button>("AddUrlButton")!;
            DataGrid grid = initial.FindControl<DataGrid>("ThreadsDataGrid")!;
            input.Text = "https://boards.4chan.org/g/thread/123";
            Click(add);
            await app.Transport.MediaRequested.WaitAsync(Timeout);
            await Program.WaitUntilAsync(() => initial.UrlList.Count == 1 && initial.UrlList[0].KnownMedia == 1
                && initial.UrlList[0].Active == 1 && add.IsEnabled);
            ThreadRow retained = initial.UrlList[0];
            bool wrongThread = false;
            retained.PropertyChanged += (_, _) => wrongThread |= !Dispatcher.UIThread.CheckAccess();
            grid.SelectedItem = retained;
            Require(retained.Title == DownloadSessionTransport.Title && retained.CompletedMedia == 0,
                "The UI did not receive the real provider title and in-flight queue state.");
            Require(!File.Exists(savedFile), "The gated media was marked saved before the transport returned its body.");
            await Program.WaitUntilAsync(() => grid.GetVisualDescendants().OfType<DataGridRow>()
                .Any(row => row.IsEffectivelyVisible && ReferenceEquals(row.DataContext, retained)));
            Require(Config.LoadConfig(profile).WorkspaceMode == "Simple"
                && Application.Current?.RequestedThemeVariant == ThemeVariant.Dark,
                "The real download did not start in the Simple dark presentation.");
            Program.CaptureWindow(initial, "smoke-download-simple.png");

            input.Text = "https://boards.4channel.org/g/thread/123/title#p124";
            Click(add);
            await Program.WaitUntilAsync(() => add.IsEnabled && string.IsNullOrEmpty(input.Text));
            Require(initial.UrlList.Count == 1 && ReferenceEquals(initial.UrlList[0], retained),
                "The alias Add command duplicated or replaced the real watch row.");

            const string draft = "https://unsupported.invalid/keep-this-draft";
            input.Text = draft;
            double simpleWidth = initial.Bounds.Width;
            TraceLayout(initial, "before Barebones mode with selected active row");
            await PresentationChecks.SwitchModeAsync(initial, "Barebones");
            TraceLayout(initial, "after Barebones mode with selected active row");
            await Program.WaitUntilAsync(() => initial.Bounds.Width < simpleWidth && grid.GetVisualDescendants().OfType<DataGridRow>()
                .Any(row => row.IsEffectivelyVisible && ReferenceEquals(row.DataContext, retained)));
            Require(input.Text == draft && initial.UrlList.Count == 1 && ReferenceEquals(initial.UrlList[0], retained)
                && ReferenceEquals(grid.SelectedItem, retained) && retained.Active == 1 && retained.CompletedMedia == 0
                && app.Transport.MediaRequests == 1 && !File.Exists(savedFile),
                "Barebones changed the pending input, selected watch or real in-flight media download.");
            Program.CaptureWindow(initial, "smoke-download-barebones.png");
            TraceLayout(initial, "before Advanced mode");
            await PresentationChecks.SwitchModeAsync(initial, "Advanced");
            TraceLayout(initial, "after Advanced mode");
            Click(initial.FindControl<Button>("ThemeToggleButton")!);
            await Program.WaitUntilAsync(() => Config.LoadConfig(profile).Theme == "Light" && add.IsEnabled
                && Application.Current?.RequestedThemeVariant == ThemeVariant.Light);
            Require(input.Text == draft && ReferenceEquals(grid.SelectedItem, retained),
                "Persisting presentation preferences lost draft input or the active download selection.");

            Border inspector = initial.FindControl<Border>("WorkspaceInspector")!;
            if (!inspector.IsVisible) Click(initial.FindControl<Button>("DetailsToggleButton")!);
            await Program.WaitUntilAsync(() => inspector.IsEffectivelyVisible
                && initial.FindControl<SelectableTextBlock>("InspectorDirectory")!.Text == retained.DownloadDirectory);
            Program.CaptureWindow(initial, "smoke-download-in-flight.png");

            SettingsEditor settings = initial.SettingsEditor;
            Click(initial.FindControl<Button>("SettingsNavigationButton")!);
            await Program.WaitUntilAsync(() => settings.IsEffectivelyVisible && settings.Bounds.Width > 0);
            string settingsDraft = Path.Combine(profile, "retained-settings-draft");
            settings.DownloadPath = settingsDraft;
            Require(retained.Active == 1 && retained.CompletedMedia == 0 && app.Transport.MediaRequests == 1,
                "Navigating to Settings changed the in-flight download.");
            Button pause = initial.FindControl<Button>("PauseButton")!;
            Click(pause);
            await app.Transport.MediaCancelled.WaitAsync(Timeout);
            await Program.WaitUntilAsync(() => initial.SessionHost.IsPaused && pause.IsEnabled && retained.Active == 0);
            Require(!File.Exists(savedFile) && retained.CompletedMedia == 0 && initial.SessionHost.ReceivedMediaBytes == 0,
                "Pausing the gated real request published a file or counted bytes that were never received.");
            await WindowChromeChecks.CheckReachableAsync(initial);
            initial.FindControl<MenuItem>("PauseMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => !initial.SessionHost.IsPaused && pause.IsEnabled
                && retained.Active == 1 && app.Transport.MediaRequests == 2);
            app.Transport.AllowMedia();
            await Program.WaitUntilAsync(() => retained.CompletedMedia == 1 && retained.Active == 0 && retained.Queued == 0);
            Require(initial.SessionHost.ReceivedMediaBytes == DownloadSessionTransport.Payload.Length,
                "Session transfer telemetry did not count the actual media response body.");
            await Program.WaitUntilAsync(() => initial.FindControl<TextBlock>("TransferSpeedLabel")!.Text is string speed
                && speed.StartsWith("↓ ", StringComparison.Ordinal) && speed != "↓ 0 B/s");
            Require(initial.SettingsPageVisible && settings.DownloadPath == settingsDraft,
                "A background download discarded the settings page or its draft.");
            Click(initial.FindControl<Button>("DownloadsNavigationButton")!);
            Require(!initial.SettingsPageVisible && ReferenceEquals(initial.SettingsEditor, settings)
                && inspector.IsVisible && ReferenceEquals(grid.SelectedItem, retained),
                "Returning to Downloads reconstructed controls or lost the inspector/selection.");
            Require(!wrongThread && ReferenceEquals(initial.UrlList.Single(), retained)
                && ReferenceEquals(grid.SelectedItem, retained) && input.Text == draft,
                "Real download updates crossed the UI thread or replaced selection/input.");
            Require(retained.SourceState == ThreadSourceState.Active, "Saving current media incorrectly ended source monitoring.");
            Require(app.Transport.MediaRequests == 2 && (await File.ReadAllBytesAsync(savedFile)).SequenceEqual(DownloadSessionTransport.Payload),
                "The real download queue did not produce exactly one verified fixture file.");
            string assignedDirectory = retained.DownloadDirectory;
            await Program.WaitUntilAsync(() => initial.FindControl<SelectableTextBlock>("InspectorProgress")!.Text == retained.Progress);
            Program.CaptureWindow(initial, "smoke-download-saved.png");
            Console.WriteLine("SMOKE download session: native Add/alias, real provider/queue/storage, retained page drafts during background download, in-flight preferences, dispatcher updates and verified saved file passed.");

            await CloseNormallyAsync(initial);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
                Console.WriteLine("SMOKE download session: first native close released the profile lease.");

            MainWindow restored = app.CreateRestoredWindow();
            restored.PropertyChanged += TraceGeometryChange;
            restored.SizeChanged += TraceSizeChange;
            current = restored;
            desktop.MainWindow = restored;
            restored.Show();
            await Program.WaitUntilAsync(() => restored.IsVisible && restored.Bounds.Width > 0
                && restored.FindControl<Button>("AddUrlButton")!.IsEnabled && restored.UrlList.Count == 1
                && restored.UrlList[0].KnownMedia == 1 && restored.UrlList[0].CompletedMedia == 1
                && restored.UrlList[0].Active == 0 && restored.UrlList[0].Queued == 0);
            ThreadRow recovered = restored.UrlList[0];
            Require(recovered.DownloadDirectory == assignedDirectory && recovered.Url == retained.Url,
                "Restart changed the durable watch identity or assigned directory.");
            Require(app.Transport.MediaRequests == 2 && (await File.ReadAllBytesAsync(savedFile)).SequenceEqual(DownloadSessionTransport.Payload),
                "Restoring verified local media issued another media request or changed the saved file.");
            Require(restored.SessionHost.ReceivedMediaBytes == 0,
                "Restoring local media was incorrectly counted as network download traffic.");
            Config preferences = Config.LoadConfig(profile);
            Require(preferences.Theme == "Light" && preferences.WorkspaceMode == "Advanced"
                && Application.Current?.RequestedThemeVariant == ThemeVariant.Light
                && (restored.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectedItem as ComboBoxItem)?.Tag as string == "Advanced",
                "The second native window did not restore presentation preferences.");
            Program.CaptureWindow(restored, "smoke-download-restored.png");
            restored.FindControl<DataGrid>("ThreadsDataGrid")!.SelectedItem = recovered;
            Click(restored.FindControl<Button>("DetailsToggleButton")!);
            Button check = restored.FindControl<Button>("InspectorCheckButton")!;
            Console.WriteLine("SMOKE restored inspector diagnostic: selection=" + restored.FindControl<DataGrid>("ThreadsDataGrid")!.SelectedItems.Count + "; inspector=" + restored.FindControl<Border>("WorkspaceInspector")!.IsVisible + "; checkvisible=" + check.IsEffectivelyVisible + "; checkenabled=" + check.IsEnabled + "; paused=" + restored.SessionHost.IsPaused);
            Program.CaptureWindow(restored, "smoke-download-inspector-selected.png");
            await Program.WaitUntilAsync(() => check.IsEffectivelyVisible && check.IsEnabled);
            Click(check);
            await Program.WaitUntilAsync(() => restored.GetVisualDescendants()
                .OfType<Avalonia.Controls.Notifications.NotificationCard>().Any(card => card.IsEffectivelyVisible
                    && card.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Texts.T("check.scheduled")
                        || text.Text == Texts.T("check.alreadyScheduled"))));
            Require(ReferenceEquals(restored.UrlList.Single(), recovered), "The real Check action replaced the restored watch row.");
            // The Check notification is already verified; dismiss it before archive visual checks.
            foreach (Avalonia.Controls.Notifications.NotificationCard notification in restored.GetVisualDescendants()
                .OfType<Avalonia.Controls.Notifications.NotificationCard>().ToArray())
                notification.Close();
            await Program.WaitUntilAsync(() => !restored.GetVisualDescendants()
                .OfType<Avalonia.Controls.Notifications.NotificationCard>()
                .Any(notification => notification.IsEffectivelyVisible));
            await ArchivePageChecks.RunAsync(restored, recovered.WatchId, new ResourceId("4chan", "g", "123"));
            await CloseNormallyAsync(restored);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
                Console.WriteLine("SMOKE download session: restart restored the real watch/media without another media request; second native close released the profile lease.");
        }
        finally
        {
            initial.PropertyChanged -= TraceGeometryChange;
            initial.SizeChanged -= TraceSizeChange;
            if (!ReferenceEquals(current, initial))
            {
                current.PropertyChanged -= TraceGeometryChange;
                current.SizeChanged -= TraceSizeChange;
            }
            // Release the fixture gate even if an assertion failed before media admission was allowed.
            app.Transport.AllowMedia();
            try
            {
                if (current.IsVisible) await CloseNormallyAsync(current);
            }
            finally { app.DisposeFixture(); }
        }
    }

    private static void TraceLayout(MainWindow window, string stage)
    {
        DataGrid? grid = window.FindControl<DataGrid>("ThreadsDataGrid");
        string columns = grid == null ? "unavailable" : string.Join(";", grid.Columns.Select(column =>
            $"{column.SortMemberPath}:visible={column.IsVisible},width={column.Width.Value}/{column.Width.UnitType},actual={column.ActualWidth},min={column.MinWidth}"));
        string? mode = (window.FindControl<ComboBox>("WorkspaceModeComboBox")?.SelectedItem as ComboBoxItem)?.Tag as string;
        Console.WriteLine($"SMOKE download layout {stage}: mode={mode}; "
            + $"state={window.WindowState}; requested={window.Width}x{window.Height}; minimum={window.MinWidth}x{window.MinHeight}; "
            + $"bounds={window.Bounds}; client={window.ClientSize}; frame={window.FrameSize}; desired={window.DesiredSize}; "
            + $"position={window.Position}; scale={window.RenderScaling}; working-area={window.Screens.ScreenFromWindow(window)?.WorkingArea}; "
            + $"grid-bounds={grid?.Bounds}; grid-desired={grid?.DesiredSize}; columns=[{columns}].");
    }

    private static async Task CloseNormallyAsync(MainWindow window)
    {
        TaskCompletionSource closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClosed(object? sender, EventArgs args) => closed.TrySetResult();
        window.Closed += OnClosed;
        try
        {
            Func<WindowCloseReason, bool> requestClose = window.PlatformImpl?.Closing
                ?? throw new InvalidOperationException("The native window close callback was not available.");
            requestClose(WindowCloseReason.WindowClosing);
            await closed.Task.WaitAsync(Timeout);
            Require(!window.IsVisible, "A native Closed event left the download window visible.");
        }
        finally { window.Closed -= OnClosed; }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

/// <summary>Every URL is intercepted here; no request can reach a network handler.</summary>
internal sealed class DownloadSessionTransport : HttpMessageHandler
{
    internal const string Title = "Offline native download fixture";
    internal static readonly byte[] Payload = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAACAAAAAYCAIAAAAUMWhjAAAAJElEQVR4nGMwmraFpohh1IJRC0YtGLVg1IJRC0YtGLVgaFgAACLJdD2wiEkfAAAAAElFTkSuQmCC");
    private readonly TaskCompletionSource _mediaAllowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _mediaRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _mediaCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _mediaRequests;

    internal Task MediaRequested => _mediaRequested.Task;
    internal Task MediaCancelled => _mediaCancelled.Task;
    internal int MediaRequests => Volatile.Read(ref _mediaRequests);
    internal void AllowMedia() => _mediaAllowed.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("A fixture request had no URI.");
        if (uri.Host == "i.4cdn.org" && uri.AbsolutePath == "/g/777.png")
        {
            Interlocked.Increment(ref _mediaRequests);
            _mediaRequested.TrySetResult();
            try { await _mediaAllowed.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _mediaCancelled.TrySetResult();
                throw;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };
        }
        if (uri.Host == "a.4cdn.org" && uri.AbsolutePath == "/boards.json")
            return Json("{\"boards\":[{\"board\":\"g\",\"title\":\"Technology\"}]}");
        if (uri.Host == "a.4cdn.org" && uri.AbsolutePath == "/g/thread/123.json")
        {
            return Json(JsonSerializer.Serialize(new
            {
                posts = new[] { new { no = 123, sub = Title, com = "Offline &amp; readable<br>&gt;&gt;124", time = 1700000000, tim = 777, filename = "fixture", ext = ".png",
                    fsize = Payload.Length, md5 = Convert.ToBase64String(MD5.HashData(Payload)) } }
            }));
        }
        throw new InvalidOperationException("Unexpected offline fixture request: " + uri);
    }

    private static HttpResponseMessage Json(string json) => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
