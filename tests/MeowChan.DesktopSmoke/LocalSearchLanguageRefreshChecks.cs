using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;
using Microsoft.Data.Sqlite;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Root dispatch only, after Ready on the UI dispatcher. No Program.cs changes.
// Real themed native windows/fonts; fixture Text assignment is NOT OS typing evidence.
internal static class LocalSearchLanguageRefreshChecks
{
    private const string Marker = "SMOKE LOCAL_SEARCH_LANGUAGE_W26 ";
    private const string Needle = "wave24-synthetic";
    private static readonly string Hash = "0" + new string('a', 63);
    private static readonly string[] Languages = ["en", "de", "es", "fr", "ru", "en"];
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    internal static async Task RunAsync(MainWindow owner)
    {
        Gate(owner);
        string previous = Texts.Language;
        try
        {
            await RunFilenameAsync(owner);
            await RunHashAsync(owner);
            Console.WriteLine(Marker + "COMPLETE_ASSERTIONS_PASSED five_languages same_instances minimum360 realized_glyph_layout no_requery");
        }
        catch
        {
            Console.WriteLine(Marker + "FAILED no_completion_claim");
            throw;
        }
        finally { Texts.SetLanguage(previous); }
    }

    // Separately callable so a filename layout failure need not prevent root checking hash.
    internal static async Task RunFilenameAsync(MainWindow owner)
    {
        Gate(owner);
        string previous = Texts.Language;
        string profile = TemporaryWorkspacePaths.CreateDirectory("local-search-language-w24-");
        try
        {
            await SeedAsync(profile);
            Texts.SetLanguage("en");
            int readers = 0;
            var dialog = new FilenameSearchDialog(token =>
            {
                Interlocked.Increment(ref readers);
                return ArchiveReader.OpenAsync(profile, token);
            });
            Task<ArchiveFilenameHit?> shown = dialog.ShowDialog<ArchiveFilenameHit?>(owner);
            try
            {
                await VisibleAsync(owner, dialog);
                using var native = new MediaNativeInput(dialog);
                TextBox query = Named<TextBox>(dialog, "FilenameSearchQuery");
                ListBox results = Named<ListBox>(dialog, "FilenameSearchResults");
                query.Text = Needle; await SettleAsync(dialog);
                query.SelectionStart = 2; query.SelectionEnd = 7;
                var draft = dialog.Session.Current;
                foreach (string code in Languages)
                {
                    Texts.SetLanguage(code); await SettleAsync(dialog);
                    Require(dialog.Title == Expected("filename.search.title", code)
                        && ReferenceEquals(draft, dialog.Session.Current) && readers == 0
                        && query.Text == Needle && query.SelectionStart == 2 && query.SelectionEnd == 7
                        && results.Items.Count == 0 && !shown.IsCompleted,
                        "filename unsubmitted draft changed/searched on refresh");
                    CheckAttached(dialog, query);
                    Require(Named<TextBlock>(dialog, "FilenameSearchStatus").Text == Expected("filename.search.idle", code),
                        "filename draft status stale");
                    Console.WriteLine(Marker + "FILENAME_DRAFT_OK locale=" + code);
                }
                await native.ClickAsync(Named<Button>(dialog, "FilenameSearchSubmit"));
                await WaitAsync(() => dialog.Session.Current.StatusKey == "filename.search.ready" && results.Items.Count == 1);
                results.SelectedIndex = 0; results.ScrollIntoView(results.SelectedItem!);
                query.SelectionStart = 2; query.SelectionEnd = 7;
                await SettleAsync(dialog);
                var view = dialog.Session.Current;
                var hit = view.Rows.Single();
                int baseline = Volatile.Read(ref readers);
                Require(baseline == 1, "filename explicit search did not acquire exactly one reader");
                foreach (string code in Languages)
                {
                    Texts.SetLanguage(code);
                    await WaitAsync(() => dialog.Title == Expected("filename.search.title", code));
                    await SettleAsync(dialog);
                    Require(query.Text == Needle && query.SelectionStart == 2 && query.SelectionEnd == 7,
                        "filename language refresh changed query/text selection");
                    Require(ReferenceEquals(view, dialog.Session.Current) && ReferenceEquals(hit, dialog.Session.Current.Rows.Single())
                        && Volatile.Read(ref readers) == baseline && results.SelectedIndex == 0
                        && Named<Button>(dialog, "FilenameSearchSelect").IsEnabled && !shown.IsCompleted,
                        "filename language refresh changed search/result selection or dispatched reader");
                    Require(AutomationProperties.GetName(query) == Expected("filename.search.query", code)
                        && AutomationProperties.GetName(results) == Expected("filename.search.results", code), "filename accessible inputs stale");
                    Require(Named<TextBlock>(dialog, "FilenameSearchStatus").Text == Expected("filename.search.ready", code, 1L, 1, 0),
                        "filename count/status stale");
                    // Independent fixture projection, not the production DescribeHit formatter.
                    string row = Expected("filename.search.row", code, Needle + ".png", "synthetic", "g", "101", "102",
                        "preview", 0, Expected("filename.search.unknown", code), 7L,
                        Expected("filename.search.notDeleted", code), Expected("filename.search.notDeleted", code));
                    Require(results.SelectedItem?.ToString() == row, "filename selected row translation stale");
                    results.ScrollIntoView(results.SelectedItem!); await SettleAsync(dialog);
                    TextBlock renderedRow = results.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == row);
                    CheckAttached(dialog, renderedRow);
                    Require(Fits(renderedRow, results), "filename translated result outside viewport");
                    Console.WriteLine(Marker + "FILENAME_REFRESH_OK locale=" + code);
                    await MinimumAsync(dialog);
                    foreach (string action in new[] { "search", "cancel", "previous", "next", "select", "close" })
                    {
                        string suffix = action == "search" ? "Submit" : char.ToUpperInvariant(action[0]) + action[1..];
                        CheckAction(dialog, Named<Button>(dialog, "FilenameSearch" + suffix), Expected("filename.search." + action, code), code);
                    }
                    Require(ReferenceEquals(view, dialog.Session.Current) && Volatile.Read(ref readers) == baseline
                        && results.SelectedIndex == 0 && query.Text == Needle
                        && query.SelectionStart == 2 && query.SelectionEnd == 7 && !shown.IsCompleted,
                        "filename layout changed selection or queried");
                    Console.WriteLine(Marker + "FILENAME_LAYOUT_OK locale=" + code + " client=" + dialog.ClientSize);
                    // Restore height/width to avoid using tiny result viewport as a refresh oracle.
                    dialog.Width = 820; dialog.Height = 680; await SettleAsync(dialog);
                }
                await native.ClickAsync(Named<Button>(dialog, "FilenameSearchSelect"));
                Require(ReferenceEquals(hit, await shown.WaitAsync(Deadline)), "filename native selection returned wrong hit");
                Console.WriteLine(Marker + "FILENAME_RETURN_OK exact_reference");
            }
            finally
            {
                if (dialog.IsVisible) dialog.Close();
                await shown.WaitAsync(Deadline); await dialog.TerminateAsync().WaitAsync(Deadline);
            }
        }
        finally { Texts.SetLanguage(previous); /* Retain synthetic fixture; no cleanup. */ }
    }

    internal static async Task RunHashAsync(MainWindow owner)
    {
        Gate(owner);
        string previous = Texts.Language;
        var asset = new MediaAssetReference("asset-w24", "media-w24", "preview", "synthetic.bin",
            "/synthetic-never-opened/w24", 7, Hash, "https://example.invalid/synthetic", "synthetic.bin", false);
        var snapshot = new MediaAssetSnapshot("/synthetic-never-opened", MediaManifestStatus.Available,
            4, "generation-w24", [asset], "synthetic");
        Texts.SetLanguage("en");
        var dialog = new MediaHashSearchDialog(snapshot);
        Task<MediaAssetReference?> shown = dialog.ShowDialog<MediaAssetReference?>(owner);
        try
        {
            await VisibleAsync(owner, dialog);
            using var native = new MediaNativeInput(dialog);
            TextBox query = Named<TextBox>(dialog, "MediaHashSearchInput");
            query.Text = Hash.ToUpperInvariant(); await SettleAsync(dialog);
            query.SelectionStart = 1; query.SelectionEnd = 9;
            var draft = dialog.Operation.Current;
            foreach (string code in Languages)
            {
                Texts.SetLanguage(code); await SettleAsync(dialog);
                Require(dialog.Title == Expected("mediahash.title", code)
                    && ReferenceEquals(draft, dialog.Operation.Current) && query.Text == Hash.ToUpperInvariant()
                    && query.SelectionStart == 1 && query.SelectionEnd == 9 && !shown.IsCompleted
                    && Named<StackPanel>(dialog, "MediaHashSearchResults").Children.Count == 0,
                    "hash unsubmitted draft changed/searched on refresh");
                CheckAttached(dialog, query);
                Require(Named<TextBlock>(dialog, "MediaHashSearchStatus").Text == Expected("mediahash.ready", code),
                    "hash draft status stale");
                Console.WriteLine(Marker + "HASH_DRAFT_OK locale=" + code);
            }
            await native.ClickAsync(Named<Button>(dialog, "MediaHashSearchStart"));
            await WaitAsync(() => dialog.Operation.Current.Status == MediaHashSearchOperation.Status.Available
                && Named<StackPanel>(dialog, "MediaHashSearchResults").Children.Count == 1);
            query.SelectionStart = 1; query.SelectionEnd = 9;
            var view = dialog.Operation.Current;
            Require(ReferenceEquals(asset, view.Page.Single()), "hash fixture result identity mismatch");
            foreach (string code in Languages)
            {
                Texts.SetLanguage(code);
                await WaitAsync(() => dialog.Title == Expected("mediahash.title", code));
                await SettleAsync(dialog);
                Require(ReferenceEquals(view, dialog.Operation.Current) && query.Text == Hash.ToUpperInvariant()
                    && query.SelectionStart == 1 && query.SelectionEnd == 9 && !shown.IsCompleted,
                    "hash refresh changed query/selection/request/result");
                foreach (var pair in new[] { ("Heading", "title"), ("Scope", "scope"), ("InputLabel", "input") })
                    Require(Named<TextBlock>(dialog, "MediaHashSearch" + pair.Item1).Text == Expected("mediahash." + pair.Item2, code),
                        "hash fixed text stale: " + pair.Item1);
                Require(AutomationProperties.GetName(query) == Expected("mediahash.input", code), "hash accessible input stale");
                Require(Named<TextBlock>(dialog, "MediaHashSearchGeneration").Text == Expected("mediahash.generation", code, "generation-w24")
                    && Named<TextBlock>(dialog, "MediaHashSearchStatus").Text == Expected("mediahash.matches", code, 1)
                    && Named<TextBlock>(dialog, "MediaHashSearchPage").Text == Expected("mediahash.page", code, 1, 1, 1), "hash formatted labels stale");
                Button result = Named<StackPanel>(dialog, "MediaHashSearchResults").Children.OfType<Button>().Single();
                string row = Expected("mediahash.row", code, asset.AssetId, asset.MediaIdentifier, asset.Variant, Expected("mediahash.no", code));
                CheckAttached(dialog, result);
                Require(result.Content is TextBlock text && text.Text == row && AutomationProperties.GetName(result) == row,
                    "hash result action text/accessibility stale");
                Require(ReferenceEquals(asset, dialog.Operation.Select(view.Request, view.Offset, 0)), "hash result authority changed");
                Console.WriteLine(Marker + "HASH_REFRESH_OK locale=" + code);
                await MinimumAsync(dialog);
                foreach (var pair in new[] { ("Start", "search"), ("Previous", "previous"), ("Next", "next"), ("Close", "close") })
                {
                    Button action = Named<Button>(dialog, "MediaHashSearch" + pair.Item1);
                    action.BringIntoView(); await SettleAsync(dialog);
                    CheckAction(dialog, action, Expected("mediahash." + pair.Item2, code), code);
                }
                result.BringIntoView(); await SettleAsync(dialog);
                CheckAction(dialog, result, row, code);
                Require(ReferenceEquals(view, dialog.Operation.Current) && query.Text == Hash.ToUpperInvariant()
                    && query.SelectionStart == 1 && query.SelectionEnd == 9 && !shown.IsCompleted,
                    "hash layout changed query/selection/request");
                Console.WriteLine(Marker + "HASH_LAYOUT_OK locale=" + code + " client=" + dialog.ClientSize);
                dialog.Width = 760; dialog.Height = 640; await SettleAsync(dialog);
            }
            Button selected = Named<StackPanel>(dialog, "MediaHashSearchResults").Children.OfType<Button>().Single();
            selected.BringIntoView(); await SettleAsync(dialog);
            CheckAttached(dialog, selected);
            await native.ClickAsync(selected);
            Require(ReferenceEquals(asset, await shown.WaitAsync(Deadline)), "hash native selection returned wrong asset");
            Console.WriteLine(Marker + "HASH_RETURN_OK exact_reference");
        }
        finally
        {
            try
            {
                if (dialog.IsVisible) dialog.Close();
                await shown.WaitAsync(Deadline); await dialog.TerminateAsync().WaitAsync(Deadline);
            }
            finally { Texts.SetLanguage(previous); }
        }
    }

    private static void CheckAction(Window window, Button button, string expected, string code)
    {
        CheckAttached(window, button);
        Require(button.IsEffectivelyVisible && Fits(button, window), "action outside client: " + button.Name + "/" + code + " bounds=" + button.Bounds + " client=" + window.ClientSize);
        foreach (Control ancestor in button.GetVisualAncestors().OfType<Control>())
            if (ancestor.ClipToBounds || ancestor is ScrollViewer || ancestor is WrapPanel)
                Require(Fits(button, ancestor), "action outside container: " + button.Name + "/" + code);
        TextBlock[] labels = button.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == expected).ToArray();
        Require(labels.Length == 1, "renderer unavailable/ambiguous: " + button.Name + "/" + code);
        TextBlock label = labels[0];
        Require(AutomationProperties.GetName(button) == expected && Fits(label, button), "action name/text bounds mismatch");
        foreach (Control ancestor in label.GetVisualAncestors().OfType<Control>().TakeWhile(c => !ReferenceEquals(c, button)))
            Require(Fits(label, ancestor), "label exceeds realized presenter: " + button.Name + "/" + code);
        var layout = label.TextLayout;
        string rendered = string.Concat(layout.TextLines.SelectMany(line => line.TextRuns)
            .Where(run => run is not TextEndOfLine).Select(run => run.Text.ToString()));
        Require(layout.TextLines.Any() && double.IsFinite(layout.WidthIncludingTrailingWhitespace) && layout.Height > 0,
            "renderer bounds unavailable: " + button.Name + "/" + code);
        Require(rendered == expected && layout.TextLines.All(line => !line.HasCollapsed)
            && layout.WidthIncludingTrailingWhitespace <= label.Bounds.Width + 1 && layout.Height <= label.Bounds.Height + 1,
            "glyph clipping/collapse: " + button.Name + "/" + code + " label=" + label.Bounds.Size
            + " glyphs=" + layout.WidthIncludingTrailingWhitespace + "x" + layout.Height);
    }

    private static void CheckAttached(Window window, Control control)
    {
        Require(window.IsVisible && window.TryGetPlatformHandle()?.Handle is nint h && h != 0
            && ReferenceEquals(TopLevel.GetTopLevel(control), window)
            && control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0,
            "control detached/unmaterialized: " + control.Name);
    }

    private static bool Fits(Control child, Control container)
    {
        Point? origin = child.TranslatePoint(default, container);
        return double.IsFinite(child.Bounds.Width) && double.IsFinite(child.Bounds.Height)
            && child.Bounds.Width > 0 && child.Bounds.Height > 0 && origin is Point p
            && p.X >= -1 && p.Y >= -1 && p.X + child.Bounds.Width <= container.Bounds.Width + 1
            && p.Y + child.Bounds.Height <= container.Bounds.Height + 1;
    }
    private static async Task MinimumAsync(Window window)
    {
        window.Width = 360; window.Height = 680;
        await WaitAsync(() => Math.Abs(window.ClientSize.Width - 360) <= 1);
        await SettleAsync(window); // Minimum WIDTH, deliberately not a minimum-height claim.
    }
    private static async Task VisibleAsync(MainWindow owner, Window dialog)
    {
        await WaitAsync(() => dialog.IsVisible && owner.OwnedWindows.Contains(dialog)
            && dialog.TryGetPlatformHandle()?.Handle is nint handle && handle != 0);
        dialog.Activate(); await WaitAsync(() => dialog.IsActive); await SettleAsync(dialog);
    }
    private static async Task SettleAsync(Window window)
    {
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }
    private static Task WaitAsync(Func<bool> predicate) => Program.WaitUntilAsync(predicate);
    private static T Named<T>(Window window, string name) where T : Control
    {
        Control[] controls = window.GetVisualDescendants().OfType<Control>().Prepend(window).Where(c => c.Name == name).ToArray();
        Require(controls.Length == 1 && controls[0] is T, "missing/ambiguous actual control: " + name);
        return (T)controls[0];
    }
    private static string Expected(string key, string code, params object?[] args)
    {
        string template;
        if (code is "en" or "de")
        {
            var entry = FilenameSearchCatalog.Entries.Concat(MediaHashSearchCatalog.Entries).Single(e => e.Key == key);
            template = code == "de" ? entry.German : entry.English;
        }
        else
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream("MeowChan.Desktop.Localization.Packs." + code + ".json")
                ?? throw new InvalidOperationException(Marker + "missing embedded pack " + code);
            using JsonDocument document = JsonDocument.Parse(stream);
            template = document.RootElement.GetProperty("strings").GetProperty(key).GetString()!;
        }
        return string.Format(CultureInfo.GetCultureInfo(code), template, args);
    }
    private static void Gate(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        bool allowed = OperatingSystem.IsLinux()
            ? Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1" && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
            : (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") == "1";
        Require(allowed && Application.Current != null && owner.SessionHost.State == DesktopSessionState.Ready && owner.IsVisible && owner.TryGetPlatformHandle()?.Handle is nint h && h != 0,
            "requires existing Ready owner and isolated authorized native runner; no fallback");
        foreach (string code in Languages) Require(Texts.Languages.Any(l => l.Code == code), "runner missing bundled language " + code);
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(Marker + "ASSERT_FAILED " + message); }

    private static async Task SeedAsync(string profile)
    {
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        { await using var catalog = await ArchiveCatalog.OpenAsync(profile, lease); }
        using var connection = ArchiveSql.OpenConnection(Path.Combine(profile, "archive.db"), SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction();
        using var command = ArchiveSql.Command(connection, transaction, """
            INSERT INTO archive_threads(source,board,thread,url,title,media_directory,source_state,capture_enabled,first_seen_at,revision)
            VALUES('synthetic','g','101','https://example.invalid/synthetic','synthetic','synthetic-never-opened',2,0,'2026-11-06T00:00:00.0000000+00:00',7);
            INSERT INTO archive_posts(source,board,thread,post,ordinal,fields,is_deleted)
            VALUES('synthetic','g','101','102',0,0,0);
            INSERT INTO archive_attachments(source,board,thread,post,position,variant,original_name,planned_path,url,is_deleted,attachment_revision_key)
            VALUES('synthetic','g','101','102',0,'preview','wave24-synthetic.png','synthetic-never-opened','https://example.invalid/synthetic',0,NULL);
            """);
        command.ExecuteNonQuery(); transaction.Commit();
    }
}
