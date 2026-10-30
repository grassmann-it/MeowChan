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
        string originalMode = window.SessionHost.Config.WorkspaceMode;
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
            bool beforeTextPause = window.SessionHost.IsPaused;
            Require(!Press(url, "session.pause").Handled && window.SessionHost.IsPaused == beforeTextPause
                && Config.LoadConfig().DownloadsPaused == beforeTextPause, "Pause intercepted a native input.");
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
            window.Activate();
            await Program.WaitUntilAsync(() => window.IsActive && pause.IsEffectivelyVisible && pause.IsEffectivelyEnabled
                && !window.SessionHost.IsPauseTransitioning && ReferenceEquals(TopLevel.GetTopLevel(pause), window));
            Require(pause.Focus() && pause.IsFocused && ReferenceEquals(window.FocusManager?.GetFocusedElement(), pause),
                "The pause shortcut fixture did not focus the attached header button.");
            Require(Press(pause, "session.pause").Handled, "The pause shortcut was blocked by the focused header button.");
            await Program.WaitUntilAsync(() => pause.IsEnabled && !window.SessionHost.IsPauseTransitioning
                && window.SessionHost.IsPaused != paused);
            Require(Config.LoadConfig().DownloadsPaused == !paused, "The focused-button shortcut bypassed durable pause.");
            pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => pause.IsEnabled && window.SessionHost.IsPaused == paused);
            await CheckNativeButtonActivationAsync(window, pause);
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
        Console.WriteLine("SMOKE poweruser: B/S/A direct navigation, retained drafts, native-input/dialog/help guards, scoped EN/DE F1, focused-button durable pause and native Enter/Space passed.");
    }

    private static async Task CheckNativeButtonActivationAsync(MainWindow window, Button pause)
    {
        Config original = window.SessionHost.Config.Clone();
        try
        {
            Config candidate = original.Clone();
            // Remove the existing Enter/Space commands so the shell remap is valid,
            // then prove the gestures are live before checking button ownership.
            foreach (string id in new[] { "thread.read", "media.open", "video.play-pause" })
                candidate.ShortcutBindings = candidate.ShortcutBindings.Set(id, Array.Empty<ShortcutGesture>());
            candidate.ShortcutBindings = candidate.ShortcutBindings.Set("navigation.archive",
                new[] { ShortcutGesture.Parse("Enter"), ShortcutGesture.Parse("Space") });
            long revision = ShortcutService.Revision;
            await window.SessionHost.ApplySettingsAsync(candidate);
            await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
            foreach (Key key in new[] { Key.Enter, Key.Space })
            {
                var navigation = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key };
                window.RaiseEvent(navigation);
                Require(navigation.Handled && window.SelectedPage == MainWindow.WorkspacePage.Archive,
                    "The native button guard fixture used an inactive shell remap: " + key);
                Require(Press(window, "navigation.downloads").Handled, "The activation fixture could not return to Downloads.");
                await Program.WaitUntilAsync(() => pause.IsEnabled && !window.SessionHost.IsPauseTransitioning);
                Require(pause.Focus() && pause.IsFocused && ReferenceEquals(window.FocusManager?.GetFocusedElement(), pause),
                    "The native activation fixture did not focus Pause: " + key);
                bool paused = window.SessionHost.IsPaused;
                pause.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
                pause.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key });
                Require(window.SelectedPage == MainWindow.WorkspacePage.Downloads,
                    "A shell remap stole native button activation: " + key);
                await Program.WaitUntilAsync(() => pause.IsEnabled && !window.SessionHost.IsPauseTransitioning
                    && window.SessionHost.IsPaused != paused);
                Require(Config.LoadConfig().DownloadsPaused == !paused, "Native button activation did not persist pause: " + key);
                pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => pause.IsEnabled && window.SessionHost.IsPaused == paused);
            }
        }
        finally
        {
            long revision = ShortcutService.Revision;
            await window.SessionHost.ApplySettingsAsync(original);
            await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
        }
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
