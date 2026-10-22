using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Rules;

namespace MeowChan.DesktopSmoke;

internal static class PowerUserCommandChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string originalMode = Config.LoadConfig().WorkspaceMode;
        string originalLanguage = Texts.Language;
        string path = window.SettingsEditor.DownloadPath;
        TextBox url = window.FindControl<TextBox>("UrlInput")!;
        string? originalUrl = url.Text;
        RulesPage rules = window.RulesPage;
        MediaRuleScope originalScope = rules.ActiveScope;
        MediaRuleScope draftScope = MediaRuleScope.ForThread("shortcut-fixture", "unknown", "42");
        rules.EditScope(draftScope);
        rules.TypesMode = (int)MediaTypeSelectionMode.Selected;
        rules.ExtensionsText = "image/jpeg";
        window.SettingsEditor.DownloadPath = "retained shortcut settings draft";
        url.Text = "retained shortcut URL draft";
        try
        {
            NativeMenuItem clipboardTrayAction = new NativeMenuItem();
            CommandBindings.Apply(clipboardTrayAction, "watch.import-clipboard");
            Require(clipboardTrayAction.Gesture == null, "The native tray registered a Downloads-only paste accelerator globally.");
            (clipboardTrayAction.Icon as IDisposable)?.Dispose();
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                foreach ((string id, MainWindow.WorkspacePage page) in new[]
                {
                    ("navigation.archive", MainWindow.WorkspacePage.Archive), ("navigation.rules", MainWindow.WorkspacePage.Rules),
                    ("navigation.sources", MainWindow.WorkspacePage.Sources), ("navigation.diagnostics", MainWindow.WorkspacePage.Diagnostics),
                    ("navigation.downloads", MainWindow.WorkspacePage.Downloads)
                })
                {
                    Require(Press(window, id).Handled && window.SelectedPage == page, "Page shortcut did not navigate in " + mode + ": " + id);
                    Require(window.SettingsEditor.DownloadPath == "retained shortcut settings draft" && url.Text == "retained shortcut URL draft"
                        && rules.ActiveScope == draftScope && rules.ExtensionsText == "image/jpeg" && rules.HasValidationError,
                        "Direct page navigation lost a draft.");
                }
            }
            Press(window, "navigation.downloads");
            Require(!Press(url, "navigation.archive").Handled && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
                "A page shortcut intercepted a native text input.");
            Require(!Press(url, "session.pause").Handled, "Pause intercepted a native input.");
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1 });
                string help = window.FindControl<TextBlock>("ShellHelpText")!.Text ?? string.Empty;
                Require(help.Contains(CommandCatalog.Label("navigation.rules"), StringComparison.Ordinal)
                    && help.Contains(CommandCatalog.ShortcutDisplay("session.pause")!, StringComparison.Ordinal)
                    && help.Contains(CommandCatalog.ScopeHint("watch.import-clipboard")!, StringComparison.Ordinal), "F1 omitted the scoped command help.");
                Require(!Press(window, "navigation.archive").Handled && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
                    "A page shortcut escaped the help overlay.");
                Program.CaptureWindow(window, "smoke-poweruser-help-" + language + ".png");
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            }
            Window dialog = new Window { Width = 240, Height = 120, Content = new TextBox() };
            Task dismissed = dialog.ShowDialog(window);
            try
            {
                Require(!Press(window, "navigation.archive").Handled && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
                    "Direct navigation escaped an owned modal dialog.");
            }
            finally { dialog.Close(); await dismissed; }
            Button pause = window.FindControl<Button>("PauseButton")!;
            bool paused = window.SessionHost.IsPaused;
            Require(Press(window, "session.pause").Handled, "The pause shortcut was unavailable without blockers.");
            if (!pause.IsEnabled || window.SessionHost.IsPauseTransitioning)
                Press(window, "session.pause"); // Only an in-flight request must not reverse or duplicate the transition.
            await Program.WaitUntilAsync(() => pause.IsEnabled && window.SessionHost.IsPaused != paused);
            Require(Config.LoadConfig().DownloadsPaused == !paused, "The shortcut bypassed the durable pause path.");
            pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => pause.IsEnabled && window.SessionHost.IsPaused == paused);
        }
        finally
        {
            rules.EditScope(draftScope);
            rules.DiscardCurrentDraft();
            rules.EditScope(originalScope);
            window.SettingsEditor.DownloadPath = path;
            url.Text = originalUrl;
            Texts.SetLanguage(originalLanguage);
            await PresentationChecks.SwitchModeAsync(window, originalMode);
            Press(window, "navigation.downloads");
        }
        Console.WriteLine("SMOKE poweruser: B/S/A direct navigation, retained drafts, native-input/dialog/help guards, scoped EN/DE F1, shared pause/resume passed.");
    }

    private static KeyEventArgs Press(Control source, string command)
    {
        KeyGesture gesture = CommandBindings.Gesture(command)!;
        KeyEventArgs args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers };
        source.RaiseEvent(args);
        return args;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
