using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.DesktopSmoke;

// Root hook: await NativeTransferV4PreviewChecks.RunAsync(owner), after Ready in
// MEOWCHAN_NATIVE_TRANSFER_PREVIEW_ONLY. This journey owns a real host-backed
// single-thread-v4 import dialog and only synthetic local packages/picker data.
internal static class NativeTransferV4PreviewChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly DateTimeOffset At = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId First = new("4chan", "g", "73001"), Second = new("4chan", "g", "73002");
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("W31 absent original bytes; not playable-video evidence");
    private static readonly byte[] Preview = Encoding.UTF8.GetBytes("W31 synthetic derived preview bytes");
    private static readonly byte[] Poster = CreatePoster();
    private static readonly TransferImportStatusCounts Zero = new(0, 0, 0, 0, 0, 0);

    // Independent numeric oracles: never passed to the production session/dialog.
    private sealed record ExpectedThread(ResourceId Id, int Posts, TransferImportStatusCounts Originals,
        TransferImportStatusCounts Previews, TransferImportStatusCounts Retained,
        TransferImportStatusCounts Derived, TransferImportStatusCounts Posters);
    private static readonly ExpectedThread[] V4Threads =
    [
        new(First, 1, new(0, 1, 0, 0, 0, 0), new(1, 0, 0, 0, 0, 0), new(0, 1, 0, 1, 0, 0),
            new(1, 1, 0, 1, 0, 0), new(2, 0, 0, 1, 0, 0)),
        new(Second, 0, Zero, Zero, new(0, 2, 0, 0, 0, 0), new(1, 1, 0, 0, 0, 0), new(1, 1, 0, 0, 0, 0))
    ];
    private static readonly ExpectedThread[] SingleV4Threads =
    [new(First, 1, new(0, 1, 0, 0, 0, 0), Zero, Zero,
        new(1, 0, 0, 0, 0, 0), new(1, 0, 0, 0, 0, 0))];

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(Environment.GetEnvironmentVariable("MEOWCHAN_NATIVE_TRANSFER_PREVIEW_ONLY") == "1",
            "Run this helper only in Root's isolated native-transfer smoke mode.");
        Require(owner.IsVisible && owner.SessionHost.State == DesktopSessionState.Ready
            && owner.TryGetPlatformHandle()?.Handle is nint native && native != 0,
            "A real visible native owner and naturally Ready host are required.");
        Require(owner.SessionHost.GetThreadWorkSnapshots().Count == 0, "The isolated smoke host must have no active watches.");
        foreach (string code in new[] { "en", "de", "es", "fr", "ru" })
            Require(Texts.Languages.Any(language => language.Code == code), "Missing bundled language: " + code);

        Fixture fixture = await Fixture.CreateAsync();
        bool completed = false;
        try
        {
            await LiveImportDialogAsync(owner, fixture);
            fixture.RequireSourcesUnchanged();
            RequireEmpty(fixture.Work);
            Require(owner.SessionHost.State == DesktopSessionState.Ready
                && owner.SessionHost.GetThreadWorkSnapshots().Count == 0,
                "Import inspection/apply activated source work or left the host non-Ready.");
            completed = true;
        }
        finally
        {
            // Explicit return to EN is part of the smoke contract; no Config save.
            Texts.SetLanguage("en");
            owner.Activate();
            if (completed) fixture.DeleteKnownFixtureFiles();
            else Console.WriteLine("SMOKE W31: synthetic fixture retained after failure; no recursive cleanup attempted.");
        }
        Console.WriteLine("SMOKE W31 v4 import: real host-backed Choose/Inspect/Ready/Apply/Completed, clear multithread/private rejection, cleanup/lifecycle and five-language refresh completed.");
    }

    private static async Task LiveImportDialogAsync(MainWindow owner, Fixture fixture)
    {
        DesktopSessionHost host = owner.SessionHost;
        NativeTransferImportSession session = host.CreateNativeTransferImportSession(fixture.Work);
        int prepared = 0, cleaned = 0, invalidCleanup = 0, picks = 0, commits = 0, sqlCommits = 0;
        string selected = fixture.SingleV4;
        string profileBeforeImport = ProfileSnapshot(host);
        session.AfterPreparationForTests = _ =>
        {
            Interlocked.Increment(ref prepared);
            string child = Directory.GetDirectories(fixture.Work).Single();
            string file = Directory.GetFiles(child).Single();
            Require(Path.GetFileName(child).StartsWith(".meowchan-native-import-", StringComparison.Ordinal)
                && Path.GetFileName(file).EndsWith(".pending", StringComparison.Ordinal)
                && new FileInfo(file).Length == new FileInfo(selected).Length,
                "The real import never retained its complete verified stage at Ready.");
            return Task.CompletedTask;
        };
        session.BeforeDirectoryCleanupForTests = child =>
        {
            if (Path.GetDirectoryName(child) != fixture.Work || Directory.GetFileSystemEntries(child).Length != 0)
                Interlocked.Increment(ref invalidCleanup);
            Interlocked.Increment(ref cleaned);
        };
        host.BeforeNativeImportCommitForTests = () => { Interlocked.Increment(ref commits); return Task.CompletedTask; };
        host.NativeImportCheckpointForTests = phase =>
        {
            if (phase == NativeMediaImportPhase.SqlCommitted) Interlocked.Increment(ref sqlCommits);
        };

        NativeTransferImportDialog? dialog = null;
        Task? shown = null, choosing = null;
        try
        {
            Texts.SetLanguage("de");
            dialog = new(session, async () =>
            {
                ++picks;
                // The native picker is asynchronous. Let the dialog-owned initial
                // Cancel/cleanup drain before InspectAsync is admitted.
                await Task.Delay(25);
                return selected;
            })
            { Width = 760, Height = 620 };
            shown = dialog.ShowDialog(owner);
            await WaitUntilAsync(() => dialog.IsVisible && owner.OwnedWindows.Contains(dialog)
                && dialog.TryGetPlatformHandle()?.Handle is nint handle && handle != 0);
            Require(dialog.DisplayForTests.State == NativeTransferImportState.Idle
                && !dialog.ApplyButton.IsEnabled && dialog.ChooseButton.IsEnabled,
                "Import dialog did not start idle with Apply disabled.");

            // Actual dialog Choose -> host Inspect -> Ready. The source is a writer-validated
            // v4 package; no preview-only or hand-built snapshot is admitted here.
            choosing = dialog.ChooseForTestsAsync();
            await choosing.WaitAsync(Deadline);
            NativeTransferImportSnapshot ready = session.Current;
            CheckReady(ready, dialog, SingleV4Threads, new(First, false, 1, 0, 1, 0, 1, 2, 2));
            Require(picks == 1 && prepared == 1 && !ready.CleanupRetained,
                "Choose/Inspect did not reach one retained Ready stage.");
            CheckRenderedImport(dialog, ready, SingleV4Threads, 2, Preview.LongLength + Poster.LongLength);

            // Language changes act on the same open dialog/session. New ES/FR/RU keys may
            // use the bundled English fallback, but raw keys and stale controls are forbidden.
            foreach (string code in new[] { "en", "de", "es", "fr", "ru", "en" })
            {
                Texts.SetLanguage(code);
                await SettleAsync(dialog);
                Require(Texts.Language == code && Texts.Warnings.Count == 0, code + ": language fallback/warning.");
                Require(ReferenceEquals(ready, session.Current) && picks == 1 && dialog.IsVisible,
                    code + ": language refresh replaced or reinspected the live import plan.");
                CheckRenderedImport(dialog, ready, SingleV4Threads, 2, Preview.LongLength + Poster.LongLength);
                CheckImportActions(dialog, applyEnabled: true);
                CheckNoRawLocalizationKeys(dialog);
            }

            // Apply through the real dialog command. The host must commit exactly once and
            // cleanup must finish before Completed is exposed.
            string selectedSourceHash = Hash(File.ReadAllBytes(selected));
            await dialog.ApplyForTestsAsync().WaitAsync(Deadline);
            NativeTransferImportSnapshot done = session.Current;
            Require(done.State == NativeTransferImportState.Completed && done.Error == NativeTransferImportError.None
                && done.Generation == ready.Generation && done.Result is { Status: NativeMediaImportCommitStatus.Committed }
                && !done.CleanupRetained && done.Inventory != null && done.Plan != null,
                "Ready Apply did not settle as Completed with a committed result.");
            Require(done.Result!.NewThreads == 1 && done.Result.NewPosts == 1 && done.Result.NewAttachments == 1
                && done.Result.EvidenceAdditions == 1 && done.Result.DerivedDescriptorAdditions == 2
                && done.Result.ContainedDerivedAdditions == 2 && !done.Result.IsReplay,
                "Completed result did not report the actual single-thread-v4 materialization.");
            Require(commits == 1 && sqlCommits == 1 && prepared == 1 && cleaned == 1 && invalidCleanup == 0,
                "Apply did not cross one SQL commit and one complete owned cleanup boundary.");
            RequireEmpty(fixture.Work);
            Require(Hash(File.ReadAllBytes(selected)) == selectedSourceHash,
                "Apply changed the selected source package.");
            NativeTransferImportDialog completedDialog = dialog ?? throw new InvalidOperationException("Import dialog disappeared before Completed.");
            CheckRenderedImport(completedDialog, done, SingleV4Threads, 2, Preview.LongLength + Poster.LongLength);
            CheckImportActions(completedDialog, applyEnabled: false);
            Require(completedDialog.StateText.Text == Texts.T("transfer.import.completed")
                && (completedDialog.SummaryText.Text ?? string.Empty).Contains(Texts.T("transfer.import.result", 1, 1, 0, 1, 0), StringComparison.Ordinal),
                "Completed result was not rendered in the real dialog.");
            string profileAfterImport = ProfileSnapshot(host);
            Require(profileAfterImport != profileBeforeImport, "A successful Apply did not change the isolated archive profile.");

            // A valid multi-thread v4 package reaches inventory but is not admitted by the
            // current host contract. Apply stays disabled and no profile write is allowed.
            selected = fixture.Public;
            choosing = dialog.ChooseForTestsAsync();
            await choosing.WaitAsync(Deadline);
            NativeTransferImportSnapshot unsupported = session.Current;
            Require(unsupported.State == NativeTransferImportState.Unsupported
                && unsupported.Error == NativeTransferImportError.UnsupportedPackage
                && unsupported.Inventory != null && unsupported.Plan == null && unsupported.Result == null
                && !unsupported.CleanupRetained,
                "Multi-thread v4 did not receive a clear unsupported-package outcome.");
            CheckInventory(unsupported, V4Threads, 2, Preview.LongLength + Poster.LongLength);
            Require(dialog.StateText.Text == Texts.T("transfer.import.unsupported")
                && dialog.ErrorText.Text == Texts.T("transfer.import.error.package")
                && !dialog.ApplyButton.IsEnabled && commits == 1 && sqlCommits == 1,
                "Unsupported multi-thread import exposed Apply or a write-capable state.");
            CheckImportActions(dialog, applyEnabled: false);
            CheckNoRawLocalizationKeys(dialog);
            Require(cleaned == 2 && invalidCleanup == 0 && prepared == 1,
                "Unsupported multi-thread inspection did not clean its owned stage.");
            RequireEmpty(fixture.Work);
            Require(ProfileSnapshot(host) == profileAfterImport, "Unsupported package changed the archive profile.");

            // Private metadata is rejected before inventory is exposed; the message is
            // specific and the previous completed inventory must not remain visible.
            selected = fixture.Private;
            choosing = dialog.ChooseForTestsAsync();
            await choosing.WaitAsync(Deadline);
            NativeTransferImportSnapshot privatePackage = session.Current;
            Require(privatePackage.State == NativeTransferImportState.Unsupported
                && privatePackage.Error == NativeTransferImportError.PrivateMetadataUnsupported
                && privatePackage.Inventory == null && privatePackage.Plan == null && privatePackage.Result == null
                && !privatePackage.CleanupRetained,
                "Private package was not specifically rejected without inventory.");
            Require(dialog.StateText.Text == Texts.T("transfer.import.unsupported")
                && dialog.ErrorText.Text == Texts.T("transfer.import.error.private")
                && dialog.SummaryText.Text == string.Empty
                && Named(dialog, "NativeTransferImportDetails").Text == string.Empty
                && !dialog.ApplyButton.IsEnabled && commits == 1 && sqlCommits == 1,
                "Private rejection left stale inventory or a write-capable state.");
            CheckImportActions(dialog, applyEnabled: false);
            CheckNoRawLocalizationKeys(dialog);
            Require(cleaned == 3 && invalidCleanup == 0 && prepared == 1,
                "Private inspection did not clean its owned rejected stage.");
            RequireEmpty(fixture.Work);
            Require(ProfileSnapshot(host) == profileAfterImport, "Private package changed the archive profile.");
        }
        finally
        {
            try
            {
                if (dialog == null) await session.DisposeAsync().AsTask().WaitAsync(Deadline);
                else
                {
                    dialog.Close();
                    await dialog.TerminateAsync().WaitAsync(Deadline);
                    if (choosing != null) await choosing.WaitAsync(Deadline);
                    if (shown != null) await shown.WaitAsync(Deadline);
                    Require(ReferenceEquals(dialog.TerminateAsync(), dialog.CleanupCompletion)
                        && dialog.SessionDisposalCountForTests == 1 && !dialog.IsLanguageSubscribedForTests
                        && !dialog.IsVisible && session.Current.State == NativeTransferImportState.Closed,
                        "Native import Closed/finally did not share one terminal session drain and unsubscribe.");
                    string? state = dialog.StateText.Text;
                    string? title = dialog.Title;
                    object? choose = dialog.ChooseButton.Content;
                    Texts.SetLanguage("de");
                    await SettleAsync(owner);
                    Require(dialog.StateText.Text == state && dialog.Title == title
                        && Equals(dialog.ChooseButton.Content, choose),
                        "A closed import dialog still responded to language changes.");
                }
            }
            finally
            {
                host.BeforeNativeImportCommitForTests = null;
                host.NativeImportCheckpointForTests = null;
            }
        }
    }

    private static void CheckReady(NativeTransferImportSnapshot snapshot, NativeTransferImportDialog dialog,
        ExpectedThread[] threads, NativeTransferImportPlanSummary expectedPlan)
    {
        Require(snapshot.State == NativeTransferImportState.Ready && snapshot.Error == NativeTransferImportError.None
            && snapshot.Generation > 0 && snapshot.Inventory != null && snapshot.Plan == expectedPlan
            && snapshot.Result == null && !snapshot.CleanupRetained,
            "Real single-thread-v4 inspection did not settle as Ready with the expected plan.");
        CheckInventory(snapshot, threads, 2, Preview.LongLength + Poster.LongLength);
        Require(dialog.DisplayForTests == snapshot && dialog.StateText.Text == Texts.T("transfer.import.ready")
            && dialog.ErrorText.Text == string.Empty && dialog.ApplyButton.IsEnabled,
            "Ready snapshot was not rendered or did not enable Apply.");
    }

    private static void CheckInventory(NativeTransferImportSnapshot snapshot, ExpectedThread[] threads,
        int payloads, long bytes)
    {
        Require(snapshot.Inventory != null && !snapshot.CleanupRetained,
            "Import inventory was missing or retained unsafe cleanup state.");
        TransferImportPreview actual = snapshot.Inventory!;
        Require(actual.Threads.Length == threads.Length && actual.Posts == threads.Sum(t => t.Posts)
            && actual.UniquePayloads == payloads && actual.UniquePayloadBytes == bytes,
            "Inventory invented posts or counted payload aliases/expected original sizes as unique bytes.");
        CheckCounts(actual.CurrentOriginals, Sum(threads, t => t.Originals), "aggregate originals");
        CheckCounts(actual.CurrentPreviews, Sum(threads, t => t.Previews), "aggregate legacy previews");
        CheckCounts(actual.RetainedBindings, Sum(threads, t => t.Retained), "aggregate retained");
        CheckCounts(actual.DerivedPreviews, Sum(threads, t => t.Derived), "aggregate derived previews");
        CheckCounts(actual.VideoPosters, Sum(threads, t => t.Posters), "aggregate video posters");
        for (int i = 0; i < threads.Length; ++i)
        {
            ExpectedThread expected = threads[i];
            TransferImportThreadPreview item = actual.Threads[i];
            Require(item.Id == expected.Id && item.Posts == expected.Posts, "Inventory fabricated a current post/thread.");
            CheckCounts(item.CurrentOriginals, expected.Originals, "thread originals");
            CheckCounts(item.CurrentPreviews, expected.Previews, "thread legacy previews");
            CheckCounts(item.RetainedBindings, expected.Retained, "thread retained");
            CheckCounts(item.DerivedPreviews, expected.Derived, "thread derived previews");
            CheckCounts(item.VideoPosters, expected.Posters, "thread video posters");
        }
    }

    private static void CheckRenderedImport(NativeTransferImportDialog dialog, NativeTransferImportSnapshot snapshot,
        ExpectedThread[] threads, int payloads, long bytes)
    {
        string Format(string key, params object?[] values) => string.Format(Texts.Culture, Texts.T(key), values);
        string Counts(string key, TransferImportStatusCounts c) => Format(key, c.Contained, c.Omitted, c.Unknown, c.Unavailable, c.Failed, c.Cancelled);
        TransferImportPreview inventory = snapshot.Inventory!;
        string planText = snapshot.Plan is { } plan
            ? plan.IsMerge
                ? Format("transfer.import.planMerge", plan.NewPosts, plan.UpdatedPosts, plan.NewAttachments,
                    plan.Conflicts, plan.EvidenceAdditions, plan.DerivedDescriptorAdditions)
                : Format("transfer.import.planNew", plan.NewPosts, plan.NewAttachments,
                    plan.EvidenceAdditions, plan.DerivedDescriptorAdditions)
            : string.Empty;
        string resultText = snapshot.Result is { } result
            ? Format("transfer.import.result", result.NewThreads, result.NewPosts,
                result.UpdatedPosts, result.NewAttachments, result.Conflicts)
                + (result.IsReplay ? Environment.NewLine + Texts.T("transfer.import.replay") : string.Empty)
            : string.Empty;
        // The production dialog intentionally keeps the empty plan/result slots in its
        // five-line summary; retain that shape so the smoke detects accidental UI drift.
        string expected = string.Join(Environment.NewLine,
            Format("transfer.import.summary", threads.Length, threads.Sum(t => t.Posts), payloads, bytes),
            Counts("transfer.import.originals", Sum(threads, t => t.Originals)),
            Counts("transfer.import.previews", Sum(threads, t => t.Previews)),
            Counts("transfer.import.retained", Sum(threads, t => t.Retained)), planText, resultText);
        string details = string.Join(Environment.NewLine, threads.Select(thread => Format("transfer.import.thread",
            thread.Id.Source, thread.Id.Board, thread.Id.Thread, thread.Posts,
            Counts("transfer.import.originals", thread.Originals),
            Counts("transfer.import.previews", thread.Previews),
            Counts("transfer.import.retained", thread.Retained))));
        string notice = Texts.T("transfer.import.inventory") + Environment.NewLine + Texts.T("transfer.import.privacy");
        Require(dialog.SummaryText.Text == expected && Named(dialog, "NativeTransferImportDetails").Text == details
            && dialog.NoticeText.Text == notice && inventory != null,
            Texts.Language + ": real import dialog summary/details differ from the independent inventory.");
        Require(dialog.Title == Texts.T("transfer.import.title")
            && dialog.StateText.Text == Texts.T(snapshot.State switch
            {
                NativeTransferImportState.Ready => "transfer.import.ready",
                NativeTransferImportState.Completed => "transfer.import.completed",
                _ => "transfer.import.unsupported"
            }), Texts.Language + ": import state/title did not refresh.");
    }

    private static void CheckImportActions(NativeTransferImportDialog dialog, bool applyEnabled)
    {
        Button[] buttons = dialog.GetVisualDescendants().OfType<Button>()
            .Where(button => !button.GetVisualAncestors().Any(a => a is WindowChromeBar or ScrollBar)).ToArray();
        Require(buttons.Length == 4 && buttons.Contains(dialog.ChooseButton) && buttons.Contains(dialog.CancelButton)
            && buttons.Contains(dialog.ApplyButton) && buttons.Contains(dialog.CloseButton),
            "Import dialog exposed a missing or extra action.");
        Require(dialog.ChooseButton.IsEffectivelyEnabled && dialog.CloseButton.IsEffectivelyEnabled
            && !dialog.CancelButton.IsEnabled && dialog.ApplyButton.IsEffectivelyEnabled == applyEnabled,
            "Import dialog action enabled state does not match the host state.");
        foreach ((Button button, string key) in new[] { (dialog.ChooseButton, "transfer.import.choose"),
            (dialog.CancelButton, "transfer.import.cancel"), (dialog.ApplyButton, "transfer.import.apply"),
            (dialog.CloseButton, "transfer.import.close") })
            Require(Equals(button.Content, Texts.T(key)) && AutomationProperties.GetName(button) == Texts.T(key),
                "Import action label/accessibility name did not follow the open-dialog language.");
    }

    private static void CheckNoRawLocalizationKeys(NativeTransferImportDialog dialog)
    {
        foreach (TextBlock label in dialog.GetVisualDescendants().OfType<TextBlock>())
            Require(!(label.Text ?? string.Empty).Contains("transfer.import.", StringComparison.Ordinal)
                && !Regex.IsMatch(label.Text ?? string.Empty, @"\{\d+(?:[^}]*)\}"),
                "Import dialog exposed a raw localization key or placeholder.");
    }

    private static void CheckCounts(TransferImportStatusCounts actual, TransferImportStatusCounts expected, string scene) =>
        Require(actual == expected, scene + ": expected " + expected + ", got " + actual);
    private static TransferImportStatusCounts Sum(ExpectedThread[] threads, Func<ExpectedThread, TransferImportStatusCounts> select)
    {
        TransferImportStatusCounts[] counts = threads.Select(select).ToArray();
        return new(counts.Sum(c => c.Contained), counts.Sum(c => c.Omitted), counts.Sum(c => c.Unknown),
            counts.Sum(c => c.Unavailable), counts.Sum(c => c.Failed), counts.Sum(c => c.Cancelled));
    }
    private static async Task SettleAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("W31 native import window watchdog expired.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
    private static TextBlock Named(Window window, string name) => window.GetVisualDescendants().OfType<TextBlock>()
        .Single(label => label.Name == name);
    private static void RequireEmpty(string path) => Require(Directory.GetFileSystemEntries(path).Length == 0,
        "Owned staging child/files remain before fixture teardown.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Logical database state, not racy SQLite/WAL file hashes. Reads only; no seeding/SQL mutations.
    private static string ProfileSnapshot(DesktopSessionHost host)
    {
        List<string> parts = [];
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(host.ProfileDirectory, "archive.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using (SqliteTransaction transaction = connection.BeginTransaction(deferred: true))
        {
            foreach (string table in new[] { "archive_threads", "archive_posts", "archive_attachments", "archive_media_directories",
                "archive_media_import_operations", "archive_media_import_receipts", "archive_media_merge_operations",
                "archive_media_merge_receipts", "archive_import_candidates", "archive_import_receipts", "media_decisions", "archive_import_fence" })
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT * FROM " + table; // Fixed test-authored names only.
                using SqliteDataReader reader = command.ExecuteReader();
                List<string> rows = [];
                while (reader.Read())
                {
                    Require(rows.Count < 256, "Expected a bounded isolated smoke profile, not user content.");
                    rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                        .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
                }
                parts.Add(table + ":" + string.Join(";", rows.Order(StringComparer.Ordinal)));
            }
            transaction.Commit();
        }
        string settings = Path.Combine(host.ProfileDirectory, "settings.json");
        parts.Add("settings:" + Hash(File.ReadAllBytes(settings)));
        foreach (string path in new[] { host.Config.DownloadPath, Path.Combine(host.ProfileDirectory, "import-media-operations"),
            Path.Combine(host.ProfileDirectory, "merge-media-operations") })
        {
            Require(Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(host.ProfileDirectory) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal), "Media observation must stay inside the runner's isolated profile.");
            parts.Add(path + ":" + Directory.Exists(path));
            if (!Directory.Exists(path)) continue;
            foreach (string entry in Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                parts.Add(Path.GetRelativePath(path, entry) + (Directory.Exists(entry) ? "/" : ":" + Hash(File.ReadAllBytes(entry))));
        }
        return string.Join("\n", parts);
    }

    private sealed class Fixture
    {
        internal string Root { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-v4-preview-smoke-");
        internal string Work { get; }
        internal string Public => Path.Combine(Root, "public-v4.zip");
        internal string Private => Path.Combine(Root, "private-v4.zip");
        internal string SingleV4 => Path.Combine(Root, "single-v4.zip");
        private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
        private Fixture() { Work = Directory.CreateDirectory(Path.Combine(Root, "trusted-work")).FullName; }

        internal static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            TransferAttachment current = Derived(Media(First, "15"), TransferContentStatus.Contained, TransferContentStatus.Contained);
            TransferAttachment oldPreview = new(new(1, "112", "preview", "https://i.4cdn.org/g/112s.jpg", "preview.jpg"),
                TransferMediaRole.Preview, TransferContentStatus.Contained, Hash(Preview), null);
            TransferThread first = Thread(First, Post(current, oldPreview)) with
            {
                RetainedBindings = [
                    new("999", Derived(Media(First, "999"), TransferContentStatus.Omitted, TransferContentStatus.Unavailable)),
                    new("998", Derived(Media(First, "998", TransferContentStatus.Unavailable), TransferContentStatus.Unavailable, TransferContentStatus.Contained))]
            };
            TransferThread second = Thread(Second) with
            {
                RetainedBindings = [
                    new("997", Derived(Media(Second, "997"), TransferContentStatus.Contained, TransferContentStatus.Contained)),
                    new("996", Derived(Media(Second, "996"), TransferContentStatus.Omitted, TransferContentStatus.Omitted))]
            };
            NativeTransferCatalog single = new(At, [Thread(First, Post(current))]);
            await fixture.WriteAsync(fixture.Public, new(At, [first, second]), [Preview, Poster], v4: true);
            await fixture.WriteAsync(fixture.Private, single, [Preview, Poster], v4: true,
                privateMetadata: new([First], new(true, false, false), [First], [], []));
            await fixture.WriteAsync(fixture.SingleV4, single, [Preview, Poster], v4: true);
            return fixture;
        }

        private async Task WriteAsync(string path, NativeTransferCatalog catalog, byte[][] payloads, bool v4,
            NativePrivateMetadataProjection? privateMetadata = null)
        {
            using MemoryStream bytes = new();
            TransferPayloadSource[] sources = payloads.Select(p => new TransferPayloadSource(new(p.Length, Hash(p)),
                _ => Task.FromResult<Stream>(new MemoryStream(p, writable: false)))).ToArray();
            TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(bytes, catalog, sources, Guid.NewGuid(),
                privateMetadata: privateMetadata).WaitAsync(Deadline);
            Require(written.IsComplete && written.Package != null && written.Package.IsChanV4 == v4,
                "Actual writer failed to produce the requested valid package: " + written.Error);
            Require((written.Package!.PrivateMetadata != null) == (privateMetadata != null), "Private fixture was not actually private.");
            byte[] output = bytes.ToArray();
            using (ZipArchive zip = new(new MemoryStream(output, writable: false), ZipArchiveMode.Read))
            {
                Require(zip.GetEntry("payload/" + Hash(Original)) == null, "Fixture accidentally includes the supposed absent original.");
                Require(zip.Entries.Count(entry => entry.FullName.StartsWith("payload/", StringComparison.Ordinal)) == payloads.Length,
                    "Writer package did not contain exactly one entry per distinct selected payload hash.");
                foreach (byte[] payload in payloads)
                {
                    using Stream input = (zip.GetEntry("payload/" + Hash(payload))
                        ?? throw new InvalidOperationException("Contained derived payload missing from actual ZIP.")).Open();
                    using MemoryStream actual = new();
                    input.CopyTo(actual);
                    Require(actual.ToArray().SequenceEqual(payload), "ZIP contains different derived bytes.");
                }
            }
            File.WriteAllBytes(path, output);
            _hashes.Add(path, Hash(output));
        }
        internal void RequireSourcesUnchanged()
        {
            Require(Directory.GetFiles(Root).Order(StringComparer.Ordinal).SequenceEqual(_hashes.Keys.Order(StringComparer.Ordinal)),
                "Inspection created/deleted files beside the original packages.");
            foreach ((string path, string hash) in _hashes)
                Require(File.Exists(path) && Hash(File.ReadAllBytes(path)) == hash, "Inspection changed/deleted its original package.");
        }
        internal void DeleteKnownFixtureFiles()
        {
            // Runtime teardown only, after every actual operation/terminal task has joined.
            RequireEmpty(Work);
            foreach (string path in _hashes.Keys) File.Delete(path);
            Directory.Delete(Work, recursive: false);
            Directory.Delete(Root, recursive: false);
        }
    }

    private static TransferThread Thread(ResourceId id, params TransferPost[] posts) => new(
        id, "https://boards.4chan.org/" + id.Board + "/thread/" + id.Thread, "W31_INERT_TITLE",
        ThreadSourceState.NotFound, At, null, null, null, posts.ToImmutableArray());
    private static TransferPost Post(params TransferAttachment[] attachments) => new(new("15", 1, PostFieldPresence.All,
        Subject: "W31_INERT_SUBJECT", Html: "<b>W31_INERT_BODY</b>", Attachments: attachments.Select(a => a.Metadata).ToImmutableArray()), attachments.ToImmutableArray());
    private static TransferAttachment Media(ResourceId id, string post, TransferContentStatus status = TransferContentStatus.Omitted)
    {
        TransferOriginalByteVersion version = new(Original.Length, Hash(Original), Convert.ToBase64String(MD5.HashData(Original)),
            Convert.ToBase64String(SHA1.HashData(Original)));
        MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/g/111.webm", "synthetic.webm", 987654321, version.Md5, version.Sha256);
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(id, post, 0, provider) };
        return new(metadata, TransferMediaRole.Original, status, null, null) { OriginalByteVersion = version };
    }
    private static TransferAttachment Derived(TransferAttachment source, TransferContentStatus preview, TransferContentStatus poster) => source with
    {
        DerivedRepresentations = [
            new(TransferDerivedRepresentationKind.Preview, source.Metadata.AttachmentRevisionKey!, source.OriginalByteVersion!, preview,
                preview == TransferContentStatus.Contained ? Hash(Preview) : null),
            new(TransferDerivedRepresentationKind.VideoPosterPng, source.Metadata.AttachmentRevisionKey!, source.OriginalByteVersion!, poster,
                poster == TransferContentStatus.Contained ? Hash(Poster) : null)]
    };

    // Real bounded 1x1 grayscale PNG with CRCs and a valid deflate row. No decoder,
    // thumbnail generator, original-video availability or playback claim is made.
    private static byte[] CreatePoster()
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        byte[] Chunk(string type, byte[] data)
        {
            byte[] result = new byte[data.Length + 12];
            BinaryPrimitives.WriteUInt32BigEndian(result, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
            data.CopyTo(result, 8);
            uint crc = uint.MaxValue;
            foreach (byte value in result.AsSpan(4, data.Length + 4))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), ~crc);
            return result;
        }
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", header),
            .. Chunk("IDAT", [0x78, 0x9c, 0x63, 0x60, 0, 0, 0, 2, 0, 1]), .. Chunk("IEND", [])];
    }
}
