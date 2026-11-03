using System.Net;
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

// Root invokes this on the UI thread after Ready/emptyGate, in the isolated smoke process.
// Real ArchivePage dialog + managed Start click + published files, not reflective handler calls.
internal static class HtmlExportLanguageChecks
{
    private static readonly ResourceId First = new("html-language-fixture", "local", "101");
    private static readonly ResourceId Second = new("html-language-fixture", "local", "202");
    private const string SourceTitle = "Synthetic <source> & title";
    private const string SourceBody = "Saved media & original source text — not a translated wrapper.";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(15);
    // Incomplete cancellation is not permission to dispose the fixture's ownership.
    private sealed record IncompleteCleanup(string Root, ArchivePage? Page, Window? Host, ProfileLease? Lease);
    private static readonly List<IncompleteCleanup> IncompleteCleanups = new();

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string originalLanguage = Texts.Language;
        foreach (string language in new[] { "en", "es", "fr", "ru" })
            Require(Texts.Languages.Any(item => item.Code == language), "Bundled language unavailable: " + language);

        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-html-language-smoke-");
        string profile = Path.Combine(root, "profile");
        string exports = Path.Combine(root, "exports");
        Directory.CreateDirectory(exports);
        ArchivePage? page = null;
        Window? host = null;
        ProfileLease? lease = null;
        TaskCompletionSource? readerGate = null;
        TaskCompletionSource? readerEntered = null;
        Exception? failure = null;
        void CleanupFailed(string phase, Exception error)
        {
            var cleanup = new InvalidOperationException($"SMOKE HTML export language cleanup {phase}; fixture: {root}", error);
            Console.Error.WriteLine(cleanup);
            failure = failure == null ? cleanup : new AggregateException("HTML-language smoke and cleanup failures.", failure, cleanup);
        }
        Console.WriteLine("SMOKE HTML export language fixture: " + root);
        try
        {
            lease = ProfileLease.Acquire(profile);
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                foreach (ResourceId id in new[] { First, Second })
                    await catalog.CaptureAsync(new CapturedThreadSnapshot(id,
                        "https://offline.invalid/local/" + id.Thread, SourceTitle,
                        ThreadSourceState.Archived, DateTimeOffset.UtcNow, profile,
                        [new CapturedPost(id.Thread!, 0, PostFieldPresence.All, Author: "Synthetic author",
                            Html: "<p>" + WebUtility.HtmlEncode(SourceBody) + "</p>")]));
            }

            page = new ArchivePage(async token =>
            {
                TaskCompletionSource? gate = readerGate;
                if (gate != null)
                {
                    readerEntered?.TrySetResult();
                    await gate.Task.WaitAsync(token);
                }
                return await ArchiveReader.OpenAsync(profile, token);
            }, (_, _) => throw new InvalidOperationException("Text-only smoke must not alter capture settings."),
                _ => throw new InvalidOperationException("Smoke must not launch an external folder or URL."))
            {
                IsAvailable = true,
                LastExportDirectoryPreference = exports,
                ArchiveColumnModePreference = "Simple"
            };
            host = new Window { Title = "HTML export language — synthetic offline journey", Width = 1000, Height = 760, Content = page };
            host.Show(owner);
            await page.ActivateAsync();
            await page.LoadThreadAsync(First);
            await page.MediaLoadTask;
            Require(page.CurrentThread?.Summary.Id == First, "Synthetic reader did not load its own archive.");

