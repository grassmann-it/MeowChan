using System.Text.Json;
using MeowChan.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Avalonia.Interactivity;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveExportChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivedThreadView saved)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("meowchan-export-profile-");
        string? evidence = Environment.GetEnvironmentVariable("MEOWCHAN_SMOKE_EXPORT_EVIDENCE");
        string destinationRoot = evidence ?? TemporaryWorkspacePaths.CreateDirectory("meowchan-export-evidence-");
        Directory.CreateDirectory(destinationRoot);
        ResourceId readerIdentity = new(saved.Summary.Id.Source, saved.Summary.Id.Board, "900000001");
        try
        {
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                await catalog.CaptureAsync(new CapturedThreadSnapshot(readerIdentity, saved.Summary.Url, "Reader A", ThreadSourceState.Archived,
                    DateTimeOffset.UtcNow, saved.Summary.MediaDirectory, Array.Empty<CapturedPost>()));
                CapturedPost first = saved.GetPosts(0, 1).Single();
                await catalog.CaptureAsync(new CapturedThreadSnapshot(saved.Summary.Id, saved.Summary.Url, saved.Summary.Title,
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, saved.Summary.MediaDirectory,
                    new[] { first with { Html = first.Html + "<a href='#p124'>&gt;&gt;124</a><a href='#p999'>&gt;&gt;999</a><a href='/a/thread/400#p400'>&gt;&gt;400</a><a href='https://external.test/#p9'>&gt;&gt;9</a>" },
                        new CapturedPost("124", 1, PostFieldPresence.All, Html: "Retained offline reply &lt;img src=x onerror=alert(1)&gt;<script>export-script-must-not-run</script>", IsDeleted: true) }));
            }
            using ProfileLease exportLease = ProfileLease.Acquire(profile);
            ExportHistoryStore history = new(profile, exportLease);
            string? opened = null;
            ContentControl host = window.FindControl<ContentControl>("ArchivePageHost")!;
            object? retained = host.Content;
            bool holdReader = false;
            TaskCompletionSource? exportGate = null;
            TaskCompletionSource readerGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ArchivePage fixture = new(async token =>
            {
                if (exportGate != null) await exportGate.Task.WaitAsync(token);
                if (holdReader) { holdReader = false; await readerGate.Task.WaitAsync(token); }
                return await ArchiveReader.OpenAsync(profile, token);
            }, (_, _) => Task.CompletedTask, path => opened = path) { ExportHistory = history, CollectionExportHistory = new CollectionExportHistoryStore(profile, exportLease) };
            host.Content = fixture;
            try
            {
                fixture.IsAvailable = true;
                await fixture.LoadThreadAsync(readerIdentity);
                await fixture.OpenExportForThreadAsync(saved.Summary.Id, fixture.FindControl<Button>("ExportArchiveButton"));
                Require(fixture.FindControl<Button>("ArchiveExportOpen")!.IsVisible && !fixture.FindControl<Button>("ArchiveExportOpen")!.IsEnabled, "Current result folder action must be visible and disabled before success.");
                Require(fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible, "Export action did not open its workflow.");
                Require(!fixture.FindControl<Button>("ArchiveExportPrevious")!.IsVisible && !fixture.FindControl<Button>("ArchiveExportAssociate")!.IsVisible
                    && fixture.FindControl<CheckBox>("ArchiveExportMedia")!.IsVisible && fixture.FindControl<Control>("ArchiveExportOptionsLabel") == null,
                    "Without export history the history actions must be hidden together and no Options heading may remain above the media checkbox.");
                // A busy page refuses to open an export (readiness gate), so the independent
                // reader load starts only once the context export dialog is open.
                holdReader = true;
                Task delayedReader = fixture.LoadThreadAsync(readerIdentity);
                string output = Path.Combine(destinationRoot, "thread-export");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = output;
                await fixture.OpenExportForThreadAsync(readerIdentity, fixture.FindControl<Button>("ExportArchiveButton"));
                Require(fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text == output &&
                    fixture.FindControl<TextBlock>("ArchiveExportSummary")!.Text!.Contains(saved.Summary.Id.Thread!),
                    "Repeated dialog opening replaced the bound identity or draft.");
                exportGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = Path.Combine(destinationRoot, "changed-during-export");
                fixture.FindControl<CheckBox>("ArchiveExportMedia")!.IsChecked = false;
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                exportGate.TrySetResult(); exportGate = null;
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                ArchiveHtmlExportResult result = fixture.LastExport ?? throw new InvalidOperationException(fixture.FindControl<TextBlock>("ArchiveExportStatus")!.Text);
                Require(result.Directory == output && !Directory.Exists(Path.Combine(destinationRoot, "changed-during-export")),
                    "Running export reread the mutable draft or started twice.");
                fixture.FindControl<CheckBox>("ArchiveExportMedia")!.IsChecked = true;
                readerGate.TrySetResult();
                await delayedReader;
                Require(result.Metadata?.Thread == saved.Summary.Id && fixture.CurrentThread?.Summary.Id == readerIdentity,
                    "Context export B was redirected to independently loading reader A.");
                Require(result.PostCount == 2 && result.FileCount == 1 && result.Omissions.Count == 0, "UI export counts differ from the real local content.");
                Require(File.Exists(result.IndexPath) && File.Exists(Path.Combine(result.Directory, "style.css")), "UI reported success before publishing its actual output.");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = Path.Combine(destinationRoot, "edited-after-export");
                fixture.FindControl<Button>("ArchiveExportOpen")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(opened == result.Directory, "Result folder action used edited draft instead of the published result.");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = output;
                Program.CaptureWindow(window, "smoke-archive-export-result.png");
                await CheckCompactLayoutsAsync(window, fixture);
                byte[] publishedIndex = await File.ReadAllBytesAsync(result.IndexPath);
                string publishedStylePath = Path.Combine(result.Directory, "style.css");
                byte[] publishedStyle = await File.ReadAllBytesAsync(publishedStylePath);
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = Path.Combine(destinationRoot, "edited-before-failed-launch");
                string? failedLaunchTarget = null;
                int failedLaunchCalls = 0;
                Button openResult = fixture.FindControl<Button>("ArchiveExportOpen")!;
                TextBlock resultStatus = fixture.FindControl<TextBlock>("ArchiveExportStatus")!;
                TextBlock resultError = fixture.FindControl<TextBlock>("ArchiveExportError")!;
                MeowChan.Desktop.Controls.WorkspaceBanner errorBanner = fixture.FindControl<MeowChan.Desktop.Controls.WorkspaceBanner>("ArchiveExportErrorBanner")!;
                string? publishedStatus = resultStatus.Text;
                string expectedPublishedStatus = Texts.T("archive.exportDone", result.PostCount, result.FileCount,
                    result.Omissions.Count, result.Warnings.Count) + "\n" + result.Directory;
                Require(publishedStatus == expectedPublishedStatus
                    && ReferenceEquals(host.Content, fixture) && ReferenceEquals(fixture.LastExport, result)
                    && fixture.CurrentThread?.Summary.Id == readerIdentity && openResult.IsEnabled
                    && fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible,
                    "The layout matrix replaced the bound export fixture, result or available folder action.");
                fixture.OpenExportFolder = path =>
                {
                    failedLaunchTarget = path; failedLaunchCalls++;
                    return Task.FromException(new IOException("fixture launch failure"));
                };
                openResult.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => failedLaunchCalls == 1 && errorBanner.IsVisible
                    && resultError.Text == Texts.T("export.openFailed", "fixture launch failure") && openResult.IsEnabled);
                Console.WriteLine("SMOKE archive export folder failure: " + JsonSerializer.Serialize(new
                {
                    failedLaunchCalls, failedLaunchTarget, expectedTarget = result.Directory,
                    sameHost = ReferenceEquals(host.Content, fixture), sameResult = ReferenceEquals(fixture.LastExport, result),
                    reader = fixture.CurrentThread?.Summary.Id, resultStatus = resultStatus.Text, resultError = resultError.Text,
                    errorBanner.IsVisible, errorBanner.IsEffectivelyVisible, openResult.IsEnabled
                }));
                Require(ReferenceEquals(host.Content, fixture) && ReferenceEquals(fixture.LastExport, result)
                    && failedLaunchCalls == 1 && failedLaunchTarget == result.Directory && resultStatus.Text == publishedStatus
                    && errorBanner.IsVisible && errorBanner.IsEffectivelyVisible
                    && resultError.Text == Texts.T("export.openFailed", "fixture launch failure"),
                    "Folder handoff error erased the successful export or lacked dialog feedback.");
                byte[] indexAfterFolderFailure = await File.ReadAllBytesAsync(result.IndexPath);
                byte[] styleAfterFolderFailure = await File.ReadAllBytesAsync(publishedStylePath);
                Require(publishedIndex.SequenceEqual(indexAfterFolderFailure)
                    && publishedStyle.SequenceEqual(styleAfterFolderFailure),
                    "Folder handoff failure changed the successfully published index or stylesheet bytes.");
                fixture.OpenExportFolder = null;
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = output;
                // A second click at the same target must fail without damaging the first output.
                byte[] firstOutput = publishedIndex;
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                byte[] afterConflict = await File.ReadAllBytesAsync(result.IndexPath);
                Require(fixture.LastExport == null && firstOutput.SequenceEqual(afterConflict), "A failed repeated export changed an existing output.");
                Require(errorBanner.IsVisible && errorBanner.IsEffectivelyVisible
                    && resultError.Text?.StartsWith(Texts.T("export.failed"), StringComparison.Ordinal) == true,
                    "Repeated output conflict falsely reported success or lacked visible error feedback.");
                Require(!fixture.FindControl<Button>("ArchiveExportOpen")!.IsEnabled, "Failed new attempt retained an old result folder action.");
                fixture.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                fixture.ExportHistory = new ExportHistoryStore(profile, exportLease);
                await fixture.OpenExportForThreadAsync(saved.Summary.Id, fixture.FindControl<Button>("ExportArchiveButton"));
                Require(fixture.FindControl<ComboBox>("ArchiveExportHistory")!.SelectedItem != null, "Persisted successful export was not recognized after reopening.");
                Require(fixture.FindControl<RadioButton>("ArchiveExportNew")!.IsChecked == true, "Recognizing history silently selected overwrite.");
                Require(!fixture.FindControl<Button>("ArchiveExportOpen")!.IsEnabled && fixture.FindControl<Button>("ArchiveExportPrevious")!.IsEnabled, "Previous export and current result were conflated.");
                fixture.FindControl<RadioButton>("ArchiveExportUpdate")!.IsChecked = true;
                fixture.FindControl<CheckBox>("ArchiveExportBackup")!.IsChecked = true;
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                Require(fixture.LastExport?.Directory == output && fixture.LastExport.Metadata!.Generation == result.Metadata!.Generation + 1, "Explicit update did not publish a new generation at the selected path.");
                fixture.FindControl<RadioButton>("ArchiveExportNew")!.IsChecked = true;
                string extra = Path.Combine(destinationRoot, "independent-second-export");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = extra;
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                Require(fixture.LastExport?.Directory == extra && File.Exists(Path.Combine(output, "index.html")), "New folder selection did not retain the previous export.");
                fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text = Path.Combine(destinationRoot, "cancelled-export");
                exportGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                fixture.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                Require(fixture.LastExport == null && fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible,
                    "Escape while exporting closed early or retained an old result.");
                Require(!Directory.Exists(Path.Combine(destinationRoot, "cancelled-export")), "Cancelled export published a destination.");
                exportGate.TrySetResult(); exportGate = null;
                string moved = Path.Combine(destinationRoot, "relocated thread");
                ComboBox known = fixture.FindControl<ComboBox>("ArchiveExportHistory")!;
                known.SelectedIndex = known.Items.Cast<object>().Select((item, index) => (item, index))
                    .Single(item => item.item.ToString()!.Contains(output, StringComparison.Ordinal)).index;
                fixture.FindControl<RadioButton>("ArchiveExportUpdate")!.IsChecked = true;
                fixture.FindControl<CheckBox>("ArchiveExportBackup")!.IsChecked = true;
                Directory.Move(output, moved);
                fixture.FindControl<Button>("ArchiveExportStart")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !fixture.IsExportRunning);
                Require(fixture.LastExport == null && !fixture.FindControl<Button>("ArchiveExportPrevious")!.IsEnabled &&
                    !fixture.FindControl<RadioButton>("ArchiveExportUpdate")!.IsEnabled &&
                    fixture.FindControl<TextBox>("ArchiveExportDestination")!.Text == output,
                    "Failed update retained a stale available export or silently selected another target.");
                fixture.FindControl<RadioButton>("ArchiveExportNew")!.IsChecked = true;
                fixture.FindControl<Button>("ArchiveExportLegacyFind")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                fixture.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => fixture.FindControl<Button>("ArchiveExportLegacyFind")!.IsEnabled);
                Require(fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible, "Cancelling lookup closed the dialog before its work ended.");
                fixture.FindControl<Button>("ArchiveExportLegacyFind")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => fixture.FindControl<Button>("ArchiveExportLegacyFind")!.IsEnabled);
                // The fixture folder holds no legacy candidate, so a live retry ends with the explained
                // empty result. A reused cancelled token would instead end with the cancelled status.
                string lookupStatus = fixture.FindControl<TextBlock>("ArchiveExportStatus")!.Text ?? string.Empty;
                Require(lookupStatus == Texts.T("export.legacyNone"),
                    "Retrying a cancelled legacy lookup reused its cancelled token. status=" + lookupStatus);
                Console.WriteLine("SMOKE portable export index: " + Path.Combine(moved, "index.html"));
                fixture.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Require(!fixture.FindControl<Border>("ArchiveExportDialog")!.IsVisible, "Export workflow did not close with Escape.");
                RequireExportOriginFocus(fixture, "Closing export did not restore its real origin focus.");
                await CheckExportOriginFocusAsync(fixture, saved.Summary.Id);
                await ArchiveCollectionExportChecks.RunAsync(fixture, saved.Summary.Id, readerIdentity, destinationRoot, value => exportGate = value, () => opened);
                await ArchiveResultsExportChecks.RunAsync(window, fixture, profile, exportLease, destinationRoot, value => exportGate = value);
            }
            finally { readerGate.TrySetResult(); exportGate?.TrySetResult(); fixture.EndLifetime(); host.Content = retained; }
        }
        finally { Directory.Delete(profile, true); if (evidence == null) Directory.Delete(destinationRoot, true); }
        Console.WriteLine("SMOKE HTML export: real shared library writer, verified PNG, retained reply, internal quote, relocation and no-overwrite conflict passed.");
    }
    // The origin is the toolbar action itself while it is a visible direct item. In a closed
    // More popup it has no window presentation and cannot hold focus, so the visible More
    // toggle that carries it is the real origin; focus must never be left on nothing.
    private static void RequireExportOriginFocus(ArchivePage page, string message)
    {
        Button origin = page.FindControl<Button>("ExportArchiveButton")!;
        ActionToolBar toolbar = page.FindControl<ActionToolBar>("ReaderToolbar")!;
        bool direct = origin.IsEffectivelyVisible && origin.Bounds.Width > 0 && origin.Bounds.Height > 0
            && origin.GetVisualAncestors().Contains(toolbar);
        bool overflowed = toolbar.OverflowHost?.Children.Contains(origin) == true;
        Control? expected = direct ? origin : overflowed ? toolbar.More : null;
        object? focused = TopLevel.GetTopLevel(page)?.FocusManager?.GetFocusedElement();
        string focusedName = (focused as Control)?.Name ?? focused?.GetType().Name ?? "none";
        Require(expected != null && ReferenceEquals(focused, expected), message
            + $" direct={direct}, overflowed={overflowed}, originBounds={origin.Bounds}, "
            + $"moreVisible={toolbar.More?.IsEffectivelyVisible}, focused={focusedName}, window={TopLevel.GetTopLevel(page)?.Bounds}");
    }
    // Both presentations of the same action must return focus: wide enough to stay a direct
    // item, and narrow enough to sit in the closed More popup.
    private static async Task CheckExportOriginFocusAsync(ArchivePage page, ResourceId id)
    {
        Button origin = page.FindControl<Button>("ExportArchiveButton")!;
        ActionToolBar toolbar = page.FindControl<ActionToolBar>("ReaderToolbar")!;
        double width = toolbar.Width, maxWidth = toolbar.MaxWidth;
        try
        {
            foreach (bool overflow in new[] { false, true })
            {
                toolbar.Width = overflow ? double.NaN : 3000;
                toolbar.MaxWidth = overflow ? 200 : double.PositiveInfinity;
                string state = overflow ? "overflowed" : "direct";
                await Program.WaitUntilAsync(() =>
                {
                    page.UpdateLayout();
                    return overflow
                        ? toolbar.OverflowHost?.Children.Contains(origin) == true && toolbar.More?.IsEffectivelyVisible == true
                        : toolbar.OverflowHost?.Children.Contains(origin) != true && origin.IsEffectivelyVisible
                            && origin.Bounds.Width > 0 && origin.GetVisualAncestors().Contains(toolbar);
                });
                await page.OpenExportForThreadAsync(id, origin);
                Border dialog = page.FindControl<Border>("ArchiveExportDialog")!;
                Require(dialog.IsVisible, $"Export from the {state} origin did not open.");
                page.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                Require(!dialog.IsVisible, $"Export from the {state} origin did not close with Escape.");
                RequireExportOriginFocus(page, $"Closing export from the {state} origin did not restore its focus.");
                // A queued toolbar repair must not move the restored focus either.
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                RequireExportOriginFocus(page, $"A deferred toolbar repair moved the focus restored for the {state} origin.");
            }
        }
        finally { toolbar.Width = width; toolbar.MaxWidth = maxWidth; page.UpdateLayout(); }
    }
    private static async Task CheckCompactLayoutsAsync(MainWindow window, ArchivePage page)
    {
        string modeBefore = window.SessionHost.Config.WorkspaceMode;
        string languageBefore = Texts.Language;
        ThemeVariant? themeBefore = Application.Current?.RequestedThemeVariant;
        double widthBefore = window.Width, heightBefore = window.Height;
        try
        {
            foreach (string mode in new[] { "Simple", "Barebones" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                double width = mode == "Barebones" ? 640 : 760;
                double height = mode == "Barebones" ? 480 : 500;
                window.WindowState = WindowState.Normal;
                window.Width = width; window.Height = height;
                foreach (string language in new[] { "en", "de" })
                foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Texts.SetLanguage(language);
                    Application.Current!.RequestedThemeVariant = theme;
                    await Program.WaitUntilAsync(
                        () => CompactExportLayoutReady(window, page, width, height),
                        () => DescribeCompactExportLayout(window, page, width, height));
                    Program.CaptureWindow(window, $"smoke-export-{mode.ToLowerInvariant()}-{language}-{theme.ToString().ToLowerInvariant()}.png");
                }
            }
        }
        finally
        {
            Texts.SetLanguage(languageBefore);
            Application.Current!.RequestedThemeVariant = themeBefore;
            await PresentationChecks.SwitchModeAsync(window, modeBefore);
            window.Width = widthBefore; window.Height = heightBefore; window.UpdateLayout();
        }
    }
    private static bool CompactExportLayoutReady(MainWindow expectedOwner, ArchivePage page, double width, double height)
    {
        expectedOwner.UpdateLayout();
        page.UpdateLayout();
        Window? owner = TopLevel.GetTopLevel(page) as Window;
        if (!ReferenceEquals(owner, expectedOwner)) return false;

        Border dialog = page.FindControl<Border>("ArchiveExportDialog")!;
        if (dialog.Child is not Border card) return false;
        // Resize the actual overlay/card after the owner has acknowledged the native resize. The
        // dialog owns the scroll body; updating only the page can leave the card on its old measure
        // during the first pass after a mode or window-size change.
        dialog.UpdateLayout();
        card.UpdateLayout();
        ScrollViewer? body = card.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(viewer => viewer.Name == DialogFrame.BodyName);
        if (body == null) return false;
        Control dialogHost = dialog.Parent as Control ?? page;
        return PresentationChecks.MatchesRequestedSize(owner, width, height)
            && page.Bounds.Width > 0 && page.Bounds.Height > 0
            && dialog.IsEffectivelyVisible && dialogHost.Bounds.Width > 0 && dialogHost.Bounds.Height > 0
            && dialog.Bounds.Width <= dialogHost.Bounds.Width + 1
            && Fits(owner, page.FindControl<Button>("ArchiveExportClose")!)
            && Fits(owner, page.FindControl<Button>("ArchiveExportOpen")!)
            && Fits(owner, page.FindControl<Button>("ArchiveExportStart")!)
            && card.Bounds.Width <= 680 + 1 && body.Viewport.Height > 30
            && body.Extent.Width <= body.Viewport.Width + 1;
    }

    private static string DescribeCompactExportLayout(MainWindow expectedOwner, ArchivePage page, double width, double height)
    {
        expectedOwner.UpdateLayout();
        Window? owner = TopLevel.GetTopLevel(page) as Window;
        Border? dialog = page.FindControl<Border>("ArchiveExportDialog");
        Border? card = dialog?.Child as Border;
        ScrollViewer? body = card?.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(viewer => viewer.Name == DialogFrame.BodyName);
        Control? dialogHost = dialog?.Parent as Control;
        Button? close = page.FindControl<Button>("ArchiveExportClose");
        Button? open = page.FindControl<Button>("ArchiveExportOpen");
        Button? start = page.FindControl<Button>("ArchiveExportStart");
        string FitState(Control? control) => owner == null || control == null
            ? "missing"
            : Fits(owner, control).ToString();
        string Geometry(Control? control) => control == null ? "missing" : control.Bounds.ToString();
        return $"requested={width}x{height}; actual={owner?.Bounds.Size.ToString() ?? "none"}; "
            + $"ownerMatch={ReferenceEquals(owner, expectedOwner)}; page={Geometry(page)}; "
            + $"dialog={Geometry(dialog)}; host={Geometry(dialogHost)}; card={Geometry(card)}; "
            + $"body={Geometry(body)}; viewport={body?.Viewport.ToString() ?? "none"}; extent={body?.Extent.ToString() ?? "none"}; "
            + $"visible={dialog?.IsEffectivelyVisible.ToString() ?? "missing"}; "
            + $"fits=close:{FitState(close)},open:{FitState(open)},start:{FitState(start)}; "
            + $"cardWidth={card?.Bounds.Width.ToString() ?? "missing"}; "
            + $"bodyHeight={body?.Viewport.Height.ToString() ?? "missing"}; "
            + $"bodyHorizontalOverflow={(body == null ? "missing" : (body.Extent.Width - body.Viewport.Width).ToString())}";
    }

    private static bool Fits(Window window, Control control)
    {
        Point? point = control.TranslatePoint(default, window);
        return control.IsEffectivelyVisible && point.HasValue && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && point.Value.X >= -1 && point.Value.Y >= -1
            && point.Value.X + control.Bounds.Width <= window.Bounds.Width + 1
            && point.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
