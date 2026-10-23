using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Microsoft.Data.Sqlite;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

// Opt-in, disposable comparison. No production switch or profile schema is introduced.
internal static class ArchiveComparisonChecks
{
    private const string Baseline = "3b69fe4e5796db28b33ee0ca668a8fdcb86a14cd8ca79e2d293186f9d7935269";
    private static readonly string[] Boards = ["x", "y", "b0", "b1", "b2", "b3", "b4", "b5", "b6", "b7"];
    private sealed record DateCase(string Id, string First, string? Capture, string? Post);
    private static readonly DateCase[] Dates = [new("early", "2026-09-20", "2026-10-10", "2026-10-11"),
        new("updated", "2026-10-05", "2026-10-22", "2026-09-29"), new("no_posts", "2026-10-18", null, null),
        new("post_old", "2026-09-10", "2026-10-05", "2026-10-18")];
    private sealed class Evidence(string output)
    {
        internal readonly List<object> Events = [];
        internal Window? Window;
        internal string Task = "setup";
        internal string Variant = "product";
        internal string Fixture = "";
        internal string Presentation = "";
        internal MainWindow? Owner;
        internal ArchivePage? Product;
        internal string? ExpectedLanguage, ExpectedTheme, ExpectedMode;
        internal double? ExpectedWidth, ExpectedHeight;
        internal readonly bool LocaleOnly = Environment.GetEnvironmentVariable("MEOWCHAN_ARCHIVE_COMPARISON_LOCALE_ONLY") == "1";
        internal readonly bool MatrixOnly = Environment.GetEnvironmentVariable("MEOWCHAN_ARCHIVE_COMPARISON_MATRIX_ONLY") == "1";
        private readonly bool _captureOwnedX11 = Environment.GetEnvironmentVariable("MEOWCHAN_ARCHIVE_COMPARISON_X11_CAPTURE") == "1";
        internal readonly List<string> CompletedPresentations = [];
        private TextBlock? _localeProbe;
        internal void Scene(Control content)
        {
            if (content.Parent is Panel previous) previous.Children.Remove(content);
            Window!.Content = null;
            _localeProbe = new TextBlock { Name = "ComparisonActualLocale", Text = Texts.T("nav.archive") + " · " + Texts.T("archive.dateBasis"), Margin = new Thickness(8, 2) };
            DockPanel.SetDock(_localeProbe, Dock.Top);
            Window!.Content = new DockPanel { Children = { _localeProbe, content } };
        }
        internal void AssertPresentation()
        {
            if (ExpectedLanguage == null || Window == null || Owner == null || Product == null) return;
            Config persisted = Config.LoadConfig();
            string actualLabel = C<TextBlock>(Product, "ArchiveDateTitle").Text ?? "";
            string actualTheme = Window.ActualThemeVariant.ToString() ?? "";
            string actualMode = (Owner.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            Presentation = $"{Texts.Language}/{actualTheme}/{actualMode}/{Window.Bounds.Width}x{Window.Bounds.Height}";
            Write("actual-presentation", new { activeLanguage = Texts.Language, actualTheme, actualMode, actualLabel,
                visibleLocaleProbe = _localeProbe?.Text, probeTheme = _localeProbe?.ActualThemeVariant.ToString(),
                sessionLanguage = Owner.SessionHost.Config.Language, persisted.Language, persisted.Theme, persisted.WorkspaceMode,
                renderScaling = Window.RenderScaling, requestedScale = Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"),
                actualWidth = Window.Bounds.Width, actualHeight = Window.Bounds.Height,
                requested = new { ExpectedLanguage, ExpectedTheme, ExpectedMode, ExpectedWidth, ExpectedHeight } });
            if (ExpectedWidth.HasValue && ExpectedHeight.HasValue)
                Require(Math.Abs(Window.Bounds.Width - ExpectedWidth.Value) <= 1 && Math.Abs(Window.Bounds.Height - ExpectedHeight.Value) <= 1,
                    "Comparison window was clamped away from the requested DIP size.");
            if (double.TryParse(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"), NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedScale))
                Require(Math.Abs(Window.RenderScaling - requestedScale) < 0.01, "Actual comparison RenderScaling differs from the runner scale.");
            Require(Texts.Language == ExpectedLanguage && Owner.SessionHost.Config.Language == ExpectedLanguage && persisted.Language == ExpectedLanguage,
                "Active/session/persisted language mismatch.");
            Require(actualTheme == ExpectedTheme && _localeProbe?.ActualThemeVariant.ToString() == ExpectedTheme && Owner.SessionHost.Config.Theme == ExpectedTheme && persisted.Theme == ExpectedTheme,
                "Rendered/session/persisted theme mismatch.");
            Require(actualMode == ExpectedMode && persisted.WorkspaceMode == ExpectedMode && Owner.SessionHost.Config.WorkspaceMode == ExpectedMode,
                "Rendered/session/persisted workspace mode mismatch.");
            Require(actualLabel == Texts.T("archive.dateBasis") && _localeProbe?.Text == Texts.T("nav.archive") + " · " + actualLabel,
                "Actual product/probe locale label mismatch.");
        }
        internal void Write(string kind, object data)
        {
            Events.Add(new { sequence = Events.Count, task = Task, variant = Variant, presentation = Presentation,
                kind, observer = "harness", input = "in-process control events/properties; not OSinput", data });
            File.WriteAllText(Path.Combine(output, "events.json"), JsonSerializer.Serialize(Events, new JsonSerializerOptions { WriteIndented = true }));
        }
        internal void Predict(string task, string variant, object expected)
        {
            Task = task; Variant = variant;
            AssertPresentation();
            Write("prediction-before-actions", expected);
        }
        internal void Action(Control control, string action, Action body)
        {
            string? before = Focus();
            control.BringIntoView(); bool focusRequested = control.Focus(); string? focusBeforeBody = Focus(); body();
            Write("actual-action", new { control = control.Name ?? control.GetType().Name, action,
                scrollPreparation = "BringIntoView programmatic setup; not a user scroll action", accessible = AutomationProperties.GetName(control), visible = control.IsEffectivelyVisible, enabled = control.IsEffectivelyEnabled, control.Focusable, control.IsTabStop, focusRequested, focusBefore = before, focusBeforeBody, focusAfter = Focus() });
        }
        private string? Focus() => Window?.FocusManager?.GetFocusedElement() is Control c ? (c.Name ?? c.GetType().Name) : null;
        internal void Capture(string suffix)
        {
            if (Window == null) return;
            Window.UpdateLayout();
            if (suffix != "failure") AssertPresentation();
            double scale = Window.RenderScaling;
            using RenderTargetBitmap bitmap = new(new PixelSize(Math.Max(1, (int)Math.Ceiling(Window.Bounds.Width * scale)),
                Math.Max(1, (int)Math.Ceiling(Window.Bounds.Height * scale))), new Vector(96 * scale, 96 * scale));
            string file = $"{Events.Count:D4}-{Task}-{suffix}.png";
            bitmap.Render(Window); bitmap.Save(Path.Combine(output, file), PngBitmapEncoderOptions.Default);
            Write("screenshot", new { file, scale, bounds = Window.Bounds.ToString(), actualLanguage = Texts.Language, actualTheme = Window.ActualThemeVariant.ToString(), focus = Focus() });
        }
        internal async Task CapturePopupAsync(Control picker, bool transientLanguage = false)
        {
            if (!transientLanguage) AssertPresentation();
            Window owner = Window ?? throw new InvalidOperationException("No comparison window for popup evidence.");
            Popup popup = CalendarPopup(picker, this);
            Control child = popup.Child!;
            TopLevel? popupRoot = TopLevel.GetTopLevel(child);
            Control imageRoot = popupRoot != null && !ReferenceEquals(popupRoot, owner) ? popupRoot : child;
            imageRoot.UpdateLayout();
            double scale = popupRoot?.RenderScaling ?? owner.RenderScaling;
            PixelSize pixels = new(Math.Max(1, (int)Math.Ceiling(imageRoot.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(imageRoot.Bounds.Height * scale)));
            Require(imageRoot.Bounds.Width > 0 && imageRoot.Bounds.Height > 0, "Calendar popup has no measured content.");
            PixelPoint origin = imageRoot.PointToScreen(default);
            PixelRect? area = owner.Screens.ScreenFromWindow(owner)?.WorkingArea;
            bool fitsScreen = area is { } screen && origin.X >= screen.X - 1 && origin.Y >= screen.Y - 1
                && origin.X + pixels.Width <= screen.Right + 1 && origin.Y + pixels.Height <= screen.Bottom + 1;
            string file = $"{Events.Count:D4}-{Task}-popup.png";
            string? x11BeforeFile = await CaptureOwnedX11Async(Path.GetFileNameWithoutExtension(file) + "-before-rtb");
            using RenderTargetBitmap bitmap = new(pixels, new Vector(96 * scale, 96 * scale));
            bitmap.Render(imageRoot); bitmap.Save(Path.Combine(output, file), PngBitmapEncoderOptions.Default);
            if (_captureOwnedX11) await Settle();
            string? x11AfterFile = await CaptureOwnedX11Async(Path.GetFileNameWithoutExtension(file) + "-after-rtb");
            Write("popup-screenshot", new { file, actualLanguage = Texts.Language, transientLanguage, persistedLanguage = Config.LoadConfig().Language, picker = picker.GetType().Name, rootType = popupRoot?.GetType().Name,
                imageRoot = imageRoot.GetType().Name, actualTheme = imageRoot.ActualThemeVariant.ToString(), scale,
                bounds = imageRoot.Bounds.ToString(), screenOrigin = origin.ToString(), pixelSize = pixels.ToString(), workingArea = area?.ToString(), fitsScreen,
                x11BeforeFile, x11AfterFile,
                popupScreen = new PixelRect(origin, pixels).ToString(),
                ownerScreen = new PixelRect(owner.PointToScreen(default), PixelSize.FromSize(owner.Bounds.Size, owner.RenderScaling)).ToString(),
                popupStillOpen = popup.IsOpen, samePopupChild = ReferenceEquals(popup.Child, child),
                displayedLabels = child.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).Where(text => !string.IsNullOrWhiteSpace(text)).Take(32).ToArray(),
                imageMethod = "actual open popup visual-tree render; separate parent-window image excludes a native PopupRoot; not OS desktop compositing" });
            Require(fitsScreen && Math.Abs(scale - owner.RenderScaling) < 0.01 && imageRoot.ActualThemeVariant.ToString() == ExpectedTheme, "Calendar popup is outside its screen or has the wrong actual theme.");
            if (_captureOwnedX11) Require(popup.IsOpen && ReferenceEquals(popup.Child, child), "Native capture must retain the same actual open calendar popup.");
        }
        private async Task<string?> CaptureOwnedX11Async(string name)
        {
            if (!_captureOwnedX11) return null;
            Require(LocaleOnly && !MatrixOnly, "Archive X11 comparison is restricted to the bounded locale-only replay.");
            Require(OperatingSystem.IsLinux()
                && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1"
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")),
                "X11 capture requires the smoke runner's owned isolated Xvfb display.");
            Require(File.Exists("/usr/bin/import"), "Owned-X11 screenshot tool /usr/bin/import is unavailable.");
            // Same capture contract as SuggestionsPresentationChecks: actual isolated X11 root pixels.
            string file = name + "-x11-root.png";
            ProcessStartInfo start = new("/usr/bin/import")
            { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-window"); start.ArgumentList.Add("root");
            start.ArgumentList.Add(Path.Combine(output, file));
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start owned-X11 screenshot.");
            Task<string> error = process.StandardError.ReadToEndAsync();
            Task<string> standard = process.StandardOutput.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            string stderr = await error; await standard;
            Require(process.ExitCode == 0 && File.Exists(Path.Combine(output, file)), "Owned-X11 screenshot failed: " + stderr);
            Write("x11-capture", new { file,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, file)))).ToLowerInvariant(),
                source = "ImageMagick import -window root; actual owned X11 display pixels; before/after Avalonia RTB",
                display = Environment.GetEnvironmentVariable("DISPLAY"), isolatedXvfb = true });
            return file;
        }
        internal void Finish(string result, Exception? error = null) => File.WriteAllText(Path.Combine(output, "result.json"),
            JsonSerializer.Serialize(new { result, error = error?.ToString(), baselineSourceDigest = Baseline,
                fixtureDigest = Fixture, executionSourceDigest = Environment.GetEnvironmentVariable("MEOWCHAN_ARCHIVE_COMPARISON_SOURCE_DIGEST") ?? "runner source manifest required alongside these artifacts", os = Environment.OSVersion.ToString(), backend = Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1" ? "X11 software smoke" : "platform-detected",
                runKind = LocaleOnly ? "targeted local calendar culture replay; no full matrix or benchmark" : MatrixOnly ? "representative presentation checks; full functional suite not repeated" : "bounded full comparison",
                completedPresentations = CompletedPresentations, matrixBaselineSourceDigest = MatrixOnly ? "386b43064a0bbacadd2bf23e17856db546476e7c6a44bcb64afa730edf7e890d" : null,
                humanJudgmentOpen = true, nativeAtImeOpen = true, interpretation = "retain-provisionally; execution evidence does not decide human preference",
                largeDatasetCost = "not-run; 24-row comparison fixture is not the 120000-row performance fixture" }, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static async Task RunAsync(MainWindow owner)
    {
        string output = Environment.GetEnvironmentVariable("MEOWCHAN_ARCHIVE_COMPARISON_OUTPUT")
            ?? throw new InvalidOperationException("Set MEOWCHAN_ARCHIVE_COMPARISON_OUTPUT to a fresh absolute evidence directory.");
        Require(Path.IsPathFullyQualified(output) && !Directory.Exists(output), "Evidence directory must be absolute and fresh.");
        Directory.CreateDirectory(output);
        Evidence evidence = new(output);
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "archive-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ArchivePage? page = null; Window? host = null;
        string language = owner.SessionHost.Config.Language;
        string mode = owner.SessionHost.Config.WorkspaceMode;
        string theme = owner.SessionHost.Config.Theme;
        try
        {
            if (!evidence.MatrixOnly && !evidence.LocaleOnly)
            {
                await ArchiveSearchStateChecks.RunAsync();
                evidence.Write("method-checks-separate-from-ui", new { suite = "ArchiveSearchStateChecks", fixture = "existing independent temporary 120-row state fixture", result = "pass" });
            }
            else evidence.Write("matrix-scope", new { scope = "representative pair/date/error/calendar presentation checks only", functionalCheckpoint = "FULL16 bounded comparison; no assertion relaxation or new semantic claim", suggestions = "separate W04 matrix" });
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            await SeedAsync(catalog, profile);
            string descriptor = JsonSerializer.Serialize(new { pairs = new[] { "A", "B" }.SelectMany(source => Boards.Select(board => $"{source}/{board}/pair")), dates = Dates,
                dateZone = "Europe/Berlin", dateTime = "12:00:00Z", rows = 24, scalarSavedView = "A/x" });
            evidence.Fixture = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(output, "fixture.json"), descriptor);
            evidence.Write("source-and-fixture", new { baseline = Baseline, fixture = evidence.Fixture, profile = "disposable", rowCount = 24 });
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            host = new Window { Title = "URSA14/15 bounded comparison — harness", Width = 1100, Height = 760, Content = page };
            evidence.Window = host; evidence.Owner = owner; evidence.Product = page; host.Show(owner);
            await Settle();
            foreach (var presentation in PresentationCases(evidence.MatrixOnly, evidence.LocaleOnly))
            {
                // Mode saves call ApplyAppearance and restore persisted language/theme.
                // Commit all appearance fields through real Settings after that save.
                await PresentationChecks.SwitchModeAsync(owner, presentation.Item3);
                await PersistPresentation(owner, presentation.Item1, presentation.Item2);
                evidence.ExpectedLanguage = presentation.Item1; evidence.ExpectedTheme = presentation.Item2; evidence.ExpectedMode = presentation.Item3;
                host.Width = presentation.Item4; host.Height = presentation.Item5;
                evidence.ExpectedWidth = presentation.Item4; evidence.ExpectedHeight = presentation.Item5;
                evidence.Scene(page); host.Activate(); await Settle();
                evidence.AssertPresentation();
                if (evidence.LocaleOnly)
                {
                    await LocaleScenes(page, evidence);
                    evidence.CompletedPresentations.Add(evidence.Presentation);
                    continue;
                }
                if (evidence.MatrixOnly)
                {
                    await MatrixScenes(page, evidence);
                    evidence.CompletedPresentations.Add(evidence.Presentation);
                    continue;
                }
                await PairTasks(page, evidence);
                // Reverse order between runs; no inferred speed or preference from action counts.
                if (presentation.Item1 == "en")
                {
                    await PairRivals(host, profile, evidence);
                    evidence.Scene(page); await Settle(); await ProductDates(page, evidence);
                    await PickerRival(host, evidence);
                }
                else
                {
                    await PickerRival(host, evidence);
                    evidence.Scene(page); await Settle(); await ProductDates(page, evidence);
                    await PairRivals(host, profile, evidence);
                }
                await SemanticRival(host, evidence);
                evidence.CompletedPresentations.Add(evidence.Presentation);
            }
            evidence.Finish("pass");
            Console.WriteLine("SMOKE bounded archive comparison passed; evidence=" + output);
        }
        catch (Exception error)
        {
            evidence.Write("failure", new { error = error.ToString() });
            try { evidence.Capture("failure"); } catch (Exception capture) { evidence.Write("capture-failure", capture.ToString()); }
            evidence.Finish("fail", error); throw;
        }
        finally
        {
            page?.EndLifetime(); host?.Close();
            await PresentationChecks.SwitchModeAsync(owner, mode);
            await PersistPresentation(owner, language, theme);
            try { Directory.Delete(profile, true); } catch (IOException) { }
        }
    }

    private static (string, string, string, int, int)[] PresentationCases(bool matrixOnly, bool localeOnly)
    {
        if (localeOnly)
        {
            string? theme = Environment.GetEnvironmentVariable("MEOWCHAN_ARCHIVE_COMPARISON_LOCALE_THEME");
            Require(theme == null || theme is "Light" or "Dark", "Locale theme filter must be Light or Dark.");
            return new[] { ("en", "Light", "Barebones", 640, 480), ("en", "Dark", "Barebones", 640, 480) }
                .Where(presentation => theme == null || presentation.Item2 == theme).ToArray();
        }
        if (!matrixOnly) return [("en", "Dark", "Advanced", 1100, 760), ("de", "Light", "Barebones", 640, 480),
            ("de", "Light", "Simple", 760, 500), ("de", "Light", "Advanced", 760, 500)];
        List<(string, string, string, int, int)> cases = [];
        foreach (string language in new[] { "en", "de" }) foreach (string theme in new[] { "Light", "Dark" })
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
                cases.Add((language, theme, mode, mode == "Barebones" ? 640 : 760, mode == "Barebones" ? 480 : 500));
        if (Environment.GetEnvironmentVariable("MEOWCHAN_ARCHIVE_COMPARISON_MATRIX_WIDE") == "1") cases.Add(("en", "Dark", "Advanced", 1100, 760));
        return cases.ToArray();
    }
    private static async Task MatrixScenes(ArchivePage page, Evidence e)
    {
        await Reset(page, e);
        e.Predict("matrix-pair", "product-pair-list", PairIds("A/x", "B/y"));
        e.Action(C<Expander>(page, "ArchiveFilters"), "expand filters", () => C<Expander>(page, "ArchiveFilters").IsExpanded = true); await Settle();
        Source(page, "A", e); Source(page, "B", e); Board(page, "A", "x", e); Board(page, "B", "y", e);
        await Search(page, e, PairIds("A/x", "B/y"));
        e.Action(C<WrapPanel>(page, "ArchiveDraftBoards"), "inspect qualified pair labels (programmatic scroll)", () => { }); await Settle();
        Require(Tags(page, "ArchiveDraftBoards").SequenceEqual(new[] { "A /x/", "B /y/" }), "Matrix qualified board labels changed."); e.Capture("qualified-pairs");

        await Reset(page, e, new(sources: ["D"], dateRange: new(new DateOnly(2026, 1, 1), null, "Europe/Berlin")));
        TextBox start = Raw(C<ArchiveDateEditor>(page, "ArchiveStartDate")), end = Raw(C<ArchiveDateEditor>(page, "ArchiveEndDate"));
        e.Predict("matrix-date-error", "product-paired-editors", new { raw = "2026-02-29", retainApplied = true, naturalReveal = true });
        object? applied = page.AppliedSearchSpec; string[] before = Rows(page);
        Edit(start, "2026-02-29", e); Edit(end, "", e);
        e.Action(C<Expander>(page, "ArchiveFilters"), "collapse filters before invalid Search", () => C<Expander>(page, "ArchiveFilters").IsExpanded = false);
        Click(C<Button>(page, "SearchArchiveButton"), e); await Settle();
        bool rawVisible = InViewport(start, e.Window!), focused = ReferenceEquals(e.Window!.FocusManager?.GetFocusedElement(), start);
        e.Write("result", new { raw = start.Text, retainedRows = Rows(page), naturalInvalidFieldInViewport = rawVisible, focused,
            fieldHasValidationError = DataValidationErrors.GetHasErrors(start), error = C<TextBlock>(page, "ArchiveSearchError").Text, noInspectionBeforeCapture = true });
        e.Capture("natural-invalid");
        Require(ReferenceEquals(applied, page.AppliedSearchSpec) && Rows(page).SequenceEqual(before) && start.Text == "2026-02-29"
            && rawVisible && focused && DataValidationErrors.GetHasErrors(start), "Matrix invalid-date retention/reveal failed.");

        e.Predict("matrix-date-corrected", "product-paired-editors", new { rows = new[] { "D/dates/no_posts", "D/dates/updated" }, zone = "Europe/Berlin" });
        Edit(start, "2026-10-01", e); Edit(end, "2026-10-31", e); await Search(page, e, ["D/dates/no_posts", "D/dates/updated"]);
        e.Action(end, "inspect corrected endpoints (programmatic scroll)", () => { }); await Settle();
        Require(!DataValidationErrors.GetHasErrors(start) && !DataValidationErrors.GetHasErrors(end), "Corrected matrix date retained a field error."); e.Capture("corrected-fields");
        e.Predict("matrix-calendar", "product-paired-editors", new { openClose = true, noExtraSearch = true });
        object? corrected = page.AppliedSearchSpec;
        DateOnlyPicker calendar = C<ArchiveDateEditor>(page, "ArchiveStartDate").GetVisualDescendants().OfType<DateOnlyPicker>().Single();
        await CalendarKeys(calendar, () => calendar.IsDropdownOpen, () => ReferenceEquals(corrected, page.AppliedSearchSpec), e, capturePopup: true);

        ArchiveRangeComparisonHost range = new(); e.Scene(range); await Settle();
        e.Predict("matrix-range-error", "Ursa-DateOnlyRangePicker-isolated", new { invalidRawRetained = true, commits = 0 });
        Edit(range.Start, "2026-02-29", e); Click(range.Apply, e); await Settle();
        Require(range.Commits == 0 && range.Start.Text == "2026-02-29" && !string.IsNullOrWhiteSpace(range.Error.Text), "Matrix range raw guard failed.");
        e.Write("matrix-check", new { raw = range.Start.Text, error = range.Error.Text, range.Commits }); e.Capture("range-error");
        e.Predict("matrix-range-corrected", "Ursa-DateOnlyRangePicker-isolated", new { start = "2026-10-01", end = "2026-10-31", zone = "Europe/Berlin" });
        Edit(range.Start, "2026-10-01", e); Edit(range.End, "2026-10-31", e); Click(range.Apply, e); await Settle();
        Require(range.Commits == 1 && range.Applied!.Spec.DateRange == new ArchiveDateRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "Europe/Berlin"), "Matrix range endpoint commit failed.");
        e.Write("matrix-check", new { rawStart = range.Start.Text, rawEnd = range.End.Text, range.Commits, definition = ArchiveSearchSpecJson.Serialize(range.Applied!.Spec) }); e.Capture("range-corrected");
        e.Predict("matrix-range-calendar", "Ursa-DateOnlyRangePicker-isolated", new { openClose = true, noExtraCommit = true });
        await CalendarKeys(range.Picker, () => range.Picker.IsDropdownOpen, () => range.Commits == 1, e, capturePopup: true);
        e.Scene(page); await Settle();
        e.Write("matrix-case-complete", new { scenes = 7, popupImages = 2, requested = new { e.ExpectedLanguage, e.ExpectedTheme, e.ExpectedMode, e.ExpectedWidth, e.ExpectedHeight }, actualScale = e.Window!.RenderScaling });
    }

