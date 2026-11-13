using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Root hook: await ArchiveExportStatusLanguageRefreshChecks.RunAsync(owner), after Ready,
// on the native UI thread in the isolated smoke process. No Program.cs hook in this sidecar.
// Real ArchivePage controls/handlers/writers; managed command clicks, not OS input automation.
internal static class ArchiveExportStatusLanguageRefreshChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly ResourceId First = new("export-status-fixture", "local", "101");
    private static readonly ResourceId Second = new("export-status-fixture", "local", "202");
    private const string RawDetail = "Synthetic diagnostic: export.failed {0} <raw> ü 日本\nunchanged detail";
    private const string ExtraContent = "Synthetic preserved content — archive.exportDone {0}";
    private static readonly (string Code, string Start, string Working)[] Languages =
    [
        ("en", "Export", "Preparing export…"),
        ("de", "Exportieren", "Export wird vorbereitet…"),
        ("es", "Exportar", "Preparando exportación…"),
        ("fr", "Exporter", "Préparation de l’export…"),
        ("ru", "Экспортировать", "Подготовка экспорта…")
    ];
    // A cleanup timeout is not permission to drop ownership while a writer is still running.
    private sealed record RetainedFixture(string Root, ArchivePage? Page, Window? Host, ProfileLease? Lease);
    private static readonly List<RetainedFixture> IncompleteCleanups = new();

    private sealed class ReaderBoundary(string profile)
    {
        internal TaskCompletionSource? Release;
        internal TaskCompletionSource? Entered;
        internal bool Fail;
        internal int Calls;
        internal Task? PendingOpen;
        internal bool PreviousInspectionPending;

        internal void Hold()
        {
            Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal async Task<ArchiveReader> OpenAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            TaskCompletionSource? release = Release;
            if (release != null)
            {
                Entered!.TrySetResult();
                // Delay cancellation acknowledgement only at this fake factory boundary so
                // the actual Cancelling UI can be checked deterministically. No reader/IO yet.
                await release.Task;
            }
            token.ThrowIfCancellationRequested();
            if (Fail) throw new IOException(RawDetail);
            return await ArchiveReader.OpenAsync(profile, token);
        }
    }

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(TopLevel.GetTopLevel(owner) == owner && owner.IsVisible, "A visible native smoke owner is required.");
        string originalLanguage = Texts.Language;
        foreach (var cell in Languages)
            Require(Texts.Languages.Any(item => item.Code == cell.Code), "Bundled language unavailable: " + cell.Code);
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-export-status-language-");
        string profile = Path.Combine(root, "profile");
        string exports = Path.Combine(root, "exports");
        string media = Path.Combine(root, "synthetic-media");
        Directory.CreateDirectory(exports);
        Directory.CreateDirectory(media);
        ReaderBoundary boundary = new(profile);
        ArchivePage? page = null;
        Window? host = null;
        ProfileLease? lease = null;
        Exception? failure = null;
        void CleanupFailed(string phase, Exception error)
        {
            var cleanup = new InvalidOperationException("Export-status smoke cleanup " + phase + "; fixture: " + root, error);
            Console.Error.WriteLine(cleanup);
            failure = failure == null ? cleanup : new AggregateException(failure, cleanup);
        }
        Console.WriteLine("SMOKE export status language fixture: " + root);
        try
        {
            lease = ProfileLease.Acquire(profile);
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                foreach (ResourceId id in new[] { First, Second })
                    await catalog.CaptureAsync(new CapturedThreadSnapshot(id,
                        "https://offline.invalid/local/" + id.Thread, "Synthetic title — not translated",
                        ThreadSourceState.Archived, DateTimeOffset.UtcNow, media,
                        [new CapturedPost(id.Thread!, 0, PostFieldPresence.All, Html: "<p>Synthetic source text</p>",
                            Attachments: id == First
                                ? [new CapturedAttachment(0, "synthetic-image", Url: "https://offline.invalid/image.png", OriginalFileName: "synthetic.png")]
                                : Array.Empty<CapturedAttachment>())]));
            }
            page = new ArchivePage(boundary.OpenAsync,
                (_, _) => throw new InvalidOperationException("Smoke must not change capture settings."),
                _ => throw new InvalidOperationException("Smoke must not launch an external folder or URL."))
            {
                IsAvailable = true,
                LastExportDirectoryPreference = exports,
                ExportHistory = new ExportHistoryStore(profile, lease),
                CollectionExportHistory = new CollectionExportHistoryStore(profile, lease)
            };
            host = new Window { Title = "Export status language — synthetic offline fixture", Width = 1100, Height = 850, Content = page };
            host.Show(owner);
            // No Activate/LoadThread: the dialog binds explicit identities; no list/thumbnail
            // producers can hide an accidental reader restart during language refresh.
            foreach (bool collection in new[] { false, true })
            {
                string kind = collection ? "collection" : "single";
                string destination = Path.Combine(exports, kind + "-raw-path-ü");
                await OpenAsync(page, boundary, collection, destination);
                await MatrixAsync(host, page, boundary, kind + "/draft", () => string.Empty, () => string.Empty);
                await SetLanguageAsync(host, page, "en");
                boundary.Hold();
                Click(page, "ArchiveExportStart");
                Require(page.IsExportRunning, "Start did not enter the production export handler.");
                await boundary.Entered!.Task.WaitAsync(Deadline);
                await MatrixAsync(host, page, boundary, kind + "/preparing", () => Texts.T("archive.exportWorking"), () => string.Empty);
                // Complete while Russian remains active: a late read of Texts.Language
                // would now publish the wrong HTML locale instead of the English Start snapshot.
                await SetLanguageAsync(host, page, "ru");
                Require(Need<TextBlock>(page, "ArchiveExportStatus").Text == Texts.T("archive.exportWorking"),
                    "Pending status did not refresh before publication.");
                boundary.Release!.TrySetResult();
                await WaitUntilAsync(() => !page.IsExportRunning, "export completion");
                boundary.Release = null;
                boundary.Entered = null;
                RequireResult(page, collection, destination, generation: 1, language: "en");
                Func<string> success = () => ExpectedResult(page, collection);
                await MatrixAsync(host, page, boundary, kind + "/success", success, () => string.Empty);

                int folderCalls = 0;
                string? folderTarget = null;
                page.OpenExportFolder = path =>
                {
                    folderCalls++;
                    folderTarget = path;
                    return Task.FromException(new IOException(RawDetail));
                };
                Click(page, "ArchiveExportOpen");
                await WaitUntilAsync(() => folderCalls == 1 && Need<Button>(page, "ArchiveExportOpen").IsEnabled
                    && Error(page) == Texts.T("export.openFailed", RawDetail), "folder diagnosis");
                await MatrixAsync(host, page, boundary, kind + "/folder-error", success, () => Texts.T("export.openFailed", RawDetail));
                Require(folderCalls == 1 && folderTarget == destination, "Refresh retried folder launch or changed the exact result path.");

                Close(page);
                // Modify only our synthetic output, then reopen to get a fresh real inspection.
                // Explicit update/preserve produces a genuine BackupDirectory status line.
                await File.WriteAllTextAsync(Path.Combine(destination, "synthetic-extra.txt"), ExtraContent);
                await OpenAsync(page, boundary, collection, destination);
                Need<RadioButton>(page, "ArchiveExportUpdate").IsChecked = true;
                Need<CheckBox>(page, "ArchiveExportBackup").IsChecked = true;
                await SetLanguageAsync(host, page, "en");
                Click(page, "ArchiveExportStart");
                await WaitUntilAsync(() => !page.IsExportRunning, "backup update completion");
                RequireResult(page, collection, destination, generation: 2, language: "en");
                string? backup = collection ? page.LastCollectionExport!.BackupDirectory : page.LastExport!.BackupDirectory;
                Require(backup != null && await File.ReadAllTextAsync(Path.Combine(backup, "synthetic-extra.txt")) == ExtraContent,
                    "Explicit update did not preserve the synthetic extra file.");
                await MatrixAsync(host, page, boundary, kind + "/backup-success", success, () => string.Empty);

                if (collection)
                {
                    // Real stale-inspection path: change the synthetic collection fingerprint
                    // after its history was verified, then use Previous. No forged status objects.
                    await File.WriteAllTextAsync(Path.Combine(destination, "synthetic-later.txt"), "changed after inspection");
                    boundary.PreviousInspectionPending = true;
                    Click(page, "ArchiveExportPrevious");
                    await WaitUntilAsync(() => Need<Button>(page, "ArchiveExportPrevious").IsEnabled
                        && Error(page) == Texts.T("export.openFailed", Texts.T("export.changed")), "nested changed-export diagnosis");
                    boundary.PreviousInspectionPending = false;
                    await MatrixAsync(host, page, boundary, kind + "/nested-changed-error", success,
                        () => Texts.T("export.openFailed", Texts.T("export.changed")));
                    Require(folderCalls == 1, "Changed-export guard invoked the external folder callback.");
                }

                Close(page);
                string failedDestination = Path.Combine(exports, kind + "-failed");
                await OpenAsync(page, boundary, collection, failedDestination);
                boundary.Fail = true;
                Click(page, "ArchiveExportStart");
                await WaitUntilAsync(() => !page.IsExportRunning, "failed export settlement");
                boundary.Fail = false;
                Require(page.LastExport == null && page.LastCollectionExport == null && !Directory.Exists(failedDestination),
                    "Synthetic failed reader published output or retained a previous success.");
                await MatrixAsync(host, page, boundary, kind + "/failed", () => Texts.T("archive.exportWorking"),
                    () => Texts.T("export.failed") + "\n" + RawDetail);
                Close(page);

                string cancelledDestination = Path.Combine(exports, kind + "-cancelled");
                await OpenAsync(page, boundary, collection, cancelledDestination);
                boundary.Hold();
                Click(page, "ArchiveExportStart");
                await boundary.Entered!.Task.WaitAsync(Deadline);
                Click(page, "ArchiveExportClose");
                Require(page.IsExportRunning, "Fake reader must remain pending until cancellation is acknowledged.");
                await MatrixAsync(host, page, boundary, kind + "/cancelling", () => Texts.T("archive.exportCancelling"), () => string.Empty);
                boundary.Release!.TrySetResult();
                await WaitUntilAsync(() => !page.IsExportRunning, "cancelled export settlement");
                boundary.Release = null;
                boundary.Entered = null;
                await MatrixAsync(host, page, boundary, kind + "/cancelled", () => Texts.T("archive.exportCancelled"), () => string.Empty);
                Require(page.LastExport == null && page.LastCollectionExport == null && !Directory.Exists(cancelledDestination),
                    "Cancelled export published output or retained a previous success.");
                Close(page);
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            bool settled = false;
            try
            {
                if (host != null) host.IsEnabled = false;
                page?.EndLifetime();
                boundary.Release?.TrySetResult();
                if (page != null)
                {
                    await WaitUntilAsync(() => !page.IsExportRunning, "cleanup export settlement");
                    await page.ReaderLocalizationTask.WaitAsync(Deadline);
                }
                if (boundary.PendingOpen != null) await boundary.PendingOpen.WaitAsync(Deadline);
                // Previous is an async-void production action. If its bounded wait failed,
                // there is no public settlement task: retain ownership rather than guess.
                if (boundary.PreviousInspectionPending)
                    throw new InvalidOperationException("Previous-export inspection settlement was not observed.");
                settled = true;
            }
            catch (Exception error) { CleanupFailed("settlement", error); }
            if (settled)
            {
                try { if (host != null) { host.Content = null; host.Close(); } }
                catch (Exception error) { CleanupFailed("window close", error); }
                try { lease?.Dispose(); }
                catch (Exception error) { CleanupFailed("lease release", error); }
            }
            else
            {
                IncompleteCleanups.Add(new(root, page, host, lease));
                CleanupFailed("incomplete; retain ownership until isolated process exits", new TimeoutException("Root must terminate the isolated smoke process."));
            }
            try { Texts.SetLanguage(originalLanguage); owner.Activate(); }
            catch (Exception error) { CleanupFailed("language/owner restoration", error); }
            Console.WriteLine("SMOKE export status language retained synthetic fixture: " + root);
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("SMOKE export status language: EN/DE/ES/FR/RU live labels, single/collection preparation, results, omissions, backup, folder failure, nested changed-export diagnosis, raw failure and cancellation passed; managed input only.");
    }

    private static async Task OpenAsync(ArchivePage page, ReaderBoundary boundary, bool collection, string destination)
    {
        boundary.PendingOpen = collection ? page.OpenExportForThreadsAsync([First, Second], null)
            : page.OpenExportForThreadAsync(First, null);
        await boundary.PendingOpen.WaitAsync(Deadline);
        boundary.PendingOpen = null;
        Require(Need<Border>(page, "ArchiveExportDialog").IsVisible, "Actual dialog failed to open.");
        Need<TextBox>(page, "ArchiveExportDestination").Text = destination;
        Need<CheckBox>(page, "ArchiveExportMedia").IsChecked = false;
        Require(page.LastExport == null && page.LastCollectionExport == null, "Opening retained a previous result.");
    }

    private static void RequireResult(ArchivePage page, bool collection, string destination, int generation, string language)
    {
        if (collection)
        {
            var result = page.LastCollectionExport ?? throw new InvalidOperationException("Missing collection result: " + Error(page));
            Require(page.LastExport == null && result.Directory == destination && result.ThreadCount == 2 && result.PostCount == 2
                && result.FileCount == 0 && result.Omissions.Count == 1 && result.Omissions[0].Thread == First
                && result.Omissions[0].Reason == ArchiveExportOmissionReason.MediaNotIncluded && result.Warnings.Count == 0
                && result.Metadata?.Generation == generation && result.Metadata.Options.Language == language
                && !result.Metadata.Options.IncludeMedia && result.Metadata.Members.Select(item => item.Thread).SequenceEqual(new[] { First, Second }),
                "Collection result identity/counts/options/generation did not match the synthetic fixture.");
        }
        else
        {
            var result = page.LastExport ?? throw new InvalidOperationException("Missing single result: " + Error(page));
            Require(page.LastCollectionExport == null && result.Directory == destination && result.PostCount == 1 && result.FileCount == 0
                && result.Omissions.Count == 1 && result.Omissions[0].Reason == ArchiveExportOmissionReason.MediaNotIncluded
                && result.Warnings.Count == 0 && result.Metadata?.Generation == generation && result.Metadata.Thread == First
                && result.Metadata.Options.Language == language && !result.Metadata.Options.IncludeMedia,
                "Single result identity/counts/options/generation did not match the synthetic fixture.");
        }
        Require(File.Exists(Path.Combine(destination, "index.html")), "Result returned before its output was published.");
        string[] htmlPaths = collection
            ? page.LastCollectionExport!.Metadata!.Members.Select(item => Path.Combine(destination, item.PagePath))
                .Prepend(Path.Combine(destination, "index.html")).ToArray()
            : [Path.Combine(destination, "index.html")];
        foreach (string path in htmlPaths)
            Require(File.ReadAllText(path).Contains("<html lang=\"" + language + "\">", StringComparison.Ordinal),
                "Published HTML did not retain the Start-time language: " + path);
    }

    // Build expected visible text independently of ArchiveExportStatusText/CollectionExportResultText.
    private static string ExpectedResult(ArchivePage page, bool collection)
    {
        if (!collection)
        {
            var result = page.LastExport!;
            return Texts.T("archive.exportDone", 1, 0, 1, 0) + "\n" + result.Directory
                + (result.BackupDirectory == null ? "" : "\n" + Texts.T("export.backup", result.BackupDirectory));
        }
        var group = page.LastCollectionExport!;
        return Texts.T("export.collectionDone", 2, 2, 0, 1, 0) + "\n" + group.Directory
            + "\n" + Texts.T("export.collectionMemberDone", Member(First), 1, 1)
            + "\n" + Texts.T("export.collectionMemberDone", Member(Second), 1, 0)
            + "\n" + Member(First) + ": " + Texts.T("export.omission.MediaNotIncluded") + " × 1"
            + (group.BackupDirectory == null ? "" : "\n" + Texts.T("export.backup", group.BackupDirectory));
    }

    private static async Task MatrixAsync(Window host, ArchivePage page, ReaderBoundary boundary, string scenario,
        Func<string> expectedStatus, Func<string> expectedError)
    {
        Dispatcher.UIThread.VerifyAccess();
        int calls = Volatile.Read(ref boundary.Calls);
        bool running = page.IsExportRunning;
        var single = page.LastExport;
        var collection = page.LastCollectionExport;
        string? destination = Need<TextBox>(page, "ArchiveExportDestination").Text;
        bool? media = Need<CheckBox>(page, "ArchiveExportMedia").IsChecked;
        bool? backup = Need<CheckBox>(page, "ArchiveExportBackup").IsChecked;
        bool? updating = Need<RadioButton>(page, "ArchiveExportUpdate").IsChecked;
        object? selected = Need<ComboBox>(page, "ArchiveExportHistory").SelectedItem;
        TextBlock status = Need<TextBlock>(page, "ArchiveExportStatus");
        TextBlock error = Need<TextBlock>(page, "ArchiveExportError");
        string[] tracked = new[] { single?.Directory, single?.BackupDirectory, collection?.Directory, collection?.BackupDirectory }
            .OfType<string>().ToArray();
        var before = Fingerprints(tracked);
        foreach (var cell in Languages.Concat(Languages.Take(1))) // Return to English on the same instance, not a new dialog.
        {
            await SetLanguageAsync(host, page, cell.Code);
            Require(Equals(Need<Button>(page, "ArchiveExportStart").Content, cell.Start)
                && Texts.T("archive.exportWorking") == cell.Working, cell.Code + ": independent label/locale anchor mismatch.");
            Require(ReferenceEquals(status, Need<TextBlock>(page, "ArchiveExportStatus"))
                && ReferenceEquals(error, Need<TextBlock>(page, "ArchiveExportError"))
                && status.Text == expectedStatus() && error.Text == expectedError(), scenario + "/" + cell.Code + ": stale outcome text or replaced controls.");
            Require(status.IsEffectivelyVisible && Need<Border>(page, "ArchiveExportDialog").IsEffectivelyVisible
                && Need<Ursa.Controls.Banner>(page, "ArchiveExportErrorBanner").IsVisible == (expectedError().Length > 0)
                && (expectedError().Length == 0 || error.IsEffectivelyVisible), scenario + ": outcome visibility mismatch.");
            Require(Equals(Need<Button>(page, "ArchiveExportClose").Content, Texts.T(running ? "archive.exportCancel" : "action.close"))
                && Need<TextBlock>(page, "ArchiveExportTargetLabel").Text == Texts.T("export.target")
                && Need<TextBlock>(page, "ArchiveExportOptionsLabel").Text == Texts.T("export.options"), scenario + ": live labels stale.");
            Require(page.IsExportRunning == running && Volatile.Read(ref boundary.Calls) == calls
                && ReferenceEquals(single, page.LastExport) && ReferenceEquals(collection, page.LastCollectionExport)
                && Need<TextBox>(page, "ArchiveExportDestination").Text == destination
                && Need<CheckBox>(page, "ArchiveExportMedia").IsChecked == media
                && Need<CheckBox>(page, "ArchiveExportBackup").IsChecked == backup
                && Need<RadioButton>(page, "ArchiveExportUpdate").IsChecked == updating
                && ReferenceEquals(Need<ComboBox>(page, "ArchiveExportHistory").SelectedItem, selected), scenario + ": language change mutated work/result/draft/history.");
            Require(Need<Button>(page, "ArchiveExportStart").IsEnabled == !running
                && Need<Button>(page, "ArchiveExportOpen").IsEnabled == (!running && (single != null || collection != null)),
                scenario + ": language change altered busy/result actions.");
            Require(before.SequenceEqual(Fingerprints(tracked)), scenario + ": language change rewrote published or backup bytes.");
            Console.WriteLine("SMOKE export status language cell: " + JsonSerializer.Serialize(new
            {
                scenario, language = cell.Code, status = status.Text, diagnosis = error.Text,
                running, readerCalls = calls, destination, publishedFiles = before.Length
            }));
        }
    }

    private static KeyValuePair<string, string>[] Fingerprints(IEnumerable<string> directories) => directories
        .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => new KeyValuePair<string, string>(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
        .ToArray();

    private static async Task SetLanguageAsync(Window host, ArchivePage page, string language)
    {
        Dispatcher.UIThread.VerifyAccess();
        Texts.SetLanguage(language);
        await page.ReaderLocalizationTask.WaitAsync(Deadline);
        await Dispatcher.UIThread.InvokeAsync(() => host.UpdateLayout(), DispatcherPriority.Background);
        Dispatcher.UIThread.VerifyAccess();
        Require(Texts.Language == language, "Requested language not active: " + language);
    }

    private static void Click(ArchivePage page, string name)
    {
        Dispatcher.UIThread.VerifyAccess();
        Button button = Need<Button>(page, name);
        Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "Production action is unavailable: " + name);
        if (button.Command != null) Require(button.Command.CanExecute(button.CommandParameter), "Production command rejects action: " + name);
        SmokeActionClick.Execute(button);
    }
    private static void Close(ArchivePage page)
    {
        Click(page, "ArchiveExportClose");
        Require(!Need<Border>(page, "ArchiveExportDialog").IsVisible, "Settled dialog did not close.");
    }
    private static async Task WaitUntilAsync(Func<bool> predicate, string phase)
    {
        using CancellationTokenSource budget = new(Deadline);
        try { while (!predicate()) await Task.Delay(25, budget.Token); }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        { throw new TimeoutException("Export-status smoke timed out: " + phase); }
        Dispatcher.UIThread.VerifyAccess();
    }
    private static string Member(ResourceId id) => id.Source + " /" + id.Board + "/ " + id.Thread;
    private static string Error(ArchivePage page) => Need<TextBlock>(page, "ArchiveExportError").Text ?? string.Empty;
    private static T Need<T>(ArchivePage page, string name) where T : Control =>
        page.FindControl<T>(name) ?? throw new InvalidOperationException("Missing production export control: " + name);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
