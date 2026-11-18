using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop.WorkViews;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static partial class MediaPageChecks
{
    // A small standalone registry run, also using exactly the capture assertions called by media-page's full journey.
    internal static async Task RunBooruCapturesAsync(MainWindow owner)
    {
        DesktopSessionHost host = owner.SessionHost;
        string language = Texts.Language;
        Texts.SetLanguage("en");
        foreach (string name in new[] { "synthetic", "orientation", "portrait" })
        {
            var tag = await host.DefinePrivateTagAsync(name).WaitAsync(Deadline);
            await host.SetThreadTagAsync(tag.Tag, ExifOrientationFixture.Resource, true).WaitAsync(Deadline);
        }
        MediaPage page = new(host.OpenArchiveReaderAsync) { ReadUserData = () => UserData(host) };
        Window window = new() { Width = 1400, Height = 900, Content = page, Title = "Synthetic media — Booru mode", ShowInTaskbar = false };
        try
        {
            window.Show(owner);
            page.IsAvailable = true;
            await page.ActivateAsync().WaitAsync(Deadline);
            Require(page.Tiles.Count == 2, "Booru capture requires exactly two real synthetic tiles.");
            await UntilAsync(() => page.GetVisualDescendants().OfType<MediaLibraryTileView>().Count(tile => tile.HasThumbnail) == 2);
            page.BooruModeButton.IsChecked = true;
            await UntilAsync(() => page.IsBooruMode && page.TagCountTask.IsCompleted && page.TagCounts?.Media == 2);
            Require(page.TagSidebar.Items.Count(row => row.Kind == MediaTagListKind.Tag && row.Count == 2) == 3,
                "Booru capture requires all three synthetic tags and exact counts.");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                await CaptureBooruModesAsync(window, page, theme);
            Console.WriteLine("SMOKE media Booru captures: 1400/1000/minimum/1400 DIP, all work views, tag preference on/off, viewer-first collapse, restored unsaved preferences, main/selection center bounds, Light/Dark.");
        }
        finally { page.EndLifetime(); window.Close(); Texts.SetLanguage(language); }
    }

    private static async Task CaptureBooruModesAsync(Window window, MediaPage page, ThemeVariant theme)
    {
        window.RequestedThemeVariant = theme;
        MediaPageLayout original = page.PageLayout;
        var saveLayout = page.SaveLayoutAsync;
        WorkView originalView = page.CurrentWorkView;
        Func<int>? minimumSource = page.PanelMinimumPageWidthSource;
        page.PanelMinimumPageWidthSource = () => 1100;
        // The tag sidebar yields below this window width: its minimum width, its chrome and the 616 DIP center no longer fit side by side.
        double tagListFrom = MediaPanelColumns.MinimumCombinedContentWidth + MediaPageLayoutPlan.BodyChrome + MeowChan.Config.MediaTagListWidthMinimum;
        Exception? primaryFailure = null;
        try
        {
            foreach (WorkView view in Enum.GetValues<WorkView>())
            foreach (bool tags in new[] { true, false })
            {
                page.ApplyWorkView(view, viewChanged: true);
                List<MediaPageLayout> saves = new();
                page.SaveLayoutAsync = layout => { saves.Add(layout); return Task.CompletedTask; };
                MediaPageLayout preference = original with { TagList = tags, Panel = true };
                page.ApplyLayout(preference);
                int saved = saves.Count;
                int above = (int)Math.Ceiling(tagListFrom) + 6, below = (int)Math.Floor(tagListFrom) - 6;
                foreach ((int width, string stage) in new[] { (1400, "wide"), (1000, "viewer-collapsed"), (above, "tag-threshold-above"), (below, "tag-threshold-below"),
                             (view == WorkView.Barebones ? 640 : 760, "minimum"), (1400, "restored") })
                {
                    window.Width = width; window.Height = 900;
                    await UntilAsync(() => Math.Abs(window.ClientSize.Width - width) < 1 && window.ActualThemeVariant == theme);
                    await SmokeDispatcher.SettleAsync();
                    window.UpdateLayout();
                    string cell = $"{view}-{tags}-{width}-{theme}";
                    Control surface = C<Control>(page, "MediaGridSurface");
                    Rect grid = BooruVisibleBounds(surface, window, cell);
                    Require(grid.Width >= 200 && grid.Height > 100, cell + ": center grid lost usable room: " + grid);
                    Require(page.PageLayout == preference && saves.Count == saved,
                        cell + ": responsive collapse changed or saved the user's layout preference.");
                    Require(page.TagSidebar.IsEffectivelyVisible == (tags && width >= tagListFrom), cell + ": tag sidebar collapse/restore order is wrong (yields below " + tagListFrom + " DIP).");
                    Require(page.TagListHandle.IsVisible == page.TagSidebar.IsEffectivelyVisible, cell + ": tag list handle does not follow the sidebar.");
                    Require(page.IsPanelShown == (width == 1400), cell + ": viewer must collapse before the tag sidebar and return at wide size.");
                    if (page.TagSidebar.IsEffectivelyVisible)
                    {
                        Rect sidebar = BooruVisibleBounds(page.TagSidebar, window, cell);
                        Require(sidebar.Right <= grid.Left + 1, cell + ": tag sidebar overlaps the center.");
                    }
                    foreach (bool selection in new[] { false, true })
                    {
                        page.ClearSelection();
                        if (selection) page.Grid.Selection.SelectAll();
                        await SmokeDispatcher.SettleAsync();
                        window.UpdateLayout();
                        Control toolbar = C<Control>(page, "MediaToolbar");
                        Rect bar = BooruVisibleBounds(toolbar, window, cell);
                        Require(bar.Left >= grid.Left - 1 && bar.Right <= grid.Right + 1, cell + ": toolbar is outside the visible center.");
                        // Export all and the ratings manager are under More in every view, so the main toolbar's More is always on screen.
                        Rect more = BooruVisibleBounds(C<Control>(page, "MediaMore"), window, cell);
                        Require(bar.Inflate(1).Contains(more), cell + ": the toolbar's More button is outside its row: " + more + " / " + bar);
                        foreach (Control row in selection ? new[] { toolbar, C<Control>(page, "MediaSelectionRow") } : new[] { toolbar })
                        {
                            Rect bounds = BooruVisibleBounds(row, window, cell);
                            Require(bounds.Left >= grid.Left - 1 && bounds.Right <= grid.Right + 1, cell + ": action row escapes center: " + bounds);
                            foreach (Control action in row.GetVisualDescendants().OfType<Control>().Where(control => control is Button or ComboBox or TextBox).Where(control => control.IsEffectivelyVisible))
                            {
                                Rect actionBounds = BooruVisibleBounds(action, window, cell);
                                Require(bounds.Inflate(1).Contains(actionBounds) && actionBounds.Left >= grid.Left - 1 && actionBounds.Right <= grid.Right + 1,
                                    cell + ": action including More escapes its visible center row: " + action.Name + " " + actionBounds);
                            }
                        }
                        string capture = $"smoke-media-page-booru-{cell.ToLowerInvariant()}-{(selection ? "selection" : "main")}.png";
                        Console.WriteLine("SMOKE media-layout " + System.Text.Json.JsonSerializer.Serialize(new { cell, selection, client = window.ClientSize.ToString(), center = grid.ToString(), toolbar = bar.ToString(), tags = page.TagSidebar.IsEffectivelyVisible, viewer = page.IsPanelShown, preference, saves = saves.Count, scale = window.RenderScaling, capture }));
                        Program.CaptureWindow(window, capture);
                    }
                }
            }

            // The viewer alone at the default page-width setting: from 1000 DIP it shows beside the grid, and the toolbar with its More stays inside the center.
            page.PanelMinimumPageWidthSource = () => MeowChan.Config.MediaPanelMinimumPageWidthDefault;
            foreach (WorkView view in Enum.GetValues<WorkView>())
            {
                page.ApplyWorkView(view, viewChanged: true);
                MediaPageLayout viewerOnly = original with { TagList = false, Panel = true };
                page.ApplyLayout(viewerOnly, save: false);
                foreach (int width in new[] { MeowChan.Config.MediaPanelMinimumPageWidthDefault, MeowChan.Config.MediaPanelMinimumPageWidthDefault + 40 })
                {
                    window.Width = width; window.Height = 900;
                    await UntilAsync(() => Math.Abs(window.ClientSize.Width - width) < 1 && window.ActualThemeVariant == theme);
                    await SmokeDispatcher.SettleAsync();
                    window.UpdateLayout();
                    string cell = $"{view}-viewer-only-{width}-{theme}";
                    Require(page.IsPanelShown && !page.TagSidebar.IsEffectivelyVisible, cell + ": the viewer alone must show at the default page width.");
                    Rect grid = BooruVisibleBounds(C<Control>(page, "MediaGridSurface"), window, cell);
                    Control toolbar = C<Control>(page, "MediaToolbar");
                    Rect bar = BooruVisibleBounds(toolbar, window, cell);
                    Rect more = BooruVisibleBounds(C<Control>(page, "MediaMore"), window, cell);
                    Require(bar.Left >= grid.Left - 1 && bar.Right <= grid.Right + 1 && bar.Inflate(1).Contains(more),
                        cell + ": toolbar or its More escapes the center beside the viewer: " + bar + " / " + more + " / " + grid);
                    foreach (Control action in toolbar.GetVisualDescendants().OfType<Control>().Where(control => control is Button or ComboBox or TextBox).Where(control => control.IsEffectivelyVisible))
                        Require(bar.Inflate(1).Contains(BooruVisibleBounds(action, window, cell)), cell + ": toolbar action escapes its row: " + action.Name);
                    Console.WriteLine("SMOKE media-layout " + System.Text.Json.JsonSerializer.Serialize(new { cell, client = window.ClientSize.ToString(), center = grid.ToString(), toolbar = bar.ToString(), more = more.ToString() }));
                    Program.CaptureWindow(window, $"smoke-media-page-booru-{cell.ToLowerInvariant()}.png");
                }
            }
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            await SmokeCleanup.RunAsync(primaryFailure, async () =>
            {
                page.ClearSelection();
                page.SaveLayoutAsync = saveLayout;
                page.PanelMinimumPageWidthSource = minimumSource;
                page.ApplyWorkView(originalView, viewChanged: true);
                page.ApplyLayout(original, save: false);
                window.Width = 1400;
                await UntilAsync(() => Math.Abs(window.ClientSize.Width - 1400) < 1);
            }, failure => Console.Error.WriteLine("SMOKE media Booru capture cleanup: " + failure));
        }
    }

    private static Rect BooruVisibleBounds(Control control, Window window, string cell)
    {
        Require(control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0, cell + ": invisible/empty " + control.Name);
        Rect bounds = new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
        Require(new Rect(window.ClientSize).Inflate(1).Contains(bounds), cell + ": clipped by client: " + control.Name + " " + bounds);
        foreach (Control ancestor in control.GetVisualAncestors().OfType<Control>().Where(ancestor => ancestor.ClipToBounds))
        {
            Rect clip = new(ancestor.TranslatePoint(default, window)!.Value, ancestor.Bounds.Size);
            Require(clip.Inflate(1).Contains(bounds), cell + ": " + control.Name + " clipped by " + ancestor.Name + ": " + bounds + " / " + clip);
        }
        return bounds;
    }
}
