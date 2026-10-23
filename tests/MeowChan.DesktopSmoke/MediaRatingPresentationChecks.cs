using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

/// <summary>Optional 12-cell presentation pass at the executor's scale; no successful rating mutations.</summary>
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
    private static void RequireLocalizedRatingError(Window dialog, bool removing)
    {
        const string cause = "The ratings index path is not a regular file.";
        string operation = Texts.Language == "de"
            ? removing ? "Bewertung konnte nicht entfernt werden." : "Bewertung konnte nicht gespeichert werden."
            : removing ? "Could not remove the rating." : "Could not save the rating.";
        string expected = operation + "\nDetails: " + cause;
        string? actual = Find<TextBlock>(dialog, "MediaRatingError").Text;
        bool result = actual == expected;
        Evidence.Add(new { sequence = Evidence.Count + 1,
            ratingErrorPresentation = removing ? "failed Clear" : "failed Save", language = Texts.Language,
            expectedOperation = operation, retainedTechnicalCause = cause, actualText = actual, result });
        SaveEvidence(result ? "running localized rating error checks" : "failed localized rating error check");
        Require(result, "Rating error must show the exact localized operation and unchanged technical cause as Details");
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
                MediaRatingDialog dialog = await OpenStar(Row(2));
                Control editor = Editor(dialog);
                Button clear = Find<Button>(dialog, "MediaRatingClear");
                await PreparePresentationDialog(dialog, editor, cell);
                RequireInsideWindow(dialog, editor, "Star editor"); RequireInsideWindow(dialog, clear, "Clear action");
                Require(clear.IsEffectivelyEnabled && AutomationProperties.GetName(clear) == CommandCatalog.Label("media.rating.clear"),
                    "Clear must be genuinely available and labelled in the current language");
                Require(Find<TextBlock>(dialog, "MediaRatingValue").Text == MediaRatingText.Value(3), "Initial rating label must follow language");
                Program.CaptureWindow(_window, "smoke-rating-matrix-" + cell + "-owner.png");
                Program.CaptureWindow(dialog, "smoke-rating-matrix-" + cell + "-star-clear.png");
                string backup = RatingPath + ".bak", parked = backup + ".presentation-parked";
                Log("Keep rating bytes unchanged while displaying real commit error", "disposable fixture backup-path obstruction");
                if (File.Exists(backup)) File.Move(backup, parked);
                Directory.CreateDirectory(backup);
                try
                {
                    await Choose(dialog, 4);
                    Press(editor, Key.Enter, "Attempt rating commit for real error presentation");
                    await Program.WaitUntilAsync(() => Find<Button>(dialog, "MediaRatingRetry").IsVisible && editor.IsEnabled);
                    await AssertEditorRender(dialog, 3, "presentation failed save restored confirmed rating");
                    RequireLocalizedRatingError(dialog, removing: false);
                    Require(Rating(Row(2)) == 3 && Digest(RatingPath) == initialRatings, "Presentation failed commit changed durable rating");
                    // Clear is also exercised through its actual command under the same failed-write condition.
                    Click(clear, "Exercise actual Clear with durable error and unchanged value");
                    await Program.WaitUntilAsync(() => Find<Button>(dialog, "MediaRatingRetry").IsVisible && editor.IsEnabled);
                    await AssertEditorRender(dialog, 3, "presentation failed Clear restored confirmed rating");
                    RequireLocalizedRatingError(dialog, removing: true);
                    TextBlock error = Find<TextBlock>(dialog, "MediaRatingError");
                    Require(!string.IsNullOrWhiteSpace(error.Text), "Presentation commit error is missing");
                    RequireInsideWindow(dialog, error, "Commit error"); RequireInsideWindow(dialog, Find<Button>(dialog, "MediaRatingRetry"), "Retry action");
                    Require(editor.Focus(NavigationMethod.Tab) && ReferenceEquals(dialog.FocusManager?.GetFocusedElement(), editor),
                        "Error presentation must retain reachable editor focus");
                    Program.CaptureWindow(dialog, "smoke-rating-matrix-" + cell + "-error.png");
                    Observe("Presentation cell failed Set/Clear preserves same exact rating and file", Rating(Row(2)) == 3 && Digest(RatingPath) == initialRatings);
                }
                finally
                {
                    Directory.Delete(backup);
                    if (File.Exists(parked)) File.Move(parked, backup);
                }
                await Close(dialog);
                await ManagerCancelFocus("smoke-rating-matrix-" + cell + "-manager-focus.png");
                Require(Digest(RatingPath) == initialRatings, "Presentation manager cancel changed rating bytes");
                cells++;
                Evidence.Add(new { presentationCell = cell, result = true, requestedClientDip = new { width, height },
                    actualClientDip = _window.ClientSize.ToString(), actualMode = _window.SessionHost.Config.WorkspaceMode,
                    actualLanguage = Texts.Language, actualTheme = _window.ActualThemeVariant.ToString(), actualScale = _window.RenderScaling,
                    ratingFileSha256 = Digest(RatingPath), scope = "real presentation/focus/error controls, no successful rating write; no human/native-AT claim" });
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
