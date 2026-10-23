using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Rules;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Opt-in real-control presentation recipe. Routed input is explicitly not OS/IME/AT evidence.
internal static class SuggestionsPresentationChecks
{
    private static readonly ArchiveBoardIdentity[] Pairs = [new("A", "x"), new("A", "y"), new("B", "x"), new("B", "y")];
    private sealed record Cell(string Language, string Theme, string Mode)
    {
        internal int Width => Mode == "Barebones" ? 640 : 760;
        internal int Height => Mode == "Barebones" ? 480 : 500;
        public override string ToString() => Language + "-" + Theme + "-" + Mode;
    }

    internal static async Task RunAsync(MainWindow owner)
    {
        string output = Environment.GetEnvironmentVariable("MEOWCHAN_SUGGESTIONS_OUTPUT")
            ?? throw new InvalidOperationException("Set a fresh absolute MEOWCHAN_SUGGESTIONS_OUTPUT.");
        string sourceDigest = Environment.GetEnvironmentVariable("MEOWCHAN_SUGGESTIONS_SOURCE_DIGEST")
            ?? throw new InvalidOperationException("Set immutable MEOWCHAN_SUGGESTIONS_SOURCE_DIGEST.");
        Require(sourceDigest.Length == 64 && sourceDigest.All(Uri.IsHexDigit), "Expected a SHA256 source digest.");
        double expectedScale = double.Parse(Environment.GetEnvironmentVariable("MEOWCHAN_SUGGESTIONS_EXPECTED_SCALE")
            ?? throw new InvalidOperationException("Set MEOWCHAN_SUGGESTIONS_EXPECTED_SCALE to the runner scale."), CultureInfo.InvariantCulture);
        Require(Path.IsPathFullyQualified(output) && !Directory.Exists(output), "Output must be absolute and fresh.");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "fixture.json"), JsonSerializer.Serialize(new { archivePairs = Pairs, passiveRulePairs = Pairs, removedRule = "removed/old", activeDraftRaw = "png, image/jpeg" }));
        List<object> events = [];
        void Write(string kind, object data)
        {
            events.Add(new { sequence = events.Count, kind, input = "in-process routed control events, not OS input", data });
            File.WriteAllText(Path.Combine(output, "events.json"), JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
        }
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "suggestions-presentation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        Config original = Config.LoadConfig();
        Window? host = null; ArchivePage? archive = null; RulesPage? rules = null;
        int writes = 0, cellsCompleted = 0, popupsCaptured = 0;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            foreach (var pair in Pairs)
                await catalog.RegisterThreadAsync(new(new ResourceId(pair.Source, pair.Board, "pair"),
                    $"https://fixture.test/{pair.Source}/{pair.Board}", pair.Source + "/" + pair.Board, profile, true));
            archive = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            MediaRuleSet confirmed = new("suggestions-fixture", [new MediaRule(MediaRuleScope.ForBoard("removed", "old"))]);
            rules = new RulesPage(() => confirmed, (_, _) => { writes++; throw new InvalidOperationException("Suggestion input must not save rules."); })
            { IsAvailable = true, GetSuggestionContexts = () => Pairs };
            rules.EditScope(MediaRuleScope.ForBoard("removed", "old"));
            rules.TypesMode = (int)MediaTypeSelectionMode.Selected;
            rules.ExtensionsText = "png, image/jpeg";
            rules.NewScopeKind = 2;
            rules.NewThreadId = "thread raw β' untouched";
            MediaRuleScope active = rules.ActiveScope;
            string extensionRaw = rules.ExtensionsText;
            host = new Window { Title = "URSA06 real suggestions — disposable harness", Width = 640, Height = 480, Content = archive };
            host.Show(owner); await Settle();
            await archive.ActivateAsync();
            Require(archive.AppliedSearchSpec != null, "Fixture archive did not publish its initial result.");
            string applied = ArchiveSearchSpecJson.Serialize(archive.AppliedSearchSpec!);
            Cell[] cells = Environment.GetEnvironmentVariable("MEOWCHAN_SUGGESTIONS_MATRIX") == "1"
                ? (from language in new[] { "en", "de" } from theme in new[] { "Light", "Dark" }
                   from mode in new[] { "Barebones", "Simple", "Advanced" } select new Cell(language, theme, mode)).ToArray()
                : [new(Environment.GetEnvironmentVariable("MEOWCHAN_SUGGESTIONS_LANGUAGE") ?? "en",
                    Environment.GetEnvironmentVariable("MEOWCHAN_SUGGESTIONS_THEME") ?? "Dark",
                    Environment.GetEnvironmentVariable("MEOWCHAN_SUGGESTIONS_MODE") ?? "Barebones")];
            foreach (Cell cell in cells)
            {
                Require(cell.Language is "en" or "de" && cell.Theme is "Light" or "Dark"
                    && cell.Mode is "Barebones" or "Simple" or "Advanced", "Unknown presentation cell.");
                await PresentationChecks.SwitchModeAsync(owner, cell.Mode);
                // Mode persistence reapplies appearance; commit language/theme afterward via actual Settings.
                await PersistAppearance(owner, cell.Language, cell.Theme);
                host.Width = cell.Width; host.Height = cell.Height; host.Activate(); await Settle();
                await Program.WaitUntilAsync(() => Math.Abs(host.Bounds.Width - cell.Width) < 1 && Math.Abs(host.Bounds.Height - cell.Height) < 1);
                AssertPresentation(owner, host, cell, expectedScale);
                host.Content = archive; await Settle();
                C<Expander>(archive, "ArchiveFilters").IsExpanded = true;
                await Settle();
                await Field(archive, "ArchiveSourceFilter", "A", false, "archive-source", () =>
                    Require(ArchiveSearchSpecJson.Serialize(archive.AppliedSearchSpec!) == applied, "Suggestion applied an archive search."));
                // Explicit global scope exposes A/x and B/x side by side; no source is inferred on selection.
                C<ComboBox>(archive, "ArchiveBoardScope").SelectedIndex = 0;
                string? sourceDraft = archive.ReadIdentityEditor("ArchiveSourceFilter");
                await Field(archive, "ArchiveBoardFilter", "x", true, "archive-board", () =>
                {
                    Require(ArchiveSearchSpecJson.Serialize(archive.AppliedSearchSpec!) == applied, "Board suggestion changed the applied search.");
                    Require(archive.ReadIdentityEditor("ArchiveSourceFilter") == sourceDraft, "Board selection changed source raw input.");
                });
                host.Content = rules; await Settle();
                C<Expander>(rules, "NewRuleScopeExpander").IsExpanded = true;
                await Settle();
                await Field(rules, "NewRuleSourceInput", "A", false, "rules-source", PreserveRules);
                rules.NewSourceId = ""; await Settle();
                await Field(rules, "NewRuleBoardInput", "x", true, "rules-board", PreserveRules);
                cellsCompleted++;
                Write("cell-pass", new { cell.Language, cell.Theme, cell.Mode, actualWidth = host.Bounds.Width,
                    actualHeight = host.Bounds.Height, actualScale = host.RenderScaling, actualTheme = host.ActualThemeVariant.ToString(),
                    popupsCaptured, writes, activeScope = rules.ActiveScope, rawExtensions = rules.ExtensionsText });

                void PreserveRules() => Require(writes == 0 && rules.ActiveScope == active && rules.ExtensionsText == extensionRaw
                    && rules.NewThreadId == "thread raw β' untouched", "Suggestions changed an active dirty rule draft, thread raw input, scope or writes.");

                async Task Field(Control page, string name, string prefix, bool qualified, string scene, Action preserve)
                {
                    IdentitySuggestionBox field = C<IdentitySuggestionBox>(page, name);
                    string hintName = scene.StartsWith("archive", StringComparison.Ordinal) ? "ArchiveSuggestionsHint" : "RuleSuggestionsHint";
                    string hint = C<TextBlock>(page, hintName).Text ?? "";
                    Require(hint == (cell.Language == "en" ? "Up to 20 local suggestions; type any other value freely."
                        : "Bis zu 20 lokale Vorschläge; andere Werte frei eingeben."), "Actual suggestion hint is not in the requested language.");
                    field.BringIntoView(); await Settle();
                    TextBox input = field.GetVisualDescendants().OfType<TextBox>().First();
                    input.Focus(NavigationMethod.Tab); await Settle();
                    input.Text = ""; await Settle(); input.Text = prefix;
                    await Program.WaitUntilAsync(() => input.Text == prefix && field.Text == prefix && field.SearchText == prefix
                        && field.IsDropDownOpen && field.ItemsSource?.Cast<IdentitySuggestion>() is { } shown
                        && shown.Any() && shown.All(x => x.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
                    await Settle();
                    Popup popup = OpenPopup(field);
                    Control popupChild = popup.Child!;
                    await Program.WaitUntilAsync(() => popup.IsOpen && popupChild.IsEffectivelyVisible && popupChild.Bounds.Width > 0
                        && popupChild.Bounds.Height > 0 && TopLevel.GetTopLevel(popupChild)?.IsVisible == true);
                    var candidates = field.ItemsSource!.Cast<IdentitySuggestion>().ToArray();
                    Require(candidates.Length is > 0 and <= 20 && candidates.All(x => x.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)), "Unexpected visible candidates.");
                    if (qualified) Require(candidates.Any(x => x.Source == "A") && candidates.Any(x => x.Source == "B")
                        && candidates.Select(x => x.Label).Distinct(StringComparer.Ordinal).Count() == candidates.Length,
                        "Same-name boards lost their source qualification.");
                    AssertPresentation(owner, host, cell, expectedScale);
                    Require(field.ActualThemeVariant.ToString() == cell.Theme && popupChild.ActualThemeVariant.ToString() == cell.Theme,
                        "Actual field or real popup theme differs from requested theme.");
                    Require(InViewport(field, host), "Suggestion editor is clipped at the requested minimum window.");
                    string actualVisibleCaption;
                    if (scene.StartsWith("archive", StringComparison.Ordinal))
                    {
                        Button add = C<Button>(page, qualified ? "ArchiveAddBoard" : "ArchiveAddSource");
                        actualVisibleCaption = add.Content as string ?? "";
                        Require(actualVisibleCaption == (cell.Language == "en" ? "Add" : "Hinzufügen") && InViewport(add, host),
                            "Actual visible archive field action is not localized/reachable.");
                    }
                    else
                    {
                        string caption = qualified ? (cell.Language == "en" ? "Board ID" : "Board-ID")
                            : (cell.Language == "en" ? "Source ID" : "Quellen-ID");
                        TextBlock label = page.GetVisualDescendants().OfType<TextBlock>()
                            .FirstOrDefault(x => x.Text == caption && InViewport(x, host))
                            ?? throw new InvalidOperationException("Actual localized rule field label is not visible.");
                        actualVisibleCaption = label.Text!;
                    }
                    TopLevel popupTop = TopLevel.GetTopLevel(popupChild)!;
                    Require(Math.Abs(popupTop.RenderScaling - expectedScale) < .01, "Popup RenderScaling mismatch.");
                    // Capture from the native popup root so its template transforms participate.
                    // Starting at Popup.Child omits those ancestors in RenderTargetBitmap.Render.
                    Control popupImageRoot = !ReferenceEquals(popupTop, host) ? popupTop : popupChild;
                    popupImageRoot.UpdateLayout();
                    var candidateGeometry = candidates.Select(candidate =>
                    {
                        TextBlock label = popupChild.GetVisualDescendants().OfType<TextBlock>()
                            .Single(x => x.Text == candidate.Label && x.DataContext is IdentitySuggestion item && item == candidate);
                        Require(label.IsEffectivelyVisible && label.Bounds.Width > 0 && label.Bounds.Height > 0,
                            "Candidate label is not materialized and visible.");
                        Matrix transform = label.TransformToVisual(popupImageRoot)
                            ?? throw new InvalidOperationException("Candidate label has no transform to the captured popup root.");
                        Rect rendered = new Rect(label.Bounds.Size).TransformToAABB(transform);
                        bool Fits(Rect clip) => rendered.Left >= clip.Left - 1 && rendered.Top >= clip.Top - 1
                            && rendered.Right <= clip.Right + 1 && rendered.Bottom <= clip.Bottom + 1;
                        Require(Fits(new Rect(popupImageRoot.Bounds.Size)), "Candidate label extends beyond the captured popup surface.");
                        foreach (Visual ancestor in label.GetVisualAncestors().TakeWhile(x => !ReferenceEquals(x, popupImageRoot)))
                        {
                            if (!ancestor.ClipToBounds) continue;
                            Matrix clipTransform = ancestor.TransformToVisual(popupImageRoot)
                                ?? throw new InvalidOperationException("Candidate clip has no transform to the captured popup root.");
                            Require(Fits(new Rect(ancestor.Bounds.Size).TransformToAABB(clipTransform)),
                                "Candidate label is clipped by a popup ancestor.");
                        }
                        return new { candidate.Label, bounds = label.Bounds.ToString(), renderedBounds = rendered.ToString(),
                            transform = transform.ToString(), accessibleName = AutomationProperties.GetName(label) };
                    }).ToArray();
                    PixelPoint origin = popupImageRoot.PointToScreen(default);
                    PixelRect popupScreen = new(origin, PixelSize.FromSize(popupImageRoot.Bounds.Size, popupTop.RenderScaling));
                    // Avalonia constrains the child's content, then inflates the root by
                    // the child margin for out-of-bounds effects such as drop shadows.
                    PixelRect popupContentScreen = new(popupChild.PointToScreen(default),
                        PixelSize.FromSize(popupChild.Bounds.Size, popupTop.RenderScaling));
                    var screen = host.Screens.ScreenFromWindow(host) ?? throw new InvalidOperationException("No screen for popup bounds.");
                    string capture = cell + "-" + scene;
                    Write("popup-workarea-preflight", new
                    {
                        cell.Language, cell.Theme, cell.Mode, scene, expectedScale,
                        hostScale = host.RenderScaling, popupScale = popupTop.RenderScaling,
                        windowBounds = host.Bounds.ToString(), windowOrigin = host.PointToScreen(default).ToString(),
                        fieldBounds = field.Bounds.ToString(), fieldOrigin = field.PointToScreen(default).ToString(),
                        popupBounds = popupChild.Bounds.ToString(), popupChildOrigin = popupChild.PointToScreen(default).ToString(),
                        rootBounds = popupImageRoot.Bounds.ToString(), rootOrigin = origin.ToString(),
                        popupScreen = popupScreen.ToString(), popupContentScreen = popupContentScreen.ToString(), workingArea = screen.WorkingArea.ToString(), screenBounds = screen.Bounds.ToString(),
                        placement = popup.Placement.ToString(), constraintAdjustment = popup.PlacementConstraintAdjustment.ToString(),
                        placementAnchor = popup.PlacementAnchor.ToString(), placementGravity = popup.PlacementGravity.ToString(),
                        placementRect = popup.PlacementRect?.ToString(), horizontalOffset = popup.HorizontalOffset, verticalOffset = popup.VerticalOffset,
                        placementTargetType = popup.PlacementTarget?.GetType().Name, placementTargetBounds = popup.PlacementTarget?.Bounds.ToString(),
                        rootTransform = (popupTop as PopupRoot)?.Transform?.Value.ToString(), candidateGeometry,
                        ancestors = popupChild.GetVisualAncestors().Select(v => new
                        {
                            type = v.GetType().Name, name = (v as Control)?.Name, bounds = v.Bounds.ToString(),
                            renderTransform = v.RenderTransform?.Value.ToString(), v.ClipToBounds, v.IsVisible
                        }).ToArray()
                    });
                    string? x11BeforeFile = null;
                    Exception? x11BeforeError = null;
                    try { x11BeforeFile = await CaptureOwnedX11(capture + "-before-rtb"); }
                    catch (Exception error)
                    {
                        x11BeforeError = error;
                        Write("popup-workarea-native-capture-error", new { scene, error = error.ToString() });
                    }
                    Write("popup-workarea-after-native-capture", new { scene, x11BeforeFile,
                        rootBounds = popupImageRoot.Bounds.ToString(), rootOrigin = popupImageRoot.PointToScreen(default).ToString() });
                    Require(popupContentScreen.X >= screen.WorkingArea.X - 1 && popupContentScreen.Y >= screen.WorkingArea.Y - 1
                        && popupContentScreen.Right <= screen.WorkingArea.Right + 1 && popupContentScreen.Bottom <= screen.WorkingArea.Bottom + 1,
                        "Real popup content extends beyond the available work area.");
                    if (x11BeforeError != null) throw new InvalidOperationException("Owned-X11 preflight screenshot failed.", x11BeforeError);
                    string windowFile = Capture(host, capture + "-window", host.RenderScaling);
                    string popupFile = Capture(popupImageRoot, capture + "-popup", popupTop.RenderScaling);
                    await Settle();
                    string? x11AfterFile = await CaptureOwnedX11(capture + "-after-rtb");
                    popupsCaptured++;
                    Write("real-popup", new { cell.Language, cell.Theme, cell.Mode, scene, windowFile, popupFile, x11BeforeFile, x11AfterFile,
                        windowScreen = new PixelRect(host.PointToScreen(default), PixelSize.FromSize(host.Bounds.Size, host.RenderScaling)).ToString(),
                        windowBounds = host.Bounds.ToString(), actualScale = host.RenderScaling, actualTheme = host.ActualThemeVariant.ToString(),
                        captureScope = "Window and real native PopupRoot (overlay fallback: Popup.Child) are separate rendered surfaces; not OS compositing evidence",
                        popupImageRootType = popupImageRoot.GetType().Name, popupImageRootBounds = popupImageRoot.Bounds.ToString(),
                        popupRootTransform = (popupTop as PopupRoot)?.Transform?.Value.ToString(), candidateGeometry,
                        actualHint = hint, actualVisibleCaption, editorBounds = field.Bounds.ToString(), popupBounds = popupChild.Bounds.ToString(),
                        popupScreen = popupScreen.ToString(), popupContentScreen = popupContentScreen.ToString(), workingArea = screen.WorkingArea.ToString(), popupTopType = popupTop.GetType().Name,
                        popupVisible = popup.IsOpen && popupChild.IsEffectivelyVisible, popupActualTheme = popupChild.ActualThemeVariant.ToString(),
                        actualAccessibleName = AutomationProperties.GetName(field), candidates });
                    KeyEventArgs down = Press(input, Key.Down); await Settle();
                    Require(down.Handled, "Arrow Down escaped the actual suggestion adapter.");
                    KeyEventArgs enter = Press(input, Key.Enter); await Settle();
                    Require(enter.Handled && !field.IsDropDownOpen && field.AcceptedCandidate is { } accepted
                        && accepted.Value == field.Text && candidates.Contains(accepted), "Popup Enter did not accept exactly a shown canonical candidate.");
                    preserve();
                    // Reopen and navigate, then Tab must not accept the highlighted candidate.
                    void ObserveTab(string step, KeyEventArgs? key = null) => Write("tab-diagnostic", new
                    {
                        scene, step, key = key?.Key.ToString(), handled = key?.Handled,
                        rawEditorText = input.Text, canonicalText = field.Text, searchText = field.SearchText,
                        accepted = field.AcceptedCandidate, highlighted = field.SelectedItem as IdentitySuggestion,
                        popupOpen = field.IsDropDownOpen, editorFocused = input.IsFocused,
                        fieldFocusWithin = field.IsKeyboardFocusWithin,
                        focused = (host.FocusManager?.GetFocusedElement() as Control)?.GetType().Name,
                        visibleItems = field.ItemsSource?.Cast<IdentitySuggestion>().ToArray()
                    });
                    ObserveTab("after-previous-enter");
                    input.Text = ""; await Settle(); ObserveTab("after-raw-clear");
                    input.Text = prefix;
                    // The inner TextBox posts AutoComplete.TextUpdated. A still-open popup
                    // from the cleared prefix is not readiness for the newly typed prefix.
                    await Program.WaitUntilAsync(() => input.Text == prefix && field.Text == prefix && field.SearchText == prefix
                        && field.IsDropDownOpen && field.ItemsSource?.Cast<IdentitySuggestion>() is { } shown
                        && shown.Any() && shown.All(x => x.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
                    ObserveTab("after-reopen-before-down");
                    var tabDown = Press(input, Key.Down); ObserveTab("after-down", tabDown);
                    var tabKey = Press(input, Key.Tab); ObserveTab("immediately-after-tab", tabKey);
                    await Settle(); ObserveTab("settled-after-tab", tabKey);
                    Require(!field.IsDropDownOpen && field.Text == prefix && field.AcceptedCandidate == null,
                        "Tab accepted a highlighted candidate or changed the free text.");
                    string raw = "unbekannt β' / raw " + scene;
                    input.Text = raw; await Settle();
                    KeyEventArgs escape = Press(input, Key.Escape); await Settle();
                    Button blurTarget = C<Button>(page, scene.StartsWith("archive", StringComparison.Ordinal) ? "SearchArchiveButton" : "OpenNewRuleScopeButton");
                    blurTarget.BringIntoView(); blurTarget.Focus(NavigationMethod.Tab); await Settle();
                    Require(!field.IsKeyboardFocusWithin && blurTarget.IsKeyboardFocusWithin, "Raw-preservation check did not actually leave the input.");
                    Require(escape.Handled && field.Text == raw && !field.IsDropDownOpen,
                        "Escape/blur lost unknown raw text or reopened the popup.");
                    preserve();
                    Write("keyboard-and-raw-pass", new { scene, enterHandled = enter.Handled, downHandled = down.Handled,
                        escapeHandled = escape.Handled, raw = field.Text, popupClosed = !field.IsDropDownOpen,
                        tab = "routed Tab closes without candidate acceptance; OS focus traversal not established", statePreserved = true });
                }
            }
            Write("result", new { result = "pass", cellsCompleted, popupsCaptured, sourceDigest, expectedScale,
                fixtureSha256 = Sha(Path.Combine(output, "fixture.json")),
                productAssembly = Sha(typeof(ArchivePage).Assembly.Location), harnessAssembly = Sha(typeof(SuggestionsPresentationChecks).Assembly.Location),
                nativeImeAt = "not established by in-process routed events", fixture = "four local archive pairs A/x A/y B/x B/y; same passive rule contexts + removed/old rule" });
            Console.WriteLine($"SMOKE suggestions presentation PASS: {cellsCompleted} actual cells, {popupsCaptured} real popup captures; evidence={output}");
        }
        catch (Exception error)
        {
            if (host?.IsVisible == true) try { Capture(host, "failure-window", host.RenderScaling); } catch { }
            Write("result", new { result = "fail", error = error.ToString(), cellsCompleted, popupsCaptured, sourceDigest });
            throw;
        }
        finally
        {
            archive?.EndLifetime(); rules?.EndLifetime(); host?.Close();
            await PresentationChecks.SwitchModeAsync(owner, original.WorkspaceMode);
            await PersistAppearance(owner, original.Language, original.Theme);
            try { Directory.Delete(profile, true); } catch (IOException) { }
        }

        async Task<string?> CaptureOwnedX11(string name)
        {
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SUGGESTIONS_X11_CAPTURE") != "1") return null;
            Require(OperatingSystem.IsLinux()
                && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1"
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")),
                "X11 capture requires the smoke runner's owned isolated Xvfb display.");
            Require(File.Exists("/usr/bin/import"), "Owned-X11 screenshot tool /usr/bin/import is unavailable.");
            // This reads X11's real root pixels, independently of Avalonia's RenderTargetBitmap.
            // The runner supplies a disposable profile, display, WM, bus and Xauthority.
            string file = name + "-x11-root.png";
            ProcessStartInfo start = new("/usr/bin/import")
            {
                UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true
            };
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
            Write("x11-capture", new { file, sha256 = Sha(Path.Combine(output, file)),
                source = "ImageMagick import -window root; actual owned X11 display pixels; before/after Avalonia RTB",
                display = Environment.GetEnvironmentVariable("DISPLAY"), isolatedXvfb = true });
            return file;
        }

        string Capture(Control control, string name, double scale)
        {
            Require(control.Bounds.Width > 0 && control.Bounds.Height > 0, "Cannot capture an empty surface.");
            string file = name + ".png";
            using RenderTargetBitmap image = new(PixelSize.FromSize(control.Bounds.Size, scale), new Vector(96 * scale, 96 * scale));
            image.Render(control); image.Save(Path.Combine(output, file), PngBitmapEncoderOptions.Default);
            Write("capture", new { file, sha256 = Sha(Path.Combine(output, file)), bounds = control.Bounds.ToString(), actualScale = scale });
            return file;
        }
    }

    private static void AssertPresentation(MainWindow owner, Window host, Cell cell, double scale)
    {
        Config persisted = Config.LoadConfig();
        string? actualMode = (owner.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectedItem as ComboBoxItem)?.Tag as string;
        Require(Texts.Language == cell.Language && owner.SessionHost.Config.Language == cell.Language && persisted.Language == cell.Language,
            "Active/session/persisted language mismatch.");
        Require(host.ActualThemeVariant.ToString() == cell.Theme && owner.SessionHost.Config.Theme == cell.Theme && persisted.Theme == cell.Theme,
            "Actual/session/persisted theme mismatch.");
        Require(actualMode == cell.Mode && owner.SessionHost.Config.WorkspaceMode == cell.Mode && persisted.WorkspaceMode == cell.Mode, "Actual/session/persisted mode mismatch.");
        Require(Math.Abs(host.RenderScaling - scale) < .01 && Math.Abs(host.Bounds.Width - cell.Width) < 1 && Math.Abs(host.Bounds.Height - cell.Height) < 1,
            "Actual minimum window or RenderScaling differs from the requested cell.");
    }
    private static async Task PersistAppearance(MainWindow owner, string language, string theme)
    {
        owner.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => owner.SettingsPageVisible);
        SettingsEditor settings = owner.SettingsEditor;
        settings.FindControl<ComboBox>("LanguageInput")!.SelectedItem = settings.LanguageChoices.Single(x => x.Code == language);
        settings.FindControl<ComboBox>("ThemeInput")!.SelectedItem = settings.ThemeChoices.Single(x => x.Value == theme);
        settings.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !settings.IsBusy && owner.FindControl<Button>("AddUrlButton")!.IsEnabled
            && owner.SessionHost.Config.Language == language && owner.SessionHost.Config.Theme == theme
            && Config.LoadConfig().Language == language && Config.LoadConfig().Theme == theme && Texts.Language == language);
        owner.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !owner.SettingsPageVisible); await Settle();
    }
    private static Popup OpenPopup(IdentitySuggestionBox field) => field.GetVisualDescendants().OfType<Popup>()
        .Concat(field.GetLogicalDescendants().OfType<Popup>()).Distinct().Single(p => p.IsOpen && p.Child != null);
    private static bool InViewport(Control control, Window window)
    {
        Point? origin = control.TranslatePoint(default, window);
        if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0 || origin == null) return false;
        Rect target = new(origin.Value, control.Bounds.Size);
        bool Fits(Rect clip) => target.Left >= clip.Left - 1 && target.Top >= clip.Top - 1 && target.Right <= clip.Right + 1 && target.Bottom <= clip.Bottom + 1;
        return Fits(new Rect(window.Bounds.Size)) && control.GetVisualAncestors().OfType<ScrollViewer>().All(scroll =>
            scroll.TranslatePoint(default, window) is { } position && Fits(new Rect(position, scroll.Bounds.Size)));
    }
    private static T C<T>(Control page, string name) where T : Control => page.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing real control: " + name);
    private static KeyEventArgs Press(Control control, Key key)
    {
        KeyEventArgs args = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key };
        control.RaiseEvent(args); return args;
    }
    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); await Task.Delay(60); }
    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
