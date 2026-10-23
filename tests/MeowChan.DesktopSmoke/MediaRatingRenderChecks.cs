using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MeowChan.DesktopSmoke;

internal static partial class MediaRatingChecks
{
    private static Border? StarFill(Control character) => character.GetVisualDescendants()
        .OfType<Border>().SingleOrDefault(border => border.Name == "PART_IconBorder");

    private static async Task EditorReady(Window dialog)
    {
        Control editor = Editor(dialog);
        // Ursa 2.2.0 ignores Value changes before Loaded and initializes fill from DefaultValue
        // on Loaded. Wait for the actual lifecycle and template, without modifying product state.
        await Program.WaitUntilAsync(() => editor.IsLoaded && editor.IsEffectivelyVisible
            && editor.Bounds.Width > 0 && editor.Bounds.Height > 0
            && (editor is not Ursa.Controls.Rating star || star.Items.Count == 5
                && star.Items.All(character => character.IsLoaded && character.IsEffectivelyVisible
                    && character.Bounds.Width > 0 && character.Bounds.Height > 0 && StarFill(character) != null)));
        await Dispatcher.UIThread.InvokeAsync(dialog.UpdateLayout, DispatcherPriority.Render);
    }

    private static async Task AssertEditorRender(Window dialog, int expected, string phase)
    {
        await EditorReady(dialog);
        Control editor = Editor(dialog);
        if (editor is not Ursa.Controls.Rating star)
        {
            Require(Draft(dialog) == expected, phase + ": numeric selection differs from expected rating");
            return;
        }
        var characters = star.Items.Select((character, index) =>
        {
            Border fill = StarFill(character)!;
            bool selected = index < expected;
            bool fullWidth = Math.Abs(fill.Bounds.Width - character.Bounds.Width) <= 0.5;
            return new
            {
                index, expectedSelected = selected, character.IsLoaded, character.IsPointerOver,
                bounds = character.Bounds.ToString(), fill.IsVisible, fill.IsEffectivelyVisible,
                fillBounds = fill.Bounds.ToString(), fillWidth = fill.Bounds.Width,
                characterWidth = character.Bounds.Width,
                matches = selected ? fill.IsVisible && fill.IsEffectivelyVisible && fullWidth && fill.Bounds.Height > 0
                    : !fill.IsVisible && !fill.IsEffectivelyVisible
            };
        }).ToArray();
        bool result = star.Value == expected && characters.Length == 5 && characters.All(character => character.matches);
        var snapshot = new
        {
            sequence = Evidence.Count + 1, assertion = "Materialized star foreground fill matches " + phase,
            phase, expected, result, star.Value, star.DefaultValue, star.IsLoaded, star.IsPointerOver,
            actualFilledCount = characters.Count(character => character.IsEffectivelyVisible),
            label = Find<TextBlock>(dialog, "MediaRatingValue").Text, characters,
            inputScope = "rendered template inspection after Loaded and Render-priority layout; no native input or human preference claim"
        };
        Evidence.Add(snapshot);
        Console.WriteLine("URSA16 star-render: " + JsonSerializer.Serialize(snapshot));
        SaveEvidence(result ? "running" : "failed rendered star assertion");
        if (!result) Program.CaptureWindow(dialog, "smoke-rating-star-render-failure.png");
        Require(result, phase + ": actual visible star foreground fill differs from expected " + expected);
    }
}
