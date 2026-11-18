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
                    ("navigation.archive", MainWindow.WorkspacePage.Archive), ("navigation.media", MainWindow.WorkspacePage.Media),
                    ("navigation.rules", MainWindow.WorkspacePage.Rules),
                    ("navigation.sources", MainWindow.WorkspacePage.Sources), ("navigation.status", MainWindow.WorkspacePage.Status),
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
            // The footer names each page's shortcut in its tooltip, Media included.
            foreach ((string name, string id) in new[] { ("DownloadsNavigationButton", "navigation.downloads"),
                ("ArchiveNavigationButton", "navigation.archive"), ("MediaNavigationButton", "navigation.media") })
                Require(ToolTip.GetTip(window.FindControl<Button>(name)!) is string tip
                    && tip.StartsWith(CommandCatalog.Label(id) + " \u00b7 " + CommandCatalog.ShortcutDisplay(id), StringComparison.Ordinal),
                    "The footer tooltip of " + id + " does not name its shortcut.");
            Require(url.Focus(), "The safe-chord fixture could not focus the URL input.");
            Require(Press(url, "navigation.archive").Handled && window.SelectedPage == MainWindow.WorkspacePage.Archive,
                "A safe page chord did not navigate from a native input.");
            Require(Press(window, "navigation.downloads").Handled, "Could not return to the URL input.");
            Button inputPause = window.FindControl<Button>("PauseButton")!;
            bool beforeTextPause = window.SessionHost.IsPaused;
            Require(url.Focus() && Press(url, "session.pause").Handled, "A safe pause chord was blocked in a native input.");
            await Program.WaitUntilAsync(() => inputPause.IsEnabled && !window.SessionHost.IsPauseTransitioning
                && window.SessionHost.IsPaused != beforeTextPause);
            Require(Config.LoadConfig().DownloadsPaused == !beforeTextPause, "Input pause did not persist.");
            Require(Press(url, "session.pause").Handled, "The input resume chord was blocked.");
            await Program.WaitUntilAsync(() => inputPause.IsEnabled && !window.SessionHost.IsPauseTransitioning
                && window.SessionHost.IsPaused == beforeTextPause);
            Require(Config.LoadConfig().DownloadsPaused == beforeTextPause, "Input resume did not persist.");
            RequireDrafts();
            await CheckTypingRemapsAsync(window, url);
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F1 });
                // F1 opens the one shortcuts window (KS2); its plain text is the former F1 list of this context.
                await Program.WaitUntilAsync(() => MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window) is { IsVisible: true });
                var shortcutsWindow = MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window)!;
                string help = shortcutsWindow.PlainText;
                Require(help.Contains(CommandCatalog.Label("navigation.rules"), StringComparison.Ordinal)
                    && help.Contains(CommandCatalog.ShortcutDisplay("session.pause")!, StringComparison.Ordinal)
                    && help.Contains(CommandCatalog.ScopeHint("watch.import-clipboard")!, StringComparison.Ordinal), "F1 omitted the scoped command help.");
                // UX round 3: the "outside inputs and overlays" qualifier is stated once as a note, not on every page shortcut.
                string generic = CommandCatalog.ScopeHint("navigation.rules")!;
                Require(!help.Contains(generic, StringComparison.Ordinal) && help.Contains(Texts.T("help.scope.generalNote"), StringComparison.Ordinal)
                    && help.Split(Texts.T("help.scope.generalNote")).Length == 2, "F1 repeats the generic scope qualifier or lacks its single note.");
                // Explicitly return native focus to the owner: modeless help does not own its shortcuts.
                window.Activate();
                await Program.WaitUntilAsync(() => window.IsActive);
                Require(url.Focus(), "The modeless-help fixture could not focus the owner input.");
                // The window is modeless: the owner's page shortcuts keep working while it is open.
                Require(Press(window, "navigation.archive").Handled && window.SelectedPage == MainWindow.WorkspacePage.Archive,
                    "A page shortcut was blocked by the modeless shortcuts window.");
                Require(Press(window, "navigation.downloads").Handled && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
                    "The downloads shortcut did not return with the shortcuts window open.");
                Program.CaptureWindow(shortcutsWindow, "smoke-poweruser-help-" + language + ".png");
                window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                await Program.WaitUntilAsync(() => MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.For(window) == null);
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
            RequireDrafts();
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
        void RequireDrafts() => Require(window.SettingsEditor.DownloadPath == "retained shortcut settings draft"
            && url.Text == "retained shortcut URL draft" && rules.ActiveScope == draftScope
            && rules.ExtensionsText == "image/jpeg" && rules.HasValidationError, "Input shortcuts lost a draft.");
        Console.WriteLine("SMOKE poweruser: B/S/A direct navigation, retained drafts, native-input/dialog/help guards, scoped EN/DE F1, focused-button durable pause and native Enter/Space passed.");
    }

    private static async Task CheckTypingRemapsAsync(MainWindow window, TextBox url)
    {
        Config original = window.SessionHost.Config.Clone();
        Exception? failure = null;
        try
        {
            foreach (string chord in new[] { "J", "Shift+J", "Alt+J" })
            foreach (string command in new[] { "navigation.archive", "session.pause" })
            {
                Config candidate = original.Clone();
                candidate.ShortcutBindings = candidate.ShortcutBindings.Set(command, new[] { ShortcutGesture.Parse(chord) });
                long revision = ShortcutService.Revision;
                await window.SessionHost.ApplySettingsAsync(candidate);
                await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
                Button pause = window.FindControl<Button>("PauseButton")!;
                bool paused = window.SessionHost.IsPaused;
                // Prove this is a live binding before testing the input guard.
                Require(Press(window, command).Handled, "Inactive typing remap: " + command + " " + chord);
                if (command == "session.pause")
                {
                    await Program.WaitUntilAsync(() => pause.IsEnabled && !window.SessionHost.IsPauseTransitioning
                        && window.SessionHost.IsPaused != paused);
                    Require(Config.LoadConfig().DownloadsPaused == !paused, "Live pause remap did not persist.");
                    pause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Program.WaitUntilAsync(() => pause.IsEnabled && !window.SessionHost.IsPauseTransitioning
                        && window.SessionHost.IsPaused == paused);
                }
                else Require(window.SelectedPage == MainWindow.WorkspacePage.Archive, "Live typing remap did not navigate.");
                Require(Press(window, "navigation.downloads").Handled, "Could not return for typing protection.");
                Require(url.Focus(), "Typing fixture could not focus the URL input.");
                Require(!Press(url, command).Handled && window.SelectedPage == MainWindow.WorkspacePage.Downloads
                    && window.SessionHost.IsPaused == paused && Config.LoadConfig().DownloadsPaused == paused
                    && url.Text == "retained shortcut URL draft", "A shell remap stole typing: " + command + " " + chord);
            }
        }
        catch (Exception thrown) { failure = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failure, async () =>
            {
                long revision = ShortcutService.Revision;
                await window.SessionHost.ApplySettingsAsync(original);
                await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
            }, exception => Console.Error.WriteLine("SMOKE typing-remap cleanup failed: " + exception));
        }
    }

    private static async Task CheckNativeButtonActivationAsync(MainWindow window, Button pause)
    {
        Config original = window.SessionHost.Config.Clone();
        Exception? failureBeforeCleanup = null;
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
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                long revision = ShortcutService.Revision;
                await window.SessionHost.ApplySettingsAsync(original);
                await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in PowerUserCommandChecks: " + exception));
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
