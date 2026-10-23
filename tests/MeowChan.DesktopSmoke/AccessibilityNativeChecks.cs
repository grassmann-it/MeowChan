using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Admission;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

/// <summary>Holds real controls while a separate native AT-SPI client inspects and activates them.</summary>
internal static class AccessibilityNativeChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string exchange = Environment.GetEnvironmentVariable("MEOWCHAN_ACCESSIBILITY_EXCHANGE")
            ?? throw new InvalidOperationException("Native accessibility probe requires an explicit private exchange directory.");
        if (!Path.IsPathFullyQualified(exchange) || !Directory.Exists(exchange))
            throw new InvalidOperationException("Native accessibility exchange directory must already exist at an absolute path.");
        bool numericOnly = Environment.GetEnvironmentVariable("MEOWCHAN_ACCESSIBILITY_NUMERIC_ONLY") == "1";
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            if (!numericOnly)
            {
                await CheckpointAsync("main-" + language);
                MenuItem menu = window.FindControl<MenuItem>("WorkspaceMenuItem")!;
                menu.IsSubMenuOpen = true;
                await CheckpointAsync("menu-" + language);
                menu.IsSubMenuOpen = false;
            }
            window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => window.SettingsPageVisible);
            if (Environment.GetEnvironmentVariable("MEOWCHAN_URSA_NATIVE_SUITE") == "shortcuts")
            {
                await NativeFeatureCheckpoint.ShortcutsAsync(window.SettingsEditor);
                window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !window.SettingsPageVisible);
                continue;
            }
            if (numericOnly)
            {
                foreach (string field in new[] { "VideoVolumeInput", "RecheckTimerInput", "BandwidthInput", "ParallelDownloadsInput" })
                {
                    window.SettingsEditor.FindControl<NumericUpDown>(field)!.BringIntoView();
                    await CheckpointAsync("numeric-" + field + "-" + language);
                }
                window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !window.SettingsPageVisible);
                continue;
            }
            await CheckpointAsync("settings-" + language);
            window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !window.SettingsPageVisible);

            ArchiveFavoriteButton favorite = new();
            favorite.Configure(() => true, () => false, () => throw new IOException("Controlled favorite write failure"));
            Window favoriteWindow = new()
            {
                Title = "Favorite accessibility fixture", Width = 480, Height = 240,
                Content = new StackPanel { Margin = new Avalonia.Thickness(24), Children =
                { new TextBlock { Text = Texts.T("viewer.favorite.add") }, favorite } }
            };
            Task favoriteClosed = favoriteWindow.ShowDialog(window);
            await CheckpointAsync("favorite-ready-" + language);
            await Program.WaitUntilAsync(() => AutomationProperties.GetName(favorite)!.Contains("Controlled favorite write failure", StringComparison.Ordinal));
            await CheckpointAsync("favorite-error-" + language);
            favoriteWindow.Close(); await favoriteClosed;

            const string board = "https://boards.4chan.org/g/";
            const string other = "https://8kun.top/tech/";
            BoardAdmissionPolicy policy = new([new(ThreadTextField.Title, ThreadTextAction.Include, "cat")]);
            BoardAdmissionWindow modal = new([board, other], _ => policy, (url, _) => Task.FromResult(
                new BoardAdmissionPreview(url, "Offline accessibility fixture", policy,
                    [new("1", url + "thread/1", "cat fixture", "Controlled catalog text")])), true);
            Task<BoardAdmissionSelection?> selection = modal.ShowDialog<BoardAdmissionSelection?>(window);
            await Program.WaitUntilAsync(() => modal.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "BoardAdmissionApply" && button.IsEnabled));
            await CheckpointAsync("board-ready-" + language);
            if (selection.IsCompleted || modal.GetVisualDescendants().OfType<TextBox>().Any(text => text.Name == "BoardAdmissionRuleText"))
                throw new InvalidOperationException("Native client did not perform the local board edit.");
            await CheckpointAsync("board-edited-" + language);
            modal.Close();
            if (await selection != null) throw new InvalidOperationException("Native probe committed a board draft.");

            BoardAdmissionWindow failed = new([board], _ => policy,
                (_, _) => Task.FromException<BoardAdmissionPreview>(new IOException("Controlled catalog failure")), true);
            Task<BoardAdmissionSelection?> failure = failed.ShowDialog<BoardAdmissionSelection?>(window);
            await Program.WaitUntilAsync(() => failed.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "BoardAdmissionRetry" && button.IsVisible));
            await CheckpointAsync("board-error-" + language);
            failed.Close(); await failure;
        }
        Texts.SetLanguage("en");
        Console.WriteLine("SMOKE native accessibility fixture stages complete. Native assertions and Orca speech evidence belong to the external client logs.");

        async Task CheckpointAsync(string id)
        {
            window.UpdateLayout();
            File.WriteAllText(Path.Combine(exchange, "stage.json"), JsonSerializer.Serialize(new { id, language = Texts.Language }));
            Console.WriteLine("ACCESSIBILITY STAGE: " + id);
            DateTime deadline = DateTime.UtcNow.AddSeconds(35);
            while (!File.Exists(Path.Combine(exchange, id + ".done")))
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("Native accessibility client did not acknowledge " + id);
                await Task.Delay(100);
            }
        }
    }
}
