using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Interfaces;
using MeowChan.Testing;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Real production controls/templates in native windows; no OS-input or pixel-recognition claim.
// Root hooks this after Ready/emptyGate. All fixture data is local, one thread/one text post.
internal static class HtmlExportButtonLabelChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        string mode = owner.SessionHost.Config.WorkspaceMode;
        double width = owner.Width, height = owner.Height;
        WindowState state = owner.WindowState;
        string profile = TemporaryWorkspacePaths.CreateDirectory("html-export-labels-");
        ArchivePage? page = null;
        Window? host = null;
        Control? detached = null;
        bool inspectorWasVisible = owner.FindControl<Border>("WorkspaceInspector")!.IsVisible;
        try
        {
            Require(Texts.Warnings.Count == 0, "Bundled catalog loader reported warnings before HTML label checks.");
            await PresentationChecks.SwitchModeAsync(owner, "Simple");
            SmokeActionClick.Execute(owner.FindControl<Button>("DownloadsNavigationButton")!);
            await using (SyntheticShellRows synthetic = await SyntheticShellRows.CreateAsync(owner,
                [SnapshotFixtures.Thread(9907001, "9907001", "HTML export label fixture")]))
            {
                owner.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 0;
                owner.FindControl<DataGrid>("ThreadsDataGrid")!.SelectedItem = synthetic.Threads.Single();
                owner.WindowState = WindowState.Normal;
                owner.Width = 1440; owner.Height = 920;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(owner, 1440, 920));
                if (!owner.FindControl<Border>("WorkspaceInspector")!.IsVisible)
                    SmokeActionClick.Execute(owner.FindControl<Button>("DetailsToggleButton")!);
                Button inspector = owner.FindControl<Button>("InspectorHtmlExport")!;
                foreach (string code in new[] { "en", "de", "es", "fr", "ru" })
                {
                    Texts.SetLanguage(code);
                    Require(Texts.Language == code, code + ": bundled language did not become effective.");
                    if (code is "es" or "fr" or "ru")
                        Require(CommandCatalog.Label("export.open-html-dialog") != "Export",
                            code + ": HTML export command silently fell back to English; bundled pack coverage is not exercised.");
                    await SettleAsync(owner);
                    inspector.BringIntoView();
                    await SettleAsync(owner);
                    CheckButton(inspector, CommandCatalog.Label("export.open-html-dialog"), code + "/inspector");
                    Require(Equals(ToolTip.GetTip(inspector), CommandBindings.Tooltip("export.open-html-dialog")), "Inspector tooltip changed.");
                    Program.CaptureWindow(owner, "smoke-html-label-inspector-" + code + ".png");
                }
            }

            using ProfileLease lease = ProfileLease.Acquire(profile);
            ResourceId id = new("html-label-fixture", "local", "1");
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/local/1", "HTML label fixture",
                    ThreadSourceState.Archived, DateTimeOffset.UtcNow, profile,
                    [new CapturedPost("1", 0, PostFieldPresence.All, Html: "<p>Local text-only layout fixture.</p>")]));
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { })
                { IsAvailable = true, ArchiveColumnModePreference = "Simple" };
            host = new Window { Title = "HTML export labels — production layout", Width = 1440, Height = 920, Content = page };
            host.Show(owner);
            await page.ActivateAsync();
            await page.LoadThreadAsync(id);
            Button results = page.FindControl<Button>("ArchiveResultsExport")!;
            Button readerExport = page.FindControl<Button>("ExportArchiveButton")!;
            var resultCommand = results.Command;
            var readerCommand = readerExport.Command;
            ActionToolBar toolbar = page.FindControl<ActionToolBar>("ArchiveResultActions")!;
            foreach (string code in new[] { "en", "de", "es", "fr", "ru" })
            {
                Texts.SetLanguage(code);
                Require(Texts.Language == code, code + ": bundled language did not become effective for archive export.");
                if (code is "es" or "fr" or "ru")
                    Require(CommandCatalog.Label("export.open-html-dialog") != "Export",
                        code + ": archive export silently fell back to English.");
                await page.ReaderLocalizationTask;
                await SettleAsync(host);
                await Program.WaitUntilAsync(() => !page.IsBusy && results.IsEnabled);
                Require(toolbar.OverflowHost?.Children.Contains(results) == true && toolbar.More?.IsEffectivelyVisible == true,
                    "The complete results title was not reachable through the real narrow-pane URSA overflow.");
                toolbar.More!.IsChecked = true;
                await Program.WaitUntilAsync(() => results.IsEffectivelyVisible && TopLevel.GetTopLevel(results) is { IsVisible: true });
                await SettleAsync(host);
                string expected = Texts.T("export.resultsAction", 1);
                CheckButton(results, expected, code + "/results-overflow");
                // AP7: the button opens the one Export dialog with "all matches" preselected (no one-item menu).
                SmokeActionClick.Execute(results);
                await Program.WaitUntilAsync(() => page.FindControl<Border>("ArchiveExportDialog")!.IsVisible
                    && page.CurrentExportScope == ArchivePage.ExportScope.AllResults);
                Require(ReferenceEquals(results.Command, resultCommand), "Results label refresh replaced its command.");
                SmokeActionClick.Execute(page.FindControl<Button>("ArchiveExportClose")!);
                await Program.WaitUntilAsync(() => !page.FindControl<Border>("ArchiveExportDialog")!.IsVisible);
                toolbar.More.IsChecked = true;
                await SettleAsync(host);
                CapturePopup(results, "smoke-html-label-results-" + code + ".png");
                toolbar.More.IsChecked = false;
                ActionToolBar? readerOverflow = await OpenOverflowIfNeededAsync(host, readerExport);
                CheckButton(readerExport, CommandCatalog.Label("export.open-html-dialog"), code + "/split-reader" + (readerOverflow != null ? "-overflow" : ""));
                if (readerOverflow != null) { readerOverflow.More!.IsChecked = false; await SettleAsync(host); }
                Program.CaptureWindow(host, "smoke-html-label-split-reader-" + code + ".png");
            }
            detached = page.DetachReader(); // Exact production large-reader pane, not a reconstructed toolbar.
            host.Content = detached;
            foreach (var size in new[] { (Width: 1440d, Height: 920d), (Width: 760d, Height: 500d), (Width: 640d, Height: 480d) })
            foreach (string code in new[] { "en", "de", "es", "fr", "ru" })
            {
                host.Width = size.Width; host.Height = size.Height;
                Texts.SetLanguage(code);
                Require(Texts.Language == code, code + ": bundled language did not become effective for large reader.");
                if (code is "es" or "fr" or "ru")
                    Require(CommandCatalog.Label("export.open-html-dialog") != "Export",
                        code + ": large-reader export silently fell back to English.");
                await page.ReaderLocalizationTask;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(host, size.Width, size.Height));
                await SettleAsync(host);
                ActionToolBar? readerOverflow = await OpenOverflowIfNeededAsync(host, readerExport);
                CheckButton(readerExport, CommandCatalog.Label("export.open-html-dialog"), $"{code}/reader/{size.Width}" + (readerOverflow != null ? "-overflow" : ""));
                Require(ReferenceEquals(readerExport.Command, readerCommand), "Reader label refresh replaced its command.");
                if (readerOverflow != null) { readerOverflow.More!.IsChecked = false; await SettleAsync(host); }
                ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
                Require(posts.IsEffectivelyVisible && posts.Bounds.Height >= 100, "Full labels left no readable post viewport.");
                Program.CaptureWindow(host, $"smoke-html-label-reader-{code}-{size.Width}.png");
            }
            Console.WriteLine($"SMOKE HTML export labels: full production EN/DE/ES/FR/RU content, text geometry, inspector, actual URSA overflow/menu and split/large reader; scale={host.RenderScaling}. Managed presentation checks, no native input/pixel OCR claim.");
        }
        finally
        {
            if (host != null) host.Content = null;
            if (detached != null) page?.AttachReader(detached);
            page?.EndLifetime();
            host?.Close();
            await PresentationChecks.SwitchModeAsync(owner, mode);
            Texts.SetLanguage(language);
            owner.Width = width; owner.Height = height; owner.WindowState = state;
            if (!inspectorWasVisible && owner.FindControl<Border>("WorkspaceInspector")!.IsVisible)
                SmokeActionClick.Execute(owner.FindControl<Button>("DetailsToggleButton")!);
            owner.Activate();
            // No recursive cleanup: retain this small diagnostic fixture in the private smoke temp root.
        }
    }

    private static async Task SettleAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
    }

    // Export is the reader toolbar's last AsNeeded candidate, so a longer language (German) moves it into
    // the real "More" overflow. Its complete title is then checked where the user finds it, in the opened popup.
    private static async Task<ActionToolBar?> OpenOverflowIfNeededAsync(Window host, Button button)
    {
        ActionToolBar? toolbar = button.Parent as ActionToolBar ?? button.GetLogicalAncestors().OfType<ActionToolBar>().FirstOrDefault();
        if (toolbar?.OverflowHost?.Children.Contains(button) != true) return null;
        Require(toolbar.More?.IsEffectivelyVisible == true, "The overflowed reader action has no visible More toggle.");
        toolbar.More!.IsChecked = true;
        await Program.WaitUntilAsync(() => button.IsEffectivelyVisible && TopLevel.GetTopLevel(button) is { IsVisible: true });
        await SettleAsync(host);
        return toolbar;
    }

    private static void CheckButton(Button button, string expected, string scene)
    {
        TopLevel root = TopLevel.GetTopLevel(button) ?? throw new InvalidOperationException(scene + ": detached button");
        root.UpdateLayout();
        TextBlock[] labels = button.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Text == expected).ToArray();
        Require(labels.Length == 1, scene + ": realized label is not the complete expected title: " + expected);
        TextBlock label = labels[0];
        var layout = label.TextLayout;
        string renderedText = string.Concat(layout.TextLines.SelectMany(line => line.TextRuns)
            .Where(run => run is not TextEndOfLine).Select(run => run.Text.ToString()));
        Console.WriteLine("SMOKE HTML label geometry " + JsonSerializer.Serialize(new
        {
            scene, button.Name, expected, contentKind = button.Content?.GetType().Name, button.Bounds, desired = button.DesiredSize.ToString(),
            labelBounds = label.Bounds.ToString(), textWidth = layout.WidthIncludingTrailingWhitespace, textHeight = layout.Height,
            scale = root.RenderScaling, rootBounds = root.Bounds.ToString(), renderedText,
            lines = layout.TextLines.Select(line => new { line.Length, line.HasCollapsed,
                runs = line.TextRuns.Select(run => new { kind = run.GetType().Name, run.Length, text = run.Text.ToString() }) })
        }));
        Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled && label.IsEffectivelyVisible,
            scene + ": label/action not visible and usable");
        Require(AutomationProperties.GetName(button) == expected, scene + ": accessibility name differs from full title");
        // TextSource positions can include a synthetic end-of-paragraph marker;
        // compare the actual shaped text runs, not that marker, with the full title.
        Require(layout.TextLines.All(line => !line.HasCollapsed) && renderedText == expected,
            scene + ": text layout shortened/collapsed the title");
        Require(layout.WidthIncludingTrailingWhitespace <= label.Bounds.Width + 1 && layout.Height <= label.Bounds.Height + 1,
            scene + ": glyph layout exceeds realized label bounds");
        Require(Fits(label, button) && Fits(button, root), scene + ": label/button is clipped outside its actual viewport");
        foreach (Control ancestor in label.GetVisualAncestors().OfType<Control>())
            if (ancestor.ClipToBounds || ancestor is ScrollViewer || ReferenceEquals(ancestor, root))
                Require(Fits(label, ancestor), scene + ": label exceeds clip ancestor " + ancestor.GetType().Name);
    }

    private static bool Fits(Control child, Control container)
    {
        Point? origin = child.TranslatePoint(default, container);
        return origin.HasValue && child.Bounds.Width > 0 && child.Bounds.Height > 0
            && origin.Value.X >= -1 && origin.Value.Y >= -1
            && origin.Value.X + child.Bounds.Width <= container.Bounds.Width + 1
            && origin.Value.Y + child.Bounds.Height <= container.Bounds.Height + 1;
    }

    private static void CapturePopup(Button button, string name)
    {
        TopLevel popup = TopLevel.GetTopLevel(button)!;
        using Avalonia.Media.Imaging.RenderTargetBitmap image = new(PixelSize.FromSize(popup.Bounds.Size, popup.RenderScaling),
            new Vector(96 * popup.RenderScaling, 96 * popup.RenderScaling));
        image.Render(popup);
        image.Save(Path.Combine(AppContext.BaseDirectory, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
