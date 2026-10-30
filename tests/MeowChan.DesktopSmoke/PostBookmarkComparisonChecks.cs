using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

/// <summary>Bounded technical comparison, opt-in only. No product schemas or rival services.</summary>
internal static class PostBookmarkComparisonChecks
{
    private static readonly ResourceId PThread = new("4chan", "g", "11100");
    private static readonly PostBookmarkKey P = new(PThread, "11101");
    private static readonly PostBookmarkKey Q = new(PThread with { Board = "vg" }, "11101");
    private const string V1 = "<p>Original quotation V1.</p>", V2 = "<p>Changed quotation V2.</p>";
    private const string NotePrefix = "Reason A: investigate original wording.\n\nReason B: use in another project.\n\n";
    private static string LongNote => NotePrefix + string.Concat(Enumerable.Repeat("🙂", 8000 - NotePrefix.EnumerateRunes().Count()));

    private sealed record RestartCheckpoint(string SourceDigest, string FixtureDigest, string Note);

    internal static async Task RunAsync(MainWindow owner)
    {
        string stage = Environment.GetEnvironmentVariable("MEOWCHAN_BOOKMARK_COMPARISON_STAGE") ?? "all";
        if (stage is not ("all" or "before-restart" or "after-restart")) throw new ArgumentException("Unknown comparison stage.");
        string? requested = Environment.GetEnvironmentVariable("MEOWCHAN_BOOKMARK_COMPARISON_PROFILE");
        string profile = requested == null ? Path.Combine(TemporaryWorkspacePaths.TempRoot,
            "meowchan-bookmark-comparison-" + Guid.NewGuid().ToString("N")) : Path.GetFullPath(requested);
        if (stage != "all" && requested == null) throw new ArgumentException("Restart stages need a shared disposable comparison profile.");
        if (!Path.GetFullPath(profile).StartsWith(TemporaryWorkspacePaths.TempRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Comparison profiles must be below the physical temporary root.");
        Directory.CreateDirectory(profile);
        if (TemporaryWorkspacePaths.PhysicalDirectory(profile) != Path.TrimEndingDirectorySeparator(profile))
            throw new IOException("Comparison refuses a linked profile.");
        string marker = Path.Combine(profile, ".bookmark-comparison-fixture");
        string checkpointPath = Path.Combine(profile, "comparison-restart.json");
        string source = Environment.GetEnvironmentVariable("MEOWCHAN_COMPARISON_SOURCE_DIGEST") ?? "not-provided";
        bool retainForRestart = false, ownsFixture = false;
        try
        {
            string winner, fixture;
            if (stage == "after-restart")
            {
                if (!File.Exists(marker) || await File.ReadAllTextAsync(marker) != "Disposable URSA20 fixture v1")
                    throw new IOException("After-restart comparison refuses an unmarked directory.");
                RestartCheckpoint checkpoint = JsonSerializer.Deserialize<RestartCheckpoint>(await File.ReadAllTextAsync(checkpointPath))
                    ?? throw new InvalidDataException("Comparison checkpoint missing.");
                if (checkpoint.SourceDigest != source) throw new InvalidDataException("Comparison source changed between restart stages.");
                winner = checkpoint.Note; fixture = checkpoint.FixtureDigest; ownsFixture = true;
            }
            else
            {
                if (Directory.EnumerateFileSystemEntries(profile).Any()) throw new IOException("Comparison refuses a non-empty initial profile.");
                ownsFixture = true;
                await SeedAsync(profile);
                await File.WriteAllTextAsync(marker, "Disposable URSA20 fixture v1");
                fixture = Hash(await File.ReadAllTextAsync(Path.Combine(profile, "post-bookmarks.json")) + V1 + V2);
                winner = "";
            }
            PostBookmarkVisualEvidence.ComparisonFixtureDigest = fixture;
            Log(new { task = "metadata", variant = "current-reference", stage, source_digest = source, fixture_digest = fixture,
                fixture_digest_scope = "canonical initial bookmark JSON plus V1/V2 source inputs; not SQLite container bytes",
                observer_kind = "harness", input_method = "in-process control commands/events; TextBox.Text and selection property edits",
                os = Environment.OSVersion.ToString(), backend = Environment.GetEnvironmentVariable("AVALONIA_PLATFORM") ?? "runner-records-backend",
                scale = owner.RenderScaling, language = Texts.Language, theme = owner.ActualThemeVariant.ToString(),
                owner_size = owner.Bounds.Size.ToString(), entries = 1000, unicode_scalars = LongNote.EnumerateRunes().Count(),
                human_judgment_open = true, native_at_ime_open = true });
            if (stage != "after-restart")
            {
                // Alternate current/rival order; rival costs are never compared with product costs.
                await PostBookmarkRivalWalkthrough.RunOriginalAsync(owner, Log);
                winner = await CheckLongNoteAndConflictAsync(owner, profile);
                await CaptureAsync(profile, V2); // owner stopped: actual local recapture, no network.
                if (stage == "before-restart")
                {
                    await File.WriteAllTextAsync(checkpointPath, JsonSerializer.Serialize(new RestartCheckpoint(source, fixture, winner)));
                    retainForRestart = true;
                    Log(new { task = "restart-checkpoint", result = "pass", prediction_before_next_process =
                        "next process reads revision3,1000 entries,8000-scalar winner and exact sourceP/V2; no in-memory note state crosses processes" });
                    return;
                }
            }
            Log(new { task = "restart-scope", scope = stage == "after-restart"
                ? "after-stage in a new smoke process, validated source-bound disposable disk checkpoint"
                : "new host/dialog lifetime in same process; optional two-stage recipe supplies process restart" });
            await CheckChangedAndMissingAsync(owner, profile, winner);
            await PostBookmarkRivalWalkthrough.RunNotesAndDeletionAsync(owner, Log);
            await PostBookmarkGalleryReturnChecks.RunAsync(owner);
            Log(new { task = "technical-interpretation", result = "pass", interpretation = "retain-provisionally",
                scope = "bounded harness invariants only; no general performance, preference, native GUI or human acceptance claim",
                human_judgment_open = true, lifetime_choice_open = true, native_at_ime_open = true });
        }
        finally { PostBookmarkVisualEvidence.ComparisonFixtureDigest = null; if (ownsFixture && !retainForRestart) { try { Directory.Delete(profile, true); } catch (IOException) { } } }
    }

    private static async Task<string> CheckLongNoteAndConflictAsync(MainWindow owner, string profile)
    {
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        PostBookmarksDialog? overview = null, editorA = null, editorB = null;
        try
        {
            Log(new { task = "open-1000-edit-8000", variant = "current-reference", prediction_before_action =
                "1000 stored entries; select exact P; two paragraphs and 8000 Unicode scalars preserved; save revision 1→2" });
            long allocated = GC.GetTotalAllocatedBytes(); Stopwatch watch = Stopwatch.StartNew();
            overview = new PostBookmarksDialog(host, (_, _) => Task.FromResult(false));
            overview.Show(owner); await overview.InitializeAsync();
            await Program.WaitUntilAsync(() => B(overview, "OpenTarget").IsEnabled);
            await FlushAsync(overview);
            LogMetric("open-1000", watch, allocated);
            ListBox list = overview.FindControl<ListBox>("Bookmarks")!;
            Require(list.ItemCount == 1000 && (list.SelectedItem as PostBookmark)?.Key == P,
                "1000-entry overview lost count or exact first identity.");
            TextBox note = overview.FindControl<TextBox>("Note")!;
            Require(note.Text == LongNote && note.Text.EnumerateRunes().Count() == 8000,
                "Stored Unicode fixture was truncated by the current editor.");
            string edited = LongNote.Replace("use in another project.", "use in a later project.", StringComparison.Ordinal);
            allocated = GC.GetTotalAllocatedBytes(); watch.Restart();
            await EditNoteAsync(overview, edited); Execute(B(overview, "Save"));
            await Program.WaitUntilAsync(() => B(overview, "OpenTarget").IsEnabled && B(overview, "Save").IsEnabled);
            PostBookmarkSnapshot saved = await host.ReadPostBookmarksAsync();
            LogMetric("save-8000-with-1000", watch, allocated);
            Require(saved.Revision == 2 && saved.Bookmarks.Count == 1000
                && saved.Bookmarks.Single(entry => entry.Key == P).Note == edited,
                "Long note save changed identity/count or lost an independent motive.");
            Log(new { task = "open-1000-edit-8000", result = "pass", actual_actions = new[] {
                "Show real PostBookmarksDialog", "edit Note.Text", "execute Save ICommand" },
                displayed_identity = P.ToString(), before = NoteEvidence(LongNote), after = NoteEvidence(edited),
                persisted_revision_before = 1, persisted_revision_after = saved.Revision });
            await PostBookmarkVisualEvidence.CaptureMatrixAsync(owner, overview, "overview-long-note");
            string valid = note.Text!;
            string invalid = valid + "🙂";
            byte[] beforeInvalid = await File.ReadAllBytesAsync(Path.Combine(profile, "post-bookmarks.json"));
            await EditNoteAsync(overview, invalid, () =>
            {
                Log(new { task = "reject-8001-note-before-render", raw_draft = NoteEvidence(note.Text!),
                    save_button_enabled = B(overview, "Save").IsEnabled,
                    save_command_available = B(overview, "Save").Command!.CanExecute(null),
                    observation_scope = "immediately after TextBox.Text assignment, before asynchronous TextChanged" });
                Require(!B(overview, "Save").Command!.CanExecute(null),
                    "8001-codepoint raw draft was accepted by the live Save command.");
            });
            Require(note.Text == invalid && invalid.EnumerateRunes().Count() == 8001 && invalid.Length == valid.Length + 2
                && !B(overview, "Save").IsEnabled && !B(overview, "Save").Command!.CanExecute(null)
                && overview.FindControl<TextBlock>("Status")!.Text == Texts.T("bookmarks.invalidNote"),
                "8001-codepoint draft was unexpectedly savable after its real TextChanged event.");
            B(overview, "Save").Command!.Execute(null); // Real command must reject even a direct disabled invocation.
            await FlushAsync(overview);
            byte[] afterInvalid = await File.ReadAllBytesAsync(Path.Combine(profile, "post-bookmarks.json"));
            PostBookmarkSnapshot invalidAttempt = await host.ReadPostBookmarksAsync();
            Require(beforeInvalid.SequenceEqual(afterInvalid) && invalidAttempt.Revision == 2
                && invalidAttempt.Bookmarks.Single(entry => entry.Key == P).Note == edited && note.Text == invalid,
                "Disabled invalid-note Save changed persistence or the raw draft.");
            Log(new { task = "reject-8001-note", result = "pass", raw_draft = NoteEvidence(invalid),
                save_button_enabled = B(overview, "Save").IsEnabled,
                save_command_available = B(overview, "Save").Command!.CanExecute(null),
                persisted_revision = invalidAttempt.Revision, unchanged_bookmark_bytes = true });
            await PostBookmarkVisualEvidence.CaptureMatrixAsync(owner, overview, "invalid-8001-note");
            Require(note.Text == invalid, "Invalid raw Unicode draft changed during presentation matrix.");
            await EditNoteAsync(overview, valid);
            await CancelRemoveAsync(overview, host, "remove-existing-abort");
            overview.CloseForProfileEnd(); overview = null;

            editorA = new(host, (_, _) => Task.FromResult(false)); editorA.Show(owner); await editorA.InitializeAsync(P);
            editorB = new(host, (_, _) => Task.FromResult(false)); editorB.Show(owner); await editorB.InitializeAsync(P);
            string winner = edited.Replace("Reason A", "Reason E", StringComparison.Ordinal);
            string stale = edited.Replace("Reason A", "Reason F", StringComparison.Ordinal);
            Log(new { task = "two-editor-revision", variant = "current-reference", prediction_before_action =
                "A commits revision3; stale B reports conflict, keeps 8000-scalar raw draft; disk remains A" });
            await EditNoteAsync(editorA, winner);
            await EditNoteAsync(editorB, stale);
            Execute(B(editorA, "Save"));
            await Program.WaitUntilAsync(() => B(editorA, "Save").IsEnabled && B(editorA, "Reload").IsEnabled);
            Execute(B(editorB, "Save"));
            await Program.WaitUntilAsync(() => editorB.FindControl<TextBlock>("Status")!.Text == Texts.T("bookmarks.conflict"));
            PostBookmarkSnapshot conflicted = await host.ReadPostBookmarksAsync();
            Require(conflicted.Revision == 3 && conflicted.Bookmarks.Single(entry => entry.Key == P).Note == winner
                && editorB.FindControl<TextBox>("Note")!.Text == stale && !B(editorB, "Save").IsEnabled,
                "Conflict lost raw draft or overwrote the other editor's note.");
            await PostBookmarkVisualEvidence.CaptureMatrixAsync(owner, editorB, "revision-conflict");
            Require(editorB.FindControl<TextBox>("Note")!.Text == stale, "Conflict raw draft changed during visual matrix.");
            Log(new { task = "two-editor-revision", result = "pass", actual_actions = new[] {
                "open two real editors", "edit A Note.Text", "edit B Note.Text", "execute A Save", "execute stale B Save" },
                raw_draft_retained = NoteEvidence(stale), persisted = NoteEvidence(winner), revision = conflicted.Revision });
            return winner;
        }
        finally { overview?.CloseForProfileEnd(); editorB?.CloseForProfileEnd(); editorA?.CloseForProfileEnd(); }
    }

    private static async Task CheckChangedAndMissingAsync(MainWindow owner, string profile, string winner)
    {
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        ArchivePage page = new(token => host.OpenArchiveReaderAsync(token), (_, _) => Task.CompletedTask, _ => { });
        Window reader = new() { Width = 900, Height = 650, Content = page.DetachReader(), Title = "Comparison exact local post" };
        page.IsAvailable = true;
        PostBookmarksDialog? overview = null;
        int opens = 0;
        try
        {
            Log(new { task = "stored-reload-and-V2", variant = "current-reference", prediction_before_action =
                "new host/dialog lifetime reads 1000 entries and exact persisted note; OpenTarget selects P/V2, never similarly named Q",
                restart_scope = Environment.GetEnvironmentVariable("MEOWCHAN_BOOKMARK_COMPARISON_STAGE") == "after-restart"
                    ? "new smoke process over source-bound stored checkpoint" : "new owner/dialog lifetime over stored profile" });
            PostBookmarkSnapshot reloaded = await host.ReadPostBookmarksAsync();
            Require(reloaded.Revision == 3 && reloaded.Bookmarks.Count == 1000
                && reloaded.Bookmarks.Single(entry => entry.Key == P).Note == winner
                && winner.EnumerateRunes().Count() == 8000,
                "Stored restart did not recover revision3/count1000/full8000-scalar committed note.");
            overview = new(host, async (key, token) =>
            {
                Require(key == P, "Current UI retargeted Q.");
                bool opened = await page.OpenPostBookmarkAsync(key, () => reader.Show(owner), token);
                Require(opened && page.CurrentThread?.Summary.Id == P.Thread
                    && page.CurrentThread.GetPosts(page.CurrentThread.FindPostIndex(P.Post), 1).Single().Html == V2
                    && (page.FindControl<ListBox>("ArchivePosts")!.SelectedItem as ArchivePostRow)?.Id == P.Post,
                    "Changed current target did not render exact P/V2.");
                opens++; return true;
            });
            overview.Show(owner); await overview.InitializeAsync();
            await Program.WaitUntilAsync(() => B(overview, "OpenTarget").IsEnabled);
            Require(overview.FindControl<ListBox>("Bookmarks")!.ItemCount == 1000
                && overview.FindControl<TextBox>("Note")!.Text == winner,
                "Stored reload lost the committed long note/count.");
            Execute(B(overview, "OpenTarget"));
            await Program.WaitUntilAsync(() => opens == 1 && !overview.IsVisible && B(overview, "OpenTarget").IsEnabled);
            Log(new { task = "stored-reload-and-V2", result = "pass", actual_actions = new[] {
                "new real overview on stored profile", "execute OpenTarget" }, displayed_identity = P.ToString(),
                source_html = V2, own_note = NoteEvidence(winner), original_V1_copy_available = false });
            reader.Hide(); overview.ReturnFromReader();
            await Program.WaitUntilAsync(() => overview.IsVisible && B(overview, "OpenTarget").IsEnabled);
            Log(new { task = "missing-target-note-retention", variant = "current-reference", prediction_before_action =
                "delete P's archive thread; exact P becomes unavailable; Q remains available; all1000 notes survive, edits save; Remove abort changes no bytes" });
            ArchivePermanentDeletionPlan plan = await host.PrepareArchiveDeletionAsync([P.Thread]);
            ArchivePermanentDeletionResult deletion = await host.DeleteArchivePermanentlyAsync(plan);
            Require(deletion.DeletedThreads.Contains(P.Thread), "Fixture did not delete P's archive thread.");
            Click(B(overview, "Reload"));
            await Program.WaitUntilAsync(() => B(overview, "Reload").IsEnabled
                && overview.FindControl<TextBlock>("Status")!.Text == Texts.T("bookmarks.missing"));
            Require(!B(overview, "OpenTarget").IsEnabled, "Missing P enabled Open.");
            B(overview, "OpenTarget").Command!.Execute(null);
            Require(opens == 1, "Disabled missing-target command opened a fallback.");
            string afterMissing = winner.Replace("Reason E", "Reason G", StringComparison.Ordinal);
            await EditNoteAsync(overview, afterMissing);
            Execute(B(overview, "Save"));
            await Program.WaitUntilAsync(() => B(overview, "Save").IsEnabled && B(overview, "Reload").IsEnabled
                && overview.FindControl<TextBlock>("Status")!.Text == Texts.T("bookmarks.missing"));
            PostBookmarkSnapshot retained = await host.ReadPostBookmarksAsync();
            await using ArchiveReader archive = await host.OpenArchiveReaderAsync();
            Require(retained.Revision == 4 && retained.Bookmarks.Count == 1000
                && retained.Bookmarks.Single(entry => entry.Key == P).Note == afterMissing
                && await PostBookmarkTarget.ResolveAsync(archive, P, default) == null
                && await PostBookmarkTarget.ResolveAsync(archive, Q, default) != null && opens == 1,
                "Missing target lost note or silently inherited similar Q's availability.");
            await PostBookmarkVisualEvidence.CaptureMatrixAsync(owner, overview, "missing-target-retained-note");
            Require(overview.FindControl<TextBox>("Note")!.Text == afterMissing, "Missing target note changed during presentation matrix.");
            await CancelRemoveAsync(overview, host, "remove-missing-abort");
            Log(new { task = "missing-target-note-retention", result = "pass", actual_actions = new[] {
                "fixture-only archive deletion via owner", "click Reload", "attempt disabled OpenTarget ICommand",
                "edit Note.Text", "execute Save", "execute Remove", "click Keep editing" },
                exact_p_available = false, similarly_named_q_available = true, entries = retained.Bookmarks.Count,
                persisted = NoteEvidence(afterMissing), opens });
        }
        finally { overview?.CloseForProfileEnd(); page.EndLifetime(); reader.Close(); }
    }

    private static async Task CancelRemoveAsync(PostBookmarksDialog dialog, DesktopSessionHost host, string task)
    {
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(host.ProfileDirectory, "post-bookmarks.json"));
        Log(new { task, variant = "current-reference", prediction_before_action = "cancel Remove: unchanged bookmark bytes and raw draft" });
        string? raw = dialog.FindControl<TextBox>("Note")!.Text;
        Execute(B(dialog, "Remove"));
        await Program.WaitUntilAsync(() => dialog.OwnedWindows.Any(window => window.IsVisible));
        Window confirm = dialog.OwnedWindows.Single(window => window.IsVisible);
        Click(confirm.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, Texts.T("bookmarks.keepEditing"))));
        await Program.WaitUntilAsync(() => !confirm.IsVisible);
        byte[] after = await File.ReadAllBytesAsync(Path.Combine(host.ProfileDirectory, "post-bookmarks.json"));
        Require(before.SequenceEqual(after) && raw == dialog.FindControl<TextBox>("Note")!.Text,
            "Remove abort changed storage or draft.");
        Log(new { task, result = "pass", actual_actions = new[] { "execute Remove", "click Keep editing" }, file_hash_before_after = Hash(Convert.ToBase64String(before)) });
    }

    private static async Task SeedAsync(string profile)
    {
        new Config { DownloadsPaused = true, SaveUrlsOnExit = false }.SaveConfig(profile);
        await CaptureAsync(profile, V1);
        var entries = Enumerable.Range(0, 999).Select(index => new { source = PThread.Source, board = PThread.Board,
            thread = PThread.Thread, post = (11101 + index).ToString(), note = index == 0 ? LongNote : "Fixture note " + index }).ToList();
        entries.Add(new { source = Q.Thread.Source, board = Q.Thread.Board, thread = Q.Thread.Thread, post = Q.Post, note = "Different identity Q" });
        await File.WriteAllTextAsync(Path.Combine(profile, "post-bookmarks.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, revision = 1, bookmarks = entries }));
    }
    private static async Task CaptureAsync(string profile, string html)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        DateTimeOffset captured = new(2026, 10, 23, html == V1 ? 12 : 13, 0, 0, TimeSpan.Zero);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(PThread, "https://offline.invalid/g/thread/11100",
            "Similar bookmark target", ThreadSourceState.Archived, captured, Path.Combine(profile, "media-P"),
            Enumerable.Range(0, 999).Select(index => new CapturedPost((11101 + index).ToString(), index,
                PostFieldPresence.Html, Html: index == 0 ? html : "<p>Fixture post " + index + "</p>")).ToArray()));
        if (html == V1) await catalog.CaptureAsync(new CapturedThreadSnapshot(Q.Thread, "https://offline.invalid/vg/thread/11100",
            "Similar bookmark target", ThreadSourceState.Archived, captured, Path.Combine(profile, "media-Q"),
            [new CapturedPost(Q.Post, 0, PostFieldPresence.Html, Html: "<p>Different identity Q.</p>")]));
    }
    private static object NoteEvidence(string text) => new { sha256 = Hash(text), unicode_scalars = text.EnumerateRunes().Count(),
        utf16_units = text.Length, prefix = text[..Math.Min(text.Length, NotePrefix.Length)] };
    private static void LogMetric(string task, Stopwatch watch, long allocated)
    {
        watch.Stop(); Log(new { task, variant = "current-reference", observer_kind = "harness",
            elapsed_ms = watch.Elapsed.TotalMilliseconds, process_allocated_bytes_delta = GC.GetTotalAllocatedBytes() - allocated,
            metric_scope = "whole-process allocation and wall time across real dialog operation including concurrent UI/resolver work; single sample, no threshold or rival cost claim" });
    }
    internal static void Log(object value) => Console.WriteLine("BOOKMARK-COMPARISON " + JsonSerializer.Serialize(value));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Button B(PostBookmarksDialog dialog, string name) => dialog.FindControl<Button>(name)!;
    private static void Execute(Button button)
    { Require(button.Command?.CanExecute(null) == true, "Command unavailable: " + button.Name); button.Command!.Execute(null); }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task EditNoteAsync(PostBookmarksDialog dialog, string text, Action? afterTextSet = null)
    {
        TextBox note = dialog.FindControl<TextBox>("Note")!;
        await FlushAsync(dialog);
        Require(note.IsEffectivelyVisible && note.IsEffectivelyEnabled, "Actual Note editor unavailable for text edit.");
        bool observed = false;
        void Changed(object? sender, TextChangedEventArgs args)
        { if (ReferenceEquals(sender, note) && note.Text == text) observed = true; }
        note.TextChanged += Changed;
        try
        {
            bool unchanged = note.Text == text;
            note.Text = text; // Avalonia 12 raises TextChanged asynchronously after rendering this value.
            afterTextSet?.Invoke();
            if (!unchanged) await Program.WaitUntilAsync(() => observed);
            await FlushAsync(dialog);
            Require(note.Text == text, "Actual Note editor changed or truncated the requested raw draft.");
            Require(B(dialog, "Save").IsEnabled == B(dialog, "Save").Command!.CanExecute(null),
                "Rendered Save state did not match its live command after Note.TextChanged.");
        }
        catch (TimeoutException)
        {
            Log(new { task = "note-textchanged-timeout", requested = NoteEvidence(text),
                actual = NoteEvidence(note.Text ?? ""), text_changed_observed = observed,
                note.IsEffectivelyVisible, note.IsEffectivelyEnabled,
                save_button_enabled = B(dialog, "Save").IsEnabled,
                save_command_available = B(dialog, "Save").Command?.CanExecute(null),
                status = dialog.FindControl<TextBlock>("Status")!.Text });
            throw;
        }
        finally { note.TextChanged -= Changed; }
    }
    private static async Task FlushAsync(Window window)
    { window.UpdateLayout(); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded); window.UpdateLayout(); }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
