using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// Registry smoke "dialog-gallery": opens every dialog/modal/secondary window with a realistic private
// fixture state and renders it per Light/Dark x EN/DE cell (DialogGallery). Each scene is isolated: a failure is
// listed under "Not captured" and the run goes on. Closing is always Cancel/Escape/Close, never a confirmation.
internal static partial class DialogGalleryChecks
{
    private static readonly (string Language, string Theme)[] Cells =
        [("en", "Light"), ("en", "Dark"), ("de", "Light"), ("de", "Dark")];

    private static readonly (string Group, string Name, Func<MainWindow, Task> Run)[] Scenes = BuildScenes();

    internal static async Task RunAsync(MainWindow owner)
    {
        string output = Environment.GetEnvironmentVariable("MEOWCHAN_DIALOG_GALLERY_OUTPUT") is { Length: > 0 } configured
            ? configured : Path.Combine(AppContext.BaseDirectory, "dialog-gallery");
        string only = Environment.GetEnvironmentVariable("MEOWCHAN_DIALOG_GALLERY_SCENES") ?? "";
        string[] filter = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string language = Texts.Language;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        DialogGallery.Begin(output);
        try
        {
            foreach ((string cellLanguage, string cellTheme) in Cells)
            {
                Texts.SetLanguage(cellLanguage);
                Application.Current.RequestedThemeVariant = cellTheme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
                DialogGallery.Theme = cellTheme;
                await DialogFrameChecks.Settle();
                foreach ((string group, string name, Func<MainWindow, Task> run) in Scenes)
                {
                    if (filter.Length > 0 && !filter.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    DialogGallery.Group = group;
                    try
                    {
                        await run(owner);
                        Console.WriteLine("SMOKE dialog gallery scene OK: " + name + " " + cellTheme + "/" + cellLanguage);
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine("SMOKE dialog gallery scene FAILED: " + name + " " + cellTheme + "/" + cellLanguage + ": " + exception);
                        DialogGallery.Skip(name, exception.GetType().Name + ": " + exception.Message.Split('\n')[0]);
                        await CloseStrayWindowsAsync(owner);
                    }
                }
            }
        }
        finally
        {
            Texts.SetLanguage(language);
            Application.Current.RequestedThemeVariant = theme;
            DialogGallery.End();
        }
        int count = DialogGallery.Finish("source-built harness run " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm") + " UTC");
        DialogFrameChecks.Require(count > 0, "The dialog gallery captured nothing.");
    }

    // A failed scene may leave its window open; later scenes must start from the bare main window.
    private static async Task CloseStrayWindowsAsync(MainWindow owner)
    {
        for (int round = 0; round < 3 && owner.OwnedWindows.Count > 0; round++)
        {
            foreach (Window window in owner.OwnedWindows.ToArray()) { try { window.Close(); } catch (InvalidOperationException) { } }
            await DialogFrameChecks.Settle();
        }
    }

    private static (string Group, string Name, Func<MainWindow, Task> Run)[] BuildScenes()
    {
        List<(string, string, Func<MainWindow, Task>)> scenes = [];
        foreach ((string name, Func<MainWindow, Task> run) in DialogFrameChecks.Groups)
            scenes.Add(("Dialogs: " + name, "frame-" + name, run));
        AddExtraScenes(scenes);
        return [.. scenes];
    }

    static partial void AddExtraScenes(List<(string Group, string Name, Func<MainWindow, Task> Run)> scenes);
}
