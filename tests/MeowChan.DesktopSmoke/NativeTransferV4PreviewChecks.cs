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
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
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
// MEOWCHAN_NATIVE_TRANSFER_PREVIEW_ONLY. Real native windows, writer, staging and
// host; synthetic local packages/picker only. No injected inventory or admission.
internal static class NativeTransferV4PreviewChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly DateTimeOffset At = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId First = new("4chan", "g", "73001"), Second = new("4chan", "g", "73002");
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("W31 absent original bytes; not playable-video evidence");
    private static readonly byte[] Preview = Encoding.UTF8.GetBytes("W31 synthetic derived preview bytes");
    private static readonly byte[] Poster = CreatePoster();
    private static readonly TransferImportStatusCounts Zero = new(0, 0, 0, 0, 0, 0);
    private static readonly string[] NewKeys = ["transfer.preview.derivedPreviews", "transfer.preview.videoPosters", "transfer.preview.derivedNotice"];

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
    private static readonly ExpectedThread[] LegacyThreads =
    [new(First, 1, new(0, 1, 0, 0, 0, 0), new(1, 0, 0, 0, 0, 0), Zero, Zero, Zero)];

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(Environment.GetEnvironmentVariable("MEOWCHAN_NATIVE_TRANSFER_PREVIEW_ONLY") == "1",
            "Run this helper only in Root's isolated native-transfer-preview smoke mode.");
        Require(owner.IsVisible && owner.SessionHost.State == DesktopSessionState.Ready
            && owner.TryGetPlatformHandle()?.Handle is nint native && native != 0,
            "A real visible native owner and naturally Ready host are required.");
        Require(owner.SessionHost.GetThreadWorkSnapshots().Count == 0, "The isolated smoke host must have no active watches.");
        Dictionary<string, IReadOnlyDictionary<string, string>> catalogs = new[] { "en", "de", "es", "fr", "ru" }
            .ToDictionary(code => code, ReadCatalog);
        foreach (string code in catalogs.Keys)
            Require(Texts.Languages.Any(language => language.Code == code), "Missing bundled language: " + code);
        string before = ProfileSnapshot(owner.SessionHost);
        Fixture fixture = await Fixture.CreateAsync();
        bool completed = false;
        try
        {
            await LiveDialogAsync(owner, fixture, catalogs);
            Require(before == ProfileSnapshot(owner.SessionHost), "Passive dialog inspection changed the profile or media target.");
            await HostStillRejectsV4Async(owner.SessionHost, fixture);
            Require(before == ProfileSnapshot(owner.SessionHost), "Unsupported v4 Apply changed the profile or media target.");
            fixture.RequireSourcesUnchanged();
            RequireEmpty(fixture.Work);
            Require(owner.SessionHost.State == DesktopSessionState.Ready
                && owner.SessionHost.GetThreadWorkSnapshots().Count == 0, "Inspection activated source work or changed host readiness.");
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
        Console.WriteLine("SMOKE W31 v4 preview: real writer/session/dialog, retained-inclusive inventory, five-language live refresh and EN return, 640x480 layouts, legacy/private boundaries, host Unsupported and terminal cleanup completed.");
    }

    private static async Task LiveDialogAsync(MainWindow owner, Fixture fixture,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> catalogs)
    {
        NativeTransferPreviewSession session = new(fixture.Work);
        int staged = 0, cleaned = 0, picks = 0;
        string selected = fixture.Public;
        session.AfterStageForTests = _ =>
        {
            string child = Directory.GetDirectories(fixture.Work).Single();
            string file = Directory.GetFiles(child).Single();
            Require(Path.GetFileName(child).StartsWith(".meowchan-preview-", StringComparison.Ordinal)
                && new FileInfo(file).Length == new FileInfo(selected).Length,
                "The real inspection never owned its complete staged package.");
            Interlocked.Increment(ref staged);
            return Task.CompletedTask;
        };
        session.BeforeDirectoryCleanupForTests = child =>
        {
            Require(Path.GetDirectoryName(child) == fixture.Work, "Cleanup escaped the operation parent.");
            RequireEmpty(child); // The owned stage has already been disposed, not just hidden in Current.
            Interlocked.Increment(ref cleaned);
        };
        Texts.SetLanguage("de"); // First EN cell below is a real Changed event on an open dialog.
        NativeTransferPreviewDialog? dialog = null;
        Task? shown = null, choosing = null;
        try
        {
            dialog = new(session, () => { ++picks; return Task.FromResult<string?>(selected); })
            { Width = 640, Height = 480 };
            shown = dialog.ShowDialog(owner);
            await WaitUntilAsync(() => dialog.IsVisible && owner.OwnedWindows.Contains(dialog)
                && dialog.TryGetPlatformHandle()?.Handle is nint handle && handle != 0);
            await WaitUntilAsync(() =>
            {
                dialog.UpdateLayout();
                return Math.Abs(dialog.ClientSize.Width - 640) <= 1 && Math.Abs(dialog.ClientSize.Height - 480) <= 1;
            });
            nint handleBefore = dialog.TryGetPlatformHandle()!.Handle;
            choosing = dialog.ChooseForTestsAsync();
            await choosing.WaitAsync(Deadline);
            NativeTransferPreviewSnapshot v4 = session.Current;
            CheckInventory(v4, V4Threads, 2, Preview.LongLength + Poster.LongLength);
            Require(Volatile.Read(ref staged) == 1 && Volatile.Read(ref cleaned) == 1,
                "Public v4 did not pass the real stage and cleanup boundaries exactly once.");
            RequireEmpty(fixture.Work);

            foreach (string code in new[] { "en", "de", "es", "fr", "ru", "en" })
            {
                Texts.SetLanguage(code); // No manual Render, private refresh or replacement dialog.
                await SettleAsync(dialog);
                Require(Texts.Language == code && Texts.Warnings.Count == 0, code + ": language fallback/warning.");
                CheckNewCatalog(catalogs[code], catalogs["en"], code);
                Require(ReferenceEquals(v4, session.Current) && picks == 1 && dialog.IsVisible
                    && dialog.TryGetPlatformHandle()!.Handle == handleBefore,
                    code + ": language refresh replaced/reinspected the live inventory or window.");
                CheckRenderedInventory(dialog, catalogs[code], V4Threads, 2, Preview.LongLength + Poster.LongLength);
                CheckLayout(dialog, code);
                fixture.RequireSourcesUnchanged();
            }

            // Same open dialog: a real old-format read must remove, not retain, v4-only lines.
            selected = fixture.Legacy;
            choosing = dialog.ChooseForTestsAsync();
            await choosing.WaitAsync(Deadline);
            await SettleAsync(dialog);
            CheckInventory(session.Current, LegacyThreads, 1, Preview.LongLength);
            CheckRenderedInventory(dialog, catalogs["en"], LegacyThreads, 1, Preview.LongLength);
            Require(Volatile.Read(ref staged) == 2 && Volatile.Read(ref cleaned) == 2 && picks == 2,
                "Legacy selection did not replace the real package through staging.");
            RequireEmpty(fixture.Work);

            // A writer-validated PRIVATE v4 package must clear old inventory and reject consent-free inspection.
            selected = fixture.Private;
            choosing = dialog.ChooseForTestsAsync();
            await choosing.WaitAsync(Deadline);
            await SettleAsync(dialog);
            Require(session.Current.State == NativeTransferPreviewState.Rejected
                && session.Current.Error == NativeTransferPreviewError.PrivateMetadataNotYetSupported
                && session.Current.Inventory == null && !session.Current.CleanupRetained,
                "Private v4 was not specifically rejected without inventory.");
            Require(dialog.StateText.Text == catalogs["en"]["transfer.preview.rejected"]
                && dialog.ErrorText.Text == catalogs["en"]["transfer.preview.error.private"]
                && dialog.SummaryText.Text == string.Empty && Named(dialog, "NativeTransferPreviewDetails").Text == string.Empty
                && Named(dialog, "NativeTransferPreviewNotice").Text == catalogs["en"]["transfer.preview.inventoryOnly"],
                "Private rejection left stale inventory/derived notice or an opaque-error mismatch.");
            Require(Volatile.Read(ref staged) == 2 && Volatile.Read(ref cleaned) == 3 && picks == 3,
                "Private staging must clean its rejected copy without publishing an admitted Stage.");
            CheckInventoryOnlyActions(dialog);
            RequireEmpty(fixture.Work);
            fixture.RequireSourcesUnchanged();
        }
        finally
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
                    && !dialog.IsVisible && session.Current.State == NativeTransferPreviewState.Closed,
                    "Native Closed/finally did not share one terminal session drain and unsubscribe.");
                (string? State, string? Title, object? Choose) terminal = (dialog.StateText.Text, dialog.Title, dialog.ChooseButton.Content);
                Texts.SetLanguage("de");
                await SettleAsync(owner);
                Require((dialog.StateText.Text, dialog.Title, dialog.ChooseButton.Content) == terminal,
                    "A closed dialog still responded to language changes.");
                Texts.SetLanguage("en");
            }
        }
    }

    private static async Task HostStillRejectsV4Async(DesktopSessionHost host, Fixture fixture)
    {
        Require(host.BeforeNativeImportCommitForTests == null, "Another test owns the host commit observation hook.");
        int commits = 0;
        host.BeforeNativeImportCommitForTests = () => { Interlocked.Increment(ref commits); return Task.CompletedTask; };
        NativeTransferImportSession? session = null;
        Task<NativeTransferImportSnapshot>? inspection = null;
        try
        {
            session = host.CreateNativeTransferImportSession(fixture.Work);
            // Single thread, actual Original-role attachment: not rejected merely for multithread/preview-only shape.
            inspection = session.InspectAsync(fixture.SingleV4);
            NativeTransferImportSnapshot result = await inspection.WaitAsync(Deadline);
            Require(result.State == NativeTransferImportState.Unsupported
                && result.Error == NativeTransferImportError.UnsupportedPackage
                && result.Inventory == null && result.Plan == null && result.Result == null && !result.CleanupRetained,
                "Valid single-thread v4 unexpectedly acquired an import capability or became InvalidPackage.");
            Require(result == await session.ApplyAsync(result.Generation).WaitAsync(Deadline)
                && Volatile.Read(ref commits) == 0, "Unsupported v4 Apply invoked a host commit or changed disposition.");
            RequireEmpty(fixture.Work);
            fixture.RequireSourcesUnchanged();
        }
        finally
        {
            try
            {
                if (session != null) await session.DisposeAsync().AsTask().WaitAsync(Deadline);
                if (inspection != null) await inspection.WaitAsync(Deadline);
            }
            finally { host.BeforeNativeImportCommitForTests = null; }
        }
    }

    private static void CheckInventory(NativeTransferPreviewSnapshot snapshot, ExpectedThread[] threads, int payloads, long bytes)
    {
        Require(snapshot.State == NativeTransferPreviewState.Inventory && snapshot.Error == NativeTransferPreviewError.None
            && snapshot.Inventory != null && !snapshot.CleanupRetained, "Real package inspection did not settle as clean Inventory.");
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
            Require(item.Id == expected.Id && item.Posts == expected.Posts, "Retained bindings fabricated a current post/thread.");
            CheckCounts(item.CurrentOriginals, expected.Originals, "thread originals");
            CheckCounts(item.CurrentPreviews, expected.Previews, "thread legacy previews");
            CheckCounts(item.RetainedBindings, expected.Retained, "thread retained");
            CheckCounts(item.DerivedPreviews, expected.Derived, "thread derived previews");
            CheckCounts(item.VideoPosters, expected.Posters, "thread video posters");
        }
    }

    private static void CheckRenderedInventory(NativeTransferPreviewDialog dialog, IReadOnlyDictionary<string, string> strings,
        ExpectedThread[] threads, int payloads, long bytes)
    {
        string Format(string key, params object[] values) => string.Format(Texts.Culture, strings[key], values);
        string Counts(string key, TransferImportStatusCounts c) => Format(key, c.Contained, c.Omitted, c.Unknown, c.Unavailable, c.Failed, c.Cancelled);
        string Derived(TransferImportStatusCounts previews, TransferImportStatusCounts posters) => previews.Total + posters.Total == 0
            ? string.Empty : Environment.NewLine + Counts(NewKeys[0], previews) + Environment.NewLine + Counts(NewKeys[1], posters);
        TransferImportStatusCounts previewTotal = Sum(threads, t => t.Derived), posterTotal = Sum(threads, t => t.Posters);
        string expectedSummary = string.Join(Environment.NewLine,
            Format("transfer.preview.summary", threads.Length, threads.Sum(t => t.Posts), payloads, bytes),
            Counts("transfer.preview.originals", Sum(threads, t => t.Originals)),
            Counts("transfer.preview.previews", Sum(threads, t => t.Previews)),
            Counts("transfer.preview.retained", Sum(threads, t => t.Retained))) + Derived(previewTotal, posterTotal);
        string expectedDetails = string.Join(Environment.NewLine, threads.Select(t => Format("transfer.preview.thread",
            t.Id.Source, t.Id.Board, t.Id.Thread, t.Posts, Counts("transfer.preview.originals", t.Originals),
            Counts("transfer.preview.previews", t.Previews), Counts("transfer.preview.retained", t.Retained)) + Derived(t.Derived, t.Posters)));
        string expectedNotice = strings["transfer.preview.inventoryOnly"]
            + (previewTotal.Total + posterTotal.Total == 0 ? string.Empty : Environment.NewLine + strings[NewKeys[2]]);
        Require(dialog.SummaryText.Text == expectedSummary && Named(dialog, "NativeTransferPreviewDetails").Text == expectedDetails
            && Named(dialog, "NativeTransferPreviewNotice").Text == expectedNotice,
            Texts.Language + ": live summary/thread/notice differs from independent counts and bundled strings.");
        Require(dialog.StateText.Text == strings["transfer.preview.ready"] && dialog.ErrorText.Text == string.Empty
            && dialog.Title == strings["transfer.preview.title"], "Ready labels/error/title did not refresh.");
        foreach (TextBlock label in dialog.GetVisualDescendants().OfType<TextBlock>())
            Require(!(label.Text ?? "").Contains("transfer.preview.", StringComparison.Ordinal)
                && !Regex.IsMatch(label.Text ?? "", @"\{\d+(?:[^}]*)\}")
                && !(label.Text ?? "").Contains("W31_INERT", StringComparison.Ordinal),
                "Dialog exposed a raw key/placeholder or package-authored post/title text.");
        CheckInventoryOnlyActions(dialog);
    }

    private static void CheckNewCatalog(IReadOnlyDictionary<string, string> strings,
        IReadOnlyDictionary<string, string> english, string language)
    {
        foreach (string key in NewKeys)
        {
            Require(strings.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) && value != key,
                language + ": missing explicit bundled new key: " + key);
            Require(Texts.T(key) == value && (language == "en" || value != english[key]),
                language + ": a new key silently fell back to English.");
            _ = CompositeFormat.Parse(value!);
            string[] tokens = Regex.Matches(value!, @"\{[^{}]*\}").Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();
            string[] expected = key == NewKeys[2] ? [] : ["{0}", "{1}", "{2}", "{3}", "{4}", "{5}"];
            Require(tokens.SequenceEqual(expected), language + ": incorrect six-count/no-notice-placeholder contract: " + key);
        }
        Require(english[NewKeys[2]].Contains("do not replace original files", StringComparison.Ordinal)
            && english[NewKeys[2]].Contains("still image, not a playable video", StringComparison.Ordinal),
            "Canonical notice no longer distinguishes original files from nonplayable posters.");
    }

    private static void CheckInventoryOnlyActions(NativeTransferPreviewDialog dialog)
    {
        Button[] buttons = dialog.GetVisualDescendants().OfType<Button>()
            .Where(button => !button.GetVisualAncestors().Any(a => a is WindowChromeBar or ScrollBar)).ToArray();
        Require(buttons.Length == 3 && buttons.Contains(dialog.ChooseButton) && buttons.Contains(dialog.CancelButton)
            && buttons.Contains(dialog.CloseButton), "Inventory-only dialog exposed an extra action (such as Apply).");
        Require(dialog.ChooseButton.IsEffectivelyEnabled && dialog.CloseButton.IsEffectivelyEnabled && !dialog.CancelButton.IsEnabled,
            "Settled inventory actions have the wrong enabled state.");
        foreach ((Button button, string key) in new[] { (dialog.ChooseButton, "transfer.preview.choose"),
            (dialog.CancelButton, "action.cancel"), (dialog.CloseButton, "action.close") })
            Require(Equals(button.Content, Texts.T(key)) && AutomationProperties.GetName(button) == Texts.T(key),
                "Action label/accessibility name did not follow the open-dialog language.");
    }

    private static void CheckLayout(NativeTransferPreviewDialog dialog, string language)
    {
        Require(Math.Abs(dialog.ClientSize.Width - 640) <= 1 && Math.Abs(dialog.ClientSize.Height - 480) <= 1,
            "Native client did not reach requested 640x480 DIP geometry.");
        ScrollViewer scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>()
            .Single(viewer => viewer.GetVisualDescendants().Contains(dialog.SummaryText));
        Require(scroll.Viewport.Width > 0 && scroll.Viewport.Height > 0 && scroll.Extent.Width <= scroll.Viewport.Width + 1
            && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "Inventory requires horizontal scrolling or has no actual viewport.");
        Require(scroll.Extent.Height > scroll.Viewport.Height, "Multithread fixture never exercised the scrollable body at 640x480.");
        bool exercisedWrapping = false;
        foreach (TextBlock label in new[] { dialog.SummaryText, Named(dialog, "NativeTransferPreviewDetails"), Named(dialog, "NativeTransferPreviewNotice") })
        {
            var layout = label.TextLayout;
            string expected = label.Text ?? "";
            string rendered = string.Concat(layout.TextLines.SelectMany(line => line.TextRuns)
                .Where(run => run is not TextEndOfLine).Select(run => run.Text.ToString()));
            Require(label.IsEffectivelyVisible && label.Bounds.Width > 0 && label.Bounds.Height > 0
                && label.TextWrapping == TextWrapping.Wrap && label.TextTrimming == TextTrimming.None,
                "Production inventory text is detached, unmeasured or not wrapping.");
            Require(WithoutLineBreaks(rendered) == WithoutLineBreaks(expected) && layout.TextLines.All(line => !line.HasCollapsed),
                language + ": actual glyph runs lost/collapsed inventory text: " + label.Name);
            Console.WriteLine("SMOKE W31 layout " + JsonSerializer.Serialize(new
            {
                language, control = label.Name, width = dialog.ClientSize.Width, height = dialog.ClientSize.Height,
                scale = dialog.RenderScaling, bounds = label.Bounds.ToString(), layout.MaxWidth,
                layout.WidthIncludingTrailingWhitespace, glyphWidth = layout.Width, viewportWidth = scroll.Viewport.Width, layoutHeight = layout.Height, lines = layout.TextLines.Count()
            }));
            Require(double.IsFinite(layout.MaxWidth) && layout.MaxWidth > 0 && layout.MaxWidth <= scroll.Viewport.Width + 1
                // TextLayout keeps the advance of a wrapped trailing space in
                // WidthIncludingTrailingWhitespace, even though its glyphs fit.
                // All text runs (including whitespace) are still checked above.
                && layout.Width <= label.Bounds.Width + 1 && layout.Height <= label.Bounds.Height + 1,
                language + ": inventory glyph layout exceeds actual available width/height: " + label.Name);
            // Short English lines may legitimately fit at 640 DIP. Check complete
            // glyphs/width for every label and require real wrapping somewhere in
            // the constrained inventory, rather than require every line block to wrap.
            exercisedWrapping |= layout.TextLines.Count() > expected.Split('\n').Length;

        }
        Require(exercisedWrapping, language + ": inventory fixture never exercised actual wrapping at 640x480.");
        foreach (Button button in new[] { dialog.ChooseButton, dialog.CloseButton })
        {
            Point? point = button.TranslatePoint(default, dialog);
            Require(point.HasValue && point.Value.X >= -1 && point.Value.Y >= -1
                && point.Value.X + button.Bounds.Width <= dialog.ClientSize.Width + 1
                && point.Value.Y + button.Bounds.Height <= dialog.ClientSize.Height + 1,
                "Scrollable inventory pushed an enabled footer action outside the native client.");
        }
    }

    private static string WithoutLineBreaks(string text) => text.Replace("\r", "").Replace("\n", "");
    private static TextBlock Named(Window window, string name) => window.GetVisualDescendants().OfType<TextBlock>()
        .Single(label => label.Name == name);
    private static void CheckCounts(TransferImportStatusCounts actual, TransferImportStatusCounts expected, string scene) =>
        Require(actual == expected, scene + ": expected " + expected + ", got " + actual);
    private static TransferImportStatusCounts Sum(ExpectedThread[] threads, Func<ExpectedThread, TransferImportStatusCounts> select)
    {
        TransferImportStatusCounts[] counts = threads.Select(select).ToArray();
        return new(counts.Sum(c => c.Contained), counts.Sum(c => c.Omitted), counts.Sum(c => c.Unknown),
            counts.Sum(c => c.Unavailable), counts.Sum(c => c.Failed), counts.Sum(c => c.Cancelled));
    }
    private static IReadOnlyDictionary<string, string> ReadCatalog(string language)
    {
        if (language == "en") return BuiltInCatalog.English;
        if (language == "de") return BuiltInCatalog.German;
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream("MeowChan.Desktop.Localization.Packs." + language + ".json")
            ?? throw new InvalidOperationException("Bundled language resource missing: " + language);
        using JsonDocument json = JsonDocument.Parse(stream);
        Require(json.RootElement.GetProperty("code").GetString() == language, "Wrong bundled language resource.");
        return json.RootElement.GetProperty("strings").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
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
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("W31 native window watchdog expired.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
    private static void RequireEmpty(string path) => Require(Directory.GetFileSystemEntries(path).Length == 0, "Owned staging child/files remain before fixture teardown.");
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
        internal string Legacy => Path.Combine(Root, "legacy.zip");
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
            await fixture.WriteAsync(fixture.Legacy, new(At, [Thread(First, Post(Media(First, "15") with { OriginalByteVersion = null }, oldPreview))]),
                [Preview], v4: false);
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

    private static TransferThread Thread(ResourceId id, params TransferPost[] posts) => new(id, null, "W31_INERT_TITLE",
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
