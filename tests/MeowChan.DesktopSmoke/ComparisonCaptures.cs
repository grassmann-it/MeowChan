using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

/// <summary>Consistent visual-review scenes using passive public snapshots, without provider watches or HTTP.</summary>
internal static class ComparisonCaptures
{
    internal static async Task RunAsync(MainWindow window, bool visualReview = false)
    {
        Config initial = Config.LoadConfig();
        ComboBox modes = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        if (initial.WorkspaceMode != "Barebones" || (modes.SelectedItem as ComboBoxItem)?.Tag as string != "Barebones"
            || initial.Theme != "Dark" || Application.Current?.RequestedThemeVariant != ThemeVariant.Dark)
            throw new InvalidOperationException("The comparison scene did not start in the new profile's Barebones/Dark view.");
        if (window.UrlList.Count != 0 || window.BoardsList.Count != 0)
            throw new InvalidOperationException("The comparison scene requires an empty isolated session.");

        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        Border inspector = window.FindControl<Border>("WorkspaceInspector")!;
        bool inspectorInitiallyVisible = inspector.IsVisible;
        ThreadRow[] rows = CreateEntries().Select(entry => new ThreadRow(entry)).ToArray();
        try
        {
            if (visualReview) await ResizeAsync(window, 1440, 920);
            // Empty lists expose disabled toolbar controls that populated scenes cannot review.
            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            await CaptureEmptyAsync(window, "smoke-comparison-empty-dark.png");
            await SetThemeAsync(window, "Light");
            await CaptureEmptyAsync(window, "smoke-comparison-empty-light.png");
            await SetThemeAsync(window, "Dark");
            await PresentationChecks.SwitchModeAsync(window, "Simple");
            foreach (ThreadRow row in rows) window.UrlList.Add(row);
            window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 0;
            // Downloads reconcile their filtered ItemsSource in a coalesced UI update.
            // Select only after these passive snapshots are actually present in the grid.
            await Program.WaitUntilAsync(() => window.VisibleThreads.SequenceEqual(rows)
                && grid.CollectionView.Cast<ThreadRow>().SequenceEqual(rows));
            grid.SelectedItem = rows[0];
            await Program.WaitUntilAsync(() => grid.SelectedItems.Count == 1
                && ReferenceEquals(grid.SelectedItems[0], rows[0]));

            await SetInspectorAsync(window, false);
            await CaptureAsync(window, rows, "smoke-comparison-simple-dark.png");
            await CheckButtonContrastAsync(window);
            if (visualReview) await CaptureKeyboardFocusAsync(window, "smoke-comparison-focus-dark.png");
            await CaptureContextMenuAsync(window, rows[0], "smoke-context-dark.png");

            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            await SetInspectorAsync(window, true);
            await CaptureAsync(window, rows, "smoke-comparison-advanced-dark.png");

            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            if (visualReview) await ResizeAsync(window, 880, 680);
            await SetInspectorAsync(window, false);
            await CaptureAsync(window, rows, "smoke-comparison-barebones-dark.png");
            if (visualReview)
            {
                // GitHub social preview: retain the entire real header and footer at 2:1.
                await ResizeAsync(window, 1280, 640);
                await CaptureAsync(window, rows, "v0.9.0-social-preview.png");
                await ResizeAsync(window, 880, 680);
            }

            await PresentationChecks.SwitchModeAsync(window, "Simple");
            await SetInspectorAsync(window, false);
            if (visualReview) await CaptureSettingsAsync(window, "smoke-comparison-settings-dark.png");
            await SetThemeAsync(window, "Light");
            await CaptureAsync(window, rows, "smoke-comparison-simple-light.png");
            await CaptureContextMenuAsync(window, rows[0], "smoke-context-light.png");
            await CheckButtonContrastAsync(window);
            if (visualReview)
            {
                await CaptureKeyboardFocusAsync(window, "smoke-comparison-focus-light.png");
                await CaptureSettingsAsync(window, "smoke-comparison-settings-light.png");
            }
            if (visualReview) await ThemeMigrationChecks.RunAsync(window);
            Console.WriteLine("SMOKE comparison scenes: six passive thread snapshots across Simple/Dark, Advanced/Dark, Barebones/Dark and Simple/Light; no watches admitted or network requests made.");
        }
        finally
        {
            await SetThemeAsync(window, initial.Theme);
            await PresentationChecks.SwitchModeAsync(window, initial.WorkspaceMode);
            await SetInspectorAsync(window, inspectorInitiallyVisible);
            grid.SelectedItem = null;
            foreach (ThreadRow row in rows)
            {
                window.UrlList.Remove(row);
                row.Dispose();
            }
        }
    }

