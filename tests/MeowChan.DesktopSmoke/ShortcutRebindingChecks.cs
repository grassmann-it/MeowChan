using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Input;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopSmoke;

/// <summary>Runs only in the existing isolated smoke profile; never writes a separate shortcut file.</summary>
internal static class ShortcutRebindingChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Config original = window.SessionHost.Config.Clone();
        KeyGesture oldArchive = CommandBindings.Gesture("navigation.archive")!;
        var menu = new MenuItem();
        CommandBindings.Apply(menu, "navigation.archive");
        try
        {
            Config candidate = original.Clone();
            candidate.ShortcutBindings = candidate.ShortcutBindings.Set("navigation.archive", new[] { ShortcutGesture.Parse("Primary+Shift+K") });
            candidate.ShortcutBindings = candidate.ShortcutBindings.Set("reader-groups.create", new[]
                { ShortcutGesture.Parse("Primary+Shift+F9"), ShortcutGesture.Parse("Enter") });
            long revision = ShortcutService.Revision;
            await window.SessionHost.ApplySettingsAsync(candidate);
            await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
            KeyGesture replacement = CommandBindings.Gesture("navigation.archive")!;
            Require(replacement.Key == Key.K && replacement.KeyModifiers.HasFlag(KeyModifiers.Shift), "Committed replacement was not published.");
            Require(menu.InputGesture?.Key == Key.K, "An already materialized menu kept its old gesture.");
            Press(window, CommandBindings.Gesture("navigation.downloads")!);
            Require(!Press(window, oldArchive).Handled && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
                "The old free gesture remained active.");
            Require(Press(window, replacement).Handled && window.SelectedPage == MainWindow.WorkspacePage.Archive,
                "The replacement did not execute the existing navigation action.");
            Press(window, CommandBindings.Gesture("navigation.downloads")!);
            using (ShortcutService.BeginCapture())
                Require(!Press(window, replacement).Handled && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
                    "Gesture recording dispatched an application command.");
            var url = window.FindControl<TextBox>("UrlInput")!;
            Require(!Press(url, replacement).Handled && window.SelectedPage == MainWindow.WorkspacePage.Downloads,
                "Rebinding bypassed the native text-input guard.");
            await CheckRouterBoundariesAsync(window);
            var persisted = Config.LoadConfig();
            Require(EffectiveShortcutBindings.Build(persisted.ShortcutBindings).For("navigation.archive").Single().Key == "K",
                "The existing settings writer did not persist the override.");
            var invalid = candidate.Clone();
            invalid.ShortcutBindings = invalid.ShortcutBindings.Set("navigation.archive", new[] { ShortcutGesture.Parse("Escape") });
            bool rejected = false;
            try { await window.SessionHost.ApplySettingsAsync(invalid); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected && CommandBindings.Gesture("navigation.archive")?.Key == Key.K,
                "An invalid draft replaced the committed snapshot.");
            Require(EffectiveShortcutBindings.Build(Config.LoadConfig().ShortcutBindings).For("navigation.archive").Single().Key == "K",
                "Rejected validation changed the settings file.");
        }
        finally
        {
            long revision = ShortcutService.Revision;
            await window.SessionHost.ApplySettingsAsync(original);
            await Program.WaitUntilAsync(() => ShortcutService.Revision > revision);
            Press(window, CommandBindings.Gesture("navigation.downloads")!);
        }
        Console.WriteLine("SMOKE shortcuts: durable replacement, old gesture removal, live menu, capture/text guards and rejected-draft atomicity passed.");
    }
    private static async Task CheckRouterBoundariesAsync(MainWindow owner)
    {
        var source = new Button { Content = "Action" };
        var text = new TextBox();
        var panel = new StackPanel { Children = { source, text } };
        var dialog = new Window
        {
            Title = "Shortcut router boundary fixture", Width = 400, Height = 220, Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowActivated = true
        };
        int executed = 0, backgroundExecuted = 0;
        string State() => $"dialog visible={dialog.IsVisible}, active={dialog.IsActive}, enabled={dialog.IsEnabled}, bounds={dialog.Bounds}; "
            + $"source top-is-dialog={ReferenceEquals(TopLevel.GetTopLevel(source), dialog)}, visible={source.IsEffectivelyVisible}, bounds={source.Bounds}; "
            + $"owner active={owner.IsActive}, enabled={owner.IsEnabled}, visible={owner.IsVisible}; executed={executed}, background={backgroundExecuted}";
        using var router = new CommandRouter(dialog);
        using var background = new CommandRouter(owner);
        bool enabled = false;
        using var registration = router.Register(new("reader-groups.create", panel, () => true,
            () => enabled, () => executed++, Priority: 300));
        using var lower = router.Register(new("reader-groups.create", panel, () => true,
            () => true, () => executed += 100, Priority: 100));
        using var ownerRegistration = background.Register(new("reader-groups.create", owner, () => true,
            () => true, () => backgroundExecuted++));
        dialog.KeyDown += (_, e) => router.Route(e);
        CommandBindings.Apply(source, "reader-groups.create");
        dialog.Opened += (_, _) => Console.WriteLine("SMOKE router fixture opened: " + State());
        dialog.Activated += (_, _) => Console.WriteLine("SMOKE router fixture activated: " + State());
        dialog.Deactivated += (_, _) => Console.WriteLine("SMOKE router fixture deactivated: " + State());
        Task closed = dialog.ShowDialog(owner);
        try
        {
            // ShowDialog requests native activation asynchronously. Synthetic key
            // events do not activate a window like a user's click would. First
            // require the real attached/mapped surface, then explicitly activate it.
            await Program.WaitUntilAsync(() => dialog.IsVisible && source.IsEffectivelyVisible
                && ReferenceEquals(TopLevel.GetTopLevel(source), dialog) && source.Bounds.Width > 0);
            Console.WriteLine("SMOKE router fixture surface ready: " + State());
            dialog.Activate();
            await Program.WaitUntilAsync(() => dialog.IsActive);
            Console.WriteLine("SMOKE router fixture routing ready: " + State());
            KeyGesture gesture = CommandBindings.Gesture("reader-groups.create")!;
            source.Focus();
            Require(Press(source, gesture).Handled && executed == 0, "Disabled high-priority command fell through.");
            enabled = true;
            Require(Press(source, gesture).Handled && executed == 1, "Dialog command did not execute exactly once.");
            var foreign = new KeyEventArgs { Key = gesture.Key, KeyModifiers = gesture.KeyModifiers, Source = source };
            Require(!background.Route(foreign) && backgroundExecuted == 0, "Owner router accepted input from a child dialog.");
            var editing = new KeyEventArgs { Key = gesture.Key, KeyModifiers = gesture.KeyModifiers, Source = text };
            Require(!router.Route(editing) && executed == 1, "Dialog shortcut intercepted native text input.");
            var activation = new KeyEventArgs { Key = Key.Enter, Source = source };
            Require(!router.Route(activation) && executed == 1, "App shortcut stole native button activation.");
            Require(AutomationProperties.GetHelpText(source)?.Contains(gesture.Key.ToString(), StringComparison.Ordinal) == true,
                "Dialog action accessibility did not expose the committed gesture.");
            registration.Dispose(); lower.Dispose();
            Require(!Press(source, gesture).Handled && executed == 1, "Disposed registration retained an executor.");
            panel.Children.Remove(source);
            Require(!CommandRouter.IsRoutingContextActive(source, source), "Detached target remained routable.");
        }
        catch (TimeoutException error)
        {
            throw new InvalidOperationException("Router fixture native readiness timed out before/after routing: " + State(), error);
        }
        finally
        {
            dialog.Close(); await closed;
            owner.Activate();
            await Program.WaitUntilAsync(() => owner.IsActive);
        }
    }
    private static KeyEventArgs Press(Control source, KeyGesture gesture)
    { var e = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = gesture.Key, KeyModifiers = gesture.KeyModifiers }; source.RaiseEvent(e); return e; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
