using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Views.Shortcuts;

namespace MeowChan.DesktopSmoke;

// Opt-in probe for the gallery's Help tool (KS3): it no longer shows a panel inside the gallery but opens the one modeless
// shortcuts window on the Gallery group, with the tag panel's group right after it and the way-back note. The window is
// no blocking dialog: the gallery keeps routing its own shortcuts while it is open.
internal static class GalleryHelpReviewChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        bool strict = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_HELP_REVIEW") == "1";
        List<string> failures = new();
        foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
        {
            await PresentationChecks.SwitchModeAsync(window, mode);
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                double width = mode == "Barebones" ? 640 : 760;
                double height = mode == "Barebones" ? 480 : 500;
                window.Width = width; window.Height = height;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
                await window.OpenGalleryAsync(new ResourceId("4chan", "g", "1000"));
                ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
                await Program.WaitUntilAsync(() => host.Content is GalleryViewer current && current.GetGalleryVisualDescendants()
                    .OfType<Image>().Any(image => image.Name == "GalleryImage" && image.Source is Bitmap));
                GalleryViewer viewer = (GalleryViewer)host.Content!;
                string scene = mode + "-" + language + "-" + width + "x" + height;
                Button helpAction = Find<Button>(viewer, "GalleryHelp");
                helpAction.Focus(); Click(helpAction);
                await Program.WaitUntilAsync(() => ShortcutsWindow.For(window) is { IsVisible: true } opened && opened.Editor.ActionList.IsEffectivelyVisible);
                ShortcutsWindow help = ShortcutsWindow.For(window)!;
                string[] groups = help.Editor.Model.Query(new ShortcutTableQuery()).Select(group => group.Key).ToArray();
                if (help.Editor.Context.Area != ShortcutGroups.Gallery || groups.Length < 2 || groups[0] != ShortcutGroups.Gallery
                    || groups[1] != ShortcutGroups.TagPanel) failures.Add(scene + ": the window did not open on Gallery then Tag panel");
                if (help.Editor.CollapsedGroups.Contains(ShortcutGroups.Gallery) || help.Editor.CollapsedGroups.Contains(ShortcutGroups.TagPanel))
                    failures.Add(scene + ": Gallery or Tag panel group is collapsed");
                if (!help.PlainText.Contains(Texts.T("viewer.help.escape"), StringComparison.Ordinal))
                    failures.Add(scene + ": the way-back note is missing");
                if (window.OwnedWindows.Any(owned => owned.IsVisible && !ShortcutsWindow.IsPassive(owned)))
                    failures.Add(scene + ": a blocking dialog is open next to the shortcuts window");
                Console.WriteLine("SMOKE gallery-help window: " + JsonSerializer.Serialize(new { scene, groups = groups.Take(3), area = help.Editor.Context.Area,
                    client = help.ClientSize.ToString(), scale = help.RenderScaling }));
                Program.CaptureWindow(help, "gallery-help-" + scene + "-window.png");
                // F1 in the gallery toggles the same window shut.
                viewer.Focus();
                Press(viewer, Avalonia.Input.Key.F1);
                await Program.WaitUntilAsync(() => ShortcutsWindow.For(window) == null);
                Click(Find<Button>(viewer, "GalleryClose"));
                await window.GalleryCleanupTask;
            }
        }
        Console.WriteLine("SMOKE gallery-help result: " + JsonSerializer.Serialize(new { strict, failures }));
        if (strict && failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures.Distinct()));
        Console.WriteLine("SMOKE gallery-help: Help tool and F1 open/close the shortcuts window on the Gallery group; in-process, no OS input or AT claim.");
    }

    private static T Find<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Press(Control source, Avalonia.Input.Key key) => source.RaiseEvent(new Avalonia.Input.KeyEventArgs
        { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key });
}