    private static bool CalendarIsOpen(Control picker) => picker switch
    { DatePickerBase single => single.IsDropdownOpen, DateRangePickerBase range => range.IsDropdownOpen, _ => false };
    private static string? FocusName(IInputElement? element) => element is Control control ? control.Name ?? control.GetType().Name : element?.GetType().Name;
    private static Popup CalendarPopup(Control picker, Evidence e)
    {
        Popup[] popups = picker.GetLogicalDescendants().OfType<Control>().Concat(picker.GetVisualDescendants().OfType<Control>()).OfType<Popup>().Distinct().ToArray();
        Popup[] open = popups.Where(item => item.IsOpen && item.Child != null).ToArray();
        e.Write("calendar-popup-state", new { picker = picker.GetType().Name, attached = picker.IsAttachedToVisualTree(), picker.IsEffectivelyVisible,
            dropdownOpen = CalendarIsOpen(picker), focus = FocusName(e.Window?.FocusManager?.GetFocusedElement()), matches = open.Length,
            popups = popups.Select(item => new { item.Name, item.IsOpen, hasChild = item.Child != null,
                childAttached = item.Child?.IsAttachedToVisualTree(), childVisible = item.Child?.IsEffectivelyVisible,
                rootType = item.Child == null ? null : TopLevel.GetTopLevel(item.Child)?.GetType().Name, bounds = item.Child?.Bounds.ToString() }).ToArray() });
        Require(open.Length == 1, "Expected one actual attached open calendar popup; see preceding calendar-popup-state.");
        return open[0];
    }
    private static IEnumerable<DatePickerCalendarView> OpenCalendars(Control picker, Evidence e) =>
        CalendarPopup(picker, e).Child!.GetVisualDescendants().OfType<DatePickerCalendarView>();
    private sealed class CalendarFocusTrace : IDisposable
    {
        private readonly Control _picker; private readonly Evidence _e;
        internal CalendarFocusTrace(Control picker, Evidence e)
        { _picker = picker; _e = e; picker.AddHandler(InputElement.LosingFocusEvent, LosingFocus, RoutingStrategies.Bubble, true); picker.AddHandler(InputElement.LostFocusEvent, LostFocus, RoutingStrategies.Bubble, true); picker.PropertyChanged += Changed; }
        private void LosingFocus(object? sender, FocusChangingEventArgs args) => _e.Write("calendar-losing-focus", new
        { source = (args.Source as Control)?.Name ?? args.Source?.GetType().Name, oldFocus = FocusName(args.OldFocusedElement), oldVisible = (args.OldFocusedElement as Control)?.IsVisible,
            proposedFocusAfterPublicRedirect = FocusName(args.NewFocusedElement), args.Canceled, dropdownOpen = CalendarIsOpen(_picker) });
        private void LostFocus(object? sender, FocusChangedEventArgs args) => _e.Write("calendar-lost-focus", new
        { source = (args.Source as Control)?.Name ?? args.Source?.GetType().Name, oldFocus = FocusName(args.OldFocusedElement), newFocus = FocusName(args.NewFocusedElement), dropdownOpen = CalendarIsOpen(_picker) });
        private void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
        { if (args.Property.Name == "IsDropdownOpen") _e.Write("calendar-dropdown-change", new { dropdownOpen = CalendarIsOpen(_picker), focus = FocusName(_e.Window?.FocusManager?.GetFocusedElement()) }); }
        public void Dispose() { _picker.RemoveHandler(InputElement.LosingFocusEvent, LosingFocus); _picker.RemoveHandler(InputElement.LostFocusEvent, LostFocus); _picker.PropertyChanged -= Changed; }
    }
    private static T CalendarPart<T>(DatePickerCalendarView view, string name) where T : Control =>
        view.GetVisualDescendants().OfType<T>().Single(item => item.Name == name);
    private static void CalendarSelect(DatePickerCalendarYearButton button, Evidence e) => e.Action(button, "PointerReleased on actual month/year item (in-process)", () =>
    {
        TopLevel root = TopLevel.GetTopLevel(button) ?? throw new InvalidOperationException("Calendar item is not attached.");
        Point position = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), root)
            ?? throw new InvalidOperationException("Calendar item has no popup position.");
        using Avalonia.Input.Pointer pointer = new(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
        button.RaiseEvent(new PointerReleasedEventArgs(button, pointer, root, position, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left));
    });
    private static int CalendarMonth(DatePickerCalendarView view) =>
        ((DateOnly)CalendarPart<Grid>(view, DatePickerCalendarView.PART_MonthGrid).Children.OfType<DatePickerCalendarDayButton>().ElementAt(14).DataContext!).Month;
    private static void AssertCalendarCulture(Control picker, Evidence e, int count, bool yearView = false)
    {
        DateTimeFormatInfo info = Texts.Culture.DateTimeFormat;
        DatePickerCalendarView[] views = OpenCalendars(picker, e).ToArray();
        Require(views.Length == count, "Unexpected number of actual popup calendars.");
        foreach (DatePickerCalendarView view in views)
        {
            string[] days = CalendarPart<Grid>(view, DatePickerCalendarView.PART_MonthGrid).Children.OfType<TextBlock>()
                .Where(text => Grid.GetRow(text) == 0).OrderBy(Grid.GetColumn).Select(text => text.Text ?? "").ToArray();
            string[] expectedDays = Enumerable.Range(0, 7).Select(i => info.ShortestDayNames[((int)info.FirstDayOfWeek + i) % 7]).ToArray();
            string? month = CalendarPart<Button>(view, DatePickerCalendarView.PART_MonthButton).Content?.ToString();
            string[] months = CalendarPart<Grid>(view, DatePickerCalendarView.PART_YearGrid).Children.OfType<DatePickerCalendarYearButton>().Select(button => button.Content?.ToString() ?? "").ToArray();
            bool cellsAligned = CalendarPart<Grid>(view, DatePickerCalendarView.PART_MonthGrid).Children.OfType<DatePickerCalendarDayButton>()
                .All(button => button.DataContext is DateOnly date && (int)date.DayOfWeek == ((int)info.FirstDayOfWeek + Grid.GetColumn(button)) % 7);
            Require(cellsAligned && view.FirstDayOfWeek == info.FirstDayOfWeek && days.SequenceEqual(expectedDays), "Actual calendar weekday labels/order differ from selected UI culture.");
            Require(month == info.AbbreviatedMonthNames[CalendarMonth(view) - 1], "Actual calendar month header differs from selected UI culture.");
            if (yearView) Require(months.SequenceEqual(info.AbbreviatedMonthNames.Take(12)), "Actual calendar month grid differs from selected UI culture.");
            e.Write("calendar-culture", new { actualLanguage = Texts.Language, view.FirstDayOfWeek, cellsAligned, days, month, monthNumber = CalendarMonth(view), yearView, months = yearView ? months : null });
        }
    }
    private static void CalendarCultureBlurRegression(DateOnlyPicker picker, TextBox raw, TextBox focusTarget, Evidence e)
    {
        TextBox display = picker.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == DatePickerBase.PART_TextBox);
        CultureInfo beforeCulture = CultureInfo.CurrentCulture, beforeUiCulture = CultureInfo.CurrentUICulture;
        DateOnly? selected = picker.SelectedDate; string? rawBefore = raw.Text;
        CultureInfo thai = CultureInfo.GetCultureInfo("th-TH");
        try
        {
            // Test-only synchronous culture scope. Never flows over an await or
            // changes DefaultThread culture; restore even when the assertion fails.
            CultureInfo.CurrentCulture = thai; CultureInfo.CurrentUICulture = thai;
            Require(display.IsReadOnly && !picker.IsReadOnly && display.Text == selected?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Blur regression did not reach the actual read-only ISO picker display.");
            Require(display.Focus() && ReferenceEquals(e.Window!.FocusManager?.GetFocusedElement(), display), "Could not focus the real picker display for blur regression.");
            Require(focusTarget.Focus() && ReferenceEquals(e.Window!.FocusManager?.GetFocusedElement(), focusTarget), "Could not blur the real picker display.");
            Require(picker.SelectedDate == selected && raw.Text == rawBefore, "Thai Buddhist culture reparsed the ISO display and silently changed the Gregorian selected/raw date on blur.");
            e.Write("calendar-culture-blur", new { testCulture = thai.Name, calendar = thai.Calendar.GetType().Name, display = display.Text, selected, actualSelected = picker.SelectedDate, rawBefore, rawAfter = raw.Text, source = "actual FocusManager transition; test-only synchronous culture scope" });
        }
        finally { CultureInfo.CurrentCulture = beforeCulture; CultureInfo.CurrentUICulture = beforeUiCulture; }
        Require(ReferenceEquals(CultureInfo.CurrentCulture, beforeCulture) && ReferenceEquals(CultureInfo.CurrentUICulture, beforeUiCulture), "Blur regression leaked test culture.");
    }
    private static void CalendarGregorianSourceRegression(DateOnlyPicker picker, Evidence e)
    {
        CultureInfo beforeCulture = CultureInfo.CurrentCulture, beforeUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            // Ursa normalizes ar-SA's default Hijri calendar to Gregorian when
            // generating source month names. Exercise those actual vendor names.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
            DatePickerCalendarView view = OpenCalendars(picker, e).Single();
            Click(CalendarPart<Button>(view, DatePickerCalendarView.PART_MonthButton), e);
            AssertCalendarCulture(picker, e, 1, yearView: true);
            CalendarSelect(CalendarPart<Grid>(view, DatePickerCalendarView.PART_YearGrid).Children.OfType<DatePickerCalendarYearButton>().ElementAt(9), e);
            AssertCalendarCulture(picker, e, 1);
            e.Write("calendar-gregorian-source", new { testCulture = CultureInfo.CurrentCulture.Name, sourceCalendar = CultureInfo.CurrentCulture.Calendar.GetType().Name, actualUiLanguage = Texts.Language, source = "actual vendor year-view generation; test-only synchronous culture scope" });
        }
        finally { CultureInfo.CurrentCulture = beforeCulture; CultureInfo.CurrentUICulture = beforeUiCulture; }
    }
    private static async Task LocaleScenes(ArchivePage page, Evidence e)
    {
        e.Predict("calendar-locale", "product-paired-editors", new { languages = new[] { "en", "de", "en" }, navigation = "next/previous month, year/month selection, reopen", processCultureUnchanged = true });
        await Reset(page, e, new(sources: ["D"], dateRange: new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), "Europe/Berlin")));
        e.Action(C<Expander>(page, "ArchiveFilters"), "expand filters before locale editor inspection", () => C<Expander>(page, "ArchiveFilters").IsExpanded = true); await Settle();
        ArchiveDateEditor editor = C<ArchiveDateEditor>(page, "ArchiveStartDate");
        await Program.WaitUntilAsync(() => editor.IsEffectivelyVisible && editor.GetVisualDescendants().OfType<TextBox>().Any(box => !box.IsReadOnly)
            && C<ArchiveDateEditor>(page, "ArchiveEndDate").GetVisualDescendants().OfType<TextBox>().Any(box => !box.IsReadOnly)
            && editor.GetVisualDescendants().OfType<DateOnlyPicker>().Count() == 1);
        TextBox start = Raw(editor), end = Raw(C<ArchiveDateEditor>(page, "ArchiveEndDate"));
        DateOnlyPicker picker = editor.GetVisualDescendants().OfType<DateOnlyPicker>().Single();
        using CalendarFocusTrace productFocusTrace = new(picker, e);
        object? applied = page.AppliedSearchSpec; string definition = ArchiveSearchSpecJson.Serialize(page.AppliedSearchSpec!);
        ResolvedDateWindow? utcWindow = ArchiveSearchRequest.Create(page.AppliedSearchSpec!).DateWindow;
        string rawStart = start.Text ?? "", rawEnd = end.Text ?? "";
        DateOnly? selected = picker.SelectedDate;
        string culture = CultureInfo.CurrentCulture.Name, uiCulture = CultureInfo.CurrentUICulture.Name;
        CultureInfo? defaultCulture = CultureInfo.DefaultThreadCurrentCulture, defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;
        string decimalSeparator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        void Unchanged()
        {
            Require(ReferenceEquals(applied, page.AppliedSearchSpec) && ArchiveSearchSpecJson.Serialize(page.AppliedSearchSpec!) == definition
                && ArchiveSearchRequest.Create(page.AppliedSearchSpec!).DateWindow == utcWindow
                && start.Text == rawStart && end.Text == rawEnd && picker.SelectedDate == selected, "Calendar display refresh changed raw dates, selection, or applied UTC filter contract.");
            Require(CultureInfo.CurrentCulture.Name == culture && CultureInfo.CurrentUICulture.Name == uiCulture
                && ReferenceEquals(CultureInfo.DefaultThreadCurrentCulture, defaultCulture) && ReferenceEquals(CultureInfo.DefaultThreadCurrentUICulture, defaultUiCulture)
                && CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator == decimalSeparator, "Local calendar display changed process/engine culture.");
        }
        Key(picker, Avalonia.Input.Key.Down, e); await Settle(); Require(picker.IsDropdownOpen, "Locale calendar did not open.");
        AssertCalendarCulture(picker, e, 1); await e.CapturePopupAsync(picker);
        DatePickerCalendarView view = OpenCalendars(picker, e).Single();
        Click(CalendarPart<Button>(view, DatePickerCalendarView.PART_NextButton), e); await Settle();
        Require(CalendarMonth(view) == 11, "Next month did not navigate to November."); AssertCalendarCulture(picker, e, 1); await e.CapturePopupAsync(picker);
        Click(CalendarPart<Button>(view, DatePickerCalendarView.PART_PreviousButton), e); await Settle();
        Require(CalendarMonth(view) == 10, "Previous month did not restore October.");
        foreach (string language in new[] { "de", "en" })
        {
            // Isolate the live display contract: transient Texts change is not a
            // Settings persistence claim. Popup stays open throughout the change.
            Texts.SetLanguage(language); await Settle();
            Require(picker.IsDropdownOpen && C<TextBlock>(page, "ArchiveDateTitle").Text == Texts.T("archive.dateBasis"), "Live language refresh closed the calendar or missed product labels.");
            AssertCalendarCulture(picker, e, 1); Unchanged(); await e.CapturePopupAsync(picker, transientLanguage: true);
            if (language == "de") Click(CalendarPart<Button>(view, DatePickerCalendarView.PART_MonthButton), e);
            else Key(CalendarPart<Button>(view, DatePickerCalendarView.PART_MonthButton), Avalonia.Input.Key.Enter, e);
            await Settle();
            AssertCalendarCulture(picker, e, 1, yearView: true); await e.CapturePopupAsync(picker, transientLanguage: true);
            DatePickerCalendarYearButton october = CalendarPart<Grid>(view, DatePickerCalendarView.PART_YearGrid).Children.OfType<DatePickerCalendarYearButton>().ElementAt(9);
            CalendarSelect(october, e); await Settle(); AssertCalendarCulture(picker, e, 1); Unchanged();
            Click(CalendarPart<Button>(view, DatePickerCalendarView.PART_YearButton), e); await Settle();
            DatePickerCalendarYearButton year = CalendarPart<Grid>(view, DatePickerCalendarView.PART_YearGrid).Children.OfType<DatePickerCalendarYearButton>().Single(button => button.Content?.ToString() == "2026");
            CalendarSelect(year, e); await Settle(); AssertCalendarCulture(picker, e, 1, yearView: true);
            CalendarSelect(october, e); await Settle(); AssertCalendarCulture(picker, e, 1); Unchanged();
        }
        Key(picker, Avalonia.Input.Key.Escape, e); await Settle(); Unchanged();
        Key(picker, Avalonia.Input.Key.Down, e); await Settle(); AssertCalendarCulture(picker, e, 1); await e.CapturePopupAsync(picker);
        Key(picker, Avalonia.Input.Key.Escape, e); await Settle(); Require(!picker.IsDropdownOpen, "Calendar Escape did not close after navigation."); Unchanged();
        Key(picker, Avalonia.Input.Key.Down, e); await Settle();
        Key(picker, Avalonia.Input.Key.Tab, e); await Settle(); Require(!picker.IsDropdownOpen, "Calendar Tab did not close after navigation."); Unchanged();
        Key(picker, Avalonia.Input.Key.Down, e); await Settle();
        Require(CalendarPart<Button>(OpenCalendars(picker, e).Single(), DatePickerCalendarView.PART_PreviousButton).Focus(), "Could not focus inside the actual popup before testing outside blur.");
        Require(end.Focus(), "Could not establish real outside date-field focus."); await Settle();
        Require(!picker.IsDropdownOpen && ReferenceEquals(e.Window!.FocusManager?.GetFocusedElement(), end), "Real outside focus did not close the calendar or was stolen."); Unchanged();
        // Force an outside focus before the guarded queued header repair runs.
        // Null-to-outside focus does not route a second LostFocus via the picker.
        Key(picker, Avalonia.Input.Key.Down, e); await Settle();
        view = OpenCalendars(picker, e).Single();
        Require(CalendarPart<Button>(view, DatePickerCalendarView.PART_PreviousButton).Focus(), "Could not establish actual popup focus before the header/outside interleaving probe.");
        Click(CalendarPart<Button>(view, DatePickerCalendarView.PART_MonthButton), e);
        bool pendingNullHeaderFocus = e.Window!.FocusManager?.GetFocusedElement() is null;
        e.Write("calendar-header-outside-interleaving", new { popupOpenBeforeOutside = picker.IsDropdownOpen, pendingNullHeaderFocus, actualFocusBeforeOutside = FocusName(e.Window.FocusManager?.GetFocusedElement()) });
        Require(picker.IsDropdownOpen, "Header navigation closed before the deliberate outside-focus step.");
        Require(end.Focus(), "Could not focus outside during the pending header repair."); await Settle();
        Require(!picker.IsDropdownOpen && ReferenceEquals(e.Window!.FocusManager?.GetFocusedElement(), end), "Pending header repair stole outside focus or left its old popup open."); Unchanged();
        // Restore the same vendor calendar to month view for subsequent cases.
        Key(picker, Avalonia.Input.Key.Down, e); await Settle();
        view = OpenCalendars(picker, e).Single();
        CalendarSelect(CalendarPart<Grid>(view, DatePickerCalendarView.PART_YearGrid).Children.OfType<DatePickerCalendarYearButton>().ElementAt(9), e); await Settle();
        Key(picker, Avalonia.Input.Key.Escape, e); await Settle(); Require(!picker.IsDropdownOpen, "Calendar did not dismiss after outside-focus restoration check."); Unchanged();
        e.Write("calendar-close-guards", new { escape = true, tab = true, actualOutsideFocus = true, outsideFocusAfterHeader = true, pendingNullHeaderFocus, rawStart, rawEnd, definition });
        CalendarCultureBlurRegression(picker, start, end, e); Unchanged();
        Key(picker, Avalonia.Input.Key.Down, e); await Settle();
        CalendarGregorianSourceRegression(picker, e); Unchanged(); await e.CapturePopupAsync(picker);
        Key(picker, Avalonia.Input.Key.Escape, e); await Settle(); Unchanged();
        ArchiveRangeComparisonHost range = new(); e.Scene(range); await Settle();
        e.Predict("calendar-locale-range", "Ursa-DateOnlyRangePicker-isolated", new { languages = new[] { "en", "de", "en" }, unchangedDateContract = true });
        Edit(range.Start, "2026-10-01", e); Edit(range.End, "2026-10-31", e); Click(range.Apply, e); await Settle();
        string rangeDefinition = ArchiveSearchSpecJson.Serialize(range.Applied!.Spec);
        using CalendarFocusTrace rangeFocusTrace = new(range.Picker, e);
        Key(range.Picker, Avalonia.Input.Key.Down, e); await Settle();
        foreach (string language in new[] { "en", "de", "en" })
        {
            Texts.SetLanguage(language); await Settle(); Require(range.Picker.IsDropdownOpen, "Live range language refresh closed popup.");
            AssertCalendarCulture(range.Picker, e, 2); await e.CapturePopupAsync(range.Picker, transientLanguage: true);
            Require(range.Commits == 1 && range.Start.Text == "2026-10-01" && range.End.Text == "2026-10-31"
                && ArchiveSearchSpecJson.Serialize(range.Applied!.Spec) == rangeDefinition, "Range display refresh changed committed date contract.");
            Unchanged();
        }
        Key(range.Picker, Avalonia.Input.Key.Escape, e); await Settle(); e.Scene(page); await Settle();
        e.AssertPresentation(); e.Write("calendar-locale-complete", new { culture, uiCulture, decimalSeparator, definition, utcWindow, rawStart, rawEnd, selected, rangeDefinition });
    }

    private static async Task PersistPresentation(MainWindow owner, string language, string theme)
    {
        owner.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => owner.SettingsPageVisible);
        SettingsEditor settings = owner.SettingsEditor;
        settings.FindControl<ComboBox>("LanguageInput")!.SelectedItem = settings.LanguageChoices.Single(choice => choice.Code == language);
        settings.FindControl<ComboBox>("ThemeInput")!.SelectedItem = settings.ThemeChoices.Single(choice => choice.Value == theme);
        settings.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !settings.IsBusy && owner.FindControl<Button>("AddUrlButton")!.IsEnabled
            && owner.SessionHost.Config.Language == language && owner.SessionHost.Config.Theme == theme
            && Config.LoadConfig().Language == language && Config.LoadConfig().Theme == theme && Texts.Language == language);
        owner.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !owner.SettingsPageVisible);
        await Settle();
    }
    private static bool InViewport(Control control, Window window)
    {
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        Point? position = control.TranslatePoint(default, window);
        if (position == null) return false;
        Rect target = new(position.Value, control.Bounds.Size);
        bool Contains(Rect bounds) => target.Left >= bounds.Left && target.Top >= bounds.Top && target.Right <= bounds.Right && target.Bottom <= bounds.Bottom;
        if (!Contains(new Rect(window.Bounds.Size))) return false;
        foreach (ScrollViewer scroll in control.GetVisualAncestors().OfType<ScrollViewer>())
        {
            Point? origin = scroll.TranslatePoint(default, window);
            if (origin == null || !Contains(new Rect(origin.Value, scroll.Bounds.Size))) return false;
        }
        return true; // Geometric observation only; screenshots still require review.
    }

    private static async Task SeedAsync(ArchiveCatalog catalog, string profile)
    {
        foreach (string source in new[] { "A", "B" }) foreach (string board in Boards)
            await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(new ResourceId(source, board, "pair"), $"https://fixture.test/{source}/{board}", $"{source}/{board}", profile, true));
        foreach (DateCase item in Dates)
        {
            ResourceId id = new("D", "dates", item.Id);
            await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(id, "https://fixture.test/" + item.Id, item.Id, profile, true));
            if (item.Capture != null)
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://fixture.test/" + item.Id, item.Id, ThreadSourceState.Active,
                    Utc(item.Capture), profile, [new CapturedPost("1", 0, PostFieldPresence.All, PostedAt: Utc(item.Post!), Html: item.Id)]));
        }
        // Fixed first-registration fixture timestamps. No production clock hook or schema change.
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = catalog.DatabasePath }.ToString());
        await connection.OpenAsync();
        foreach (DateCase item in Dates)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE archive_threads SET first_seen_at=$first WHERE source='D' AND board='dates' AND thread=$id";
            command.Parameters.AddWithValue("$first", Utc(item.First).ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$id", item.Id); Require(await command.ExecuteNonQueryAsync() == 1, "Date fixture row missing.");
        }
    }
    private static DateTimeOffset Utc(string day) => DateTimeOffset.Parse(day + "T12:00:00Z", CultureInfo.InvariantCulture);
    private static T C<T>(ArchivePage page, string name) where T : Control => page.FindControl<T>(name) ?? throw new InvalidOperationException(name);
    private static string[] Rows(ArchivePage page) => (C<ListBox>(page, "ArchiveThreads").ItemsSource?.Cast<ArchiveThreadRow>() ?? []).Select(row => Id(row.Summary.Id)).Order().ToArray();
    private static string Id(ResourceId id) => $"{id.Source}/{id.Board}/{id.Thread}";
    private static string[] PairIds(params string[] pairs) => pairs.Select(pair => pair + "/pair").Order().ToArray();
    private static async Task Reset(ArchivePage page, Evidence e, ArchiveSearchSpec? spec = null)
    {
        e.Write("fixture-setup-not-action", new { spec = ArchiveSearchSpecJson.Serialize(spec ?? new()) });
        Require(await page.ApplySearchSpecAsync(spec ?? new()), "Fixture reset failed."); await Settle();
    }
    private static void Click(Button button, Evidence e) => e.Action(button, "Click routed event", () => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
    private static void Edit(TextBox field, string raw, Evidence e) => e.Action(field, "edit raw text: " + raw, () => field.Text = raw);
    private static void Key(Control control, Key key, Evidence e) => e.Action(control, "KeyDown " + key, () => control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key }));
    private static void Source(ArchivePage page, string source, Evidence e)
    {
        IdentitySuggestionBox field = C<IdentitySuggestionBox>(page, "ArchiveSourceFilter");
        e.Action(field, "edit identity: " + source, () => field.Text = source); Click(C<Button>(page, "ArchiveAddSource"), e);
    }
    private static void Board(ArchivePage page, string? source, string board, Evidence e)
    {
        ComboBox scope = C<ComboBox>(page, "ArchiveBoardScope");
        object? option = source == null ? scope.Items.Cast<object>().First() : scope.Items.Cast<object>().Single(item => item.ToString() == source);
        e.Action(scope, "select scope: " + option, () => scope.SelectedItem = option);
        IdentitySuggestionBox field = C<IdentitySuggestionBox>(page, "ArchiveBoardFilter");
        e.Action(field, "edit board: " + board, () => field.Text = board); Click(C<Button>(page, "ArchiveAddBoard"), e);
    }
    private static async Task Search(ArchivePage page, Evidence e, string[] expected)
    {
        Click(C<Button>(page, "SearchArchiveButton"), e); await Program.WaitUntilAsync(() => !page.IsBusy); await Settle();
        string[] rows = Rows(page);
        e.Write("result", new { rows, spec = page.AppliedSearchSpec == null ? null : ArchiveSearchSpecJson.Serialize(page.AppliedSearchSpec),
            rawStart = C<ArchiveDateEditor>(page, "ArchiveStartDate").RawText, rawEnd = C<ArchiveDateEditor>(page, "ArchiveEndDate").RawText,
            sourceLabels = Tags(page, "ArchiveDraftSources"), boardLabels = Tags(page, "ArchiveDraftBoards"),
            appliedLabels = Tags(page, "ArchiveAppliedFilters"), zone = C<TextBlock>(page, "ArchiveDateZone").Text });
        e.Capture("result"); Require(rows.SequenceEqual(expected.Order()), "Unexpected rows for " + e.Task + ": " + string.Join(',', rows));
    }
    private static string[] Tags(ArchivePage page, string name) => C<WrapPanel>(page, name).Children.OfType<ClosableTag>().Select(tag => (tag.Content as TextBlock)?.Text ?? "").ToArray();
    private static ClosableTag Tag(ArchivePage page, string panel, string starts) => C<WrapPanel>(page, panel).Children.OfType<ClosableTag>().Single(tag => ((tag.Content as TextBlock)?.Text ?? "").StartsWith(starts, StringComparison.Ordinal));

    private static async Task PairTasks(ArchivePage page, Evidence e)
    {
        e.Action(C<Expander>(page, "ArchiveFilters"), "expand filters (control property edit)", () => C<Expander>(page, "ArchiveFilters").IsExpanded = true); await Settle();
        await Reset(page, e);
        e.Predict("pair-exact", "product-pair-list", PairIds("A/x", "B/y"));
        Source(page, "A", e); Source(page, "B", e); Board(page, "A", "x", e); Board(page, "B", "y", e);
        await Search(page, e, PairIds("A/x", "B/y"));
        await Reset(page, e);
        e.Predict("global-x", "product-pair-list", PairIds("A/x", "B/x"));
        Board(page, null, "x", e); await Search(page, e, PairIds("A/x", "B/x"));
        await Reset(page, e);
        e.Predict("source-removal", "product-pair-list", new { rows = PairIds("B/y"), retainedOutsideDraft = "A/x" });
        Source(page, "A", e); Source(page, "B", e); Board(page, "A", "x", e); Board(page, "B", "y", e);
        Key(Tag(page, "ArchiveDraftSources", "A"), Avalonia.Input.Key.Enter, e);
        Require(Tags(page, "ArchiveDraftBoards").Any(label => label.Contains("A /x/") && label.Contains(Texts.T("archive.boardOutsideSources"))), "Outside-source board draft lost its warning.");
        await Search(page, e, PairIds("B/y"));
        ArchiveSearchSpec scalar = ArchiveSearchSpec.FromLegacy(new ArchiveThreadQuery(Source: "A", Board: "x"));
        await Reset(page, e, scalar);
        e.Predict("scalar-to-20", "product-pair-list", new { scalar = PairIds("A/x"), finalCount = 20, wrongDraftMustNotApply = true });
        Require(Rows(page).SequenceEqual(PairIds("A/x")) && page.AppliedSearchSpec!.Boards.Count == 1, "Scalar view changed definition.");
        // Reset selection draft via allowed fixture setup; scalar compatibility above is independently asserted.
        await Reset(page, e);
        string[] before = Rows(page);
        Source(page, "A", e); Source(page, "B", e);
        foreach (string source in new[] { "A", "B" }) foreach (string board in Boards) Board(page, source, board, e);
        Board(page, "A", "mistake", e);
        Require(Rows(page).SequenceEqual(before), "Adding a draft applied search.");
        e.Write("correction", new { reason = "deliberately entered wrong board", rawBefore = Tags(page, "ArchiveDraftBoards") });
        Key(Tag(page, "ArchiveDraftBoards", "A /mistake/"), Avalonia.Input.Key.Enter, e);
        await Search(page, e, new[] { "A", "B" }.SelectMany(source => Boards.Select(board => $"{source}/{board}/pair")).ToArray());
    }

    private static async Task PairRivals(Window host, string profile, Evidence e)
    {
        foreach (bool hierarchy in new[] { true, false })
        {
            StackPanel panel = new() { Spacing = 6 };
            Dictionary<string, CheckBox> choices = [];
            foreach (string source in new[] { "A", "B" })
            {
                StackPanel group = new();
                if (hierarchy)
                {
                    foreach (string board in Boards) { CheckBox box = new() { Content = source + "/" + board, Name = "Pair_" + source + "_" + board }; choices[source + "/" + board] = box; group.Children.Add(box); }
                    panel.Children.Add(new Expander { Header = source, IsExpanded = true, Content = group });
                }
                else { CheckBox box = new() { Content = source, Name = "Source_" + source }; choices[source] = box; panel.Children.Add(box); }
            }
            if (!hierarchy) foreach (string board in Boards) { CheckBox box = new() { Content = "All sources / " + board, Name = "Board_" + board }; choices["*/" + board] = box; panel.Children.Add(box); }
            Button query = new() { Content = "Query same ArchiveReader", Name = "RivalQuery" }; TextBlock output = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            panel.Children.Add(query); panel.Children.Add(output);
            e.Scene(new ScrollViewer { Content = panel }); await Settle();
            string[]? rows = null; Exception? failure = null;
            query.Click += async (_, _) =>
            {
                try
                {
                    ArchiveSearchSpec spec = hierarchy ? new(boards: choices.Where(pair => pair.Value.IsChecked == true).Select(pair => new ArchiveBoardConstraint(pair.Key.Split('/')[0], pair.Key.Split('/')[1])))
                        : new(sources: choices.Where(pair => !pair.Key.Contains('/') && pair.Value.IsChecked == true).Select(pair => pair.Key),
                            boards: choices.Where(pair => pair.Key.StartsWith("*/") && pair.Value.IsChecked == true).Select(pair => new ArchiveBoardConstraint(null, pair.Key[2..])));
                    await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile);
                    rows = (await reader.SearchThreadsBySpecAsync(ArchiveSearchRequest.Create(spec))).Items.Select(row => Id(row.Id)).Order().ToArray(); output.Text = string.Join(", ", rows);
                }
                catch (Exception error) { failure = error; }
            };
            foreach (bool global in new[] { false, true })
            {
                foreach (CheckBox box in choices.Values) box.IsChecked = false; rows = null; failure = null;
                string[] expected = hierarchy || global ? (global ? PairIds("A/x", "B/x") : PairIds("A/x", "B/y")) : PairIds("A/x", "A/y", "B/x", "B/y");
                e.Predict(global ? "global-x" : "pair-exact", hierarchy ? "hierarchy-fixture-host" : "independent-sets-fixture-host", new { rows = expected, result = !hierarchy && !global ? "unsupported-by-design for exact A/x+B/y" : "expected representable" });
                string[] select = hierarchy ? (global ? ["A/x", "B/x"] : ["A/x", "B/y"]) : (global ? ["A", "B", "*/x"] : ["A", "B", "*/x", "*/y"]);
                foreach (string key in select) { CheckBox box = choices[key]; e.Action(box, "select checkbox IsChecked=true (control property edit)", () => box.IsChecked = true); }
                Click(query, e); await Program.WaitUntilAsync(() => rows != null || failure != null);
                if (failure != null) throw failure;
                Require(rows!.SequenceEqual(expected), "Pair rival query mismatch.");
                e.Write("result", new { rows, result = !hierarchy && !global ? "unsupported-by-design" : "pass", labels = select.Select(key => choices[key].Content), persistedChanges = "none" }); e.Capture("rival");
            }
        }
    }

    private static TextBox Raw(ArchiveDateEditor editor) => editor.GetVisualDescendants().OfType<TextBox>().First(box => !box.IsReadOnly);
    private static async Task ProductDates(ArchivePage page, Evidence e)
    {
        await Reset(page, e, new(sources: ["D"], dateRange: new(new DateOnly(2026, 1, 1), null, "Europe/Berlin")));
        e.Action(C<Expander>(page, "ArchiveFilters"), "expand filters (control property edit)", () => C<Expander>(page, "ArchiveFilters").IsExpanded = true); await Settle();
        TextBox start = Raw(C<ArchiveDateEditor>(page, "ArchiveStartDate")), end = Raw(C<ArchiveDateEditor>(page, "ArchiveEndDate"));
        foreach (var task in DateTasks())
        {
            e.Predict(task.Name, "product-paired-editors", new { task.Start, task.End, task.Expected, task.Valid });
            object? applied = page.AppliedSearchSpec; string[] oldRows = Rows(page);
            Edit(start, task.Start, e); Edit(end, task.End, e);
            Key(start, Avalonia.Input.Key.Enter, e); Require(ReferenceEquals(applied, page.AppliedSearchSpec), "Raw Enter searched.");
            if (task.Valid) await Search(page, e, task.Expected.Select(id => "D/dates/" + id).ToArray());
            else
            {
                e.Action(C<Expander>(page, "ArchiveFilters"), "collapse filters before invalid Search", () => C<Expander>(page, "ArchiveFilters").IsExpanded = false);
                Click(C<Button>(page, "SearchArchiveButton"), e); await Settle();
                Require(ReferenceEquals(applied, page.AppliedSearchSpec) && Rows(page).SequenceEqual(oldRows) && start.Text == task.Start && end.Text == task.End, "Invalid dates widened search or lost raw text.");
                TextBlock error = C<TextBlock>(page, "ArchiveSearchError");
                Control invalid = task.Name is "inverted" or "invalid-end" ? end : start;
                e.Write("result", new { result = "pass-semantic-retention-only", rawStart = start.Text, rawEnd = end.Text, retainedRows = oldRows, error = error.Text,
                    invalidFieldInViewport = InViewport(invalid, e.Window!), errorInViewport = InViewport(error, e.Window!),
                    automaticReveal = "no harness scroll/focus after Search before this capture", focusedInvalidField = ReferenceEquals(e.Window!.FocusManager?.GetFocusedElement(), invalid),
                    fieldHasValidationError = DataValidationErrors.GetHasErrors(invalid), visualErrorPass = "pending independent screenshot review" });
                e.Capture("invalid-natural-post-search");
                Require(InViewport(invalid, e.Window!) && ReferenceEquals(e.Window!.FocusManager?.GetFocusedElement(), invalid)
                    && DataValidationErrors.GetHasErrors(invalid), "Invalid search did not naturally reveal/focus the bad date with its field error.");
                e.Action(invalid, "explicit inspection: reveal invalid raw field (not automatic product reveal)", () => { }); await Settle();
                e.Capture("invalid-field-inspection");
                error.BringIntoView(); await Settle();
                e.Write("inspection-setup-not-product-reveal", new { control = "ArchiveSearchError", inViewport = InViewport(error, e.Window!), error = error.Text });
                e.Capture("invalid-summary-inspection");
            }
        }
        e.Predict("validation-focus-superseded", "product-paired-editors", new { newerFocusMustWin = true, retainApplied = true });
        object? beforeReveal = page.AppliedSearchSpec;
        Edit(start, "2026-02-29", e); Click(C<Button>(page, "SearchArchiveButton"), e);
        e.Action(end, "newer explicit focus before deferred error reveal", () => { }); await Settle();
        Require(ReferenceEquals(e.Window!.FocusManager?.GetFocusedElement(), end) && ReferenceEquals(beforeReveal, page.AppliedSearchSpec),
            "Deferred validation reveal stole a newer focus or applied invalid dates.");
        e.Write("result", new { result = "pass", newerFocusRetained = true });
        e.Predict("dst-25-hours", "product-paired-editors", new { utcHours = 25, noSearchOnCalendarEnter = true });
        Edit(start, "2026-10-25", e); Edit(end, "2026-10-25", e);
        await Search(page, e, []);
        ArchiveSearchRequest frozen = (ArchiveSearchRequest)typeof(ArchivePage).GetField("_appliedSearch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        Require(frozen.DateWindow!.EndExclusive - frozen.DateWindow.StartInclusive == TimeSpan.FromHours(25), "Applied DST window is not 25 hours.");
        object? old = page.AppliedSearchSpec;
        DateOnlyPicker[] pickers = page.GetVisualDescendants().OfType<DateOnlyPicker>().ToArray();
        Require(pickers.Length == 2, "Both productive calendar controls must be materialized.");
        foreach (DateOnlyPicker picker in pickers)
            await CalendarKeys(picker, () => picker.IsDropdownOpen, () => ReferenceEquals(old, page.AppliedSearchSpec), e);
        e.Write("result", new { frozen.DateWindow, basis = page.AppliedSearchSpec!.DateRange!.Basis });
    }
    private sealed record DateTask(string Name, string Start, string End, bool Valid, string[] Expected);
    private static DateTask[] DateTasks() => [new("october-first-seen", "2026-10-01", "2026-10-31", true, ["no_posts", "updated"]),
        new("open-end", "2026-10-01", "", true, ["no_posts", "updated"]), new("open-start", "", "2026-09-30", true, ["early", "post_old"]),
        new("clear-both", "", "", true, ["early", "no_posts", "post_old", "updated"]), new("invalid-leap", "2026-02-29", "", false, []),
        new("invalid-end", "2026-10-01", "2026-02-29", false, []), new("inverted", "2026-10-31", "2026-10-01", false, []), new("corrected", "2026-10-01", "2026-10-31", true, ["no_posts", "updated"])];
    private static async Task CalendarKeys(Control picker, Func<bool> open, Func<bool> unchanged, Evidence e, bool capturePopup = false)
    {
        Key(picker, Avalonia.Input.Key.Down, e); await Settle(); Require(open(), "Calendar Down did not open " + picker.GetType().Name); e.Capture("calendar-open");
        if (capturePopup) await e.CapturePopupAsync(picker);
        Key(picker, Avalonia.Input.Key.Escape, e); await Settle(); Require(!open() && unchanged(), "Calendar Escape changed search or did not dismiss.");
        Key(picker, Avalonia.Input.Key.Down, e); await Settle(); Key(picker, Avalonia.Input.Key.Enter, e); await Settle();
        Require(!open() && unchanged(), "Calendar Enter searched or did not dismiss.");
        Key(picker, Avalonia.Input.Key.Tab, e); await Settle();
        e.Write("keyboard-observation", new { popupClosed = !open(), unchangedSearch = unchanged(), tab = "routed Tab event only; OS focus traversal/native AT not established" });
    }

    private static async Task PickerRival(Window host, Evidence e)
    {
        ArchiveRangeComparisonHost range = new(); e.Scene(range); await Settle();
        foreach (DateTask task in DateTasks())
        {
            e.Predict(task.Name, "Ursa-DateOnlyRangePicker-isolated", new { task.Start, task.End, task.Valid, task.Expected });
            ArchiveSearchRequest? before = range.Applied; int commits = range.Commits;
            Edit(range.Start, task.Start, e); Edit(range.End, task.End, e);
            Key(range.Start, Avalonia.Input.Key.Enter, e); Require(range.Commits == commits, "Range raw Enter committed.");
            Click(range.Apply, e); await Settle();
            Require(task.Valid ? range.Commits == commits + 1 : ReferenceEquals(before, range.Applied) && range.Commits == commits, "Range guard mismatch.");
            Require(range.Start.Text == task.Start && range.End.Text == task.End, "Range raw input lost.");
            if (task.Valid)
            {
                ArchiveDateRange? expected = string.IsNullOrWhiteSpace(task.Start) && string.IsNullOrWhiteSpace(task.End) ? null : new(Parse(task.Start), Parse(task.End), "Europe/Berlin");
                Require(Equals(range.Applied!.Spec.DateRange, expected), "Range endpoint mismatch.");
            }
            e.Write("result", new { range.Commits, rawStart = range.Start.Text, rawEnd = range.End.Text, error = range.Error.Text,
                definition = range.Applied == null ? null : ArchiveSearchSpecJson.Serialize(range.Applied.Spec), persistedChanges = "none; same request commit adapter only" }); e.Capture("range");
        }
        e.Predict("dst-25-hours", "Ursa-DateOnlyRangePicker-isolated", new { utcHours = 25 });
        Edit(range.Start, "2026-10-25", e); Edit(range.End, "2026-10-25", e); Click(range.Apply, e);
        Require(range.Applied!.DateWindow!.EndExclusive - range.Applied.DateWindow.StartInclusive == TimeSpan.FromHours(25), "Range DST mismatch.");
        int committed = range.Commits;
        await CalendarKeys(range.Picker, () => range.Picker.IsDropdownOpen, () => range.Commits == committed, e);
    }
    internal static DateOnly? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) || date.Year is < 2 or > 9998)
            throw new FormatException(Texts.T("archive.invalidDate", raw));
        return date;
    }
    private static async Task SemanticRival(Window host, Evidence e)
    {
        ComboBox basis = new() { ItemsSource = new[] { "First registered locally", "Last capture", "Post date" }, SelectedIndex = 0, Name = "DateBasis" };
        TextBox start = new() { Name = "SemanticStart", Text = "2026-10-01" }, end = new() { Name = "SemanticEnd", Text = "2026-10-31" };
        Button apply = new() { Content = "Query fixed dated fixture", Name = "SemanticQuery" }; TextBlock output = new(); string[] rows = [];
        apply.Click += (_, _) =>
        {
            ResolvedDateWindow window = ArchiveDateResolver.Resolve(new(Parse(start.Text), Parse(end.Text), "Europe/Berlin"))!;
            rows = Dates.Where(item =>
            {
                string? date = basis.SelectedIndex switch { 0 => item.First, 1 => item.Capture, _ => item.Post };
                return date != null && (!window.StartInclusive.HasValue || Utc(date) >= window.StartInclusive) && (!window.EndExclusive.HasValue || Utc(date) < window.EndExclusive);
            }).Select(item => item.Id).Order().ToArray(); output.Text = string.Join(", ", rows);
        };
        e.Scene(new StackPanel { Spacing = 12, Children = { new TextBlock { Text = "Semantic counterexamples — fixture host, no product feature" }, basis, start, end, apply, output } }); await Settle();
        foreach (var task in new[] { (0, "2026-10-01", "2026-10-31", new[] { "no_posts", "updated" }),
            (1, "2026-10-19", "2026-10-25", new[] { "updated" }), (2, "2026-10-18", "2026-10-18", new[] { "post_old" }) })
        {
            e.Predict("semantic-basis-" + task.Item1, "basis-fixture-host", new { rows = task.Item4, productSupport = task.Item1 == 0 ? "first-seen" : "unsupported-by-design" });
            e.Action(basis, "select basis index " + task.Item1, () => basis.SelectedIndex = task.Item1); Edit(start, task.Item2, e); Edit(end, task.Item3, e); Click(apply, e);
            Require(rows.SequenceEqual(task.Item4), "Semantic fixture mismatch."); e.Write("result", new { rows, basis = basis.SelectedItem, zone = "Europe/Berlin" }); e.Capture("semantic");
        }
    }
    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); await Task.Delay(60); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
