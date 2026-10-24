using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

internal static class ReaderNavigationOverlayChecks
{
    private const double GeometryEpsilonDip = 1e-7;

    internal static async Task RunAsync(MainWindow window, ArchivePage page)
    {
        string mode = Config.LoadConfig().WorkspaceMode;
        string language = Texts.Language;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        double width = window.Width, height = window.Height;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        Button history = page.FindControl<Button>("ArchiveHistoryBack")!;
        bool historyCreated = false;
        bool mediaTiles = page.MediaTilesPreference;
        try
        {
            Require(!history.IsVisible, "Reader navigation fixture expected the preceding history check to leave no entries.");
            await page.FocusPostAsync("1000");
            ArchivePostId target = new(page.CurrentThread!.Summary.Id, "1003");
            await page.NavigateQuoteAsync(new ArchiveQuoteRow(new ArchiveQuote(target, target, ">>1003", null)));
            historyCreated = true;
            await Program.WaitUntilAsync(() => history.IsEffectivelyVisible
                && (page.FindControl<ListBox>("ArchivePosts")!.SelectedItem as ArchivePostRow)?.Id == "1003");
            foreach (var size in new[] { (Mode: "Simple", Width: 1440d, Height: 920d),
                (Mode: "Simple", Width: 760d, Height: 500d), (Mode: "Advanced", Width: 760d, Height: 500d),
                (Mode: "Barebones", Width: 640d, Height: 480d) })
            {
                await PresentationChecks.SwitchModeAsync(window, size.Mode);
                window.Width = size.Width; window.Height = size.Height;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, size.Width, size.Height));
                foreach (string code in new[] { "en", "de" })
                foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Texts.SetLanguage(code);
                    Application.Current.RequestedThemeVariant = variant;
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                    await page.FocusPostAsync("1000");
                    await Program.WaitUntilAsync(() => HasRenderedPost(page));
                    CheckGeometry(window, page);
                    // Check the actual scrolled content too, not only a spacer at the end.
                    await page.FocusPostAsync("1003");
                    await Program.WaitUntilAsync(() => page.FindControl<ListBox>("ArchivePosts")!.GetVisualDescendants()
                        .OfType<ArchiveAttachmentView>().Any(view => view.Thumbnail.Source is Bitmap bitmap && bitmap.PixelSize.Width > 240
                            && view.GetVisualAncestors().OfType<ListBoxItem>()
                                .Any(item => item.DataContext is ArchivePostRow post && post.Id == "1003")));
                    window.UpdateLayout();
                    CheckGeometry(window, page);
                    string name = $"smoke-reader-navigation-{size.Mode.ToLowerInvariant()}-{size.Width}-{code}-{variant.ToString().ToLowerInvariant()}.png";
                    Program.CaptureWindow(window, name);
                }
            }
            // Exercise both sides of the adaptive transition, where an
            // insufficient horizontal gap would otherwise be easy to miss.
            // Keep enough height to exercise the width transition rather than
            // the short-window fallback that preserves the reading viewport.
            await PresentationChecks.SwitchModeAsync(window, "Simple");
            foreach (double transitionWidth in new[] { 1304d, 1308d })
            {
                window.Width = transitionWidth; window.Height = 920;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, transitionWidth, 920));
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                CheckGeometry(window, page);
                Thickness reserved = page.FindControl<ListBox>("ArchivePosts")!.Margin;
                Require(transitionWidth < 1308 ? reserved.Bottom > 0 && reserved.Right == 0 : reserved.Right > 0 && reserved.Bottom == 0,
                    "Reader navigation failed to adapt on both sides of its available-width threshold.");
            }
            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            window.Width = 640; window.Height = 480;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 640, 480));
            foreach (string code in new[] { "en", "de" })
            foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Texts.SetLanguage(code);
                Application.Current.RequestedThemeVariant = variant;
                foreach (bool tiles in new[] { true, false })
                {
                    tabs.SelectedIndex = 1;
                    page.MediaTilesPreference = tiles;
                    await page.MediaLoadTask;
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                    Require(Texts.Language == code && window.ActualThemeVariant == variant,
                        "Secondary media capture lost its requested language or theme.");
                    CheckSecondaryNavigation(window, page);
                    CheckSecondaryMediaGeometry(window, page, tiles);
                    string presentation = tiles ? "tiles" : "table";
                    Program.CaptureWindow(window, $"smoke-reader-navigation-secondary-media-{presentation}-barebones-640-{code}-{variant.ToString().ToLowerInvariant()}.png");
                }
                tabs.SelectedIndex = 2;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                Require(Texts.Language == code && window.ActualThemeVariant == variant,
                    "Secondary references capture lost its requested language or theme.");
                CheckSecondaryNavigation(window, page);
                Program.CaptureWindow(window, $"smoke-reader-navigation-secondary-references-barebones-640-{code}-{variant.ToString().ToLowerInvariant()}.png");
            }
            tabs.SelectedIndex = 0;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(page.FindControl<WrapPanel>("ReaderStatusLine")!.IsEffectivelyVisible,
                "Returning from secondary tabs did not restore the posts status line.");
            page.FindControl<Button>("ExportArchiveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => page.FindControl<Border>("ArchiveExportDialog")!.IsVisible);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Border dialog = page.FindControl<Border>("ArchiveExportDialog")!;
            Point arrow = Bounds(page.FindControl<Button>("ReaderNextPost")!, window).Center;
            Visual? hit = window.InputHitTest(arrow) as Visual;
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                hit = window.InputHitTest(arrow) as Visual;
                return hit != null && hit.GetSelfAndVisualAncestors().Contains(dialog);
            });
            page.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally
        {
            tabs.SelectedIndex = 0;
            Texts.SetLanguage(language);
            Application.Current.RequestedThemeVariant = theme;
            await PresentationChecks.SwitchModeAsync(window, mode);
            window.Width = width; window.Height = height;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
            page.MediaTilesPreference = mediaTiles;
            if (historyCreated)
            {
                history.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !history.IsVisible
                    && (page.FindControl<ListBox>("ArchivePosts")!.SelectedItem as ArchivePostRow)?.Id == "1000");
                await page.MediaLoadTask;
            }
            await page.FocusPostAsync("1000");
            window.UpdateLayout();
        }
        Console.WriteLine("SMOKE reader navigation overlay: 16 EN/DE, Dark/Light, large and B/S/A minimum geometries plus both adaptive threshold edges and 12 explicit secondary-tab states; scrolled text, scrollbar/pagebar spacing, controlled toolbar wrapping with visible history label, centered arrow cross and adjacent centered pagination, four accessible commands, retained secondary tabs and dialog hit-test passed.");
    }

    private static bool HasRenderedPost(ArchivePage page) => page.FindControl<ListBox>("ArchivePosts")!
        .GetVisualDescendants().OfType<ListBoxItem>().Any(item => item.IsEffectivelyVisible && item.Bounds.Width > 0);

    private static void CheckGeometry(MainWindow window, ArchivePage page)
    {
        WrapPanel navigation = page.FindControl<WrapPanel>("ReaderNavigation")!;
        Rect pad = Bounds(navigation, window);
        Rect surface = Bounds(page.FindControl<Grid>("ReaderContentSurface")!, window);
        Rect pageBar = Bounds(page.FindControl<Grid>("ArchivePostNavigation")!, window);
        Console.WriteLine("SMOKE reader navigation precondition: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            mode = Config.LoadConfig().WorkspaceMode, language = Texts.Language,
            theme = Application.Current!.RequestedThemeVariant?.ToString(), scale = window.RenderScaling,
            client = window.ClientSize.ToString(), navigation.IsVisible, navigation.IsEffectivelyVisible,
            navigation.IsMeasureValid, navigation.IsArrangeValid, localBounds = navigation.Bounds.ToString(),
            pad = pad.ToString(), surface = surface.ToString(), pagebar = pageBar.ToString(),
            arrows = new[] { "ReaderPreviousPost", "ReaderNextPost", "ReaderPreviousThread", "ReaderNextThread" }
                .Select(name => new { name, bounds = Bounds(page.FindControl<Button>(name)!, window).ToString() }).ToArray()
        }));
        Require(page.FindControl<WrapPanel>("ReaderStatusLine")!.IsEffectivelyVisible,
            "The posts tab lost its reader status line.");
        // The 1.5x diagnostic measured 110 2/3 DIP outside while all four
        // targets stayed 32 DIP with 2-DIP gaps: allow one physical pixel
        // for the raised border's layout rounding, not a larger cross.
        double borderRoundingDip = 1 / window.RenderScaling;
        Require(navigation.IsEffectivelyVisible && pad.Width <= 110 + borderRoundingDip
            && pad.Height <= 110 + borderRoundingDip,
            "Reader navigation is no longer a compact visible cross.");
        Require(pad.Right <= surface.Right - 20 && pad.Bottom <= pageBar.Top - 7
            && pad.Bottom >= pageBar.Top - 10 && pad.Top >= surface.Top,
            "Reader navigation is not inset at the lower right above the page bar.");
        CheckCrossGeometry(window, page, pad);
        CheckPaginationGeometry(window, page, pageBar);
        WrapPanel toolbar = page.FindControl<WrapPanel>("ReaderToolbar")!;
        CheckToolbarGeometry(window, page, toolbar);
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
        Require(scroll.Viewport.Height >= 100, "Navigation reduced the small-window reader viewport below 100 DIP.");
        Rect postBounds = Bounds(posts, window);
        Require(postBounds.Right <= pad.Left - 4 || postBounds.Bottom <= pad.Top - 4,
            "Scrolled post content can pass beneath the reader navigation.");
        Console.WriteLine($"SMOKE reader navigation geometry: mode={Config.LoadConfig().WorkspaceMode}; language={Texts.Language}; theme={Application.Current!.RequestedThemeVariant}; client={window.ClientSize}; scale={window.RenderScaling}; toolbar={toolbar.Bounds.Height}; viewport={scroll.Viewport}; posts={postBounds}; pad={pad}; pagebar={pageBar}; reservation={posts.Margin}.");
        foreach (ScrollBar bar in posts.GetVisualDescendants().OfType<ScrollBar>()
            .Where(bar => bar.IsEffectivelyVisible && bar.Orientation == Orientation.Vertical && bar.Bounds.Width > 0))
            Require(Bounds(bar, window).Right <= pad.Left - 4 || Bounds(bar, window).Bottom <= pad.Top - 4,
                "Reader navigation crowds or covers the vertical scrollbar.");
        foreach (var command in new[] { (Name: "ReaderPreviousThread", Id: "reader.previous-thread"),
            (Name: "ReaderNextThread", Id: "reader.next-thread"), (Name: "ReaderPreviousPost", Id: "reader.previous-post"),
            (Name: "ReaderNextPost", Id: "reader.next-post") })
        {
            Button button = page.FindControl<Button>(command.Name)!;
            Require(button.IsEffectivelyVisible && button.Focusable && Bounds(button, window).Width >= 32
                && Bounds(button, window).Height >= 32
                && AutomationProperties.GetName(button) == CommandCatalog.Label(command.Id)
                && ToolTip.GetTip(button) as string == CommandBindings.Tooltip(command.Id),
                command.Name + " lost its stable visible button or localized command semantics.");
        }
    }

    private static void CheckSecondaryNavigation(MainWindow window, ArchivePage page)
    {
        WrapPanel navigation = page.FindControl<WrapPanel>("ReaderNavigation")!;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        Control reservedContent = tabs.SelectedIndex == 1
            ? page.FindControl<Grid>("ArchiveMediaSurface")! : page.FindControl<ListBox>("ArchiveReferences")!;
        Rect pad = Bounds(navigation, window), content = Bounds(reservedContent, window);
        WrapPanel status = page.FindControl<WrapPanel>("ReaderStatusLine")!;
        Require(!status.IsVisible && !status.IsEffectivelyVisible,
            "The large reader retains its posts-only status line on a secondary tab.");
        Console.WriteLine("SMOKE reader navigation secondary: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            tab = tabs.SelectedIndex, language = Texts.Language, theme = window.ActualThemeVariant.ToString(),
            scale = window.RenderScaling, client = window.ClientSize.ToString(), pad = pad.ToString(),
            contentName = reservedContent.Name, content = content.ToString(),
            reservation = reservedContent.Margin.ToString(), navigation.IsEffectivelyVisible,
            statusVisible = status.IsVisible, statusEffectivelyVisible = status.IsEffectivelyVisible
        }));
        Require(navigation.IsEffectivelyVisible && Contains(Bounds(page.FindControl<Grid>("ReaderContentSurface")!, window), pad),
            "Reader navigation disappeared or is clipped on a secondary tab.");
        Require(content.Right + 4 <= pad.Left || content.Bottom + 4 <= pad.Top,
            "Secondary-tab content can sit beneath reader navigation.");
        if (tabs.SelectedIndex == 2)
            foreach (string name in new[] { "ArchiveReferencesTitle", "PreviousReferences", "ArchiveReferencesPage", "NextReferences" })
            {
                Control control = page.FindControl<Control>(name)!;
                if (!control.IsEffectivelyVisible || control.Bounds.Width == 0 || control.Bounds.Height == 0) continue;
                Rect box = Bounds(control, window);
                Require(Contains(Bounds(tabs, window), box) && Separated(box, pad, 4),
                    name + " is clipped or crowded by the reader navigation.");
            }
    }

    private static bool Separated(Rect first, Rect second, double gap) => first.Right + gap <= second.Left
        || first.Bottom + gap <= second.Top || second.Right + gap <= first.Left || second.Bottom + gap <= first.Top;

    private static void CheckSecondaryMediaGeometry(MainWindow window, ArchivePage page, bool tiles)
    {
        Control list = tiles ? page.FindControl<ListBox>("ArchiveMediaTiles")! : page.FindControl<DataGrid>("ArchiveMediaGrid")!;
        Control viewport = tiles
            ? list.GetVisualDescendants().OfType<ScrollContentPresenter>().First()
            : list.GetVisualDescendants().OfType<DataGridRowsPresenter>().First();
        Rect viewportBox = Bounds(viewport, window), listBox = Bounds(list, window);
        Rect tabBox = Bounds(page.FindControl<TabControl>("ArchiveContentTabs")!, window);
        Rect pad = Bounds(page.FindControl<WrapPanel>("ReaderNavigation")!, window);
        Rect visibleViewport = viewportBox.Intersect(listBox).Intersect(tabBox).Intersect(new Rect(default(Point), window.ClientSize));
        var items = list.GetVisualDescendants().OfType<Control>()
            .Where(control => (tiles ? control is ArchiveMediaTileView : control is DataGridRow)
                && control.IsEffectivelyVisible && control.DataContext is ArchiveAssetRow)
            .Select(control => Bounds(control, window).Intersect(visibleViewport)).ToArray();
        string[] controlNames = { "ArchiveMediaMode", "ArchiveMediaColumnsButton", "ArchiveFavoritesFilter", "ArchiveRatingFilter",
            "ArchiveMediaSort", "ArchiveVersionScope", "ArchiveEditRatingButton", "PreviewArchiveMediaButton" };
        var controls = controlNames.Select(name => page.FindControl<Control>(name)!)
            .Where(control => control.IsEffectivelyVisible)
            .Select(control => (Control: control, Box: Bounds(control, window))).ToArray();
        Console.WriteLine("SMOKE reader navigation media viewport: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            tiles, language = Texts.Language, theme = window.ActualThemeVariant.ToString(), scale = window.RenderScaling,
            client = window.ClientSize.ToString(), viewport = viewportBox.ToString(), visibleViewport = visibleViewport.ToString(),
            list = listBox.ToString(), items = items.Select(box => box.ToString()).ToArray(),
            controls = controls.Select(item => new { name = item.Control.Name, bounds = item.Box.ToString() }).ToArray()
        }));
        Require(list.IsEffectivelyVisible && ClippedSizeAtLeast(visibleViewport.Height, 100)
            && ClippedSizeAtLeast(visibleViewport.Width, 100),
            "Reader navigation/filter layout leaves less than 100 DIP of usable media viewport.");
        Require(items.Any(box => ClippedSizeAtLeast(box.Width, 100)
            && ClippedSizeAtLeast(box.Height, tiles ? 100 : 20)),
            "The media viewport has no usable visible asset tile or table row.");
        foreach (var item in controls)
        {
            Require(item.Box.Width > 0 && item.Box.Height > 0 && Contains(tabBox, item.Box)
                && Separated(item.Box, pad, 4),
                item.Control.Name + " is clipped or crowded by navigation on the small reader media tab.");
            Rect overlap = item.Box.Intersect(visibleViewport);
            Require(overlap.Width <= 1 || overlap.Height <= 1, item.Control.Name + " overlaps the media viewport.");
        }
        Require(controls.Any(item => item.Control.Name == "ArchiveEditRatingButton")
            && controls.Any(item => item.Control.Name == "PreviewArchiveMediaButton"),
            "Small reader media tab lost its rating or preview actions.");
        for (int first = 0; first < controls.Length; first++)
            for (int second = first + 1; second < controls.Length; second++)
            {
                Rect overlap = controls[first].Box.Intersect(controls[second].Box);
                Require(overlap.Width <= 1 || overlap.Height <= 1, "Small reader media controls overlap.");
            }
    }

    // Reader07: Rect.Intersect changed an exact 100-DIP viewport to
    // 99.99999999999994 DIP. Item intersections use the same subtraction.
    private static bool ClippedSizeAtLeast(double actual, double minimum) => actual >= minimum - GeometryEpsilonDip;

    private static void CheckToolbarGeometry(MainWindow window, ArchivePage page, WrapPanel toolbar)
    {
        Rect area = Bounds(toolbar, window);
        Rect client = new(default(Point), window.ClientSize);
        var buttons = toolbar.Children.OfType<Button>().Where(button => button.IsEffectivelyVisible)
            .Select(button => (Button: button, Box: Bounds(button, window)))
            .OrderBy(item => item.Box.Top).ThenBy(item => item.Box.Left).ToArray();
        Require(buttons.Length > 0, "Reader toolbar has no visible actions.");
        var rows = new List<List<Rect>>();
        foreach (var item in buttons)
        {
            Rect box = item.Box;
            Require(box.Width > 0 && Math.Abs(box.Height - 32) <= 1
                && Contains(area, box) && Contains(client, box),
                item.Button.Name + " is clipped or no longer a compact toolbar button.");
            if (rows.Count == 0 || Math.Abs(rows[^1][0].Top - box.Top) > 1) rows.Add(new List<Rect>());
            rows[^1].Add(box);
            if (item.Button.Content is Control content)
                Require(Contains(box, Bounds(content, window)), item.Button.Name + " clips its visible icon or label.");
        }
        Require(rows.Count <= 2 && (window.ClientSize.Width < 1304 || rows.Count == 1),
            "Reader toolbar needs more than two narrow rows or wraps in the wide layout.");
        foreach (List<Rect> row in rows)
        {
            row.Sort((left, right) => left.Left.CompareTo(right.Left));
            for (int index = 1; index < row.Count; index++)
                Require(Math.Abs(row[index].Left - row[index - 1].Right - 8) <= 1,
                    "Visible reader toolbar actions lost their 8-DIP horizontal spacing.");
        }
        if (rows.Count == 2)
            Require(Math.Abs(rows[1].Min(box => box.Top) - rows[0].Max(box => box.Bottom) - 6) <= 1,
                "Wrapped reader toolbar rows lost their 6-DIP vertical spacing.");
        Require(Math.Abs(area.Height - (rows.Count * 32 + (rows.Count - 1) * 6)) <= 1,
            "Reader toolbar reserves unexplained vertical space.");
        for (int first = 0; first < buttons.Length; first++)
            for (int second = first + 1; second < buttons.Length; second++)
            {
                Rect overlap = buttons[first].Box.Intersect(buttons[second].Box);
                Require(overlap.Width <= 1 || overlap.Height <= 1, "Reader toolbar actions overlap.");
            }
        Button history = page.FindControl<Button>("ArchiveHistoryBack")!;
        string expected = CommandCatalog.Label("reader.history-back");
        TextBlock? label = history.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => text.IsEffectivelyVisible && text.Text == expected);
        Require(history.IsEffectivelyVisible && history.IsEffectivelyEnabled && history.Focusable
            && label != null && label.Bounds.Width > 0 && label.DesiredSize.Width <= label.Bounds.Width + 1
            && Contains(Bounds(history, window), Bounds(label, window))
            && AutomationProperties.GetName(history) == expected
            && ToolTip.GetTip(history) as string == CommandBindings.Tooltip("reader.history-back"),
            "Reader history lacks its visible, unclipped localized label or accessible command semantics.");
        Console.WriteLine("SMOKE reader navigation toolbar: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            mode = Config.LoadConfig().WorkspaceMode, language = Texts.Language,
            theme = Application.Current!.RequestedThemeVariant?.ToString(), client = window.ClientSize.ToString(),
            scale = window.RenderScaling, bounds = area.ToString(), rows = rows.Count,
            buttons = buttons.Select(item => new { name = item.Button.Name, bounds = item.Box.ToString() }).ToArray(),
            historyLabel = label!.Text, historyLabelBounds = Bounds(label!, window).ToString()
        }));
    }

    private static bool Contains(Rect outer, Rect inner) => inner.Left >= outer.Left - 1
        && inner.Top >= outer.Top - 1 && inner.Right <= outer.Right + 1 && inner.Bottom <= outer.Bottom + 1;

    private static void CheckCrossGeometry(MainWindow window, ArchivePage page, Rect pad)
    {
        Rect up = Bounds(page.FindControl<Button>("ReaderPreviousPost")!, window);
        Rect down = Bounds(page.FindControl<Button>("ReaderNextPost")!, window);
        Rect left = Bounds(page.FindControl<Button>("ReaderPreviousThread")!, window);
        Rect right = Bounds(page.FindControl<Button>("ReaderNextThread")!, window);
        Require(Math.Abs(up.Center.X - down.Center.X) <= 1
            && Math.Abs(left.Center.Y - right.Center.Y) <= 1
            && Math.Abs(up.Center.X - (left.Center.X + right.Center.X) / 2) <= 1
            && Math.Abs(left.Center.Y - (up.Center.Y + down.Center.Y) / 2) <= 1
            && Math.Abs(up.Center.X - pad.Center.X) <= 1
            && Math.Abs(left.Center.Y - pad.Center.Y) <= 1,
            "Reader arrows do not share the centered vertical and horizontal axes of a cross.");
        Require(left.Center.X < up.Center.X && up.Center.X < right.Center.X
            && up.Center.Y < left.Center.Y && left.Center.Y < down.Center.Y
            && Math.Abs(left.Top - up.Bottom - 2) <= 1
            && Math.Abs(down.Top - left.Bottom - 2) <= 1
            && Math.Abs(up.Left - left.Right - 2) <= 1
            && Math.Abs(right.Left - up.Right - 2) <= 1,
            "Reader arrows lost their up / left-right / down arrangement or compact row/column gaps.");
        foreach (Rect button in new[] { up, down, left, right })
            Require(button.Left >= pad.Left && button.Top >= pad.Top
                && button.Right <= pad.Right && button.Bottom <= pad.Bottom,
                "A reader arrow lies outside the navigation cross.");
        Console.WriteLine("SMOKE reader navigation cross: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            previousPost = up.ToString(), nextPost = down.ToString(),
            previousThread = left.ToString(), nextThread = right.ToString()
        }));
    }

    private static void CheckPaginationGeometry(MainWindow window, ArchivePage page, Rect pageBar)
    {
        Button previous = page.FindControl<Button>("PreviousPostsButton")!;
        Button next = page.FindControl<Button>("NextPostsButton")!;
        TextBlock label = page.FindControl<TextBlock>("ArchivePostPage")!;
        Rect before = Bounds(previous, window), after = Bounds(next, window), text = Bounds(label, window);
        Rect available = Bounds((Control)page.FindControl<Grid>("ArchivePostNavigation")!.Parent!, window);
        Console.WriteLine("SMOKE reader navigation pagination: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            previous = before.ToString(), range = text.ToString(), next = after.ToString(),
            group = pageBar.ToString(), available = available.ToString(), label = label.Text
        }));
        Require(previous.IsEffectivelyVisible && next.IsEffectivelyVisible && label.IsEffectivelyVisible
            && !string.IsNullOrWhiteSpace(label.Text) && text.Width > 0,
            "Reader pagination lost its visible arrows or range label.");
        Require(Math.Abs(text.Left - before.Right - 8) <= 1
            && Math.Abs(after.Left - text.Right - 8) <= 1
            && Math.Abs(before.Center.Y - text.Center.Y) <= 1
            && Math.Abs(after.Center.Y - text.Center.Y) <= 1,
            "Reader page arrows are not immediately beside and aligned with the range label.");
        // Diagnostic06 measured only a 5.68e-14-DIP floating-point remainder at the right edge.
        Require(Math.Abs(text.Center.X - pageBar.Center.X) <= 1
            && Math.Abs(pageBar.Center.X - available.Center.X) <= 1
            && before.Left >= pageBar.Left - GeometryEpsilonDip
            && after.Right <= pageBar.Right + GeometryEpsilonDip,
            "Reader pagination is not a centered, contained group.");
    }

    private static Rect Bounds(Control control, Visual root) => new(control.TranslatePoint(default, root)
        ?? throw new InvalidOperationException(control.Name + " is detached."), control.Bounds.Size);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
