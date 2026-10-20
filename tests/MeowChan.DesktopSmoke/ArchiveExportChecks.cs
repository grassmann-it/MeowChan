using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveExportChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivedThreadView saved)
    {
        string profile = Directory.CreateTempSubdirectory("meowchan-export-profile-").FullName;
        string? evidence = Environment.GetEnvironmentVariable("MEOWCHAN_SMOKE_EXPORT_EVIDENCE");
        string destinationRoot = evidence ?? Directory.CreateTempSubdirectory("meowchan-export-evidence-").FullName;
        Directory.CreateDirectory(destinationRoot);
        try
        {
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                CapturedPost first = saved.GetPosts(0, 1).Single();
                await catalog.CaptureAsync(new CapturedThreadSnapshot(saved.Summary.Id, saved.Summary.Url, saved.Summary.Title,
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, saved.Summary.MediaDirectory,
                    new[] { first with { Html = first.Html + "<a href='#p124'>&gt;&gt;124</a><a href='#p999'>&gt;&gt;999</a><a href='/a/thread/400#p400'>&gt;&gt;400</a><a href='https://external.test/#p9'>&gt;&gt;9</a>" },
                        new CapturedPost("124", 1, PostFieldPresence.All, Html: "Retained offline reply &lt;img src=x onerror=alert(1)&gt;<script>export-script-must-not-run</script>", IsDeleted: true) }));
            }
            ContentControl host = window.FindControl<ContentControl>("ArchivePageHost")!;
            object? retained = host.Content;
            ArchivePage fixture = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
            host.Content = fixture;
            try
            {
                fixture.IsAvailable = true;
                await fixture.LoadThreadAsync(saved.Summary.Id);
                fixture.FindControl<Button>("ExportArchiveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible, "Export action did not open its workflow.");
                string output = Path.Combine(destinationRoot, "thread-export");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = output;
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                ArchiveHtmlExportResult result = fixture.LastExport ?? throw new InvalidOperationException(fixture.FindControl<TextBlock>("ArchiveExportStatus")!.Text);
                Require(result.PostCount == 2 && result.FileCount == 1 && result.Omissions.Count == 0, "UI export counts differ from the real local content.");
                Require(File.Exists(result.IndexPath) && File.Exists(Path.Combine(result.Directory, "style.css")), "UI reported success before publishing its actual output.");
                Program.CaptureWindow(window, "smoke-archive-export-result.png");
                // A second click at the same target must fail without damaging the first output.
                byte[] firstOutput = await File.ReadAllBytesAsync(result.IndexPath);
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                byte[] afterConflict = await File.ReadAllBytesAsync(result.IndexPath);
                Require(fixture.LastExport == null && firstOutput.SequenceEqual(afterConflict), "A failed repeated export changed an existing output.");
                Require(fixture.FindControl<TextBlock>("ArchiveExportStatus")!.Text!.Contains("failed", StringComparison.OrdinalIgnoreCase), "Repeated output conflict falsely reported success.");
                string moved = Path.Combine(destinationRoot, "relocated thread");
                Directory.Move(output, moved);
                Console.WriteLine("SMOKE portable export index: " + Path.Combine(moved, "index.html"));
                fixture.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible, "Export workflow did not close.");
            }
            finally { fixture.EndLifetime(); host.Content = retained; }
        }
        finally { Directory.Delete(profile, true); if (evidence == null) Directory.Delete(destinationRoot, true); }
        Console.WriteLine("SMOKE HTML export: real shared library writer, verified PNG, retained reply, internal quote, relocation and no-overwrite conflict passed.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
