using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan
{
    // PR1 (profiles): the profile folder row of "Storage and limits". The folder is shown, never edited here: another
    // profile opens in a window of its own (⋯ › Profile), so this window's profile never changes underneath it.
    public partial class SettingsEditor
    {
        private async void OnOpenProfileFolderClick(object? sender, RoutedEventArgs e)
        {
            TextBlock? error = this.FindControl<TextBlock>("ProfileFolderOpenError");
            if (error != null) { error.Text = string.Empty; error.IsVisible = false; }
            string folder = ProfileFolderPath;
            if (string.IsNullOrWhiteSpace(folder)) return;
            try { await MeowChan.Desktop.ExternalLauncher.OpenAsync(folder); }
            catch (Exception exception)
            {
                // The exception text may name the path: it goes to the log, the row shows a sentence.
                Log.Warning(exception, "The profile folder could not be opened.");
                if (error != null) { error.Text = Texts.T("location.openFailed"); error.IsVisible = true; }
            }
        }
    }
}
