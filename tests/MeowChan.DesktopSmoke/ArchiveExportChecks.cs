using MeowChan.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Avalonia.Interactivity;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveExportChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivedThreadView saved)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("meowchan-export-profile-");
        string? evidence = Environment.GetEnvironmentVariable("MEOWCHAN_SMOKE_EXPORT_EVIDENCE");
        string destinationRoot = evidence ?? TemporaryWorkspacePaths.CreateDirectory("meowchan-export-evidence-");
        Directory.CreateDirectory(destinationRoot);
        ResourceId readerIdentity = new(saved.Summary.Id.Source, saved.Summary.Id.Board, "900000001");
        try
        {
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                await catalog.CaptureAsync(new CapturedThreadSnapshot(readerIdentity, saved.Summary.Url, "Reader A", ThreadSourceState.Archived,
                    DateTimeOffset.UtcNow, saved.Summary.MediaDirectory, Array.Empty<CapturedPost>()));
                CapturedPost first = saved.GetPosts(0, 1).Single();
                await catalog.CaptureAsync(new CapturedThreadSnapshot(saved.Summary.Id, saved.Summary.Url, saved.Summary.Title,
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, saved.Summary.MediaDirectory,
                    new[] { first with { Html = first.Html + "<a href='#p124'>&gt;&gt;124</a><a href='#p999'>&gt;&gt;999</a><a href='/a/thread/400#p400'>&gt;&gt;400</a><a href='https://external.test/#p9'>&gt;&gt;9</a>" },
                        new CapturedPost("124", 1, PostFieldPresence.All, Html: "Retained offline reply &lt;img src=x onerror=alert(1)&gt;<script>export-script-must-not-run</script>", IsDeleted: true) }));
            }
            using ProfileLease exportLease = ProfileLease.Acquire(profile);
            ExportHistoryStore history = new(profile, exportLease);
            string? opened = null;
            ContentControl host = window.FindControl<ContentControl>("ArchivePageHost")!;
            object? retained = host.Content;
            bool holdReader = false;
            TaskCompletionSource? exportGate = null;
            TaskCompletionSource readerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ArchivePage fixture = new(async token =>
            {
                if (exportGate != null) await exportGate.Task.WaitAsync(token);
                if (holdReader) { holdReader = false; await readerGate.Task.WaitAsync(token); }
                return await ArchiveReader.OpenAsync(profile, token);
            }, (_, _) => Task.CompletedTask, path => opened = path) { ExportHistory = history, CollectionExportHistory = new CollectionExportHistoryStore(profile, exportLease) };
            host.Content = fixture;
            try
            {
                fixture.IsAvailable = true;
                await fixture.LoadThreadAsync(readerIdentity);
                holdReader = true;
                Task delayedReader = fixture.LoadThreadAsync(readerIdentity);
                await fixture.OpenExportForThreadAsync(saved.Summary.Id, fixture.FindControl<Button>("ExportArchiveButton"));
                Require(fixture.FindControl<Button>("ArchiveExportOpen")!.IsVisible && !fixture.FindControl<Button>("ArchiveExportOpen")!.IsEnabled, "Current result folder action must be visible and disabled before success.");
                Require(fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible, "Export action did not open its workflow.");
                string output = Path.Combine(destinationRoot, "thread-export");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = output;
                await fixture.OpenExportForThreadAsync(readerIdentity, fixture.FindControl<Button>("ExportArchiveButton"));
                Require(fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text == output &&
                    fixture.FindControl<TextBlock>("ArchiveExportSummary")!.Text!.Contains(saved.Summary.Id.Thread!),
                    "Repeated dialog opening replaced the bound identity or draft.");
                exportGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = Path.Combine(destinationRoot, "changed-during-export");
                fixture.FindControl<CheckBox>("ArchiveExportMedia")!.IsChecked = false;
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                exportGate.TrySetResult(); exportGate = null;
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                ArchiveHtmlExportResult result = fixture.LastExport ?? throw new InvalidOperationException(fixture.FindControl<TextBlock>("ArchiveExportStatus")!.Text);
                Require(result.Directory == output && !Directory.Exists(Path.Combine(destinationRoot, "changed-during-export")),
                    "Running export reread the mutable draft or started twice.");
                fixture.FindControl<CheckBox>("ArchiveExportMedia")!.IsChecked = true;
                readerGate.TrySetResult();
                await delayedReader;
                Require(result.Metadata?.Thread == saved.Summary.Id && fixture.CurrentThread?.Summary.Id == readerIdentity,
                    "Context export B was redirected to independently loading reader A.");
                Require(result.PostCount == 2 && result.FileCount == 1 && result.Omissions.Count == 0, "UI export counts differ from the real local content.");
                Require(File.Exists(result.IndexPath) && File.Exists(Path.Combine(result.Directory, "style.css")), "UI reported success before publishing its actual output.");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = Path.Combine(destinationRoot, "edited-after-export");
                fixture.FindControl<Button>("ArchiveExportOpen")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(opened == result.Directory, "Result folder action used edited draft instead of the published result.");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = output;
                Program.CaptureWindow(window, "smoke-archive-export-result.png");
                await CheckCompactLayoutsAsync(window, fixture);
                fixture.OpenExportFolder = _ => Task.FromException(new IOException("fixture launch failure"));
                fixture.FindControl<Button>("ArchiveExportOpen")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(ReferenceEquals(fixture.LastExport, result) && fixture.FindControl<TextBlock>("ArchiveExportStatus")!.Text!.Contains("fixture launch failure"),
                    "Folder handoff error erased the successful export or lacked dialog feedback.");
                fixture.OpenExportFolder = null;
                // A second click at the same target must fail without damaging the first output.
                byte[] firstOutput = await File.ReadAllBytesAsync(result.IndexPath);
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                byte[] afterConflict = await File.ReadAllBytesAsync(result.IndexPath);
                Require(fixture.LastExport == null && firstOutput.SequenceEqual(afterConflict), "A failed repeated export changed an existing output.");
                Require(fixture.FindControl<TextBlock>("ArchiveExportStatus")!.Text!.Contains("did not complete", StringComparison.OrdinalIgnoreCase), "Repeated output conflict falsely reported success.");
                Require(!fixture.FindControl<Button>("ArchiveExportOpen")!.IsEnabled, "Failed new attempt retained an old result folder action.");
                fixture.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                fixture.ExportHistory = new ExportHistoryStore(profile, exportLease);
                await fixture.OpenExportForThreadAsync(saved.Summary.Id, fixture.FindControl<Button>("ExportArchiveButton"));
                Require(fixture.FindControl<ComboBox>("ArchiveExportHistory")!.SelectedItem != null, "Persisted successful export was not recognized after reopening.");
                Require(fixture.FindControl<RadioButton>("ArchiveExportNew")!.IsChecked == true, "Recognizing history silently selected overwrite.");
                Require(!fixture.FindControl<Button>("ArchiveExportOpen")!.IsEnabled && fixture.FindControl<Button>("ArchiveExportPrevious")!.IsEnabled, "Previous export and current result were conflated.");
                fixture.FindControl<RadioButton>("ArchiveExportUpdate")!.IsChecked = true;
                fixture.FindControl<CheckBox>("ArchiveExportBackup")!.IsChecked = true;
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                Require(fixture.LastExport?.Directory == output && fixture.LastExport.Metadata!.Generation == result.Metadata!.Generation + 1, "Explicit update did not publish a new generation at the selected path.");
                fixture.FindControl<RadioButton>("ArchiveExportNew")!.IsChecked = true;
                string extra = Path.Combine(destinationRoot, "independent-second-export");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = extra;
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                Require(fixture.LastExport?.Directory == extra && File.Exists(Path.Combine(output, "index.html")), "New folder selection did not retain the previous export.");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = Path.Combine(destinationRoot, "cancelled-export");
                exportGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                fixture.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                Require(fixture.LastExport == null && fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible,
                    "Escape while exporting closed early or retained an old result.");
                Require(!Directory.Exists(Path.Combine(destinationRoot, "cancelled-export")), "Cancelled export published a destination.");
                exportGate.TrySetResult(); exportGate = null;
                string moved = Path.Combine(destinationRoot, "relocated thread");
                ComboBox known = fixture.FindControl<ComboBox>("ArchiveExportHistory")!;
                known.SelectedIndex = known.Items.Cast<object>().Select((item, index) => (item, index))
                    .Single(item => item.item.ToString()!.Contains(output, StringComparison.Ordinal)).index;
                fixture.FindControl<RadioButton>("ArchiveExportUpdate")!.IsChecked = true;
                fixture.FindControl<CheckBox>("ArchiveExportBackup")!.IsChecked = true;
                Directory.Move(output, moved);
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                Require(fixture.LastExport == null && !fixture.FindControl<Button>("ArchiveExportPrevious")!.IsEnabled &&
                    !fixture.FindControl<RadioButton>("ArchiveExportUpdate")!.IsEnabled &&
                    fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text == output,
                    "Failed update retained a stale available export or silently selected another target.");
                fixture.FindControl<RadioButton>("ArchiveExportNew")!.IsChecked = true;
                fixture.FindControl<Button>("ArchiveExportLegacyFind")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                fixture.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => fixture.FindControl<Button>("ArchiveExportLegacyFind")!.IsEnabled);
                Require(fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible, "Cancelling lookup closed the dialog before its work ended.");
                fixture.FindControl<Button>("ArchiveExportLegacyFind")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => fixture.FindControl<Button>("ArchiveExportLegacyFind")!.IsEnabled);
                Require(fixture.FindControl<TextBlock>("ArchiveExportStatus")!.Text!.StartsWith("Found ", StringComparison.Ordinal),
                    "Retrying a cancelled legacy lookup reused its cancelled token.");
                Console.WriteLine("SMOKE portable export index: " + Path.Combine(moved, "index.html"));
                fixture.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Require(!fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible, "Export workflow did not close with Escape.");
                Require(fixture.FindControl<Button>("ExportArchiveButton")!.IsFocused, "Closing export did not restore its real origin focus.");
                await ArchiveCollectionExportChecks.RunAsync(fixture, saved.Summary.Id, readerIdentity, destinationRoot, value => exportGate = value, () => opened);
                await ArchiveResultsExportChecks.RunAsync(window, fixture, profile, exportLease, destinationRoot, value => exportGate = value);
            }
            finally { readerGate.TrySetResult(); exportGate?.TrySetResult(); fixture.EndLifetime(); host.Content = retained; }
        }
        finally { Directory.Delete(profile, true); if (evidence == null) Directory.Delete(destinationRoot, true); }
        Console.WriteLine("SMOKE HTML export: real shared library writer, verified PNG, retained reply, internal quote, relocation and no-overwrite conflict passed.");
    }
    private static async Task CheckCompactLayoutsAsync(MainWindow window, ArchivePage page)
    {
        string modeBefore = Config.LoadConfig().WorkspaceMode;
        string languageBefore = Texts.Language;
        ThemeVariant? themeBefore = Application.Current?.RequestedThemeVariant;
        double widthBefore = window.Width, heightBefore = window.Height;
        try
        {
            foreach (string mode in new[] { "Simple", "Barebones" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                double width = mode == "Barebones" ? 640 : 760;
                double height = mode == "Barebones" ? 480 : 500;
                window.WindowState = WindowState.Normal;
                window.Width = width; window.Height = height;
                foreach (string language in new[] { "en", "de" })
                foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Texts.SetLanguage(language);
                    Application.Current!.RequestedThemeVariant = theme;
                    await Program.WaitUntilAsync(() =>
                    {
                        window.UpdateLayout();
                        Border dialog = page.FindControl<Border>("ArchiveExportDialog")!;
                        Border card = (Border)dialog.Child!;
                        ScrollViewer body = card.GetVisualDescendants().OfType<ScrollViewer>().First();
                        return PresentationChecks.MatchesRequestedSize(window, width, height)
                            && page.Bounds.Width > 0 && page.Bounds.Height > 0
                            && dialog.Bounds.Width <= page.Bounds.Width + 1
                            && Fits(window, page.FindControl<Button>("ArchiveExportClose")!)
                            && Fits(window, page.FindControl<Button>("ArchiveExportOpen")!)
                            && Fits(window, page.FindControl<Button>("ArchiveExportStart")!)
                            && card.Bounds.Width <= 680 && body.Viewport.Height > 30
                            && body.Extent.Width <= body.Viewport.Width + 1;
                    });
                    Program.CaptureWindow(window, $"smoke-export-{mode.ToLowerInvariant()}-{language}-{theme.ToString().ToLowerInvariant()}.png");
                }
            }
        }
        finally
        {
            Texts.SetLanguage(languageBefore);
            Application.Current!.RequestedThemeVariant = themeBefore;
            await PresentationChecks.SwitchModeAsync(window, modeBefore);
            window.Width = widthBefore; window.Height = heightBefore; window.UpdateLayout();
        }
    }
    private static bool Fits(Window window, Control control)
    {
        Point? point = control.TranslatePoint(default, window);
        return control.IsEffectivelyVisible && point.HasValue && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && point.Value.X >= -1 && point.Value.Y >= -1
            && point.Value.X + control.Bounds.Width <= window.Bounds.Width + 1
            && point.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
