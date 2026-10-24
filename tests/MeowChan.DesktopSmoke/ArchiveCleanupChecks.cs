using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Interfaces;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.DesktopSmoke;

/// <summary>Opt-in private fixtures; real controls plus explicitly separated controlled service outcomes.</summary>
internal static class ArchiveCleanupChecks
{
    private static ResourceId Id(string thread) => new("4chan", "g", thread);
    private static string MediaRoot(string profile) => profile + "-cleanup-media";
    private static string Media(string profile, string thread) => StorageLayout.GetThreadDirectory(MediaRoot(profile), "4chan", "g", thread);
    private static readonly List<object> Evidence = [];

    internal static async Task SeedAsync(string profile)
    {
        string root = MediaRoot(profile);
        Require(!Directory.Exists(root), "Cleanup refuses an existing fixture media root.");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".cleanup-smoke-owned"), "Disposable archive cleanup fixture");
        Config config = Config.LoadConfig(profile); config.DownloadPath = root;
        config.DownloadsPaused = false; config.SaveUrlsOnExit = true; config.SaveConfig(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await new SessionStore(profile, lease).SaveAsync(new SessionSnapshot { SuppressedThreadUrls = ["https://offline.invalid/retain-suppression"] });
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        for (int n = 0; n < 6; n++)
        {
            string thread = (100 + n).ToString(); string directory = Media(profile, thread);
            int count = n < 4 ? n : 1;
            if (n != 5)
            {
                Directory.CreateDirectory(directory); Dictionary<string, object> entries = new();
                for (int i = 0; i < count; i++)
                {
                    byte[] bytes = [(byte)n, (byte)i, 41, 59]; string name = $"asset{i}.bin";
                    await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
                    entries[name] = new { MediaIdentifier = $"asset{i}", SourceUrl = "https://offline.invalid/" + name,
                        FileName = name, Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
                }
                if (entries.Count > 0) await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
            }
            else directory = Path.Combine(root, "unwitnessed-legacy-location", thread);
            await catalog.RegisterThreadAsync(new(Id(thread), $"https://boards.4chan.org/g/thread/{thread}", "Cleanup fixture " + thread, directory, true));
            await catalog.CaptureAsync(new(Id(thread), $"https://boards.4chan.org/g/thread/{thread}", "Cleanup fixture " + thread,
                ThreadSourceState.Archived, DateTimeOffset.UtcNow, directory,
                [new CapturedPost(thread, 0, PostFieldPresence.All, Html: "<p>Disposable cleanup fixture text</p>", Attachments: [])]));
        }
        new MeowChan.Favorites.ThreadFavoriteStore(profile, lease).Set(Id("104"), true);
        Directory.CreateDirectory(Path.Combine(root, "export"));
        await File.WriteAllTextAsync(Path.Combine(root, "export", "index.html"), "retained export");
    }

    internal static void Cleanup(string profile)
    {
        string root = MediaRoot(profile);
        if (File.Exists(Path.Combine(root, ".cleanup-smoke-owned"))) Directory.Delete(root, true);
    }

    internal static async Task RunAsync(MainWindow owner)
    {
        Texts.SetLanguage(Environment.GetEnvironmentVariable("MEOWCHAN_CLEANUP_LANGUAGE") ?? "en");
        Avalonia.Application.Current!.RequestedThemeVariant = Environment.GetEnvironmentVariable("MEOWCHAN_CLEANUP_THEME") == "Light"
            ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark;
        if (int.TryParse(Environment.GetEnvironmentVariable("MEOWCHAN_CLEANUP_WIDTH"), out int width))
        { Require(width >= 640 && width <= 1520, "Cleanup fixture width must be 640–1520 DIP."); owner.Width = width; }
        if (int.TryParse(Environment.GetEnvironmentVariable("MEOWCHAN_CLEANUP_HEIGHT"), out int height))
        { Require(height >= 480 && height <= 1000, "Cleanup fixture height must be 480–1000 DIP."); owner.Height = height; }
        owner.UpdateLayout();
        ArchiveCleanupPreview preview = await owner.SessionHost.PrepareArchiveCleanupAsync([]);
        Require(preview.Complete && !preview.IsStale && preview.Rows.Count == 6, "Real fixture inventory is incomplete.");
        Require(preview.Rows.Count(row => row.Eligibility == ArchiveCleanupEligibility.Candidate) == 3, "Expected only real 0/1/2 candidates.");
        await ControlledChecksAsync(owner, preview);
        await RealDeletionAsync(owner);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "smoke-archive-cleanup-evidence.json"),
            JsonSerializer.Serialize(Evidence, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("SMOKE archive cleanup: real private inventory/deletion/reopen and controlled stale/cancel/failure/partial/frozen-selection/focus checks passed.");
    }

    private static async Task ControlledChecksAsync(Window owner, ArchiveCleanupPreview preview)
    {
        int executionCalls = 0; List<ResourceId[]> preparations = [];
        Func<ArchiveCleanupPreview, IReadOnlyCollection<ResourceId>, CancellationToken, Task<ArchiveCleanupExecutionPlan>> prepare = (p, ids, _) =>
        { preparations.Add(ids.ToArray()); return Task.FromResult(Plan(p, ids)); };
        Func<ArchiveCleanupExecutionPlan, CancellationToken, Task<ArchiveCleanupExecutionResult>> execute = (_, _) =>
        { executionCalls++; throw new InvalidOperationException("Unexpected test execution"); };
        TaskCompletionSource<ArchiveCleanupPreview>? pendingPreview = null; int previewCalls = 0;
        ArchiveCleanupDialog dialog = new((_, _) => { Interlocked.Increment(ref previewCalls); return pendingPreview?.Task ?? Task.FromResult(preview); },
            (p, ids, token) => prepare(p, ids, token), (p, token) => execute(p, token), () => []);
        Task closed = Show(dialog, owner);
        await Ready(dialog); await CheckFocusAsync(dialog, "Refresh", "open");
        Require(!C<Button>(dialog, "Review").IsEnabled, "Cleanup preselected candidates.");
        Capture(dialog, "initial-controlled");
        await SearchAsync(dialog, "100", [Id("100")]); Click(dialog, "SelectShown");
        await SearchAsync(dialog, "102", [Id("102")]); Click(dialog, "SelectShown");
        await SearchAsync(dialog, "103", [Id("103")], selectShownEnabled: false); Require(!C<Button>(dialog, "SelectShown").IsEnabled, "More than two assets became selectable.");
        C<ComboBox>(dialog, "Filter").SelectedIndex = 2; await SearchAsync(dialog, "", [Id("104")], selectShownEnabled: false);
        Require(!C<Button>(dialog, "SelectShown").IsEnabled, "Protected rows became selectable.");
        Click(dialog, "Review"); await Confirmation(dialog);
        Require(preparations.Single().ToHashSet().SetEquals([Id("100"), Id("102")]), "Filters changed the explicit selection.");
        Require(!C<Button>(dialog, "Confirm").IsEnabled, "Permanent confirm lacks separate acknowledgement.");
        await CheckFocusAsync(dialog, "Back", "review"); Capture(dialog, "review-controlled");
        Click(dialog, "Back"); await CheckFocusAsync(dialog, "Review", "back");
        pendingPreview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(dialog, "Refresh");
        Require(!C<Button>(dialog, "Review").IsEnabled && !C<ListBox>(dialog, "List").IsEnabled, "Refresh retained usable selection.");
        await Program.WaitUntilAsync(() => previewCalls == 2);
        Click(dialog, "Cancel"); pendingPreview.SetResult(preview); await Ready(dialog);
        Require(!C<Button>(dialog, "Review").IsEnabled && !C<Button>(dialog, "SelectShown").IsEnabled, "Canceled late preview was adopted.");
        string canceledStatus = Texts.Language == "de" ? "Prüfung abgebrochen. Vor der Auswahl neu prüfen." : "Check canceled. Check again before selecting.";
        Require(C<TextBlock>(dialog, "Status").Text == canceledStatus && !C<Button>(dialog, "Cancel").IsVisible,
            "Finished preview cancellation retained an in-progress status or busy action.");
        await CheckFocusAsync(dialog, "Refresh", "preview-cancel"); Capture(dialog, "preview-canceled");
        pendingPreview = null; Click(dialog, "Refresh"); await Ready(dialog);
        C<ComboBox>(dialog, "Filter").SelectedIndex = 0; await SearchAsync(dialog, "100", [Id("100")]); Click(dialog, "SelectShown");
        TaskCompletionSource<ArchiveCleanupExecutionPlan> pendingPlan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool preparing = false; prepare = (_, _, _) => { preparing = true; return pendingPlan.Task; };
        Click(dialog, "Review"); await Program.WaitUntilAsync(() => C<Button>(dialog, "Cancel").IsVisible);
        await Program.WaitUntilAsync(() => preparing);
        dialog.InvalidatePreview(); pendingPlan.SetResult(Plan(preview, [Id("100")])); await Ready(dialog);
        Require(!C<Button>(dialog, "Confirm").IsVisible && !C<Button>(dialog, "Review").IsEnabled, "Profile/stale invalidation adopted a late confirmation plan.");
        await CheckFocusAsync(dialog, "Refresh", "profile-stale"); Capture(dialog, "stale");
        Click(dialog, "Refresh"); await Ready(dialog); await SearchAsync(dialog, "100", [Id("100")]); Click(dialog, "SelectShown");
        prepare = (p, _, _) => Task.FromResult(Plan(p, [Id("102")]));
        Click(dialog, "Review"); await Ready(dialog);
        Require(!C<Button>(dialog, "Confirm").IsVisible && !C<Button>(dialog, "Review").IsEnabled, "Changed plan identity was accepted.");
        Require(executionCalls == 0, "Preview, back, cancel or stale review triggered deletion.");
        Click(dialog, "Refresh"); await Ready(dialog); await SearchAsync(dialog, "100", [Id("100")]); Click(dialog, "SelectShown");
        prepare = (_, _, _) => Task.FromException<ArchiveCleanupExecutionPlan>(new IOException("fixture review failure"));
        Click(dialog, "Review"); await Ready(dialog);
        Require(C<TextBlock>(dialog, "Error").Text?.Contains("fixture review failure") == true, "Prepare failure was hidden.");
        await CheckOutcomeVisibleAsync(dialog, "review-failure", hasError: true);
        Capture(dialog, "review-failure");
        Click(dialog, "Close"); await closed;

        // A controlled partial result demonstrates presentation only. Real deletion is checked separately below.
        TaskCompletionSource<ArchiveCleanupExecutionResult> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken executionToken = default; int calls = 0;
        dialog = new((_, _) => Task.FromResult(preview), (p, ids, _) => Task.FromResult(Plan(p, ids)),
            (_, token) => { calls++; executionToken = token; return result.Task; }, () => []);
        closed = Show(dialog, owner); await Ready(dialog);
        await SearchAsync(dialog, "100", [Id("100")]); Click(dialog, "SelectShown"); await SearchAsync(dialog, "102", [Id("102")]); Click(dialog, "SelectShown");
        Click(dialog, "Review"); await Confirmation(dialog);
        C<CheckBox>(dialog, "ConfirmAcknowledgement").IsChecked = true;
        Click(dialog, "Confirm"); await Program.WaitUntilAsync(() => calls == 1);
        Require(!C<ListBox>(dialog, "List").IsEnabled && !C<Button>(dialog, "Refresh").IsEnabled, "Execution leaves selection editable.");
        Click(dialog, "Cancel"); Require(executionToken.IsCancellationRequested, "Cancel did not reach execution token.");
        result.SetResult(new([Id("100")], [Id("102")], true, true, ["fixture second-thread failure"], 1, 4));
        await Ready(dialog);
        Require(dialog.ArchiveChanged && calls == 1 && !C<Button>(dialog, "Review").IsEnabled, "Partial result allowed a second execution or lost committed state.");
        Require(C<TextBlock>(dialog, "Status").Text?.Contains("1") == true
            && C<TextBlock>(dialog, "Error").Text?.Contains("fixture second-thread failure") == true, "Partial result omitted remaining selection or failure.");
        await CheckFocusAsync(dialog, "Refresh", "partial-cancel");
        await CheckOutcomeVisibleAsync(dialog, "partial-canceled", hasError: true);
        Capture(dialog, "partial-canceled-controlled");
        Click(dialog, "Close"); await closed;
    }

    private static async Task RealDeletionAsync(MainWindow owner)
    {
        DesktopSessionHost host = owner.SessionHost; string profile = host.ProfileDirectory;
        var before = host.GetView(); bool paused = host.IsPaused;
        var protectedBefore = Durable(profile);
        string keep = Path.Combine(Media(profile, "104"), "asset0.bin"); byte[] keepBytes = await File.ReadAllBytesAsync(keep);
        Click(owner.FindControl<Button>("ArchiveNavigationButton")!);
        await Program.WaitUntilAsync(() => !owner.ArchivePage.IsBusy);
        Button entry = owner.ArchivePage.FindControl<Button>("ArchiveCleanupOpen")
            ?? throw new InvalidOperationException("Archive cleanup entry is missing.");
        Require(entry.IsVisible && entry.IsEnabled, "Archive cleanup entry is unavailable.");
        entry.Focus(); Click(entry);
        await Program.WaitUntilAsync(() => owner.OwnedWindows.OfType<ArchiveCleanupDialog>().Any());
        ArchiveCleanupDialog dialog = owner.OwnedWindows.OfType<ArchiveCleanupDialog>().Single(); await Ready(dialog);
        Capture(dialog, "real-preview"); Click(dialog, "Close");
        await Program.WaitUntilAsync(() => !owner.OwnedWindows.Contains(dialog));
        Require(File.Exists(Path.Combine(Media(profile, "102"), "asset0.bin")), "Closing preview deleted real media.");
        await Program.WaitUntilAsync(() => entry.IsEnabled && ReferenceEquals(owner.FocusManager?.GetFocusedElement(), entry));
        await CheckFocusAsync(owner, entry, "entry-return");
        Click(entry); await Program.WaitUntilAsync(() => owner.OwnedWindows.OfType<ArchiveCleanupDialog>().Any());
        dialog = owner.OwnedWindows.OfType<ArchiveCleanupDialog>().Single(); await Ready(dialog);
        await SearchAsync(dialog, "100", [Id("100")]); Click(dialog, "SelectShown"); await SearchAsync(dialog, "102", [Id("102")]); Click(dialog, "SelectShown");
        Click(dialog, "Review"); await Confirmation(dialog);
        Require(!C<Button>(dialog, "Confirm").IsEnabled, "Real confirmation enabled before acknowledgement.");
        await CheckFocusAsync(dialog, "Back", "real-review"); Capture(dialog, "real-confirmation");
        C<CheckBox>(dialog, "ConfirmAcknowledgement").IsChecked = true; Click(dialog, "Confirm"); await Ready(dialog);
        Require(dialog.ArchiveChanged && string.IsNullOrEmpty(C<TextBlock>(dialog, "Error").Text), "Real cleanup did not complete cleanly.");
        Require(!File.Exists(Path.Combine(Media(profile, "102"), "asset0.bin"))
            && !File.Exists(Path.Combine(Media(profile, "102"), ".meowchan-manifest.json")), "Real owned files remain.");
        Require(File.Exists(Path.Combine(Media(profile, "101"), "asset0.bin")) && File.Exists(Path.Combine(Media(profile, "103"), "asset2.bin")), "Unselected sibling media was deleted.");
        byte[] retainedBytes = await File.ReadAllBytesAsync(keep);
        Require(keepBytes.SequenceEqual(retainedBytes), "Protected media changed.");
        Require(await File.ReadAllTextAsync(Path.Combine(MediaRoot(profile), "export", "index.html")) == "retained export", "Export changed.");
        Require(host.IsPaused == paused && before.Threads.SequenceEqual(host.GetView().Threads) && before.Boards.SequenceEqual(host.GetView().Boards), "Cleanup changed global pause or watches.");
        var after = Durable(profile);
        Require(protectedBefore.Keys.Order().SequenceEqual(after.Keys.Order()) && protectedBefore.All(pair => pair.Value.SequenceEqual(after[pair.Key])), "Cleanup changed durable watches/suppression/protection/settings.");
        await using (ArchiveReader reopened = await ArchiveReader.OpenAsync(profile))
        {
            Require(await reopened.ReadThreadAsync(Id("100")) == null && await reopened.ReadThreadAsync(Id("102")) == null, "Fresh reader retained deleted catalog rows.");
            foreach (string id in new[] { "101", "103", "104", "105" }) Require(await reopened.ReadThreadAsync(Id(id)) != null, "Fresh reader lost retained " + id);
        }
        await CheckFocusAsync(dialog, "Refresh", "real-complete");
        await CheckOutcomeVisibleAsync(dialog, "real-result", hasError: false);
        Capture(dialog, "real-result");
        Evidence.Add(new { stage = "real-private-deletion", deleted = new[] { "100", "102" }, retained = new[] { "101", "103", "104", "105" }, globalPause = host.IsPaused,
            unchangedFiles = after.Keys.Order().ToArray(), freshCatalogReader = true, retainedExport = true, protectedMediaSha256 = Convert.ToHexString(SHA256.HashData(keepBytes)) });
        Click(dialog, "Close"); await Program.WaitUntilAsync(() => !owner.OwnedWindows.Contains(dialog)); await Program.WaitUntilAsync(() => entry.IsEnabled && ReferenceEquals(owner.FocusManager?.GetFocusedElement(), entry)); await CheckFocusAsync(owner, entry, "real-result-return");
    }

    private static Task Show(ArchiveCleanupDialog dialog, Window owner)
    {
        dialog.Width = Math.Max(360, Math.Min(820, owner.ClientSize.Width - 32));
        dialog.Height = Math.Max(380, Math.Min(700, owner.ClientSize.Height - 32));
        return dialog.ShowDialog(owner);
    }

    private static ArchiveCleanupExecutionPlan Plan(ArchiveCleanupPreview preview, IReadOnlyCollection<ResourceId> ids)
        => new(preview, ids.ToArray(), preview.Rows.Where(row => ids.Contains(row.Id)).ToArray(),
            new Dictionary<ResourceId, IReadOnlyList<ArchiveCleanupDirectoryFiles>>(), []);
    private static Dictionary<string, byte[]> Durable(string profile)
    {
        var files = Directory.EnumerateFiles(profile).Where(path =>
        Path.GetFileName(path).Replace(".bak", "", StringComparison.Ordinal) is "settings.json" or "saved_urls.json" or "work-state.json" or "thread-favorites.json" or "media-favorites.json"
            or "media-ratings.json" or "reader-groups.json" or "reading-position.json" or "post-bookmarks.json")
        .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes);
        string journalPath = Path.Combine(profile, "work-state.sqlite");
        if (File.Exists(journalPath))
        {
            using SqliteConnection db = new(new SqliteConnectionStringBuilder { DataSource = journalPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            db.Open(); List<string> rows = [];
            foreach (string table in new[] { "work_resources", "work_media" })
            {
                using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM " + table + " ORDER BY payload";
                using var reader = command.ExecuteReader(); while (reader.Read()) rows.Add(table + ":" + reader.GetString(0));
            }
            files["work-state.logical"] = JsonSerializer.SerializeToUtf8Bytes(rows);
        }
        return files;
    }
    private static T C<T>(Window dialog, string suffix) where T : Control => dialog.FindControl<T>("ArchiveCleanup" + suffix)
        ?? throw new InvalidOperationException("Missing ArchiveCleanup" + suffix);
    private static async Task SearchAsync(Window dialog, string text, ResourceId[] expectedIds, bool selectShownEnabled = true)
    {
        // Avalonia TextChanged is asynchronous, after rendering. Observe the actual list and action
        // rather than assuming Text assignment has already run the product's RenderRows handler.
        C<TextBox>(dialog, "Search").Text = text;
        ResourceId[] VisibleIds() => C<ListBox>(dialog, "List").ItemsSource?.Cast<ArchiveCleanupPreviewRow>()
            .Select(row => row.Id).ToArray() ?? [];
        try
        {
            await Program.WaitUntilAsync(() => VisibleIds().SequenceEqual(expectedIds)
                && C<Button>(dialog, "SelectShown").IsEnabled == selectShownEnabled);
        }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException("Cleanup search did not settle for '" + text + "': expected rows "
                + string.Join(",", expectedIds.Select(id => id.Thread)) + "; actual rows "
                + string.Join(",", VisibleIds().Select(id => id.Thread)) + "; SelectShown expected="
                + selectShownEnabled + ", actual=" + C<Button>(dialog, "SelectShown").IsEnabled, error);
        }
        Console.WriteLine("SMOKE cleanup search settled: text=" + text + "; visible="
            + string.Join(",", VisibleIds().Select(id => id.Thread)) + "; SelectShown=" + selectShownEnabled);
    }
    private static void Click(Window dialog, string suffix) => Click(C<Button>(dialog, suffix));
    private static void Click(Button button)
    { Require(button.IsEnabled, "Cannot click disabled " + button.Name); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    private static Task Ready(Window dialog) => Program.WaitUntilAsync(() => dialog.IsVisible && C<Button>(dialog, "Refresh").IsEnabled);
    private static async Task Confirmation(Window dialog)
    {
        Control[] required = [C<CheckBox>(dialog, "ConfirmAcknowledgement"), C<Button>(dialog, "Back"), C<Button>(dialog, "Confirm")];
        try
        {
            await Program.WaitUntilAsync(() => C<Button>(dialog, "Back").IsEnabled
                && required.All(control => VisibilityOf(control, dialog).FullyVisible));
        }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException("Confirmation controls are outside the visible scroll viewport: "
                + JsonSerializer.Serialize(required.Select(control => new { control.Name, geometry = VisibilityOf(control, dialog) })), error);
        }
        Evidence.Add(new { stage = "confirmation-visible-controls", controls = required.Select(control =>
            new { control.Name, geometry = VisibilityOf(control, dialog) }).ToArray() });
    }
    private static Task CheckFocusAsync(Window dialog, string suffix, string stage) => CheckFocusAsync(dialog, C<Control>(dialog, suffix), stage);
    private static async Task CheckFocusAsync(Window dialog, Control expected, string stage)
    {
        // Observe the product's focus/reveal after layout. The harness must not scroll or focus for it.
        try
        {
            await Program.WaitUntilAsync(() => ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), expected)
                && VisibilityOf(expected, dialog).FullyVisible);
        }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException(stage + " focus target is not visibly focused: expected=" + expected.Name
                + "; actual=" + (dialog.FocusManager?.GetFocusedElement() as Control)?.Name
                + "; geometry=" + JsonSerializer.Serialize(VisibilityOf(expected, dialog)), error);
        }
        string? actual = (dialog.FocusManager?.GetFocusedElement() as Control)?.Name;
        VisibilityEvidence geometry = VisibilityOf(expected, dialog);
        Evidence.Add(new { stage, expectedFocus = expected.Name, actualFocus = actual, scale = dialog.RenderScaling, geometry });
        Console.WriteLine("SMOKE cleanup focus: " + stage + "; expected=" + expected.Name + "; actual=" + actual
            + "; geometry=" + JsonSerializer.Serialize(geometry));
        Require(ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), expected) && geometry.FullyVisible,
            stage + " focus expected visible " + expected.Name + ", got " + actual);
    }
    private static async Task CheckOutcomeVisibleAsync(Window dialog, string stage, bool hasError)
    {
        TextBlock status = C<TextBlock>(dialog, "Status");
        TextBlock errorText = C<TextBlock>(dialog, "Error");
        Button refresh = C<Button>(dialog, "Refresh");
        Control[] required = hasError ? [status, errorText, refresh] : [status, refresh];
        // No test-side scrolling: result/error text and the focused next action must be visible together.
        try
        {
            await Program.WaitUntilAsync(() => !string.IsNullOrWhiteSpace(status.Text)
                && (!hasError || !string.IsNullOrWhiteSpace(errorText.Text))
                && ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), refresh) && refresh.IsEnabled
                && required.All(control => VisibilityOf(control, dialog).FullyVisible));
        }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException(stage + " outcome text and next action are not visible together: "
                + JsonSerializer.Serialize(required.Select(control => new { control.Name, geometry = VisibilityOf(control, dialog) })), error);
        }
        var observed = new { stage = "visible-outcome-" + stage, status = status.Text, error = errorText.Text,
            controls = required.Select(control => new { control.Name, geometry = VisibilityOf(control, dialog) }).ToArray() };
        Evidence.Add(observed);
        Console.WriteLine("SMOKE cleanup visible outcome: " + JsonSerializer.Serialize(observed));
    }
    private sealed record VisibilityEvidence(bool FullyVisible, string Bounds, string ClippedBounds, string[] ClipParents);
    private static VisibilityEvidence VisibilityOf(Control control, Window dialog)
    {
        Point? origin = control.TranslatePoint(default, dialog);
        if (!control.IsEffectivelyVisible || origin == null || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            return new(false, control.Bounds.ToString(), "not visible or detached", []);
        Rect target = new(origin.Value, control.Bounds.Size);
        Rect clipped = target.Intersect(new Rect(dialog.ClientSize));
        List<string> parents = ["Window:" + new Rect(dialog.ClientSize)];
        foreach (var parent in control.GetVisualAncestors().Where(parent => parent.ClipToBounds || parent is ScrollViewer))
        {
            Point? position = parent.TranslatePoint(default, dialog);
            if (position == null) return new(false, target.ToString(), "detached ancestor", parents.ToArray());
            Rect viewport = new(position.Value, parent.Bounds.Size);
            clipped = clipped.Intersect(viewport);
            parents.Add(parent.GetType().Name + ":" + viewport);
        }
        bool fullyVisible = clipped.Width > 0 && clipped.Height > 0
            && Math.Abs(clipped.Left - target.Left) <= 0.5 && Math.Abs(clipped.Top - target.Top) <= 0.5
            && Math.Abs(clipped.Right - target.Right) <= 0.5 && Math.Abs(clipped.Bottom - target.Bottom) <= 0.5;
        return new(fullyVisible, target.ToString(), clipped.ToString(), parents.ToArray());
    }
    private static void Capture(Window dialog, string stage)
    {
        string name = "smoke-archive-cleanup-" + stage + ".png"; Program.CaptureWindow(dialog, name);
        Evidence.Add(new { stage, image = name, focus = (dialog.FocusManager?.GetFocusedElement() as Control)?.Name,
            width = dialog.Bounds.Width, height = dialog.Bounds.Height, scale = dialog.RenderScaling, language = Texts.Language,
            status = C<TextBlock>(dialog, "Status").Text, error = C<TextBlock>(dialog, "Error").Text,
            selection = C<TextBlock>(dialog, "SelectionSummary").Text, loss = C<TextBlock>(dialog, "LossSummary").Text });
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
