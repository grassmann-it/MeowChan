using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// LY-03 (review of 17.11.2026, run inside the footer-navigation smoke): every workspace page starts its title at the same distance from the
/// top of the page area. At one window size (1380 × 850 where the screen allows it) each page is opened, its visible <c>page-title</c> is
/// located in window coordinates, and all of them must stand within one dip of each other (the shell above the pages is the same, so the
/// distance from the page area's top, <see cref="PageHeader.PageTopOffset"/>, is the same). The review measured 60 (Media, Tagging), 64 and 65
/// (most) and 77 (Rules).
/// </summary>
internal static class PageTitleOffsetChecks
{
    private const double Tolerance = 1;

    // A page whose title is known to stand elsewhere until a named change lands; it is reported, never silently accepted. Media: its header
    // still has the top margin 8 (Desktop/Views/Media/MediaPage.cs, `new Thickness(12, 8, 12, 8)`) instead of PageHeader.PageTopOffset; branch
    // fix/media-page-entry-jumps-20261117 aligns it, and this entry goes with that merge.
    private static readonly Dictionary<string, string> KnownExceptions = new()
    {
        [nameof(MainWindow.WorkspacePage.Media)] = "aligned by fix/media-page-entry-jumps-20261117",
    };

    private static readonly MainWindow.WorkspacePage[] Pages =
    [
        MainWindow.WorkspacePage.Downloads, MainWindow.WorkspacePage.Archive, MainWindow.WorkspacePage.Media, MainWindow.WorkspacePage.Tagging,
        MainWindow.WorkspacePage.Rules, MainWindow.WorkspacePage.Sources, MainWindow.WorkspacePage.Status, MainWindow.WorkspacePage.Settings
    ];

    internal static async Task RunAsync(MainWindow window)
    {
        double widthBefore = window.Width, heightBefore = window.Height;
        string modeBefore = window.SessionHost.Config.WorkspaceMode;
        try
        {
            // Every page shows its title in the Advanced view; Barebones leaves the Downloads heading out on purpose (WorkViewPolicy.Downloads,
            // "downloads.header.heading": the selected footer destination names the page), so the offsets are compared where all titles stand.
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window)
                ?? throw new InvalidOperationException("The page title smoke has no screen.");
            double frame = Math.Max(0, (window.FrameSize?.Width ?? window.ClientSize.Width) - window.ClientSize.Width);
            double width = Math.Min(1380, Math.Floor(screen.WorkingArea.Width / window.RenderScaling - frame));
            await ResizeAsync(window, Math.Max(window.MinWidth, width), Math.Max(window.MinHeight, 850));

            List<string> failures = [];
            List<(string Page, double Top)> tops = [];
            foreach (MainWindow.WorkspacePage page in Pages)
            {
                await SmokeNavigation.GoToAsync(window, page);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                TextBlock[] titles = window.GetVisualDescendants().OfType<TextBlock>()
                    .Where(block => block.Classes.Contains("page-title") && block.IsEffectivelyVisible && block.Bounds.Width > 0
                        && TopLevel.GetTopLevel(block) == window).ToArray();
                if (titles.Length == 0) { failures.Add(page + ": no page title is visible"); continue; }
                // A page may show a second title further down (a hosted page); the page's own title is the topmost one.
                TextBlock title = titles.OrderBy(block => block.TranslatePoint(default, window)?.Y ?? double.MaxValue).First();
                double top = title.TranslatePoint(default, window)?.Y ?? double.NaN;
                tops.Add((page.ToString(), top));
                Console.WriteLine($"SMOKE page title offset {page}: title top {top:0.#}");
            }
            (string Page, double Top)[] strict = tops.Where(item => !KnownExceptions.ContainsKey(item.Page)).ToArray();
            if (strict.Length > 1 && strict.Max(item => item.Top) - strict.Min(item => item.Top) > Tolerance)
                failures.Add("the page titles do not start at one height: " + string.Join(", ", tops.Select(item => $"{item.Page} {item.Top:0.#}")));
            foreach ((string name, string why) in KnownExceptions)
            {
                int index = tops.FindIndex(item => item.Page == name);
                if (strict.Length == 0 || index < 0) continue;
                (string Page, double Top) known = tops[index];
                bool aligned = Math.Abs(known.Top - strict[0].Top) <= Tolerance;
                Console.WriteLine($"SMOKE page title offset {name}: known exception, title top {known.Top:0.#} against {strict[0].Top:0.#} "
                    + (aligned ? "(already aligned: remove the exception)" : "(" + why + ")"));
            }
            if (failures.Count > 0)
                throw new InvalidOperationException("Page title offsets failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
            Console.WriteLine("SMOKE page title offset: " + strict.Length + " pages start their title at " + strict[0].Top.ToString("0.#") + " dip"
                + (KnownExceptions.Count > 0 ? "; known exception(s): " + string.Join(", ", KnownExceptions.Keys) : "") + ".");
        }
        finally
        {
            await ResizeAsync(window, widthBefore, heightBefore);
            await PresentationChecks.SwitchModeAsync(window, modeBefore);
        }
    }

    /// <summary>
    /// Requests a client size and waits until the window has it. Setting <c>Width</c> to the value it already holds raises no
    /// change, so the platform never hears it: the footer smoke before this one leaves the property at its restored value while its last native
    /// resize (1400 × 600) is still what the window has, and a plain set-and-wait then waited forever. A request equal to the property is
    /// therefore nudged by one dip first, so every request reaches the platform.
    /// </summary>
    private static async Task ResizeAsync(Window window, double width, double height)
    {
        if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
        if (Math.Abs(window.Width - width) < 0.5) window.Width = width + 1;
        if (Math.Abs(window.Height - height) < 0.5) window.Height = height + 1;
        window.Width = width;
        window.Height = height;
        await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - width) <= 1 && Math.Abs(window.Bounds.Height - height) <= 1,
            () => $"bounds={window.Bounds.Size}; requested={width}x{height}; minimum={window.MinWidth}x{window.MinHeight}; state={window.WindowState}");
    }
}
