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

    private static readonly MainWindow.WorkspacePage[] Pages =
    [
        MainWindow.WorkspacePage.Downloads, MainWindow.WorkspacePage.Archive, MainWindow.WorkspacePage.Media, MainWindow.WorkspacePage.Tagging,
        MainWindow.WorkspacePage.Rules, MainWindow.WorkspacePage.Sources, MainWindow.WorkspacePage.Status, MainWindow.WorkspacePage.Settings
    ];

    internal static async Task RunAsync(MainWindow window)
    {
        double widthBefore = window.Width, heightBefore = window.Height;
        try
        {
            Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window)
                ?? throw new InvalidOperationException("The page title smoke has no screen.");
            double frame = Math.Max(0, (window.FrameSize?.Width ?? window.ClientSize.Width) - window.ClientSize.Width);
            double width = Math.Min(1380, Math.Floor(screen.WorkingArea.Width / window.RenderScaling - frame));
            window.Width = Math.Max(window.MinWidth, width);
            window.Height = Math.Max(window.MinHeight, 850);
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - window.Width) <= 1);

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
            if (tops.Count > 1 && tops.Max(item => item.Top) - tops.Min(item => item.Top) > Tolerance)
                failures.Add("the page titles do not start at one height: " + string.Join(", ", tops.Select(item => $"{item.Page} {item.Top:0.#}")));
            if (failures.Count > 0)
                throw new InvalidOperationException("Page title offsets failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
            Console.WriteLine("SMOKE page title offset: " + tops.Count + " pages start their title at " + tops[0].Top.ToString("0.#") + " dip.");
        }
        finally
        {
            window.Width = widthBefore;
            window.Height = heightBefore;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        }
    }
}
