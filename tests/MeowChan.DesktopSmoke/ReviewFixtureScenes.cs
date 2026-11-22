using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class ReviewFixtureScenes
{
    internal static readonly string[] Surfaces = ["downloads", "archive", "archive-reader", "archive-large-reader", "media", "media-panel", "tagging", "tagging-stamp", "booru", "booru-workbench", "rules", "settings", "gallery"];
    private static readonly int[] Widths = [760, 900, 1099, 1100, 1440];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly List<object> Cells = new();
    private static string _root = null!;
    private static int _seed, _media, _failed;

    internal static int Run(bool generateOnly)
    {
        // A new output directory is mandatory. There is deliberately no --profile input.
        _root = Path.GetFullPath(Environment.GetEnvironmentVariable("MEOWCHAN_REVIEW_ARTIFACTS") ?? throw new ArgumentException("Missing private review output"));
        if (Directory.Exists(_root)) throw new IOException("Review output already exists");
        Directory.CreateDirectory(_root);
        _seed = int.Parse(Environment.GetEnvironmentVariable("MEOWCHAN_REVIEW_SEED") ?? "20261117");
        _media = int.Parse(Environment.GetEnvironmentVariable("MEOWCHAN_REVIEW_MEDIA") ?? "100");
        string profile = generateOnly ? Path.Combine(_root, "fixture-profile") : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "profile"));
        if (!generateOnly && (Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1"
            || Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE") != profile))
            throw new InvalidOperationException("Review capture must use the isolated runner's owned working-directory profile.");
        Environment.SetEnvironmentVariable("OCHAN_PROFILE", null);
        Environment.SetEnvironmentVariable("MEOWCHAN_PROFILE", profile);
        Environment.SetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK", "1");
        Environment.SetEnvironmentVariable("MEOWCHAN_MOTION", "off");
        ReviewFixtureData.GenerateAsync(profile, _media, _seed).GetAwaiter().GetResult();
        File.Copy(Path.Combine(profile, "fixture.json"), Path.Combine(_root, "fixture.json"));
        Write(Path.Combine(_root, "shortcuts.json"), Enum.GetValues<ShortcutPlatform>().Select(p => new { platform = p.ToString(), commands = CommandCatalog.All.Count(), issues = EffectiveShortcutBindings.Build(null, p).Issues }));
        Write(Path.Combine(_root, "source-catalogs.json"), new { en = BuiltInCatalog.English, de = BuiltInCatalog.German });
        if (generateOnly) { Console.WriteLine("SMOKE SCENARIO: review-fixture-generate\nSMOKE PASS: generated synthetic profile"); return 0; }
        if (Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1")
            throw new InvalidOperationException("Review captures require the private Linux Xvfb runner; no native desktop fallback.");
        AppBuilder.Configure<ShowcaseSmokeApp>().UsePlatformDetect().WithInterFont().With(ReaderFonts.ManagerOptions())
            .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
            .AfterSetup(_ => Dispatcher.UIThread.Post(async () => await RunAsync(), DispatcherPriority.Background))
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        Console.WriteLine("SMOKE SCENARIO: review-fixtures");
        Console.WriteLine($"SMOKE {(_failed == 0 ? "PASS" : "FAILED")}: review fixtures; {Cells.Count - _failed}/{Cells.Count} reviewable");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task RunAsync()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            MainWindow w = (MainWindow)desktop.MainWindow!;
            await Program.WaitUntilAsync(() => w.IsVisible);
            await w.SessionHost.StartAsync();
            if (w.SessionHost.State != DesktopSessionState.Ready) throw new InvalidOperationException("Synthetic session startup failed");
            await Program.WaitUntilAsync(() => w.SessionHost.GetPublishedPrivateTagSnapshot().State == PrivateTagLoadState.Ready,
                () => "Synthetic tags not ready: " + w.SessionHost.GetPublishedPrivateTagSnapshot().Error);
            if (_media > 0)
            {
                await SmokeNavigation.GoToAsync(w, MainWindow.WorkspacePage.Media);
                await w.MediaPageForTests!.BuildTask;
                var queue = w.MediaPageForTests.ShownEntries.Take(100).Select(e => MeowChan.Tags.TagQueueItem.From(e, ReviewFixtureData.Epoch)).Where(e => e != null).Cast<MeowChan.Tags.TagQueueItem>().ToArray();
                await w.SessionHost.EnqueueTagQueueAsync(queue);
            }
            await using WorkViewBooruChecks booru = new(w, empty: _media == 0);
            await booru.InitializeAsync();
            await using SyntheticShellRows rows = await SyntheticShellRows.CreateAsync(w, Enumerable.Range(0, _media == 0 ? 0 : 6).Select(i =>
                SnapshotFixtures.Thread(990000000 + i, (800000000 + i).ToString(), $"Synthetic download {i} 合成試験 Arbeitsplatzdarstellungsgrößenüberprüfung", i, i + 2, 0, 0, "synthetic")).ToArray());
            string? only = Environment.GetEnvironmentVariable("MEOWCHAN_REVIEW_SCENE");
            int[] widths = Widths;
            if (only?.Split('-') is { Length: >= 4 } parts && int.TryParse(parts[^3], out int requestedWidth) && !Widths.Contains(requestedWidth))
                widths = [requestedWidth];
            foreach (string language in new[] { "de", "en" })
            foreach (string theme in new[] { "Light", "Dark" })
            foreach (int width in widths)
            foreach (string surface in Surfaces)
            {
                string id = $"{surface}-{width}-{theme.ToLowerInvariant()}-{language}";
                if (only != null && only != id) continue;
                Stopwatch watch = Stopwatch.StartNew();
                try
                {
                    await LeaveAsync(w);
                    await FooterNavigationChecks.PersistLanguageAsync(w, language);
                    Application.Current.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
                    w.SessionHost.Config.Theme = theme;
                    w.WindowState = WindowState.Normal; w.Width = width; w.Height = 920;
                    await Program.WaitUntilAsync(() => Math.Abs(w.ClientSize.Width - width) <= 1);
                    double prepared = watch.Elapsed.TotalMilliseconds;
                    await EnterAsync(w, surface, booru);
                    string? requestedView = Environment.GetEnvironmentVariable("MEOWCHAN_REVIEW_VIEW");
                    if (requestedView != null) await PresentationChecks.SwitchModeAsync(w, requestedView);
                    double entered = watch.Elapsed.TotalMilliseconds;
                    for (int i = 0; i < 2; i++) await Dispatcher.UIThread.InvokeAsync(w.UpdateLayout, DispatcherPriority.Background);
                    await Task.Delay(120);
                    if (Texts.Language != language || Application.Current.RequestedThemeVariant.ToString() != theme) throw new InvalidOperationException("Scene presentation drift");
                    string pending = Path.Combine(_root, ".pending-" + id);
                    Environment.SetEnvironmentVariable("MEOWCHAN_REVIEW_ARTIFACTS", pending);
                    double captureStarted = watch.Elapsed.TotalMilliseconds;
                    ReviewArtifacts.Capture(w, id, new { schema_version = 1, kind = "scene-setup", clock = "Stopwatch elapsed milliseconds", prepare_ms = prepared,
                        navigate_ms = entered - prepared, settle_ms = watch.Elapsed.TotalMilliseconds - entered, work_view = requestedView,
                        t_final = watch.Elapsed.TotalMilliseconds,
                        t_input = (double?)null, t_first_visual_ack = (double?)null, unverified = "Scene setup timing, not physical-input-to-photon latency" });
                    string bundle = Directory.GetDirectories(pending).Single();
                    var timing = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "timing.json")))!;
                    timing["capture_bundle_ms"] = watch.Elapsed.TotalMilliseconds - captureStarted;
                    timing["total_ms"] = watch.Elapsed.TotalMilliseconds;
                    File.WriteAllText(Path.Combine(bundle, "timing.json"), timing.ToJsonString(Json));
                    // Commit the complete tree as one same-filesystem rename. A partial scene is never reviewable.
                    string final = Path.Combine(_root, id);
                    Directory.Move(bundle, final); Directory.Delete(pending);
                    var files = Directory.GetFiles(final).Order().ToDictionary(p => Path.GetFileName(p), p => new { path = id + "/" + Path.GetFileName(p), sha256 = ReviewFixtureData.Hex(File.ReadAllBytes(p)) });
                    Cells.Add(new { id, surface, status = "captured", seed = _seed, media = _media, theme, language, work_view = requestedView,
                        geometry = new { width = w.ClientSize.Width, height = w.ClientSize.Height, scale = w.RenderScaling, x = w.Position.X, y = w.Position.Y }, files });
                }
                catch (Exception e)
                {
                    _failed++;
                    Cells.Add(new { id, surface, status = "missing", seed = _seed, media = _media, theme, language, geometry = new { width, height = 920 }, error = e.GetType().Name + ": " + e.Message });
                    Console.WriteLine("REVIEW MISSING " + id + ": " + e.Message);
                }
                finally { Environment.SetEnvironmentVariable("MEOWCHAN_REVIEW_ARTIFACTS", _root); Manifest(); }
            }
            // SyntheticShellRows' restore waits for the downloads projection to apply; it is suspended on other pages.
            await LeaveAsync(w);
            await SmokeNavigation.GoToAsync(w, MainWindow.WorkspacePage.Downloads);
            await Dispatcher.UIThread.InvokeAsync(w.UpdateLayout, DispatcherPriority.Background);
        }
        catch (Exception e) { _failed++; Write(Path.Combine(_root, "fatal.json"), new { error = e.ToString() }); }
        finally { Manifest(); desktop.Shutdown(_failed == 0 ? 0 : 1); }
    }

    private static async Task LeaveAsync(MainWindow w)
    {
        foreach (string host in new[] { "GalleryHost", "ReaderHost" })
        {
            ContentControl c = w.FindControl<ContentControl>(host)!;
            if (!c.IsVisible) continue;
            Button? close = c.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == (host == "GalleryHost" ? "GalleryClose" : "ArchiveCloseReader"));
            if (close != null) Click(close);
        }
        await w.GalleryCleanupTask;
    }

    private static async Task EnterAsync(MainWindow w, string surface, WorkViewBooruChecks booru)
    {
        if (surface.StartsWith("booru"))
        {
            await WorkViewBooruChecks.EnterAsync(w);
            for (int i = 0; i < 2; i++) await Dispatcher.UIThread.InvokeAsync(w.UpdateLayout, DispatcherPriority.Background);
            if (_media == 0) await w.BooruPageForTests!.ActivateAsync();
            else await booru.MaterializeAsync();
            // Search workspace versus the watch/rule workbench tab, both actual retained controls.
            BooruWorkbenchView view = w.BooruPageForTests!.View;
            if (surface == "booru") view.CloseWatchers();
            else view.ShowWatchers();
            return;
        }
        var page = surface switch
        {
            "downloads" => MainWindow.WorkspacePage.Downloads,
            "archive" or "archive-reader" or "archive-large-reader" or "gallery" => MainWindow.WorkspacePage.Archive,
            "media" or "media-panel" => MainWindow.WorkspacePage.Media,
            "tagging" or "tagging-stamp" => MainWindow.WorkspacePage.Tagging,
            "rules" => MainWindow.WorkspacePage.Rules,
            _ => MainWindow.WorkspacePage.Settings
        };
        await SmokeNavigation.GoToAsync(w, page);
        if (page == MainWindow.WorkspacePage.Archive)
        {
            ArchivePage a = w.ArchivePage;
            await Program.WaitUntilAsync(() => !a.IsBusy && !a.IsSearchLoading);
            await Dispatcher.UIThread.InvokeAsync(w.UpdateLayout, DispatcherPriority.Background);
            if (surface == "archive")
            {
                Button back = a.FindControl<Button>("ArchiveBackButton")!;
                if (back.IsEffectivelyVisible) Click(back);
                await Program.WaitUntilAsync(() => a.FindControl<ListBox>("ArchiveThreads")!.IsEffectivelyVisible);
            }
            if (surface != "archive")
            {
                if (_media == 0) throw new InvalidOperationException("Empty fixture: no reader/gallery subject");
                var subject = surface == "archive-large-reader" && _media >= 100
                    ? ReviewFixtureData.Hero with { Thread = _media >= 1000 ? "100003" : "100002" }
                    : ReviewFixtureData.Hero;
                await a.LoadThreadAsync(subject);
                await a.MediaLoadTask;
                a.FindControl<TabControl>("ArchiveContentTabs")!.SelectedIndex = 0;
                if (surface == "gallery")
                {
                    await w.OpenGalleryAsync(ReviewFixtureData.Hero, origin: a.FindControl<Button>("ArchiveResultsGallery"));
                    await Program.WaitUntilAsync(() => w.FindControl<ContentControl>("GalleryHost")!.GetVisualDescendants().OfType<Image>().Any(i => i.Name == "GalleryImage" && i.Source is Bitmap));
                }
                else if (surface == "archive-large-reader")
                {
                    Click(a.FindControl<Button>("ArchiveReadPosts")!);
                    await Program.WaitUntilAsync(() => w.FindControl<ContentControl>("ReaderHost")!.IsVisible);
                }
            }
        }
        if (page == MainWindow.WorkspacePage.Media)
        {
            MediaPage m = w.MediaPageForTests!;
            await m.BuildTask;
            bool panel = surface == "media-panel";
            if (m.PageLayout.Panel != panel) m.TogglePanelByShortcut();
            if (panel && _media > 0) { m.Grid.SelectedIndex = 0; await m.PanelTask; }
        }
        if (page == MainWindow.WorkspacePage.Tagging)
        {
            await Program.WaitUntilAsync(() => w.TaggingPage is { IsBusy: false });
            TaggingPage tagging = w.TaggingPage!;
            if (surface == "tagging-stamp")
            {
                tagging.StampButton.Flyout!.ShowAt(tagging.StampButton);
                if (!tagging.StampOpen) throw new InvalidOperationException("Stamp editor not open");
            }
            else tagging.CloseStamp();
        }
    }
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Manifest() => Write(Path.Combine(_root, "scenes.json"), new { schema_version = 1, seed = _seed, expected = Surfaces.Length * 20, scenes = Cells });
    private static void Write(string path, object value)
    {
        File.WriteAllText(path + ".pending", JsonSerializer.Serialize(value, Json) + "\n");
        File.Move(path + ".pending", path, true);
    }
}