            foreach (var cell in new[]
            {
                (Code: "es", Result: "Resultado de la exportación", Collection: "Colección de MeowChan", Back: "Página inicial de la colección"),
                (Code: "fr", Result: "Résultat de l’exportation", Collection: "Collection MeowChan", Back: "Page d’accueil de la collection"),
                (Code: "ru", Result: "Результат экспорта", Collection: "Коллекция MeowChan", Back: "Начальная страница коллекции")
            })
            {
                foreach (bool collection in new[] { false, true })
                {
                    // Open in English, then select the target language on this same live dialog.
                    Texts.SetLanguage("en");
                    await page.ReaderLocalizationTask;
                    if (collection) await page.OpenExportForThreadsAsync([First, Second], null);
                    else await page.OpenExportForThreadAsync(First, null);
                    Require(Need<Border>(page, "ArchiveExportDialog").IsVisible, "Actual export dialog did not open.");
                    string destination = Path.Combine(exports, cell.Code + (collection ? "-collection" : "-single"));
                    Need<TextBox>(page, "ArchiveExportDestination").Text = destination;
                    Need<CheckBox>(page, "ArchiveExportMedia").IsChecked = false;
                    Texts.SetLanguage(cell.Code);
                    await page.ReaderLocalizationTask;
                    host.UpdateLayout();
                    Button start = Need<Button>(page, "ArchiveExportStart");
                    Require(Texts.Language == cell.Code && Equals(start.Content, Texts.T("archive.exportStart")),
                        cell.Code + ": live dialog did not reflect the selected language.");
                    Require(Need<TextBox>(page, "ArchiveExportDestination").Text == destination
                        && Need<CheckBox>(page, "ArchiveExportMedia").IsChecked == false,
                        "Language refresh replaced the export destination/options draft.");
                    Require(start.IsEffectivelyVisible && start.IsEffectivelyEnabled,
                        "Production Start action is not visible and enabled.");

                    // Hold the real reader boundary after Start. Changing the UI language now must
                    // not alter the already frozen language or mix collection index/member locales.
                    readerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    readerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    try
                    {
                        SmokeActionClick.Execute(start);
                        Require(page.IsExportRunning, "Managed Start did not enter the actual export handler.");
                        await readerEntered.Task.WaitAsync(Deadline);
                        Texts.SetLanguage("en");
                        await page.ReaderLocalizationTask;
                    }
                    finally { readerGate.TrySetResult(); }
                    await Program.WaitUntilAsync(() => !page.IsExportRunning);
                    readerGate = null;
                    readerEntered = null;
                    string error = Need<TextBlock>(page, "ArchiveExportError").Text ?? string.Empty;
                    Require(string.IsNullOrEmpty(error), "Actual export handler reported an error: " + error);

                    if (collection)
                    {
                        ArchiveCollectionHtmlExportResult result = page.LastCollectionExport
                            ?? throw new InvalidOperationException("Collection did not publish: " + Status(page));
                        Require(page.LastExport == null && result.Directory == destination && result.ThreadCount == 2
                            && result.PostCount == 2 && result.FileCount == 0 && result.Omissions.Count == 0 && result.Warnings.Count == 0,
                            "Collection result identity/counts changed.");
                        ArchiveCollectionExportInspection persisted = await ArchiveCollectionHtmlExporter.InspectAsync(destination, [First, Second]);
                        Require(!persisted.RequiresBackup && persisted.Metadata.ExportId == result.Metadata!.ExportId
                            && persisted.Metadata.Generation == 1 && persisted.Metadata.Options.Language == cell.Code
                            && !persisted.Metadata.Options.IncludeMedia && result.Metadata.Options.Language == cell.Code
                            && persisted.Metadata.Members.Select(member => member.Thread).SequenceEqual(new[] { First, Second }),
                            "Persisted collection metadata did not retain the press-time language/member set/options.");
                        string index = await File.ReadAllTextAsync(result.IndexPath);
                        CheckLanguage(index, cell.Code);
                        Require(WebUtility.HtmlDecode(index).Contains("<h1>" + cell.Collection + "</h1>", StringComparison.Ordinal)
                            && !index.Contains("MeowChan collection", StringComparison.Ordinal)
                            && index.Contains(WebUtility.HtmlEncode(SourceTitle), StringComparison.Ordinal),
                            "Collection wrapper remained English or altered the escaped source title.");
                        foreach (ArchiveCollectionMember member in persisted.Metadata.Members)
                        {
                            string memberPath = Path.Combine(destination, member.PagePath);
                            string html = await File.ReadAllTextAsync(memberPath);
                            CheckThread(html, cell.Code, cell.Result);
                            Require(WebUtility.HtmlDecode(html).Contains("<nav><a href=\"../index.html\">" + cell.Back + "</a></nav>", StringComparison.Ordinal),
                                "Collection member did not translate its real back-navigation wrapper.");
                            LogFile(cell.Code, "collection-member", memberPath);
                        }
                        LogFile(cell.Code, "collection-index", result.IndexPath);
                        LogFile(cell.Code, "collection-metadata", Path.Combine(destination, ArchiveCollectionExportMetadataFile.Name));
                    }
                    else
                    {
                        ArchiveHtmlExportResult result = page.LastExport
                            ?? throw new InvalidOperationException("Single export did not publish: " + Status(page));
                        Require(page.LastCollectionExport == null && result.Directory == destination && result.PostCount == 1
                            && result.FileCount == 0 && result.Omissions.Count == 0 && result.Warnings.Count == 0,
                            "Single-thread result identity/counts changed.");
                        ArchiveExportInspection persisted = await ArchiveHtmlExporter.InspectAsync(destination, First);
                        Require(!persisted.RequiresBackup && persisted.Metadata.ExportId == result.Metadata!.ExportId
                            && persisted.Metadata.Generation == 1 && persisted.Metadata.Thread == First
                            && persisted.Metadata.Options.Language == cell.Code && !persisted.Metadata.Options.IncludeMedia
                            && result.Metadata.Options.Language == cell.Code,
                            "Persisted single-thread metadata did not retain the press-time language/identity/options.");
                        CheckThread(await File.ReadAllTextAsync(result.IndexPath), cell.Code, cell.Result);
                        LogFile(cell.Code, "single-index", result.IndexPath);
                        LogFile(cell.Code, "single-metadata", Path.Combine(destination, ArchiveExportMetadataFile.Name));
                    }
                    Require(Texts.Language == "en", "Export changed the active language instead of using its snapshot.");
                    Program.CaptureWindow(host, "smoke-html-export-language-" + cell.Code + (collection ? "-collection" : "-single") + ".png");
                    SmokeActionClick.Execute(Need<Button>(page, "ArchiveExportClose"));
                    Require(!Need<Border>(page, "ArchiveExportDialog").IsVisible, "Completed export dialog did not close.");
                    Console.WriteLine($"SMOKE HTML export language cell passed: {cell.Code}/{(collection ? "collection" : "single")}; actual ArchivePage handler, persisted metadata and generated wrappers; managed input only.");
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            readerGate?.TrySetResult();
            bool settled = false;
            try
            {
                if (host != null) host.IsEnabled = false;
                page?.EndLifetime(); // Signals cancellation only, NOT export completion.
                if (page != null) await WaitForExportSettlementAsync(page);
                settled = true;
            }
            catch (Exception error) { CleanupFailed("export settlement failed", error); }

            if (settled)
            {
                // IsExportRunning becomes false in the real single/collection finally,
                // after reader/exporter retirement. Only now detach and release the fixture.
                try { if (host != null) { host.Content = null; host.Close(); } }
                catch (Exception error) { CleanupFailed("window close failed", error); }
                try { lease?.Dispose(); }
                catch (Exception error) { CleanupFailed("lease release failed", error); }
            }
            else
            {
                IncompleteCleanups.Add(new(root, page, host, lease));
                CleanupFailed("cleanup-incomplete: page, host and lease retained until process exit",
                    new TimeoutException($"Cleanup budget {CleanupDeadline}; export-running={page?.IsExportRunning}. Root must terminate the isolated process."));
            }
            try { Texts.SetLanguage(originalLanguage); owner.Activate(); }
            catch (Exception error) { CleanupFailed("language/owner restoration failed", error); }
            // Retain only this small synthetic tree for diagnosis; never delete caller data.
            Console.WriteLine("SMOKE HTML export language retained fixture: " + root);
        }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task WaitForExportSettlementAsync(ArchivePage page)
    {
        using CancellationTokenSource budget = new(CleanupDeadline);
        try
        {
            while (page.IsExportRunning) await Task.Delay(25, budget.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            if (page.IsExportRunning)
                throw new TimeoutException($"The actual export did not settle within {CleanupDeadline} after EndLifetime cancellation.");
        }
    }

    private static void CheckLanguage(string html, string language) =>
        Require(html.Contains("<html lang=\"" + language + "\">", StringComparison.Ordinal)
            && !html.Contains("<html lang=\"en\">", StringComparison.Ordinal), "Generated document language was not frozen at Start.");

    private static void CheckThread(string html, string language, string resultHeading)
    {
        CheckLanguage(html, language);
        Require(WebUtility.HtmlDecode(html).Contains("<h2>" + resultHeading + "</h2>", StringComparison.Ordinal)
            && !html.Contains("<h2>Export result</h2>", StringComparison.Ordinal), "Generated thread wrapper was not translated.");
        Require(html.Contains(WebUtility.HtmlEncode(SourceTitle), StringComparison.Ordinal)
            && WebUtility.HtmlDecode(html).Contains(SourceBody, StringComparison.Ordinal)
            && !html.Contains("<source>", StringComparison.Ordinal), "Export translated or failed to escape synthetic source content.");
    }

    private static void LogFile(string language, string role, string path) =>
        Console.WriteLine("SMOKE HTML export language file: " + JsonSerializer.Serialize(new
        {
            language, role, path, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
        }));

    private static string Status(ArchivePage page) => Need<TextBlock>(page, "ArchiveExportStatus").Text ?? string.Empty;
    private static T Need<T>(ArchivePage page, string name) where T : Control =>
        page.FindControl<T>(name) ?? throw new InvalidOperationException("Missing production export control: " + name);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
