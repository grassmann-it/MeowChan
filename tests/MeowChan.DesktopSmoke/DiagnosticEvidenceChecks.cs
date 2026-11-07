using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// Root calls this on the native UI dispatcher. Only in-memory snapshots and delegates;
// no registry/provider, Config, profile, disk fixture, transport or OS clipboard access.
internal static class DiagnosticEvidenceChecks
{
    private const string Marker = "SMOKE DIAGNOSTIC_EVIDENCE_W28 ";
    private const long RefusedWatch = 28001, MissingWatch = 28002, RequestWatch = 28003, PendingWatch = 28004;
    private const string RefusedRaw = "Synthetic refused response; no inferred cause";
    private const string MissingRaw = "Synthetic missing media; source remains active";
    private static readonly string[] Keys = [
        "diagnostics.observed.operation", "diagnostics.observed.evidence", "diagnostics.observed.next",
        "diagnostics.observed.thread", "diagnostics.observed.media", "diagnostics.observed.watchUrl",
        "diagnostics.observed.mediaItem", "diagnostics.observed.http403", "diagnostics.observed.http404",
        "diagnostics.observed.media404", "diagnostics.observed.checkSource",
        "diagnostics.includeWaiting", "diagnostics.copyPrivacy"
    ];

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible && owner.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
            "Root must supply an already visible native MainWindow on the UI dispatcher.");
        string previousLanguage = Texts.Language;
        List<long> checks = [], media = [];
        List<string> copies = [];
        int opens = 0;
        bool admitted = true;
        var page = new DiagnosticsPage(_ => { opens++; return false; }, text =>
        {
            copies.Add(text);
            return Task.CompletedTask;
        }, target =>
        {
            checks.Add(target.WatchId);
            return Task.FromResult(CheckRequestStatus.Scheduled);
        }, _ => admitted)
        {
            CanRetryMedia = _ => admitted,
            RetryMediaAsync = target => { media.Add(target.WatchId); return Task.FromResult(1); }
        };
        var fixture = new Window
        {
            Title = "GP39 passive diagnostic evidence — synthetic fixture",
            Width = 760, Height = 560, MinWidth = 640, MinHeight = 480,
            Content = page
        };
        DesktopSessionView view = CreateView();
        string stage = "catalog/native attachment";
        try
        {
            var catalogs = new[] { "en", "de", "es", "fr", "ru" }.ToDictionary(code => code, ReadCatalog);
            Require(catalogs["en"]["diagnostics.observed.http403"] ==
                "The server refused this request (HTTP 403); this alone does not prove a ban or login problem.",
                "Canonical HTTP403 must describe refusal, not establish a ban/login cause.");
            Require(catalogs["en"]["diagnostics.observed.media404"] ==
                "This media file was not found (HTTP 404); this does not establish the thread state.",
                "Canonical media404 must not establish thread state.");
            Texts.SetLanguage("en");
            page.Refresh(view, true);
            fixture.Show(owner);
            await WaitAsync(() => fixture.IsVisible && owner.OwnedWindows.Contains(fixture)
                && fixture.TryGetPlatformHandle()?.Handle is nint native && native != 0, "fixture native window");
            fixture.Activate();
            await SettleAsync(fixture);
            DataGrid grid = Named<DataGrid>(page, "DiagnosticsGrid");
            TextBox details = Named<TextBox>(page, "DiagnosticDetails");
            TextBlock privacy = Named<TextBlock>(page, "DiagnosticCopyPrivacy");
            CheckBox waiting = Named<CheckBox>(page, "DiagnosticIncludeWaiting");
            CheckBox problems = Named<CheckBox>(page, "DiagnosticProblemsOnly");
            Require(waiting.Content is TextBlock, "Waiting option must retain a wrapping TextBlock.");
            var waitingLabel = (TextBlock)waiting.Content!;
            Require(waiting.IsChecked != true && problems.IsChecked != true, "Diagnostic filters did not start opt-out.");
            RequireRows(grid, (RefusedWatch, DiagnosticKind.Check), (MissingWatch, DiagnosticKind.Media),
                (RequestWatch, DiagnosticKind.Request), (PendingWatch, DiagnosticKind.PendingWork));
            await CheckDetailsAsync(fixture, page, RefusedWatch, DiagnosticKind.Check, catalogs["en"]);
            await CheckDetailsAsync(fixture, page, MissingWatch, DiagnosticKind.Media, catalogs["en"]);
            Require(checks.Count == 0 && media.Count == 0 && copies.Count == 0 && opens == 0,
                "Opening/selecting/refreshing diagnostics dispatched an action.");

            stage = "waiting membership and authority";
            await ClickAsync(fixture, problems);
            RequireRows(grid, (RefusedWatch, DiagnosticKind.Check), (MissingWatch, DiagnosticKind.Media));
            await ClickAsync(fixture, waiting);
            Require(waiting.IsChecked == true && problems.IsChecked == true, "Waiting toggle did not change independently.");
            RequireRows(grid, (RefusedWatch, DiagnosticKind.Check), (MissingWatch, DiagnosticKind.Media),
                (RequestWatch, DiagnosticKind.Request), (PendingWatch, DiagnosticKind.PendingWork));
            foreach (DiagnosticEntry row in Entries(grid).Where(e => e.Kind is DiagnosticKind.Request or DiagnosticKind.PendingWork))
                Require(!row.IsProblem, "Visible waiting row became a problem: " + row.Key);
            Require(DiagnosticRetryScope.Capture(Entries(grid)).Select(item => item.Target.WatchId)
                .SequenceEqual(new[] { RefusedWatch, MissingWatch }), "Opt-in expanded the retry scope.");
            await SelectAsync(fixture, page, PendingWatch, DiagnosticKind.PendingWork);
            Require(!Named<Button>(page, "RetryDiagnosticSelectionButton").IsEnabled,
                "Waiting-only selection enabled a problem retry batch.");

            // Even permissive callbacks cannot make waiting rows enter either batch.
            Named<TextBox>(page, "DiagnosticTextFilter").Text = "waiting-only";
            await SettleAsync(fixture);
            RequireRows(grid, (RequestWatch, DiagnosticKind.Request), (PendingWatch, DiagnosticKind.PendingWork));
            await SelectAsync(fixture, page, RequestWatch, DiagnosticKind.Request);
            Require(!Named<Button>(page, "RetryDiagnosticFilteredButton").IsEnabled
                && !Named<Button>(page, "RetryDiagnosticSelectionButton").IsEnabled
                && DiagnosticRetryScope.Capture(Entries(grid)).Length == 0, "Waiting-only filter acquired batch authority.");
            // Deliberately route disabled button events too: handlers must independently reject an empty scope.
            RaiseClick(page, "RetryDiagnosticSelectionButton");
            RaiseClick(page, "RetryDiagnosticFilteredButton");
            await SettleAsync(fixture);
            Require(checks.Count == 0 && media.Count == 0, "Empty waiting scope submitted a retry.");
            admitted = false;
            page.Refresh(view, true);
            Require(!Named<Button>(page, "RetryDiagnosticButton").IsEnabled, "Waiting opt-in bypassed single-check admission.");
            RaiseClick(page, "RetryDiagnosticButton");
            await SettleAsync(fixture);
            Require(checks.Count == 0 && media.Count == 0, "Denied single-check admission dispatched work.");
            // Do not assert that waiting forbids every single check: the existing host decides eligibility.
            admitted = true;
            await ClickAsync(fixture, Named<Button>(page, "DiagnosticClearFilters"));
            Require(waiting.IsChecked != true && problems.IsChecked != true
                && string.IsNullOrEmpty(Named<TextBox>(page, "DiagnosticTextFilter").Text), "Clear did not reset waiting opt-in.");
            await ClickAsync(fixture, problems);
            await ClickAsync(fixture, waiting);
            await ClickAsync(fixture, Named<Button>(page, "RetryDiagnosticFilteredButton"));
            await WaitAsync(() => Named<Button>(page, "RetryDiagnosticFilteredButton").IsEnabled, "finite synthetic batch completion");
            Require(checks.SequenceEqual(new[] { RefusedWatch }) && media.SequenceEqual(new[] { MissingWatch }),
                "Filtered retry included a waiting watch or confused check/media operations.");
            Console.WriteLine(Marker + "WAITING_ASSERTIONS_PASSED neutral_request_pending exact_batches denied_single_admission");

            // A page alone does not subscribe to Texts.Changed. Exercise its actual supported
            // Refresh(view) boundary on the SAME OPEN instance, not a test-added subscription.
            stage = "open-page language refresh and compact layout";
            await SelectAsync(fixture, page, RefusedWatch, DiagnosticKind.Check);
            int expectedCopies = 0;
            foreach (string code in new[] { "en", "de", "en", "es", "en", "fr", "en", "ru", "en" })
            {
                DiagnosticEntry frozen = (DiagnosticEntry)grid.SelectedItem!;
                string frozenDetails = frozen.Details;
                string priorLanguage = Texts.Language;
                Texts.SetLanguage(code);
                Require(Texts.Language == code, "UNSUPPORTED locale (no fallback accepted): " + code);
                page.Refresh(view, true);
                await SettleAsync(fixture);
                Require(ReferenceEquals(fixture.Content, page) && ReferenceEquals(grid, Named<DataGrid>(page, "DiagnosticsGrid"))
                    && ReferenceEquals(details, Named<TextBox>(page, "DiagnosticDetails")) && ReferenceEquals(waiting.Content, waitingLabel),
                    "Language refresh replaced the open page/grid/details/wrapping label.");
                Require(grid.SelectedItem is DiagnosticEntry retained && retained.Key == frozen.Key
                    && retained.Target == frozen.Target && retained.ResourceId == frozen.ResourceId && frozen.Details == frozenDetails,
                    "Refresh lost selected identity or changed a frozen entry.");
                Require(waiting.IsChecked == true && problems.IsChecked == true, "Language refresh reset filter intent.");
                IReadOnlyDictionary<string, string> catalog = catalogs[code];
                foreach (string key in Keys)
                    Require(Texts.T(key) == catalog[key], "Catalog fallback/stale key: " + code + "/" + key);
                // Verify the already-selected editor BEFORE selecting another row. Reselection
                // must not hide a defect in the live refresh of the open details pane.
                DiagnosticEntry current = (DiagnosticEntry)grid.SelectedItem!;
                Require(details.Text == current.Details
                    && details.Text!.Contains(catalog["diagnostics.observed.operation"] + ": "
                        + catalog[current.Kind == DiagnosticKind.Media ? "diagnostics.observed.media" : "diagnostics.observed.thread"], StringComparison.Ordinal)
                    && details.Text.Contains(catalog["diagnostics.observed.evidence"], StringComparison.Ordinal)
                    && details.Text.Contains(catalog[current.Kind == DiagnosticKind.Media ? "diagnostics.observed.media404" : "diagnostics.observed.http403"], StringComparison.Ordinal)
                    && (priorLanguage == code || details.Text != frozenDetails), "Selected detail text did not refresh in place: " + code);
                await RevealAsync(fixture, details);
                TextPresenter refreshed = details.GetVisualDescendants().OfType<TextPresenter>().Single();
                Require(refreshed.Text == details.Text && Flatten(refreshed.TextLayout) == WithoutNewlines(details.Text!),
                    "Already-open detail presenter did not render the new language: " + code);
                Require(privacy.Text == catalog["diagnostics.copyPrivacy"] && waitingLabel.Text == catalog["diagnostics.includeWaiting"]
                    && AutomationProperties.GetName(waiting) == catalog["diagnostics.includeWaiting"]
                    && AutomationProperties.GetHelpText(Named<Button>(page, "CopyDiagnosticButton")) == catalog["diagnostics.copyPrivacy"],
                    "Open-page notice/checkbox/accessibility language is stale: " + code);
                RequireRows(grid, (RefusedWatch, DiagnosticKind.Check), (MissingWatch, DiagnosticKind.Media),
                    (RequestWatch, DiagnosticKind.Request), (PendingWatch, DiagnosticKind.PendingWork));
                await CheckDetailsAsync(fixture, page, RefusedWatch, DiagnosticKind.Check, catalog);
                await CheckDetailsAsync(fixture, page, MissingWatch, DiagnosticKind.Media, catalog);
                string copiedDetails = details.Text!;
                Require(copies.Count == expectedCopies, "Language/selection/refresh copied details automatically.");
                await ClickAsync(fixture, Named<Button>(page, "CopyDiagnosticButton"));
                expectedCopies++;
                Require(copies.Count == expectedCopies && copies[^1] == copiedDetails
                    && copies[^1].Contains(WatchUrl(MissingWatch), StringComparison.Ordinal)
                    && copies[^1].Contains(MissingRaw, StringComparison.Ordinal),
                    "Explicit copy did not preserve exact raw selected details (no redaction claim).");

                fixture.Width = 640; fixture.Height = 480;
                await WaitAsync(() => Math.Abs(fixture.ClientSize.Width - 640) <= 1
                    && Math.Abs(fixture.ClientSize.Height - 480) <= 1, "640x480 native client");
                await SettleAsync(fixture);
                CheckLabel(fixture, privacy, catalog["diagnostics.copyPrivacy"]);
                await RevealAsync(fixture, waiting);
                CheckLabel(fixture, waitingLabel, catalog["diagnostics.includeWaiting"]);
                Require(waitingLabel.TextWrapping == TextWrapping.Wrap && privacy.TextWrapping == TextWrapping.Wrap,
                    "Compact waiting/privacy text no longer wraps.");
                await CheckDetailsAsync(fixture, page, MissingWatch, DiagnosticKind.Media, catalog);
                Require(checks.SequenceEqual(new[] { RefusedWatch }) && media.SequenceEqual(new[] { MissingWatch }) && opens == 0,
                    "Language/layout/copy dispatched further retry or navigation work.");
                Console.WriteLine(Marker + "OPEN_REFRESH_ASSERTIONS_PASSED locale=" + code
                    + " client=" + fixture.ClientSize + " explicit_snapshot_refresh real_text_presenter raw_copy_sink");
                fixture.Width = 760; fixture.Height = 560;
                await SettleAsync(fixture);
            }
        }
        catch (Exception error)
        {
            Console.WriteLine(Marker + "FAILED stage=" + stage + " locale=" + Texts.Language
                + " client=" + fixture.ClientSize + " error=" + error.Message);
            throw; // Unsupported catalogs/layout are failures, not skips or repaired controls.
        }
        finally
        {
            Dispatcher.UIThread.VerifyAccess();
            page.EndLifetime();
            try { fixture.Close(); }
            finally { Texts.SetLanguage(previousLanguage); await SettleAsync(owner); }
        }
        Console.WriteLine(Marker + "COMPLETE_ASSERTIONS_PASSED native_fixture_window observed403_media404 neutral_waiting privacy "
            + "EN_DE_ES_FR_RU explicit_open_page_refresh 640x480 routed_pointer synthetic_delegates "
            + "NOT_shell_language_dispatch_OS_input_clipboard_transport_or_linguistic_acceptance");
    }

    private static async Task CheckDetailsAsync(Window window, DiagnosticsPage page, long watch, DiagnosticKind kind,
        IReadOnlyDictionary<string, string> catalog)
    {
        DiagnosticEntry entry = await SelectAsync(window, page, watch, kind);
        TextBox details = Named<TextBox>(page, "DiagnosticDetails");
        await RevealAsync(window, details);
        Require(details.IsReadOnly && details.Text == entry.Details, "Selected details editor is stale/editable.");
        TextPresenter presenter = details.GetVisualDescendants().OfType<TextPresenter>().Single();
        string operation = catalog["diagnostics.observed.operation"] + ": "
            + catalog[kind == DiagnosticKind.Media ? "diagnostics.observed.media" : "diagnostics.observed.thread"];
        string fact = catalog[kind == DiagnosticKind.Media ? "diagnostics.observed.media404" : "diagnostics.observed.http403"];
        string raw = kind == DiagnosticKind.Media ? MissingRaw : RefusedRaw;
        string http = kind == DiagnosticKind.Media ? "Http · HTTP 404" : "Http · HTTP 403";
        string[] expected = [operation, catalog["diagnostics.observed.evidence"] + ":", fact, http, raw,
            catalog["diagnostics.observed.watchUrl"] + ": " + WatchUrl(watch),
            catalog["diagnostics.observed.next"] + ": " + catalog["diagnostics.observed.checkSource"]];
        foreach (string fragment in expected)
            Require(details.Text!.Contains(fragment, StringComparison.Ordinal), "Missing actual detail fragment: " + fragment);
        Require(!details.Text!.Contains(catalog[kind == DiagnosticKind.Media ? "diagnostics.observed.http403" : "diagnostics.observed.media404"], StringComparison.Ordinal),
            "Check403 and media404 explanations were conflated.");
        if (kind == DiagnosticKind.Media)
            Require(!details.Text.Contains(catalog["diagnostics.observed.http404"], StringComparison.Ordinal)
                && !details.Text.Contains(Texts.T("work.sourceEnded"), StringComparison.Ordinal)
                && details.Text.Contains(catalog["diagnostics.observed.mediaItem"] + ": media-404", StringComparison.Ordinal),
                "Media404 lost item context or asserted thread/source end.");
        Require(presenter.Text == details.Text && Flatten(presenter.TextLayout) == WithoutNewlines(details.Text)
            && presenter.TextLayout.TextLines.All(line => !line.HasCollapsed), "Actual TextPresenter glyph text is stale/collapsed.");
        // The bounded details editor intentionally scrolls. Verify first and last glyph
        // of each semantic line can be revealed through REAL inner/outer scroll presenters.
        foreach (string fragment in expected.Take(3))
        {
            int start = details.Text.IndexOf(fragment, StringComparison.Ordinal);
            foreach (int index in new[] { start, start + fragment.Length - 1 })
            {
                Rect glyph = presenter.TextLayout.HitTestTextPosition(index);
                glyph = new Rect(glyph.X, glyph.Y, Math.Max(1, glyph.Width), glyph.Height);
                Require(glyph.Height > 0, "No realized glyph height for detail evidence.");
                presenter.BringIntoView(glyph);
                await SettleAsync(window);
                Require(VisibleRect(window, presenter, glyph), "Observed detail glyph remains clipped: " + fragment);
            }
        }
        CheckLabel(window, Named<TextBlock>(page, "DiagnosticCopyPrivacy"), catalog["diagnostics.copyPrivacy"]);
    }

    private static async Task<DiagnosticEntry> SelectAsync(Window window, DiagnosticsPage page, long watch, DiagnosticKind kind)
    {
        DataGrid grid = Named<DataGrid>(page, "DiagnosticsGrid");
        DiagnosticEntry entry = Entries(grid).Single(e => e.Target?.WatchId == watch && e.Kind == kind);
        grid.SelectedItem = entry; // Managed selection; no OS-keyboard claim.
        grid.ScrollIntoView(entry, null);
        await WaitAsync(() =>
        {
            window.UpdateLayout();
            DataGridRow? row = grid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => ReferenceEquals(r.DataContext, entry));
            row?.BringIntoView();
            return row != null && row.IsEffectivelyVisible && row.Bounds.Height > 0
                && row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == entry.Summary);
        }, "realized diagnostic row " + entry.Key);
        await SettleAsync(window);
        Require(Named<TextBlock>(page, "DiagnosticSelectionTitle").Text == entry.Summary,
            "Selected-row title did not update.");
        return entry;
    }

    private static void CheckLabel(Window window, TextBlock label, string expected)
    {
        Require(label.Text == expected && VisibleRect(window, label, new Rect(label.Bounds.Size)),
            "Label is stale or outside visible client/scroll bounds: " + label.Name);
        TextLayout layout = label.TextLayout;
        Require(Flatten(layout) == WithoutNewlines(expected) && layout.TextLines.Count > 0
            && layout.TextLines.All(line => !line.HasCollapsed) && layout.Height > 0
            && layout.Height <= label.Bounds.Height + 1 && layout.Width <= label.Bounds.Width + 1,
            "Label glyphs truncated/collapsed: " + label.Name);
    }

    private static bool VisibleRect(Window window, Control control, Rect rect)
    {
        if (!control.IsEffectivelyVisible || !ReferenceEquals(TopLevel.GetTopLevel(control), window)
            || rect.Width <= 0 || rect.Height <= 0) return false;
        foreach (Control clip in control.GetVisualAncestors().OfType<Control>()
            .Where(c => c.ClipToBounds || c is ScrollContentPresenter).Append(window))
        {
            Point? p = control.TranslatePoint(rect.Position, clip);
            if (p == null || p.Value.X < -1 || p.Value.Y < -1 || p.Value.X + rect.Width > clip.Bounds.Width + 1
                || p.Value.Y + rect.Height > clip.Bounds.Height + 1) return false;
        }
        return true;
    }

    private static async Task ClickAsync(Window window, Control control)
    {
        await RevealAsync(window, control);
        Require(control.IsEffectivelyEnabled, "Cannot click disabled control: " + control.Name);
        Point point = default;
        string? firstMiss = null;
        string lastSample = "not sampled";
        int samples = 0;
        // BringIntoView/UpdateLayout establish layout, not completion of the native
        // renderer's hit-test scene. Like PressRowAsync in DiagnosticsScopedRetryChecks,
        // wait for actual input ownership; never bypass it or hunt for a different point.
        try
        {
            await WaitAsync(() =>
            {
                window.UpdateLayout();
                Require(control.IsEffectivelyEnabled, "Cannot click disabled control: " + control.Name);
                Point? center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
                InputElement? candidate = center is Point p ? window.InputHitTest(p) as InputElement : null;
                bool visible = VisibleRect(window, control, new Rect(control.Bounds.Size));
                samples++;
                lastSample = DescribeClick(window, control, center, candidate, visible);
                if (visible && center is Point ready && candidate is Visual visual
                    && visual.GetSelfAndVisualAncestors().Contains(control))
                {
                    point = ready;
                    return true;
                }
                if (firstMiss == null)
                {
                    firstMiss = lastSample;
                    Console.WriteLine(Marker + "CLICK_HIT_PENDING " + firstMiss);
                }
                return false;
            }, "input ownership at revealed control center: " + control.Name);
        }
        catch (TimeoutException error)
        {
            throw new TimeoutException("GP39: Click target not input-ready after reveal; samples=" + samples
                + "; first=[" + firstMiss + "]; last=[" + lastSample + "]", error);
        }
        // Recheck immediately before sending exactly one press/release. A persistent
        // layout/clip/occlusion defect still fails; no direct checkbox/handler fallback.
        InputElement hit = window.InputHitTest(point) as InputElement
            ?? throw new InvalidOperationException("No hit-test target: " + lastSample);
        bool stillVisible = VisibleRect(window, control, new Rect(control.Bounds.Size));
        Require(control.IsEffectivelyEnabled && stillVisible
            && hit is Visual finalVisual && finalVisual.GetSelfAndVisualAncestors().Contains(control),
            "Click target occluded/disabled/clipped: " + DescribeClick(window, control, point, hit, stillVisible));
        Console.WriteLine(Marker + "CLICK_HIT_READY samples=" + samples + " " + lastSample);
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        try
        {
            hit.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, point, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None));
            hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, window, point, 1,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
        }
        finally { pointer.Capture(null); }
        await SettleAsync(window);
    }

    private static string DescribeClick(Window window, Control target, Point? point, InputElement? hit, bool visible)
    {
        static string Identity(Visual node) => node.GetType().Name + "#" + ((node as Control)?.Name ?? "<unnamed>");
        string hitPath = hit is Visual visual
            ? string.Join(" > ", visual.GetSelfAndVisualAncestors().Select(Identity)) : "<none>";
        string scrolls = string.Join("; ", target.GetVisualAncestors().OfType<ScrollViewer>()
            .Select(scroll => Identity(scroll) + " offset=" + scroll.Offset + " viewport=" + scroll.Viewport + " extent=" + scroll.Extent));
        string clips = string.Join("; ", target.GetVisualAncestors().OfType<Control>()
            .Where(clip => clip.ClipToBounds || clip is ScrollContentPresenter).Append(window)
            .Select(clip => Identity(clip) + " origin=" + clip.TranslatePoint(default, window) + " bounds=" + clip.Bounds));
        return "target=" + Identity(target) + " bounds=" + target.Bounds + " origin=" + target.TranslatePoint(default, window)
            + " point=" + point + " client=" + window.ClientSize + " visible=" + visible
            + " enabled=" + target.IsEffectivelyEnabled + " hitTestVisible=" + target.IsHitTestVisible
            + " hit=[" + hitPath + "] scroll=[" + scrolls + "] clips=[" + clips + "]";
    }

    private static async Task RevealAsync(Window window, Control control)
    {
        control.BringIntoView();
        await SettleAsync(window);
        await WaitAsync(() => VisibleRect(window, control, new Rect(control.Bounds.Size)), "visible control " + control.Name);
    }

    private static async Task SettleAsync(Window window)
    {
        Dispatcher.UIThread.VerifyAccess();
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private static async Task WaitAsync(Func<bool> condition, string boundary)
    {
        try { await Program.WaitUntilAsync(condition); }
        catch (TimeoutException error) { throw new TimeoutException("GP39 native boundary: " + boundary, error); }
    }

    private static IReadOnlyDictionary<string, string> ReadCatalog(string code)
    {
        Require(Texts.Languages.Any(language => language.Code == code), "UNSUPPORTED advertised locale: " + code);
        IReadOnlyDictionary<string, string> catalog;
        if (code == "en") catalog = BuiltInCatalog.English;
        else if (code == "de") catalog = BuiltInCatalog.German;
        else
        {
            using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream("MeowChan.Desktop.Localization.Packs." + code + ".json")
                ?? throw new InvalidOperationException("UNSUPPORTED missing embedded catalog: " + code);
            using JsonDocument document = JsonDocument.Parse(stream);
            Require(document.RootElement.GetProperty("code").GetString() == code, "Embedded catalog identity mismatch: " + code);
            catalog = document.RootElement.GetProperty("strings").EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString()!, StringComparer.Ordinal);
        }
        foreach (string key in Keys)
            Require(catalog.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) && value != key
                && (code == "en" || value != BuiltInCatalog.English[key]), "UNSUPPORTED missing/untranslated GP39 key: " + code + "/" + key);
        return catalog;
    }

    private static DesktopSessionView CreateView()
    {
        DesktopThreadEntry refused = Thread(RefusedWatch, "Refused fixture");
        refused = refused with { Snapshot = refused.Snapshot with { LastCheckFailure = new(FailureKind.Http, RefusedRaw, HttpStatusCode.Forbidden) } };
        DesktopThreadEntry missing = Thread(MissingWatch, "Missing media fixture");
        missing = missing with { Snapshot = missing.Snapshot with { MediaFailures = new Dictionary<string, FailureInfo>
            { ["media-404"] = new(FailureKind.Http, MissingRaw, HttpStatusCode.NotFound) } } };
        DesktopThreadEntry request = Thread(RequestWatch, "waiting-only request");
        request = request with { Snapshot = request.Snapshot with
            { RequestProgress = new(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(30), 2) } };
        DesktopThreadEntry pending = Thread(PendingWatch, "waiting-only media");
        MediaWork work = new()
        {
            ResourceUrl = WatchUrl(PendingWatch), Generation = 1, MediaIdentifier = "pending-media",
            DownloadUrl = "https://cdn.invalid/synthetic-never-fetched.bin", DestinationPath = "/synthetic-never-opened/pending.bin",
            OriginalFileName = "pending.bin", Status = MediaWorkStatus.Waiting, DueAt = new DateTimeOffset(2031, 2, 3, 4, 5, 0, TimeSpan.Zero)
        };
        pending = pending with { Snapshot = pending.Snapshot with
            { Work = new(WatchUrl(PendingWatch), WorkIntent.Watching, ThreadSourceStateValue.Active, null, false, new[] { work }) } };
        return new(new[] { refused, missing, request, pending }, Array.Empty<DesktopBoardEntry>(), Array.Empty<string>(), DesktopSessionState.Ready, null);
    }

    private static DesktopThreadEntry Thread(long watch, string title) => new(watch,
        new ThreadSnapshot(new("fixture", "b", watch.ToString(System.Globalization.CultureInfo.InvariantCulture)), WatchUrl(watch), title,
            "/synthetic-never-opened/output", ThreadActivity.Pending, ThreadSourceState.Active, null, 1, 0,
            new Dictionary<string, FailureInfo>(), null, new DownloadCounts(0, 0)), "fixture", "b");
    private static string WatchUrl(long watch) => "https://watch.invalid/b/" + watch.ToString(System.Globalization.CultureInfo.InvariantCulture) + "?context=raw-fixture";
    private static string WithoutNewlines(string text) => text.Replace("\r", "").Replace("\n", "");
    private static string Flatten(TextLayout layout) => WithoutNewlines(string.Concat(layout.TextLines
        .SelectMany(line => line.TextRuns).Where(run => run is not TextEndOfLine).Select(run => run.Text.ToString())));
    private static T Named<T>(DiagnosticsPage page, string name) where T : Control => page.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing actual diagnostics control: " + name);
    private static DiagnosticEntry[] Entries(DataGrid grid) => grid.ItemsSource!.Cast<DiagnosticEntry>().ToArray();
    private static void RequireRows(DataGrid grid, params (long Watch, DiagnosticKind Kind)[] expected) =>
        Require(Entries(grid).Select(entry => (entry.Target!.WatchId, entry.Kind)).SequenceEqual(expected), "Unexpected rendered grid membership.");
    private static void RaiseClick(DiagnosticsPage page, string name) => Named<Button>(page, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("GP39: " + message); }
}
