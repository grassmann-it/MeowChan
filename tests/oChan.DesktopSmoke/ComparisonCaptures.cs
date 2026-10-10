using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using oChan.Desktop;
using oChan.Desktop.Localization;
using oChan.Downloader;
using oChan.Http;
using oChan.Interfaces;

namespace oChan.DesktopSmoke;

/// <summary>Consistent visual-review scenes using passive public snapshots, without provider watches or HTTP.</summary>
internal static class ComparisonCaptures
{
    internal static async Task RunAsync(MainWindow window)
    {
        Config initial = Config.LoadConfig();
        ComboBox modes = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        if (initial.WorkspaceMode != "Simple" || (modes.SelectedItem as ComboBoxItem)?.Tag as string != "Simple"
            || initial.Theme != "Dark" || Application.Current?.RequestedThemeVariant != ThemeVariant.Dark)
            throw new InvalidOperationException("The comparison scene did not start in the new profile's Simple/Dark view.");
        if (window.UrlList.Count != 0 || window.BoardsList.Count != 0)
            throw new InvalidOperationException("The comparison scene requires an empty isolated session.");

        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        Border inspector = window.FindControl<Border>("WorkspaceInspector")!;
        bool inspectorInitiallyVisible = inspector.IsVisible;
        ThreadRow[] rows = CreateEntries().Select(entry => new ThreadRow(entry)).ToArray();
        try
        {
            foreach (ThreadRow row in rows) window.UrlList.Add(row);
            window.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = 0;
            grid.SelectedItem = rows[0];

            await SetInspectorAsync(window, false);
            await CaptureAsync(window, rows, "smoke-comparison-simple-dark.png");
            await CaptureContextMenuAsync(window, rows[0], "smoke-context-dark.png");

            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            await SetInspectorAsync(window, true);
            await CaptureAsync(window, rows, "smoke-comparison-advanced-dark.png");

            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            await SetInspectorAsync(window, false);
            await CaptureAsync(window, rows, "smoke-comparison-barebones-dark.png");

            await PresentationChecks.SwitchModeAsync(window, "Simple");
            await SetInspectorAsync(window, false);
            await SetThemeAsync(window, "Light");
            await CaptureAsync(window, rows, "smoke-comparison-simple-light.png");
            await CaptureContextMenuAsync(window, rows[0], "smoke-context-light.png");
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

    private static async Task CaptureContextMenuAsync(MainWindow window, ThreadRow row, string fileName)
    {
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        ContextMenu menu = grid.ContextMenu!;
        try
        {
            await GridChecks.OpenContextForAsync(window, grid, row);
            Border[] icons = menu.Items.OfType<MenuItem>().Select(item => item.Icon).OfType<Border>().ToArray();
            await Program.WaitUntilAsync(() => menu.IsEffectivelyVisible && menu.Bounds.Width > 0 && menu.Bounds.Height > 0
                && icons.Length == 3 && icons.All(icon => icon.IsEffectivelyVisible && icon.Bounds.Width > 0));
            Color expected = Application.Current?.RequestedThemeVariant == ThemeVariant.Dark
                ? Color.Parse("#EEF1F4") : Color.Parse("#18212C");
            if (icons.Any(icon => icon.Background is not ISolidColorBrush brush || brush.Color != expected))
                throw new InvalidOperationException("Context menu icons did not resolve the active theme's text color.");
            double scale = TopLevel.GetTopLevel(menu)?.RenderScaling ?? window.RenderScaling;
            using RenderTargetBitmap screenshot = new RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(menu.Bounds.Width * scale), (int)Math.Ceiling(menu.Bounds.Height * scale)),
                new Vector(96 * scale, 96 * scale));
            screenshot.Render(menu);
            screenshot.Save(Path.Combine(AppContext.BaseDirectory, fileName));
            Console.WriteLine("SMOKE context menu render: " + fileName + "; icon color=" + expected + "; scale=" + scale);
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
        Console.WriteLine("SMOKE comparison capture: " + fileName + "; bounds=" + window.Bounds
            + "; rows=" + rows.Length + "; scale=" + window.RenderScaling + ";");
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
        string directory = Path.Combine(Environment.GetEnvironmentVariable("OCHAN_PROFILE")!, "Downloads", "4chan", board, id);
        ThreadSnapshot snapshot = new ThreadSnapshot(new ResourceId("4chan", board, id),
            "https://boards.4chan.org/" + board + "/thread/" + id, title, directory,
            activity, ThreadSourceState.Active, null, known, completed,
            new Dictionary<string, FailureInfo>(), null, new DownloadCounts(queued, active));
        return new DesktopThreadEntry(watchId, snapshot, "4chan", "/" + board + "/");
    }
}
