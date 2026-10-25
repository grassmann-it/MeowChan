using MeowChan.Testing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class SettingsPageChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        await SettingsAccessibilityChecks.RunAsync(owner);
        await CheckSmallLayoutAsync(owner);
        await CheckDraftAndFooterAsync(owner);
        await CheckPendingSaveAsync(owner);
        await CheckPickerCloseAsync(owner);
        Console.WriteLine("SMOKE settings page: retained invalid draft, navigation/focus, field-specific footer commits and failures, Save/Cancel, pending save/picker gates and shutdown passed.");
    }

    private static async Task CheckDraftAndFooterAsync(MainWindow window)
    {
        Config initial = Config.LoadConfig();
        SettingsEditor editor = window.SettingsEditor;
        TextBox urls = window.FindControl<TextBox>("UrlInput")!;
        string? initialUrls = urls.Text;
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        object? selected = grid.SelectedItem;
        string file = Path.Combine(Config.GetConfigDirectory(), "settings.json");
        string original = File.ReadAllText(file);
        string draftPath = Path.Combine(Config.GetConfigDirectory(), "draft-downloads");
        try
        {
            urls.Text = "https://unsupported.invalid/retained-page-input";
            Navigate(window, true);
            await Program.WaitUntilAsync(() => editor.IsEffectivelyVisible && editor.Bounds.Width > 0);
            editor.FindControl<ScrollViewer>("SettingsScrollViewer")!.Offset = new Vector(0, 0);
            window.UpdateLayout();
            Program.CaptureWindow(window, "smoke-settings-page-top-dark.png");
            Require(!window.OwnedWindows.OfType<SettingsWindow>().Any(), "Settings navigation opened a modal window.");
            NumericUpDown timer = editor.FindControl<NumericUpDown>("RecheckTimerInput")!;
            timer.Text = "invalid draft";
            editor.DownloadPath = draftPath;
            editor.SelectedWorkspaceMode = editor.WorkspaceChoices.Single(choice => choice.Value == "Barebones");
            string raw = timer.Text!;
            Navigate(window, false);
            Navigate(window, true);
            Require(ReferenceEquals(window.SettingsEditor, editor) && timer.Text == raw && editor.DownloadPath == draftPath,
                "Navigation reconstructed or discarded the settings draft.");
            Require(urls.Text == "https://unsupported.invalid/retained-page-input" && ReferenceEquals(grid.SelectedItem, selected)
                && File.ReadAllText(file) == original, "Navigation changed download state or persisted a draft.");
            Click(editor.FindControl<Button>("SaveSettingsButton")!);
            await Program.WaitUntilAsync(() => !editor.IsBusy && editor.HasError);
            Require(File.ReadAllText(file) == original && timer.Text == raw, "Invalid raw number was saved or discarded.");
            await RequireErrorVisibleAsync(editor, "RecheckTimerInput");
            Require(timer.Text == raw, "Revealing the field error replaced the invalid numeric draft.");
            Program.CaptureWindow(window, "smoke-settings-page-dark.png");
            Texts.SetLanguage("de");
            window.UpdateLayout();
            Require(timer.Text == raw, $"Language refresh changed invalid numeric draft: expected '{raw}', actual '{timer.Text}'.");
            Require(editor.DownloadPath == draftPath, "Language refresh changed the download-path draft.");
            Require(editor.HasError && !editor.HasErrorDetails, "Language refresh lost field validation or replaced it with a storage error.");
            Require(editor.SelectedWorkspaceMode?.Value == "Barebones", "Language refresh changed the workspace-mode draft.");
            await RequireErrorVisibleAsync(editor, "RecheckTimerInput");
            TextBlock timerError = editor.FindControl<TextBlock>("RecheckTimerInputError")!;
            Require(timerError.Text?.Contains(editor.RecheckLabel) == true, "The numeric field error was not translated with its field label.");
            Texts.SetLanguage(initial.Language);
            Require(timer.Text == raw && editor.DownloadPath == draftPath, "Restoring the UI language changed an invalid draft.");
            await RequireErrorVisibleAsync(editor, "RecheckTimerInput");

            editor.SelectedTheme = editor.ThemeChoices.Single(choice => choice.Value == initial.Theme);
            Click(window.FindControl<Button>("ThemeToggleButton")!);
            string nextTheme = initial.Theme == "Dark" ? "Light" : "Dark";
            await Program.WaitUntilAsync(() => Config.LoadConfig().Theme == nextTheme && !editor.IsBusy && editor.IsAvailable);
            Require(editor.SelectedTheme?.Value == nextTheme && editor.SelectedWorkspaceMode?.Value == "Barebones"
                && timer.Text == raw && editor.DownloadPath == draftPath && editor.HasError,
                "A footer theme commit overwrote unrelated draft fields or invalid text.");
            string nextMode = initial.WorkspaceMode == "Simple" ? "Advanced" : "Simple";
            await PresentationChecks.SwitchModeAsync(window, nextMode);
            Require(editor.SelectedWorkspaceMode?.Value == nextMode && editor.SelectedTheme?.Value == nextTheme
                && timer.Text == raw && editor.DownloadPath == draftPath,
                "A footer view commit failed to synchronize only its field.");
            Program.CaptureWindow(window, "smoke-settings-page-light.png");

            string backup = file + ".page-fixture";
            File.Move(file, backup);
            Directory.CreateDirectory(file);
            try
            {
                Click(window.FindControl<Button>("ThemeToggleButton")!);
                await Program.WaitUntilAsync(() => editor.IsAvailable && window.FindControl<Button>("ThemeToggleButton")!.IsEnabled);
                Require(Application.Current?.RequestedThemeVariant == (nextTheme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark)
                    && editor.SelectedTheme?.Value == nextTheme && editor.DownloadPath == draftPath && timer.Text == raw,
                    "A failed footer commit changed appearance or the draft.");
            }
            finally
            {
                Directory.Delete(file);
                File.Move(backup, file);
            }
            Click(editor.FindControl<Button>("CancelSettingsButton")!);
            Require(window.SettingsPageVisible && !editor.HasError && editor.DownloadPath == initial.DownloadPath
                && editor.SelectedTheme?.Value == nextTheme && editor.SelectedWorkspaceMode?.Value == nextMode,
                "Cancel closed the page or rolled back a confirmed footer action.");
            editor.FindControl<ScrollViewer>("SettingsScrollViewer")!.Offset = new Vector(0, 0);
            window.UpdateLayout();
            Program.CaptureWindow(window, "smoke-settings-page-top-light.png");
            timer.Text = "120";
            Click(editor.FindControl<Button>("SaveSettingsButton")!);
            await Program.WaitUntilAsync(() => editor.IsAvailable && !editor.IsBusy && Config.LoadConfig().RecheckTimer == 120);
            Require(window.SettingsPageVisible && !editor.HasError && Config.LoadConfig().Theme == nextTheme
                && Config.LoadConfig().WorkspaceMode == nextMode, "Save closed the page or rolled back footer preferences.");
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.L,
                KeyModifiers = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control });
            Require(!window.SettingsPageVisible && urls.IsFocused && urls.Text == "https://unsupported.invalid/retained-page-input",
                "The URL shortcut failed to navigate and retain the pending input.");
        }
        finally
        {
            Navigate(window, true);
            editor.CancelChanges();
            editor.RecheckTimer = initial.RecheckTimer;
            editor.SelectedTheme = editor.ThemeChoices.Single(choice => choice.Value == initial.Theme);
            editor.SelectedWorkspaceMode = editor.WorkspaceChoices.Single(choice => choice.Value == initial.WorkspaceMode);
            Click(editor.FindControl<Button>("SaveSettingsButton")!);
            await Program.WaitUntilAsync(() => !editor.IsBusy && editor.IsAvailable);
            Require(!editor.HasError, "Restoring the page fixture failed.");
            Navigate(window, false);
            urls.Text = initialUrls;
        }
    }

    internal static Task RunFooterOverlapDiagnosticAsync(MainWindow window) => CheckSmallLayoutAsync(window, footerOnly: true);

    private static async Task CheckSmallLayoutAsync(MainWindow window, bool footerOnly = false)
    {
        Config initial = Config.LoadConfig();
        double width = window.Width;
        double height = window.Height;
        WindowState state = window.WindowState;
        SettingsEditor editor = window.SettingsEditor;
        try
        {
            Navigate(window, true);
            window.WindowState = WindowState.Normal;
            // Preserve the prior Simple→Barebones mode transition, without replaying the full matrix.
            if (footerOnly) await PresentationChecks.SwitchModeAsync(window, "Simple");
            foreach (string mode in footerOnly ? new[] { "Barebones" } : new[] { "Simple", "Barebones" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                double targetWidth = mode == "Barebones" ? 640 : 760;
                double targetHeight = mode == "Barebones" ? 480 : 500;
                window.Width = targetWidth;
                window.Height = targetHeight;
                try
                {
                    await Program.WaitUntilAsync(() => window.WindowState == WindowState.Normal
                        && PresentationChecks.MatchesRequestedSize(window, targetWidth, targetHeight));
                }
                finally
                {
                    Console.WriteLine($"SMOKE settings minimum geometry: mode={mode}, target={targetWidth}x{targetHeight}, client={window.Bounds.Size}, frame={window.FrameSize}, minimum={window.MinWidth}x{window.MinHeight}, scale={window.RenderScaling}.");
                }
                foreach (string language in new[] { "en", "de" })
                {
                    Texts.SetLanguage(language);
                    editor.FindControl<ScrollViewer>("SettingsScrollViewer")!.Offset = new Vector(0, 0);
                    await Program.WaitUntilAsync(() => InViewport(window, editor.FindControl<Button>("SaveSettingsButton")!)
                        && InViewport(window, editor.FindControl<Button>("CancelSettingsButton")!)
                        && InViewport(window, window.FindControl<Button>("DownloadsNavigationButton")!)
                        && InViewport(window, window.FindControl<Button>("SettingsNavigationButton")!)
                        && InViewport(window, window.FindControl<ComboBox>("WorkspaceModeComboBox")!)
                        && InViewport(window, editor.FindControl<ComboBox>("WorkspaceModeInput")!));
                    RequireIconLabel(editor.FindControl<Button>("BrowseButton")!, editor.BrowseLabel);
                    RequireIconLabel(editor.FindControl<Button>("AboutSettingsButton")!, editor.AboutLabel);
                    RequireIconLabel(editor.FindControl<Button>("CancelSettingsButton")!, editor.CancelLabel);
                    RequireIconLabel(editor.FindControl<Button>("SaveSettingsButton")!, editor.SaveLabel);
                    Program.CaptureWindow(window, $"smoke-settings-page-{mode.ToLowerInvariant()}-small-{language}.png");
                    editor.RecheckTimer = 0;
                    Click(editor.FindControl<Button>("SaveSettingsButton")!);
                    await Program.WaitUntilAsync(() => !editor.IsBusy && editor.HasError);
                    await RequireErrorVisibleAsync(editor, "RecheckTimerInput");
                    if (footerOnly && language == "de")
                        SettingsFooterOverlapChecks.AssertCurrentScene(window);
                    Program.CaptureWindow(window, $"smoke-settings-page-{mode.ToLowerInvariant()}-field-error-{language}.png");
                    Require(InViewport(window, editor.FindControl<Button>("SaveSettingsButton")!),
                        "Revealing an error hid the fixed Save action.");
                    if (footerOnly && language == "de")
                        await CheckFooterReturnAndWideAsync(window);
                    Click(editor.FindControl<Button>("CancelSettingsButton")!);
                }
            }
        }
        finally
        {
            Texts.SetLanguage(initial.Language);
            await PresentationChecks.SwitchModeAsync(window, initial.WorkspaceMode);
            window.Width = width;
            window.Height = height;
            window.WindowState = state;
            editor.CancelChanges();
            Navigate(window, false);
        }
    }

    private static async Task CheckFooterReturnAndWideAsync(MainWindow window)
    {
        foreach (var scene in new[] {
            (Language: "en", Width: 640d, Height: 480d, Stage: "en-after-de", FullLabels: true),
            (Language: "de", Width: 640d, Height: 480d, Stage: "de-return", FullLabels: false),
            (Language: "en", Width: 640d, Height: 480d, Stage: "en-return", FullLabels: true),
            (Language: "en", Width: 1400d, Height: 600d, Stage: "en-wide", FullLabels: true) })
        {
            Texts.SetLanguage(scene.Language);
            window.Width = scene.Width; window.Height = scene.Height;
            Border footer = window.FindControl<Border>("WorkspaceNavigation")!;
            await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - scene.Width) <= 1
                && Math.Abs(window.Bounds.Height - scene.Height) <= 1
                && footer.IsMeasureValid && footer.IsArrangeValid
                && footer.GetVisualDescendants().OfType<Avalonia.Layout.Layoutable>()
                    .Where(control => control.IsEffectivelyVisible)
                    .All(control => control.IsMeasureValid && control.IsArrangeValid));
            // Passive oracle after natural layout: no test repair or UpdateLayout.
            SettingsFooterOverlapChecks.AssertCurrentScene(window, scene.Stage, scene.Language,
                scene.Width, scene.Height, scene.FullLabels);
        }
    }

    private static void RequireIconLabel(Button button, string expected)
    {
        Require(button.Content is StackPanel panel && panel.Children.Count == 2
            && panel.Children[0] is Control
            && panel.Children[1] is TextBlock label && label.Text == expected,
            $"{button.Name} lost its icon or translated action label.");
    }

    private static bool InViewport(Window window, Control control)
    {
        Point? origin = control.TranslatePoint(new Point(0, 0), window);
        return control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Bounds.Width > 0
            && control.Bounds.Height > 0 && origin.HasValue && origin.Value.X >= -1 && origin.Value.Y >= -1
            && origin.Value.X + control.Bounds.Width <= window.Bounds.Width + 1
            && origin.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }

    internal static async Task RunLateSaveFailureAsync(MainWindow owner)
    {
        await CheckPendingSaveAsync(owner, failShutdownSave: true);
        Console.WriteLine("SMOKE settings late save failure: shutdown released the profile, retained the last committed settings and emitted no ended-editor events.");
    }

    private static async Task CheckPendingSaveAsync(MainWindow owner, bool failShutdownSave = false)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-page-save-" + Guid.NewGuid().ToString("N"));
        SeedProfile(profile);
        GateStore? store = null;
        DesktopSessionHost host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>(), (directory, lease) =>
            store = new GateStore(new SessionStore(directory, lease)));
        MainWindow window = new MainWindow(host);
        window.Show(owner);
        TaskCompletionSource? pending = null;
        try
        {
            await Program.WaitUntilAsync(() => window.FindControl<Button>("AddUrlButton")!.IsEnabled);
            Navigate(window, true);
            SettingsEditor editor = window.SettingsEditor;
            Button save = editor.FindControl<Button>("SaveSettingsButton")!;
            editor.RecheckTimer = 120;
            pending = store!.Arm();
            int before = store.Calls;
            Click(save);
            await Program.WaitUntilAsync(() => store.Calls == before + 1 && editor.IsBusy);
            Click(save);
            Click(editor.FindControl<Button>("CancelSettingsButton")!);
            Navigate(window, false);
            Click(window.FindControl<Button>("ThemeToggleButton")!);
            Require(window.SettingsPageVisible && store.Calls == before + 1 && editor.IsBusy
                && !window.FindControl<Button>("DownloadsNavigationButton")!.IsEnabled
                && !window.FindControl<ComboBox>("WorkspaceModeComboBox")!.IsEnabled,
                "Pending save allowed duplicate work, navigation or footer changes.");
            pending.SetException(new IOException("Synthetic pending page save failure."));
            await Program.WaitUntilAsync(() => !editor.IsBusy && editor.HasError);
            Require(host.Config.RecheckTimer == 60 && editor.RecheckTimer == 120 && window.SettingsPageVisible,
                "Pending failure lost the draft or updated active settings.");
            await RequireErrorVisibleAsync(editor);
            Click(save);
            await Program.WaitUntilAsync(() => !editor.IsBusy && !editor.HasError && host.Config.RecheckTimer == 120);
            Require(store.Calls == before + 2 && window.SettingsPageVisible, "Corrected save did not commit exactly once.");

            editor.RecheckTimer = 121;
            pending = store.Arm();
            Click(save);
            await Program.WaitUntilAsync(() => editor.IsBusy && store.Calls == before + 3);
            bool lateUpdate = false;
            List<object> shutdownChanges = new();
            bool previousBusy = editor.IsBusy, previousAvailable = editor.IsAvailable;
            decimal? previousTimer = editor.RecheckTimer;
            bool lateBusyChanged = false, lateSaved = false;
            editor.BusyChanged += (_, _) => lateBusyChanged = true;
            editor.Saved += (_, _) => lateSaved = true;
            editor.PropertyChanged += (_, args) =>
            {
                lateUpdate = true;
                shutdownChanges.Add(new
                {
                    property = args.PropertyName,
                    beforeBusy = previousBusy, afterBusy = editor.IsBusy,
                    beforeAvailable = previousAvailable, afterAvailable = editor.IsAvailable,
                    beforeRecheckTimer = previousTimer, afterRecheckTimer = editor.RecheckTimer
                });
                previousBusy = editor.IsBusy; previousAvailable = editor.IsAvailable; previousTimer = editor.RecheckTimer;
            };
            window.Close();
            Require(window.IsVisible, "Shutdown did not wait for the accepted settings commit.");
            if (failShutdownSave)
                pending.SetException(new IOException("Synthetic accepted settings save failure after shutdown began."));
            else
                pending.SetResult();
            await Program.WaitUntilAsync(() => !window.IsVisible && !editor.IsBusy);
            int expectedRecheckTimer = failShutdownSave ? 120 : 121;
            Console.WriteLine("SMOKE pending settings shutdown: " + System.Text.Json.JsonSerializer.Serialize(new
            {
                failShutdownSave, lateUpdate, lateBusyChanged, lateSaved,
                persistedRecheckTimer = Config.LoadConfig(profile).RecheckTimer,
                expectedRecheckTimer, editor.RecheckTimer, editor.IsBusy, editor.IsAvailable, window.IsVisible,
                changes = shutdownChanges
            }));
            Require(!lateUpdate && !lateBusyChanged && !lateSaved
                && Config.LoadConfig(profile).RecheckTimer == expectedRecheckTimer
                && host.Config.RecheckTimer == expectedRecheckTimer && editor.RecheckTimer == 121,
                "Shutdown changed the ended editor or did not preserve the accepted save outcome.");
            using ProfileLease lease = ProfileLease.Acquire(profile);
        }
        finally
        {
            pending?.TrySetResult();
            if (window.IsVisible) { window.Close(); await Program.WaitUntilAsync(() => !window.IsVisible); }
            Directory.Delete(profile, true);
        }
    }

    private static async Task CheckPickerCloseAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-page-picker-" + Guid.NewGuid().ToString("N"));
        SeedProfile(profile);
        using IDisposable scope = AvaloniaLocator.EnterScope();
        SettingsChecks.FixtureStorageProvider provider = new SettingsChecks.FixtureStorageProvider();
        AvaloniaLocator.CurrentMutable.Bind<IStorageProviderFactory>().ToConstant(new SettingsChecks.FixtureStorageProviderFactory(provider));
        TaskCompletionSource<IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder>> pending = new TaskCompletionSource<IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder>>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Result = pending.Task;
        DesktopSessionHost host = new DesktopSessionHost(profile, Array.Empty<IImageBoard>());
        MainWindow window = new MainWindow(host);
        window.Show(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.FindControl<Button>("AddUrlButton")!.IsEnabled);
            Navigate(window, true);
            SettingsEditor editor = window.SettingsEditor;
            await Program.WaitUntilAsync(() => editor.IsAttachedToVisualTree() && editor.IsEffectivelyVisible && editor.Bounds.Width > 0);
            string path = editor.DownloadPath;
            Button browse = editor.FindControl<Button>("BrowseButton")!;
            Click(browse);
            Require(editor.IsBusy, "Picker did not enter pending state.");
            Click(browse);
            Navigate(window, false);
            Require(provider.PickerCalls == 1 && window.SettingsPageVisible
                && !window.FindControl<Button>("ThemeToggleButton")!.IsEnabled, "Picker allowed duplicate work or navigation.");
            window.Close();
            await Program.WaitUntilAsync(() => !window.IsVisible);
            bool lateUpdate = false;
            editor.PropertyChanged += (_, _) => lateUpdate = true;
            SettingsChecks.FixtureStorageFolder selected = new SettingsChecks.FixtureStorageFolder(new Uri(Path.Combine(profile, "late-picker")));
            pending.SetResult(new Avalonia.Platform.Storage.IStorageFolder[] { selected });
            await Program.WaitUntilAsync(() => !editor.IsBusy && selected.Disposed);
            Require(!lateUpdate && editor.DownloadPath == path, "A late picker result modified the ended editor.");
            using ProfileLease lease = ProfileLease.Acquire(profile);
        }
        finally
        {
            pending.TrySetResult(Array.Empty<Avalonia.Platform.Storage.IStorageFolder>());
            if (window.IsVisible) { window.Close(); await Program.WaitUntilAsync(() => !window.IsVisible); }
            Directory.Delete(profile, true);
        }
    }

    private static void SeedProfile(string profile)
    {
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "downloads");
        config.SaveConfig();
    }

    private static async Task RequireErrorVisibleAsync(SettingsEditor editor, string? fieldName = null)
    {
        // Validation reveals the offending field; storage failures reveal the summary.
        // Do not scroll the page in the assertion: this verifies the product's reveal.
        TextBlock error = editor.FindControl<TextBlock>(fieldName == null ? "SettingsError" : fieldName + "Error")!;
        NumericUpDown? field = fieldName == null ? null : editor.FindControl<NumericUpDown>(fieldName)!;
        ScrollViewer scroll = editor.FindControl<ScrollViewer>("SettingsScrollViewer")!;
        await Program.WaitUntilAsync(() =>
        {
            Point? origin = error.TranslatePoint(new Point(0, 0), scroll);
            return error.IsEffectivelyVisible && error.Bounds.Height > 0 && origin.HasValue
                && (field == null || ((field.IsFocused || field.IsKeyboardFocusWithin)
                    && Avalonia.Automation.AutomationProperties.GetHelpText(field) == error.Text))
                && origin.Value.Y >= -1 && origin.Value.Y + error.Bounds.Height <= scroll.Bounds.Height + 1;
        });
    }

    private static void Navigate(MainWindow window, bool settings) => Click(window.FindControl<Button>(settings ? "SettingsNavigationButton" : "DownloadsNavigationButton")!);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class GateStore : ISessionStore
    {
        private readonly ISessionStore _inner;
        private TaskCompletionSource? _pending;
        public int Calls { get; private set; }
        internal GateStore(ISessionStore inner) => _inner = inner;
        internal TaskCompletionSource Arm() => _pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);
        public async Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            Calls++;
            TaskCompletionSource? pending = _pending;
            _pending = null;
            if (pending != null) await pending.Task.WaitAsync(cancellationToken);
            await _inner.SaveAsync(snapshot, cancellationToken);
        }
    }
}
