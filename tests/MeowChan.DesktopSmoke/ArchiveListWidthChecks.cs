using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Persistence;
using MeowChan.Testing;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

// The left list pane of the archive page opens wide enough for the complete English search placeholder next to the scope chip
// and the search button. The width is measured on the real page, with the text engine of the platform.
internal static class ArchiveListWidthChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task RunAsync(MainWindow owner)
    {
        string directory = Path.Combine(TemporaryWorkspacePaths.TempRoot, "archive-list-width-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string language = Texts.Language;
        ArchivePage? page = null;
        Window? host = null;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(directory);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory, lease);
            Texts.SetLanguage("en");
            page = new ArchivePage(token => ArchiveReader.OpenAsync(directory, token), (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            // Default window size of the app (1380 dip wide, the navigation rail beside the page).
            host = new Window { Title = "Archive list width — diagnostic", Width = 1380, Height = 850, Content = page };
            host.Show(owner);
            host.Activate();
            await Program.WaitUntilAsync(() => host.IsActive && page.IsEffectivelyVisible && page.Bounds.Width > 0);
            page.ArchiveSplitPreference = Config.ArchiveSplitWidthDefault;
            TextBox search = page.FindControl<TextBox>("ArchiveSearchText")!;
            Grid listPane = page.FindControl<Grid>("ArchiveListPane")!;
            Grid reader = page.FindControl<Grid>("ArchiveSplit")!;
            string placeholder = Texts.T("archive.searchHint");
            Require(placeholder == "Search local posts and titles…", "The English placeholder changed: " + placeholder);
            await Program.WaitUntilAsync(() => { host.UpdateLayout(); return search.IsEffectivelyVisible && search.Bounds.Width > 0; });
            TextBlock shown = search.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == placeholder);
            FormattedText measured = new(placeholder, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(shown.FontFamily, shown.FontStyle, shown.FontWeight, shown.FontStretch), shown.FontSize, Brushes.Black);
            double available = shown.Bounds.Width, needed = measured.Width;
            double pane = listPane.Bounds.Width;
            Console.WriteLine($"SMOKE archive list width: default={Config.ArchiveSplitWidthDefault}, pane={pane}, placeholderRoom={available}, placeholderText={needed}, " +
                $"requiredPane={pane - available + Math.Ceiling(needed)}, page={page.Bounds.Width}, detail={page.FindControl<Control>("ArchiveReaderPane")!.Bounds.Width}, window={host.Bounds.Width}.");
            Require(Math.Abs(pane - Config.ArchiveSplitWidthDefault) <= 1, $"The list pane is {pane} dip wide, not the default {Config.ArchiveSplitWidthDefault}.");
            Require(needed <= available + 0.5, $"The English search placeholder needs {needed} dip but only {available} are free at the default list width {pane}.");
            Require(page.FindControl<Control>("ArchiveReaderPane")!.Bounds.Width >= 480, "The detail pane is too narrow at the default window size.");
            Console.WriteLine("SMOKE archive list width: the English search placeholder fits completely at the default list width.");
        }
        finally
        {
            Texts.SetLanguage(language);
            page?.EndLifetime();
            host?.Close();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
