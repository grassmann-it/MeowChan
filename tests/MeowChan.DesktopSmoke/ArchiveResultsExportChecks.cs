using System.Security.Cryptography;
using MeowChan.Boards.FourChan;
using MeowChan.Downloader;
using MeowChan.Storage;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Commands;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveResultsExportChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivePage page, string profile, ProfileLease lease,
        string root, Action<TaskCompletionSource?> setGate)
    {
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        List<ThreadWorkSnapshot> work = new();
        CapturedThreadSnapshot Snapshot(int index, string html = "resultneedle") => new(
            new ResourceId("exportfixture", "a", index.ToString()), "https://fixture.test/" + index,
            "Result " + index.ToString("D3"), ThreadSourceState.Archived, DateTimeOffset.UtcNow, root,
            new[] { new CapturedPost(index.ToString(), 0, PostFieldPresence.All, Html: html) });
        for (int index = 0; index < 120; index++)
        {
            CapturedThreadSnapshot snapshot = Snapshot(index);
            await catalog.CaptureAsync(snapshot);
            work.Add(new ThreadWorkSnapshot(snapshot.Url, index % 2 == 0 ? WorkIntent.Completed : WorkIntent.Suppressed,
                ThreadSourceStateValue.Archived, null, false, Array.Empty<MediaWork>()));
        }
        page.GetWorkSnapshots = () => work;
        page.FindControl<TextBox>("ArchiveSearchText")!.Text = "resultneedle";
        page.FindControl<ComboBox>("ArchiveSort")!.SelectedIndex = 2;
        page.FindControl<ComboBox>("ArchiveWorkFilter")!.SelectedIndex = 4;
        await page.ApplySearchSpecAsync(new ArchiveSearchSpec("resultneedle", ["exportfixture"],
            sort: ArchiveThreadSort.Title, workFilter: ArchiveWorkFilter.Completed));
        DataGrid table = page.FindControl<DataGrid>("ArchiveThreadTable")!;
        Check(table.ItemsSource.Cast<object>().Count() == 50, "Result export fixture must span multiple pages.");
        Button origin = page.FindControl<Button>("ArchiveResultsExport")!;
        Button Button(string name) => page.FindControl<Button>(name)!;
        void Click(string name) => SmokeActionClick.Execute(Button(name));
        string Summary() => page.FindControl<TextBlock>("ArchiveExportSummary")!.Text!;
        string Status() => page.FindControl<TextBlock>("ArchiveExportStatus")!.Text!;
        await page.OpenExportForAllResultsAsync(origin);
        window.UpdateLayout();
        TextBlock fixedScope = page.FindControl<TextBlock>("ArchiveExportFixedScope")!;
        Check(fixedScope.IsEffectivelyVisible && fixedScope.Bounds.Height > 0 && fixedScope.Text == Texts.T("export.resultsFrozen", 60),
            "The fixed target count scrolled out of the export dialog.");
        Program.CaptureWindow(window, "smoke-all-results-export-ready.png");
        string frozen = Summary();
        Check(frozen.StartsWith(Texts.T("export.resultsFrozen", 60), StringComparison.Ordinal)
            && frozen.Split('\n').Length == 61, "All-results export did not resolve the real filtered 60 targets across pages.");
        work.Clear();
        await catalog.CaptureAsync(Snapshot(0, "late content after dialog"));
        await catalog.CaptureAsync(Snapshot(120));
        page.FindControl<TextBox>("ArchiveSearchText")!.Text = "absent";
        await page.ApplySearchAsync();
        await page.OpenExportForAllResultsAsync(origin);
        Check(Summary() == frozen, "A later search, work change, new match, or repeated command replaced frozen targets.");
        string output = Path.Combine(root, "all-results");
        page.FindControl<TextBox>("ArchiveExportDestination")!.Text = output;
        page.FindControl<CheckBox>("ArchiveExportMedia")!.IsChecked = false;
        Click("ArchiveExportStart");
        await Program.WaitUntilAsync(() => !page.IsExportRunning);
        ArchiveCollectionHtmlExportResult result = page.LastCollectionExport ?? throw new InvalidOperationException(Status());
        Check(result.ThreadCount == 60 && result.Metadata!.Members.Select(member => member.Thread.Thread)
            .SequenceEqual(Enumerable.Range(0, 60).Select(index => (index * 2).ToString())),
            "Result export changed the captured membership or ordering at Start.");
        Check(Directory.GetFiles(output, "*.html", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path).Contains("late content after dialog", StringComparison.Ordinal)),
            "Result export incorrectly froze thread content when opening the dialog.");
        Program.CaptureWindow(window, "smoke-all-results-export.png");
        Click("ArchiveExportClose");

        // A zero-result resolution stays visible and cannot start an empty export.
        await page.OpenExportForAllResultsAsync(origin);
        Check(Status() == Texts.T("export.resultsEmpty") && !Button("ArchiveExportStart").IsEnabled,
            "Empty search was not explained or enabled an empty export.");
        Click("ArchiveExportClose");
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        setGate(gate);
        Task resolving = page.OpenExportForAllResultsAsync(origin);
        await page.OpenExportForAllResultsAsync(origin);
        Check(!Button("ArchiveExportStart").IsEnabled, "Resolving a result scope enabled Start.");
        Button("ArchiveExportClose").RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await resolving;
        gate.TrySetResult(); setGate(null);
        Check(!page.FindControl<Border>("ArchiveExportDialog")!.IsVisible, "Cancelled resolution reopened a stale export dialog.");
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously); setGate(gate);
        resolving = page.OpenExportForAllResultsAsync(origin);
        gate.SetException(new IOException("fixture result lookup failure"));
        await resolving; setGate(null);
        Check(Status().Contains("fixture result lookup failure", StringComparison.Ordinal) && !Button("ArchiveExportStart").IsEnabled,
            "Lookup failure was hidden or allowed an unresolved export.");
        Click("ArchiveExportClose");

        page.FindControl<TextBox>("ArchiveSearchText")!.Text = "Result 000";
        page.FindControl<ComboBox>("ArchiveWorkFilter")!.SelectedIndex = 0;
        await page.ApplySearchAsync();
        await page.OpenExportForAllResultsAsync(origin);
        Check(Summary().StartsWith(Texts.T("export.resultsFrozen", 1), StringComparison.Ordinal), "Singleton all-results export lost its fixed count.");
        await catalog.DeleteThreadsAsync(new[] { new ResourceId("exportfixture", "a", "0") });
        output = Path.Combine(root, "removed-target");
        page.FindControl<TextBox>("ArchiveExportDestination")!.Text = output;
        Click("ArchiveExportStart"); await Program.WaitUntilAsync(() => !page.IsExportRunning);
        Check(page.LastExport == null && !Directory.Exists(output) && Status().Contains(Texts.T("export.failed")),
            "Deleted target was skipped, replaced or published as success.");
        Click("ArchiveExportClose");
        Console.WriteLine("SMOKE all-result export assertions completed; checking original-export dialog states.");
        await CheckOriginalCloseAsync(window, page, root);
        Console.WriteLine("SMOKE all-result export: 60 filtered ordered targets across pages, frozen query/work/IDs, late content, null/error/cancel/duplicate lookup and missing-target failure passed.");
    }

    private static async Task CheckOriginalCloseAsync(Window owner, ArchivePage page, string root)
    {
        byte[] bytes = new byte[16 * 1024 * 1024];
        using HttpClient client = new();
        FourChanImageBoard provider = new(client);
        IThread thread = provider.GetThread(new Uri("https://boards.4chan.org/g/thread/123"));
        DownloadItem item = new(new Uri("https://fixture.invalid/large.bin"), Path.Combine(root, "original-source", "large.bin"),
            provider, thread, "original-fixture", bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)), "large.bin");
        using MemoryStream stream = new(bytes, writable: false);
        MediaCommitResult committed = await new MediaStore().CommitAsync(item, stream, CancellationToken.None);
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(Path.GetDirectoryName(committed.Path)!);
        FavoriteExportAsset asset = FavoriteExportAsset.From(new ResourceId("4chan", "g", "123"), manifest, manifest.FindById(committed.AssetId!)!);
        string originalDestination = Path.Combine(root, "original-target");
        Directory.CreateDirectory(originalDestination);
        page.LastExportDirectoryPreference = originalDestination;
        foreach ((string language, string scenario) in new[] { ("en", "cancel"), ("de", "cancel"), ("en", "success"), ("de", "error") })
        {
            Texts.SetLanguage(language);
            Task shown = page.OpenOriginalExportAsync(new[] { asset });
            await Program.WaitUntilAsync(() => owner.OwnedWindows.Any(window => window.GetVisualDescendants()
                .OfType<Button>().Any(button => button.Name == "OriginalExportClose")));
            Window dialog = owner.OwnedWindows.Single(window => window.GetVisualDescendants()
                .OfType<Button>().Any(button => button.Name == "OriginalExportClose"));
            Button close = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OriginalExportClose");
            try
            {
            Console.WriteLine("SMOKE original-export idle: language=" + language + "; content=" + close.Content + "; automation=" + AutomationProperties.GetName(close) + "; tooltip=" + ToolTip.GetTip(close));
            Check(Equals(close.Content, Texts.T("action.close")) && AutomationProperties.GetName(close) == Texts.T("action.close")
                && Equals(ToolTip.GetTip(close), Texts.T("action.close")), "Idle original-export close label, tooltip or automation is incorrect.");
            Button start = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OriginalExportStart");
            if (scenario == "error")
            {
                string invalidTarget = Path.Combine(root, "original-target-replaced-by-file");
                File.WriteAllText(invalidTarget, "unrelated file");
                dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "OriginalExportDestination").Text = invalidTarget;
            }
            start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Console.WriteLine("SMOKE original-export after Start: " + close.Content + "; expected=" + CommandCatalog.Label("export.cancel"));
            if (scenario == "cancel")
            {
                Check(Equals(close.Content, CommandCatalog.Label("export.cancel")) && AutomationProperties.GetName(close) == CommandCatalog.Label("export.cancel"),
                    "Running original export did not expose Cancel export.");
                close.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            }
            await Program.WaitUntilAsync(() => Equals(close.Content, Texts.T("action.close")));
            Check(dialog.IsVisible && AutomationProperties.GetName(close) == Texts.T("action.close"),
                "Original copy closed early or did not restore Close after " + scenario + ".");
            if (scenario == "success") Check(Directory.GetFiles(originalDestination, "*", SearchOption.AllDirectories).Any(), "Successful original export wrote no file.");
            close.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await shown;
            }
            catch (Exception exception) { Console.WriteLine("SMOKE original-export assertion: " + exception); throw; }
            finally
            {
                if (dialog.IsVisible)
                {
                    close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Program.WaitUntilAsync(() => Equals(close.Content, Texts.T("action.close")));
                    dialog.Close();
                }
                await shown;
            }
        }
        Texts.SetLanguage("en");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
