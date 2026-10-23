using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Ursa.Controls;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Dedicated presentation evidence, never a substitute for native keyboard/AT or
// the full archive export/clipboard target-set regressions.
internal static class ActionToolBarPresentationChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string source = Environment.GetEnvironmentVariable("MEOWCHAN_ACTION_OVERFLOW_SOURCE_DIGEST")
            ?? throw new InvalidOperationException("Bind the immutable executed snapshot digest.");
        double expectedScale = double.Parse(Environment.GetEnvironmentVariable("MEOWCHAN_ACTION_OVERFLOW_EXPECT_SCALE")
            ?? throw new InvalidOperationException("Set actual expected process scale."), CultureInfo.InvariantCulture);
        string output = Environment.GetEnvironmentVariable("MEOWCHAN_ACTION_OVERFLOW_OUTPUT") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(output);
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "action-overflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        string language = owner.SessionHost.Config.Language, theme = owner.SessionHost.Config.Theme, mode = owner.SessionHost.Config.WorkspaceMode;
        double width = owner.Width, height = owner.Height;
        Window? host = null; ArchivePage? page = null;
        var rows = new List<object>();
        int galleryCalls = 0;
        try
        {
            // Existing E03 fixture exercises routed Enter/Up/Escape and real focus,
            // separately labelled in-process input, not claimed as OS keyboard input.
            await ActionToolBarFocusChecks.RunAsync(owner);
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            await catalog.RegisterThreadAsync(new ArchiveThreadRegistration(new ResourceId("overflow", "b", "1"),
                "https://offline.invalid/b/1", "Local overflow presentation fixture", profile, true));
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { })
            { IsAvailable = true, OpenResultsGallery = (_, _, _) => { galleryCalls++; return Task.CompletedTask; } };
            host = new Window { Title = "URSA10 actual archive actions — harness", Content = page };
            host.Show(owner);
            // Match the real host lifecycle. Merely constructing ArchivePage
            // leaves saved-search actions uninitialized XAML placeholders.
            page.AttachSavedSearches(owner.SessionHost);
            page.OpenReaderGroupsRequested = owner.ArchivePage.OpenReaderGroupsRequested;
            page.OpenPostBookmarks = owner.ArchivePage.OpenPostBookmarks;
            page.FindControl<Button>("ArchiveReaderGroups")!.IsEnabled = owner.CanOpenReaderGroups;
            await page.ActivateAsync();
            Require(page.FindControl<ListBox>("ArchiveThreads")!.ItemCount == 1, "Real local archive fixture did not load.");
            var downloads = owner.FindControl<ActionToolBar>("WorkspaceListActions")!;
            var archive = page.FindControl<ActionToolBar>("ArchiveResultActions")!;
            Require(new[] { "ArchiveSaveSearch", "ArchiveSavedSearches", "ArchiveReaderGroups" }
                .All(name => archive.Items.OfType<Button>().Any(b => b.Name == name)), "Integrated feature actions missing.");
            var identities = downloads.Items.Concat(archive.Items).OfType<Button>().ToDictionary(b => b, b => b.Command);
            var cases = from nextLanguage in new[] { "en", "de" }
                        from nextTheme in new[] { "Dark", "Light" }
                        from nextMode in new[] { "Barebones", "Simple", "Advanced" }
                        select (Language: nextLanguage, Theme: nextTheme, Mode: nextMode);
            if (Environment.GetEnvironmentVariable("MEOWCHAN_ACTION_OVERFLOW_CASE") is { Length: > 0 } selectedCase)
            {
                cases = cases.Where(value => $"{value.Language}/{value.Theme}/{value.Mode}" == selectedCase);
                Require(cases.Any(), "Unknown bounded presentation case: " + selectedCase);
            }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_ACTION_OVERFLOW_MATRIX") == "0") cases = cases.Take(1);
            foreach (var item in cases)
            {
                await PresentationChecks.SwitchModeAsync(owner, item.Mode);
                // Keep queued ApplyAppearance commits coherent with the actual request.
                await owner.SessionHost.SaveViewPreferencesAsync(config => { config.Language = item.Language; config.Theme = item.Theme; });
                Texts.SetLanguage(item.Language);
                Application.Current!.RequestedThemeVariant = item.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
                page.ArchiveColumnModePreference = item.Mode;
                foreach (bool wide in new[] { false, true })
                {
                    double requestedWidth = wide ? 1380 : item.Mode == "Barebones" ? 640 : 760;
                    double requestedHeight = wide ? 850 : item.Mode == "Barebones" ? 480 : 500;
                    owner.WindowState = host.WindowState = WindowState.Normal;
                    owner.Width = host.Width = requestedWidth; owner.Height = host.Height = requestedHeight;
                    string resizeCase = $"{item.Language}/{item.Theme}/{item.Mode}/{(wide ? "wide" : "minimum")}";
                    await WaitForRequestedSize(owner, host, requestedWidth, requestedHeight, expectedScale, resizeCase);
                    AssertPresentation(owner, host, item.Language, item.Theme, item.Mode, requestedWidth, requestedHeight, expectedScale);
                    foreach (var surface in new[] { (Name: "downloads", Window: (Window)owner, Toolbar: downloads),
                        (Name: "archive", Window: host, Toolbar: archive) })
                    {
                        surface.Window.Activate();
                        await WaitForRequestedSize(owner, host, requestedWidth, requestedHeight, expectedScale, resizeCase + "/" + surface.Name);
                        AssertPresentation(owner, host, item.Language, item.Theme, item.Mode, requestedWidth, requestedHeight, expectedScale);
                        AssertIdentity(surface.Toolbar, identities);
                        string stem = $"smoke-action-overflow-{surface.Name}-{item.Language}-{item.Theme.ToLowerInvariant()}-{item.Mode.ToLowerInvariant()}-{(wide ? "wide" : "minimum")}";
                        bool hasOverflow = surface.Toolbar.OverflowHost!.Children.Any(c => c.IsVisible);
                        Require(surface.Toolbar.More!.IsVisible == hasOverflow, "Overflow visibility differs from actual visible actions.");
                        if (hasOverflow)
                        {
                            surface.Toolbar.More.Focus(NavigationMethod.Tab);
                            surface.Toolbar.More.IsChecked = true;
                            await Program.WaitUntilAsync(() => surface.Toolbar.OverflowHost.IsEffectivelyVisible
                                && TopLevel.GetTopLevel(surface.Toolbar.OverflowHost) is { IsVisible: true });
                            await Settle(owner, host);
                            Require(AutomationProperties.GetName(surface.Toolbar.More) == Texts.T("downloads.more"), "Actual overflow toggle label is not localized.");
                        }
                        if (surface.Name == "downloads")
                            Require(AutomationProperties.GetName(owner.FindControl<Button>("CopyAllUrlsButton")!)
                                == Texts.T("downloads.copySessionUrls", owner.UrlList.Count + owner.BoardsList.Count)
                                && Label(owner.FindControl<Button>("CopyAllUrlsButton")!) == Texts.T("downloads.copySessionUrls", owner.UrlList.Count + owner.BoardsList.Count), "Actual rendered copy-session label mismatch.");
                        else Require(AutomationProperties.GetName(page.FindControl<Button>("ArchiveResultsGallery")!) == CommandCatalog.Label("gallery.open-results")
                            && Label(page.FindControl<Button>("ArchiveResultsGallery")!) == CommandCatalog.Label("gallery.open-results"), "Actual rendered archive gallery label mismatch.");
                        AssertVisibleActionsInViewport(surface.Toolbar);
                        AssertAllLabels(surface.Toolbar);
                        AssertSurfaceTheme(surface.Toolbar, surface.Window.ActualThemeVariant);
                        AssertIdentity(surface.Toolbar, identities);
                        object[] actionsAtCapture = Describe(surface.Toolbar);
                        Capture(surface.Window, stem + ".png", output);
                        string? popupFile = null;
                        if (hasOverflow)
                        {
                            Control popup = TopLevel.GetTopLevel(surface.Toolbar.OverflowHost!)!;
                            Require(Math.Abs(((TopLevel)popup).RenderScaling - expectedScale) < 0.025, "Actual popup scale mismatch.");
                            Require(popup.ActualThemeVariant == surface.Window.ActualThemeVariant, "Actual popup theme differs from action window.");
                            AssertPopupContents(surface.Toolbar, popup);
                            popupFile = stem + "-popup.png";
                            Capture(popup, popupFile, output);
                            surface.Toolbar.OverflowHost!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                            await Program.WaitUntilAsync(() => surface.Toolbar.More.IsChecked != true
                                && ReferenceEquals(surface.Window.FocusManager?.GetFocusedElement(), surface.Toolbar.More));
                        }
                        rows.Add(new { source_digest = source, scene = surface.Name, requested = new { item.Language, item.Theme, item.Mode,
                            width = requestedWidth, height = requestedHeight, scale = expectedScale }, actual_language = Texts.Language,
                            actual_theme = surface.Window.ActualThemeVariant.ToString(), actual_mode = owner.SessionHost.Config.WorkspaceMode,
                            actual_client = surface.Window.ClientSize.ToString(), actual_bounds = surface.Window.Bounds.ToString(), actual_scale = surface.Window.RenderScaling,
                            window_render = stem + ".png", popup_render = popupFile, visible_overflow = hasOverflow,
                            input = "in-process control properties and routed keys; not native keyboard", capture_kind = "actual live Window/PopupRoot RenderTargetBitmap; not OS screenshot",
                            actions = actionsAtCapture, action_snapshot_timing = "before window/popup render and before Escape closes popup",
                            native_keyboard_at_human_review = "unrun" });
                        Write();
                    }
                }
                // Measured focus transitions on the actual archive controls/commands.
                host.Width = 1380; host.Height = 850; page.ArchiveSplitPreference = 1000;
                await WaitForRequestedSize(owner, host, 1380, 850, expectedScale,
                    $"{item.Language}/{item.Theme}/{item.Mode}/focus-transition");
                Button gallery = page.FindControl<Button>("ArchiveResultsGallery")!;
                double never = archive.Items.OfType<Control>().Where(c => ToolBar.GetOverflowMode(c) == OverflowMode.Never).Sum(c => c.DesiredSize.Width);
                double small = never + archive.MoreWidth() + 1, large = small + gallery.DesiredSize.Width + 1;
                archive.Width = small; await Settle(owner, host);
                Require(archive.OverflowHost!.Children.Contains(gallery), "Controlled measured small toolbar did not overflow gallery.");
                archive.More!.IsChecked = true;
                await Program.WaitUntilAsync(() => gallery.IsEffectivelyVisible);
                Require(gallery.Focus(NavigationMethod.Tab), "Cannot focus original overflow gallery.");
                archive.Width = large; await Settle(owner, host);
                await Program.WaitUntilAsync(() => !archive.OverflowHost.Children.Contains(gallery)
                    && ReferenceEquals(host.FocusManager?.GetFocusedElement(), gallery));
                Require(archive.More.IsVisible && archive.OverflowHost.Children.Any(c => c.IsVisible), "Focus case lost remaining overflow.");
                AssertIdentity(archive, identities);
                int before = galleryCalls;
                gallery.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                gallery.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
                Require(galleryCalls == before + 1, "Real original gallery command did not invoke its host boundary exactly once.");
                string focusStem = $"smoke-action-overflow-archive-{item.Language}-{item.Theme.ToLowerInvariant()}-{item.Mode.ToLowerInvariant()}-focus-return";
                Capture(host, focusStem + ".png", output);
                archive.Width = small; await Settle(owner, host);
                await Program.WaitUntilAsync(() => archive.OverflowHost.Children.Contains(gallery)
                    && ReferenceEquals(host.FocusManager?.GetFocusedElement(), archive.More));
                rows.Add(new { source_digest = source, scene = "actual-archive-focus-transition", item.Language, item.Theme, item.Mode,
                    scale = host.RenderScaling, same_button = ReferenceEquals(archive.Items.OfType<Button>().Single(b => b.Name == gallery.Name), gallery),
                    same_command = ReferenceEquals(gallery.Command, identities[gallery]), gallery_host_boundary_calls = galleryCalls - before,
                    grow_focus = "original gallery", shrink_focus = "More", remaining_overflow = true,
                    controlled_layout = new { small, large, archiveSplit = page.ArchiveSplitPreference }, render = focusStem + ".png",
                    input = "in-process routed Enter and direct layout properties; not native keyboard; catalog/query not mutated" });
                Write();
                archive.Width = double.NaN; page.ArchiveSplitPreference = 270;
            }
            rows.Add(new { source_digest = source, result = "pass", scenes = rows.Count, scale = expectedScale }); Write();
        }
        catch (Exception error) { rows.Add(new { result = "fail", error = error.ToString() }); Write(); throw; }
        finally
        {
            page?.EndLifetime(); host?.Close();
            await PresentationChecks.SwitchModeAsync(owner, mode);
            await owner.SessionHost.SaveViewPreferencesAsync(config => { config.Language = language; config.Theme = theme; });
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            owner.Width = width; owner.Height = height;
            try { Directory.Delete(profile, true); } catch (IOException) { }
        }
        void Write() => File.WriteAllText(Path.Combine(output, "action-overflow-results.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AssertPresentation(MainWindow owner, Window host, string language, string theme, string mode,
        double width, double height, double scale)
    {
        Config saved = Config.LoadConfig();
        Require(Texts.Language == language && owner.SessionHost.Config.Language == language && saved.Language == language, "Actual language mismatch.");
        Require(owner.ActualThemeVariant.ToString() == theme && host.ActualThemeVariant.ToString() == theme
            && saved.Theme == theme && owner.SessionHost.Config.Theme == theme, "Actual rendered/config theme mismatch.");
        Require(saved.WorkspaceMode == mode && owner.SessionHost.Config.WorkspaceMode == mode
            && (owner.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectedItem as ComboBoxItem)?.Tag as string == mode, "Actual workspace mode mismatch.");
        foreach (Window window in new Window[] { owner, host })
        {
            Require(Math.Abs(window.Bounds.Width - width) <= 1 && Math.Abs(window.Bounds.Height - height) <= 1,
                $"Actual client clamped: requested {width}x{height}, got {window.Bounds.Size}.");
            Require(Math.Abs(window.RenderScaling - scale) < 0.025, $"Actual scaling {window.RenderScaling} differs from requested {scale}.");
        }
    }

    private static void AssertIdentity(ActionToolBar toolbar, Dictionary<Button, System.Windows.Input.ICommand?> identities)
    {
        Control[] overflow = toolbar.OverflowHost!.Children.ToArray();
        foreach (Button action in toolbar.Items.OfType<Button>())
        {
            Require(identities.TryGetValue(action, out var command) && ReferenceEquals(command, action.Command), "Action or real command replaced.");
            if (!action.IsVisible) continue;
            bool inOverflow = overflow.Contains(action);
            if (ToolBar.GetOverflowMode(action) == OverflowMode.Always) Require(inOverflow, "Always action is not overflowed.");
            bool direct = action.GetVisualAncestors().Contains(toolbar);
            Require((inOverflow ? 1 : 0) + (direct ? 1 : 0) == 1, "Action must occupy exactly one direct or overflow presentation.");
            Require(overflow.Count(c => ReferenceEquals(c, action)) <= 1, "Action is duplicated in overflow.");
        }
    }

    private static void AssertVisibleActionsInViewport(ActionToolBar toolbar)
    {
        foreach (Button button in toolbar.Items.OfType<Button>().Where(button => button.IsEffectivelyVisible))
        {
            Control container = toolbar.OverflowHost!.Children.Contains(button) ? toolbar.OverflowHost : toolbar;
            Require(Fits(button, container), "Action lies outside its actual toolbar/popup clip: " + button.Name);
            TopLevel root = TopLevel.GetTopLevel(button) ?? throw new InvalidOperationException("Visible action is detached.");
            Point? origin = button.TranslatePoint(default, root);
            Require(origin.HasValue && button.Bounds.Width > 0 && button.Bounds.Height > 0
                && origin.Value.X >= -1 && origin.Value.Y >= -1
                && origin.Value.X + button.Bounds.Width <= root.Bounds.Width + 1
                && origin.Value.Y + button.Bounds.Height <= root.Bounds.Height + 1,
                "Visible toolbar/popup action is clipped outside its actual viewport: " + button.Name);
        }
    }

    private static bool Fits(Control child, Control container)
    {
        Point? point = child.TranslatePoint(default, container);
        return point.HasValue && child.Bounds.Width > 0 && child.Bounds.Height > 0
            && point.Value.X >= -1 && point.Value.Y >= -1
            && point.Value.X + child.Bounds.Width <= container.Bounds.Width + 1
            && point.Value.Y + child.Bounds.Height <= container.Bounds.Height + 1;
    }

    private static void AssertAllLabels(ActionToolBar toolbar)
    {
        foreach (Button button in toolbar.Items.OfType<Button>().Where(button => button.IsVisible))
        {
            string? id = button.Name switch
            {
                "ArchiveSaveSearch" => "archive.saved-searches.save-current",
                "ArchiveSavedSearches" => "archive.saved-searches.open-manager",
                "ArchiveReaderGroups" => "reader-groups.open-manager",
                "ArchivePostBookmarks" => "post-bookmarks.open-manager",
                "ArchiveResultsGallery" => "gallery.open-results",
                "ArchiveFavoritesOverview" => "favorites.open",
                _ => null
            };
            Require(!string.IsNullOrWhiteSpace(Label(button)), "Blank rendered action label: " + button.Name);
            if (id != null)
                Require(Label(button) == CommandCatalog.Label(id) && AutomationProperties.GetName(button) == CommandCatalog.Label(id),
                    "Actual action content/automation locale mismatch: " + button.Name);
        }
    }

    private static void AssertSurfaceTheme(ActionToolBar toolbar, ThemeVariant theme)
    {
        foreach (Button button in toolbar.Items.OfType<Button>().Where(button => button.IsEffectivelyVisible))
            Require(button.ActualThemeVariant == theme, "Action's actual materialized theme mismatch: " + button.Name);
    }

    private static void AssertPopupContents(ActionToolBar toolbar, Control root)
    {
        Button[] expected = toolbar.OverflowHost!.Children.OfType<Button>().Where(button => button.IsVisible).ToArray();
        Button[] materialized = root.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible && expected.Contains(button)).ToArray();
        Require(expected.Length > 0 && materialized.Length == expected.Length
            && materialized.Distinct().Count() == expected.Length, "Captured popup does not contain exactly its expected original action controls.");
        foreach (Button action in expected)
        {
            Require(Fits(action, toolbar.OverflowHost) && Fits(action, root), "Expected action is outside captured popup: " + action.Name);
            TextBlock[] labels = action.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible
                && !string.IsNullOrWhiteSpace(text.Text)).ToArray();
            Require(labels.Length > 0 && labels.All(text => Fits(text, action)), "Expected popup label has no visible, unclipped presenter: " + action.Name);
        }
    }

    private static string Label(Button button) => button.Content is Control content
        ? string.Join("", content.GetVisualDescendants().Prepend(content).OfType<TextBlock>().Select(text => text.Text))
        : button.Content?.ToString() ?? "";

    private static object[] Describe(ActionToolBar toolbar) => toolbar.Items.OfType<Button>().Select(button => (object)new
    {
        button.Name, localized_name = AutomationProperties.GetName(button), enabled = button.IsEnabled,
        can_execute = button.Command?.CanExecute(button.CommandParameter), help = AutomationProperties.GetHelpText(button),
        visible = button.IsVisible, effectively_visible = button.IsEffectivelyVisible,
        in_overflow = toolbar.OverflowHost!.Children.Contains(button),
        actual_theme = button.ActualThemeVariant.ToString(), actual_bounds = button.Bounds.ToString(),
        toolbar_rect = button.TranslatePoint(default, toolbar)?.ToString(),
        popup_rect = button.TranslatePoint(default, toolbar.OverflowHost!)?.ToString(),
        command_type = button.Command?.GetType().Name, rendered_label = Label(button)
    }).ToArray();

    private static void Capture(Control visual, string file, string output)
    {
        double scale = TopLevel.GetTopLevel(visual)?.RenderScaling ?? 1;
        Require(visual.IsEffectivelyVisible && visual.Bounds.Width > 0 && visual.Bounds.Height > 0, "Capture target is not actually visible.");
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(visual.Bounds.Width * scale),
            (int)Math.Ceiling(visual.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(visual); string path = Path.Combine(output, file); bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine("SMOKE window render: " + path);
        Console.WriteLine("SMOKE action-overflow-render " + JsonSerializer.Serialize(new { file, actual_scale = scale,
            source_digest = Environment.GetEnvironmentVariable("MEOWCHAN_ACTION_OVERFLOW_SOURCE_DIGEST"),
            capture_type = visual.GetType().FullName, actual_theme = visual.ActualThemeVariant.ToString(),
            pixels = bitmap.PixelSize.ToString(), bounds = visual.Bounds.ToString(), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() }));
    }
    // Dispatcher layout passes do not acknowledge an asynchronous native resize.
    // Observe actual client geometry, yielding only while the exact request is pending;
    // never reissue it or substitute a smaller size when the platform clamps it.
    private static async Task WaitForRequestedSize(Window owner, Window host, double width, double height,
        double scale, string scenario)
    {
        var elapsed = Stopwatch.StartNew();
        string? lastState = null;
        int observations = 0, loggedChanges = 0;
        while (true)
        {
            await Settle(owner, host);
            observations++;
            bool ready = Matches(owner) && Matches(host);
            bool timedOut = elapsed.Elapsed >= TimeSpan.FromSeconds(5);
            string state = JsonSerializer.Serialize(new { owner = DescribeWindow(owner), host = DescribeWindow(host) });
            if (ready || timedOut || (state != lastState && loggedChanges < 12))
            {
                Console.WriteLine("SMOKE action-overflow-resize " + JsonSerializer.Serialize(new {
                    source_digest = Environment.GetEnvironmentVariable("MEOWCHAN_ACTION_OVERFLOW_SOURCE_DIGEST"),
                    scenario, requested = new { width, height, scale }, elapsed_ms = elapsed.ElapsedMilliseconds,
                    observations, status = ready ? "ready" : timedOut ? "timeout" : "pending",
                    owner = DescribeWindow(owner), host = DescribeWindow(host) }));
                loggedChanges++;
            }
            lastState = state;
            if (ready) return;
            if (timedOut) throw new TimeoutException(
                $"Actual action windows did not reach requested {width}x{height} at scale {scale} within five seconds ({scenario}). {state}");
            // Condition polling matches the smoke runner's existing readiness cadence.
            await Task.Delay(50);
        }

        bool Matches(Window window) => window.IsVisible && window.WindowState == WindowState.Normal
            && Math.Abs(window.Bounds.Width - width) <= 1 && Math.Abs(window.Bounds.Height - height) <= 1
            && Math.Abs(window.ClientSize.Width - width) <= 1 && Math.Abs(window.ClientSize.Height - height) <= 1
            && Math.Abs(window.RenderScaling - scale) < 0.025;

        static object DescribeWindow(Window window) => new {
            type = window.GetType().FullName, requested_width = window.Width, requested_height = window.Height,
            bounds = window.Bounds.ToString(), client = window.ClientSize.ToString(),
            minimum = new { width = window.MinWidth, height = window.MinHeight },
            maximum = new { width = window.MaxWidth.ToString(CultureInfo.InvariantCulture), height = window.MaxHeight.ToString(CultureInfo.InvariantCulture) },
            state = window.WindowState.ToString(), visible = window.IsVisible, active = window.IsActive,
            scale = window.RenderScaling, frame = window.FrameSize?.ToString(),
            working_area = window.Screens.ScreenFromWindow(window)?.WorkingArea.ToString() };
    }

    private static async Task Settle(Window owner, Window host)
    {
        owner.UpdateLayout(); host.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        owner.UpdateLayout(); host.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
