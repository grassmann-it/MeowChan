using System;
using System.Threading.Tasks;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

// P5B helper for SmokeScenarios integration. It is intentionally fixture-free: the parent runner supplies the existing
// synthetic reader thread, and may call this before/after its date/thumbnail screenshots.
internal static class ReaderDisplayChecks
{
    internal static Task RunAsync(MainWindow window)
    {
        ArchivePage page = window.ArchivePage;
        int oldWidth = page.ReaderThumbnailWidth;
        ReaderDateStyle oldStyle = page.ReaderDateStyle;
        try
        {
            Require(oldWidth == ReaderDisplayPreferences.ReaderThumbnailWidthDefault,
                "The reader display default changed before the P5B preference was applied.");
            page.ReaderThumbnailWidth = ReaderDisplayPreferences.ReaderThumbnailWidthMinimum - 1;
            Require(page.ReaderThumbnailWidth == ReaderDisplayPreferences.ReaderThumbnailWidthMinimum,
                "Reader thumbnail width did not clamp at its minimum.");
            page.ReaderDateStyle = ReaderDateStyle.RelativeToNow;
            Require(page.ReaderDateStyle == ReaderDateStyle.RelativeToNow, "Reader relative date style was not applied.");
            Require(ReaderDisplayPreferences.EffectiveThumbnailWidth(page.ReaderThumbnailWidth, true)
                == page.ReaderThumbnailWidth * 3 / 2, "Large-reader thumbnail scale is not 1.5x.");
        }
        finally
        {
            page.ReaderThumbnailWidth = oldWidth;
            page.ReaderDateStyle = oldStyle;
        }
        Console.WriteLine("SMOKE reader display: clamped thumbnail preference, 1.5x large-reader mapping and relative-date hook passed.");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
