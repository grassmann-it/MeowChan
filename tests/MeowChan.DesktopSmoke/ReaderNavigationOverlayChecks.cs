using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

internal static class ReaderNavigationOverlayChecks
{
    internal static async Task RunAsync(MainWindow window, ArchivePage page)
    {
        string mode = Config.LoadConfig().WorkspaceMode;
        string language = Texts.Language;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        double width = window.Width, height = window.Height;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        try
        {
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
            await PresentationChecks.SwitchModeAsync(window, "Simple");
            foreach (double transitionWidth in new[] { 1346d, 1350d })
            {
                window.Width = transitionWidth; window.Height = 500;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, transitionWidth, 500));
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                CheckGeometry(window, page);
                Thickness reserved = page.FindControl<ListBox>("ArchivePosts")!.Margin;
                Require(transitionWidth < 1350 ? reserved.Bottom > 0 && reserved.Right == 0 : reserved.Right > 0 && reserved.Bottom == 0,
                    "Reader navigation failed to adapt on both sides of its available-width threshold.");
            }
            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            window.Width = 640; window.Height = 480;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 640, 480));
            tabs.SelectedIndex = 1;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(page.FindControl<WrapPanel>("ReaderNavigation")!.IsEffectivelyVisible,
                "Reader navigation disappeared on the saved-media tab.");
            Rect navigation = Bounds(page.FindControl<WrapPanel>("ReaderNavigation")!, window);
            Require(Bounds(tabs, window).Bottom + 4 <= navigation.Top,
                "Saved-media controls can sit beneath reader navigation.");
            tabs.SelectedIndex = 2;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(page.FindControl<WrapPanel>("ReaderNavigation")!.IsEffectivelyVisible,
                "Reader navigation disappeared on the references tab.");
            tabs.SelectedIndex = 0;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
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
            await page.FocusPostAsync("1000");
            window.UpdateLayout();
        }
        Console.WriteLine("SMOKE reader navigation overlay: 16 EN/DE, Dark/Light, large and B/S/A minimum geometries plus both adaptive threshold edges; scrolled text, scrollbar/pagebar spacing, single-row toolbar, four accessible commands, retained secondary tabs and dialog hit-test passed.");
    }

    private static bool HasRenderedPost(ArchivePage page) => page.FindControl<ListBox>("ArchivePosts")!
        .GetVisualDescendants().OfType<ListBoxItem>().Any(item => item.IsEffectivelyVisible && item.Bounds.Width > 0);

    private static void CheckGeometry(MainWindow window, ArchivePage page)
    {
        WrapPanel navigation = page.FindControl<WrapPanel>("ReaderNavigation")!;
        Rect pad = Bounds(navigation, window);
        Rect surface = Bounds(page.FindControl<Grid>("ReaderContentSurface")!, window);
        Rect pageBar = Bounds(page.FindControl<Grid>("ArchivePostNavigation")!, window);
        Require(navigation.IsEffectivelyVisible && pad.Width <= 160 && pad.Height <= 44,
            "Reader navigation is no longer a compact visible pad.");
        Require(pad.Right <= surface.Right - 20 && pad.Bottom <= pageBar.Top - 7
            && pad.Bottom >= pageBar.Top - 10 && pad.Top >= surface.Top,
            "Reader navigation is not inset at the lower right above the page bar.");
        WrapPanel toolbar = page.FindControl<WrapPanel>("ReaderToolbar")!;
        Require(toolbar.Bounds.Height <= 33, "Reader toolbar is no longer one compact row.");
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
                && AutomationProperties.GetName(button) == CommandCatalog.Label(command.Id)
                && ToolTip.GetTip(button) as string == CommandBindings.Tooltip(command.Id),
                command.Name + " lost its stable visible button or localized command semantics.");
        }
    }

    private static Rect Bounds(Control control, Visual root) => new(control.TranslatePoint(default, root)
        ?? throw new InvalidOperationException(control.Name + " is detached."), control.Bounds.Size);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
