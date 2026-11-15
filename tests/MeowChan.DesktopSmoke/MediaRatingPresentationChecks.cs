using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

/// <summary>Optional 12-cell presentation pass at the executor's scale; no rating mutations.</summary>
internal static partial class MediaRatingChecks
{
    private static void RequireInsideWindow(Window window, Control control, string intent)
    {
        Point? origin = control.TranslatePoint(default, window);
        Require(control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 && origin.HasValue,
            intent + " must be a materialized visible control");
        Point point = origin!.Value;
        Require(point.X >= -1 && point.Y >= -1 && point.X + control.Bounds.Width <= window.ClientSize.Width + 1
            && point.Y + control.Bounds.Height <= window.ClientSize.Height + 1,
            intent + " lies outside the actual dialog client area");
    }
    private static async Task RunPresentationMatrix()
    {
        Config original = _window.SessionHost.Config.Clone();
        string originalLanguage = Texts.Language;
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        string initialRatings = Digest(RatingPath);
        int cells = 0;
        try
        {
            // Choose once before the matrix; every language transition must preserve both choices.
            SetChoice(_page.FindControl<ComboBox>("ArchiveVersionScope")!, 1, "Keep all saved versions across presentation languages");
            SetChoice(_page.FindControl<ComboBox>("ArchiveMediaSort")!, 2, "Keep non-default version sort across presentation languages");
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            foreach (string language in new[] { "en", "de" })
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                int width = mode == "Barebones" ? 640 : 760, height = mode == "Barebones" ? 480 : 500;
                await PreparePresentationCell(mode, language, theme, width, height);
                if (!Tiles.IsEffectivelyVisible)
                {
                    Click(_page.FindControl<Button>("ArchiveMediaMode")!, "Expose actual media tiles in presentation cell");
                    await Program.WaitUntilAsync(() => Tiles.IsEffectivelyVisible);
                }
                SetChoice(Filter, 0, "Keep presentation cell on all ratings");
                string scale = _window.RenderScaling.ToString("0.##", CultureInfo.InvariantCulture);
                string cell = mode.ToLowerInvariant() + "-" + language + "-" + theme.ToString().ToLowerInvariant() + "-" + scale + "x";
                QuickTagPopover menu = await OpenRatingMenu(Row(2));
                RequireMenuShows(menu, 3, "presentation cell");
                Button clear = RatingItem(menu, null);
                Require(clear.IsEffectivelyEnabled && menu.DecisionRow!.StarButtons.All(star => !string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(star))),
                    "Remove rating must be genuinely available and every star labelled in the current language");
                Program.CaptureWindow(_window, "smoke-rating-matrix-" + cell + "-owner.png");
                await CloseRatingMenu(menu);
                Require(Digest(RatingPath) == initialRatings && Rating(Row(2)) == 3, "Presentation menu changed the durable rating");
                await ManagerCancelFocus("smoke-rating-matrix-" + cell + "-manager-focus.png");
                Require(Digest(RatingPath) == initialRatings, "Presentation manager cancel changed rating bytes");
                cells++;
                Evidence.Add(new { presentationCell = cell, result = true, requestedClientDip = new { width, height },
                    actualClientDip = _window.ClientSize.ToString(), actualMode = _window.SessionHost.Config.WorkspaceMode,
                    actualLanguage = Texts.Language, actualTheme = _window.ActualThemeVariant.ToString(), actualScale = _window.RenderScaling,
                    ratingFileSha256 = Digest(RatingPath), scope = "real presentation/focus controls, no rating write; no human/native-AT claim" });
                SaveEvidence("running presentation matrix");
            }
            Require(cells == 12, "Presentation matrix must complete all twelve cells");
            Console.WriteLine("SMOKE URSA16 presentation: 12 actual EN/DE × Light/Dark × Barebones/Simple/Advanced cells passed at scale " + _window.RenderScaling);
        }
        finally
        {
            await SelectPresentationMode(original.WorkspaceMode);
            await _window.SessionHost.SaveViewPreferencesAsync(config =>
            { config.Language = original.Language; config.Theme = original.Theme; });
            Texts.SetLanguage(originalLanguage); Application.Current.RequestedThemeVariant = originalTheme;
        }
    }
}
