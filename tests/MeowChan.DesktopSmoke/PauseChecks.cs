using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class PauseChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Button button = window.FindControl<Button>("PauseButton")!;
        MenuItem menu = window.FindControl<MenuItem>("PauseMenuItem")!;
        if (window.SessionHost.IsPaused) await window.SessionHost.SetPausedAsync(false);
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            await Program.WaitUntilAsync(() => button.Content?.ToString() == Texts.T("action.pauseShort") && button.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => window.SessionHost.IsPaused && button.IsEnabled);
            if (window.FindControl<TextBlock>("WorkspaceStatus")!.Text != Texts.T("shell.paused"))
                throw new InvalidOperationException("The workspace does not show its paused state.");
            if (!Config.LoadConfig().DownloadsPaused || button.Content?.ToString() != Texts.T("action.resumeShort")
                || menu.Header?.ToString() != Texts.T("action.resume"))
                throw new InvalidOperationException("Pause is not durably and visibly reflected by the actual shell controls.");
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => !window.SessionHost.IsPaused && button.IsEnabled);
            if (Config.LoadConfig().DownloadsPaused) throw new InvalidOperationException("Resume was not persisted.");
        }
        Texts.SetLanguage("en");
    }
}