    private static async Task CaptureEmptyAsync(MainWindow window, string fileName)
    {
        Button copy = window.FindControl<Button>("CopyAllUrlsButton")!;
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            Button action = copy.IsEffectivelyVisible ? copy : window.FindControl<Button>("DownloadsMoreButton")!;
            Point? origin = action.TranslatePoint(new Point(0, 0), window);
            return !copy.IsEnabled && action.IsEffectivelyVisible && action.Bounds.Width > 0
                && Math.Abs(window.Bounds.Width - window.Width) <= 1 && Math.Abs(window.Bounds.Height - window.Height) <= 1
                && origin.HasValue && origin.Value.X >= 0 && origin.Value.Y >= 0
                && origin.Value.X + action.Bounds.Width <= window.Bounds.Width + 1
                && window.FindControl<TextBlock>("ThreadsEmptyState")!.IsEffectivelyVisible;
        });
        Program.CaptureWindow(window, fileName);
    }

    private static async Task ResizeAsync(MainWindow window, double width, double height)
    {
        window.WindowState = WindowState.Normal;
        window.Width = width;
        window.Height = height;
        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
    }

    private static async Task CaptureSettingsAsync(MainWindow window, string fileName)
    {
        window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => window.SettingsPageVisible && window.SettingsEditor.Bounds.Height > 0);
        window.SettingsEditor.FindControl<ScrollViewer>("SettingsScrollViewer")!.Offset = default;
        Program.CaptureWindow(window, fileName);
        string original = window.SettingsEditor.DownloadPath;
        window.SettingsEditor.DownloadPath = Path.Combine(original, "pending-edit");
        window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (window.SettingsEditor.DownloadPath != Path.Combine(original, "pending-edit"))
            throw new InvalidOperationException("Visual page navigation lost the pending settings edit.");
        window.SettingsEditor.DownloadPath = original;
        window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !window.SettingsPageVisible);
    }

    private static async Task CaptureKeyboardFocusAsync(MainWindow window, string fileName)
    {
        Button add = window.FindControl<Button>("AddUrlButton")!;
        add.Focus(NavigationMethod.Tab);
        await Program.WaitUntilAsync(() => add.IsFocused);
        Program.CaptureWindow(window, fileName);
        window.FindControl<TextBox>("UrlInput")!.Focus();
    }

    private static async Task CheckButtonContrastAsync(MainWindow window)
    {
        Button add = window.FindControl<Button>("AddUrlButton")!;
        TextBlock? label = null;
        await Program.WaitUntilAsync(() =>
        {
            label = add.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text =>
                text.Text == Avalonia.Automation.AutomationProperties.GetName(add));
            return label?.IsEffectivelyVisible == true;
        });
        // Check the rendered label: a global TextBlock style can override Button.Foreground.
        if (label!.Foreground is not ISolidColorBrush text || add.Background is not ISolidColorBrush fill)
            throw new InvalidOperationException("The primary action did not resolve solid foreground/background brushes.");
        double first = Luminance(text.Color);
        double second = Luminance(fill.Color);
        double contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        if (contrast < 4.5)
            throw new InvalidOperationException("Primary action text contrast is below 4.5:1: " + contrast);
        Console.WriteLine("SMOKE primary action rendered text contrast: " + contrast.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte value)
        {
            double component = value / 255d;
            return component <= 0.04045 ? component / 12.92 : Math.Pow((component + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static async Task CaptureContextMenuAsync(MainWindow window, ThreadRow row, string fileName)
    {
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        ContextMenu menu = grid.ContextMenu!;
        try
        {
            await GridChecks.OpenContextForAsync(window, grid, row);
            string[] requiredActions = { "thread.read", "gallery.open", "thread.details", "export.open-html-dialog",
                "selection.copy-links", "location.open-folder", "location.open-source", "watch.check",
                "watch.retry-media", "rules.edit-scope", "watch.remove" };
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            foreach (string action in requiredActions)
                if (items.Count(item => AutomationProperties.GetName(item) == CommandCatalog.Label(action)) != 1)
                    throw new InvalidOperationException("Context menu did not expose exactly one required action: " + action);
            if (items.Any(item => item.Icon is not Border))
                throw new InvalidOperationException("A context action or availability explanation is missing its shared icon.");
            Border[] icons = items.Select(item => (Border)item.Icon!).ToArray();
            // The popup can have a nonzero one-row bounds during its first layout.
            // Wait for every item to fit before recording a full menu as evidence.
            string menuGeometry = "";
            try
            {
            await Program.WaitUntilAsync(() =>
            {
                menu.UpdateLayout();
                menuGeometry = $"menu={menu.Bounds}; " + string.Join("; ", items.Select(item => $"{item.Header}: bounds={item.Bounds}, point={item.TranslatePoint(default, menu)}, visible={item.IsEffectivelyVisible}"));
                return menu.IsEffectivelyVisible && menu.Bounds.Width > 0 && menu.Bounds.Height > 0
                    && icons.All(icon => icon.IsEffectivelyVisible && icon.Bounds.Width > 0)
                    && items.All(item =>
                    {
                        Point? origin = item.TranslatePoint(default, menu);
                        return item.Bounds.Height > 0 && origin.HasValue && origin.Value.Y >= 0
                            && origin.Value.Y + item.Bounds.Height <= menu.Bounds.Height + 1;
                    });
            });
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException("Full context menu was not laid out: " + menuGeometry, exception);
            }
            bool dark = window.ActualThemeVariant == ThemeVariant.Dark;
            Color expected = dark ? Color.Parse("#EEF1F4") : Color.Parse("#18212C");
            foreach (MenuItem item in items)
            {
                // Shared masks use Workspace.Text, independently of the vendor menu text brushes.
                if (((Border)item.Icon!).Background is not ISolidColorBrush ink || ink.Color != expected)
                    throw new InvalidOperationException("Context icon does not match the active Workspace.Text palette: " + item.Header);
                if (item.IsEnabled && (dark ? Luminance(ink.Color) < 0.5 : Luminance(ink.Color) > 0.2))
                    throw new InvalidOperationException("Enabled context icon did not resolve readable ink for the active theme: " + item.Header);
            }
            string colors = string.Join(",", icons.Select(icon => ((ISolidColorBrush)icon.Background!).Color).Distinct());
            double scale = TopLevel.GetTopLevel(menu)?.RenderScaling ?? window.RenderScaling;
            using RenderTargetBitmap screenshot = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(menu.Bounds.Width * scale), (int)Math.Ceiling(menu.Bounds.Height * scale)),
                new Vector(96 * scale, 96 * scale));
            screenshot.Render(menu);
            screenshot.Save(Path.Combine(AppContext.BaseDirectory, fileName), PngBitmapEncoderOptions.Default);
            Console.WriteLine("SMOKE context menu render: " + fileName + "; icons=" + icons.Length + "; Workspace.Text inks=" + colors + "; scale=" + scale);
        }
        finally
        {
            menu.Close();
        }
    }

    private static async Task CaptureAsync(MainWindow window, ThreadRow[] rows, string fileName)
    {
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        grid.ScrollIntoView(rows[0], null);
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            if (window.WindowState == WindowState.Normal && double.IsFinite(window.Width)
                && Math.Abs(window.Bounds.Width - window.Width) > 1)
                return false;
            DataGridRow? first = grid.GetVisualDescendants().OfType<DataGridRow>()
                .FirstOrDefault(row => row.IsEffectivelyVisible && ReferenceEquals(row.DataContext, rows[0]));
            Point? position = first?.TranslatePoint(new Point(20, first.Bounds.Height / 2), window);
            Visual? hit = position.HasValue ? window.InputHitTest(position.Value) as Visual : null;
            DataGridRow? hitRow = hit as DataGridRow ?? hit?.FindAncestorOfType<DataGridRow>();
            return first?.Bounds.Height > 0 && ReferenceEquals(hitRow?.DataContext, rows[0])
                && window.FindControl<TextBlock>("WorkspaceCounts")!.Text == Texts.T("downloads.statusSummary",
                    rows.Length, 0, rows.Sum(row => (long)row.Queued), rows.Sum(row => (long)row.Active));
        });
        if (!window.UrlList.SequenceEqual(rows) || !ReferenceEquals(grid.SelectedItem, rows[0]))
            throw new InvalidOperationException("Changing the comparison view replaced its passive rows or selection.");
        DataGridRow selected = grid.GetVisualDescendants().OfType<DataGridRow>().First(row => row.IsEffectivelyVisible && ReferenceEquals(row.DataContext, rows[0]));
        if (!selected.IsSelected || grid.SelectedItems.Count != 1 || !ReferenceEquals(grid.SelectedItems[0], rows[0]))
            throw new InvalidOperationException($"The comparison row is not selected: rowSelected={selected.IsSelected}; selectedItems={grid.SelectedItems.Count}; selectedIndex={grid.SelectedIndex}.");
        Border background = selected.GetVisualDescendants().OfType<Border>()
            .First(border => border.Name == "BackgroundBorder" && ReferenceEquals(border.TemplatedParent, selected));
        Color expectedSelection = Application.Current?.RequestedThemeVariant == ThemeVariant.Dark
            ? Color.Parse("#25344A") : Color.Parse("#E0EBFC");
        if (background.Background is not ISolidColorBrush selection || selection.Color != expectedSelection)
            throw new InvalidOperationException($"The row template overrode the workspace's selection color: expected={expectedSelection}; actual={background.Background}; theme={selected.ActualThemeVariant}.");
        Console.WriteLine("SMOKE comparison capture: " + fileName + "; bounds=" + window.Bounds
            + "; rows=" + rows.Length + "; scale=" + window.RenderScaling + "; rowSelected=" + selected.IsSelected
            + "; selectionColor=" + selection.Color + ";");
        Program.CaptureWindow(window, fileName);
    }

    private static async Task SetInspectorAsync(MainWindow window, bool visible)
    {
        Border inspector = window.FindControl<Border>("WorkspaceInspector")!;
        if (inspector.IsVisible != visible)
            window.FindControl<Button>("DetailsToggleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            return inspector.IsVisible == visible && (!visible || inspector.Bounds.Height > 0);
        });
    }

    private static async Task SetThemeAsync(MainWindow window, string theme)
    {
        if (Config.LoadConfig().Theme != theme)
            window.FindControl<Button>("ThemeToggleButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ThemeVariant variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        await Program.WaitUntilAsync(() => Config.LoadConfig().Theme == theme
            && Application.Current?.RequestedThemeVariant == variant
            && window.FindControl<Button>("AddUrlButton")!.IsEnabled);
    }

    private static DesktopThreadEntry[] CreateEntries()
    {
        DesktopThreadEntry downloading = Entry(700001, "wg", "Desktop setups & workspaces", 96, 148,
            ThreadActivity.Downloading, queued: 49, active: 3);
        DesktopThreadEntry cooldown = Entry(700002, "g", "Linux desktops and window managers", 72, 86,
            ThreadActivity.Downloading, queued: 14);
        cooldown = cooldown with
        {
            Snapshot = cooldown.Snapshot with
            {
                RequestProgress = new RequestPolicyProgress(RequestPolicyPhase.Cooldown, TimeSpan.FromSeconds(30), 1)
            }
        };
        DesktopThreadEntry archived = Entry(700003, "po", "Papercraft collection", 32, 32, ThreadActivity.Finished);
        archived = archived with { Snapshot = archived.Snapshot with { SourceState = ThreadSourceState.Archived } };
        DesktopThreadEntry failed = Entry(700004, "g", "Self-hosted services", 42, 55, ThreadActivity.Error);
        failed = failed with
        {
            Snapshot = failed.Snapshot with
            {
                LastCheckFailure = new FailureInfo(FailureKind.Timeout, "The latest thread check timed out."),
                MediaFailures = new Dictionary<string, FailureInfo>
                {
                    ["fixture-image.jpg"] = new FailureInfo(FailureKind.Network, "The media connection was interrupted.")
                }
            }
        };
        DesktopThreadEntry saved = Entry(700005, "wg", "Landscape photography", 64, 64, ThreadActivity.Finished);
        DesktopThreadEntry pending = Entry(700006, "ck", "Weekend recipes", 0, 0, ThreadActivity.Pending);
        return new[] { downloading, cooldown, archived, failed, saved, pending };
    }

    private static DesktopThreadEntry Entry(long watchId, string board, string title, int completed, int known,
        ThreadActivity activity, int queued = 0, int active = 0)
    {
        string id = watchId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string directory = Path.Combine(Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE")!, "Downloads", "4chan", board, id);
        ThreadSnapshot snapshot = new ThreadSnapshot(new ResourceId("4chan", board, id),
            "https://boards.4chan.org/" + board + "/thread/" + id, title, directory,
            activity, ThreadSourceState.Active, null, known, completed,
            new Dictionary<string, FailureInfo>(), null, new DownloadCounts(queued, active));
        return new DesktopThreadEntry(watchId, snapshot, "4chan", "/" + board + "/");
    }
}
