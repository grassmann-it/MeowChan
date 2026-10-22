using MeowChan.Testing;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class SettingsChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-settings-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CheckApplyAsync(owner, Path.Combine(profile, "apply"));
            await CheckOwnerCloseWhileBusyAsync(owner, Path.Combine(profile, "owner-close"));
            await CheckDirectSaveAsync(owner, Path.Combine(profile, "direct-save"));
            await CheckPickerAsync(owner, Path.Combine(profile, "picker"));
            await CheckFieldErrorsAsync(owner, Path.Combine(profile, "field-errors"));
            await CheckCancelPreferencesAsync(owner, Path.Combine(profile, "cancel-preferences"));
            Console.WriteLine("SMOKE settings: pending close/duplicate protection, visible errors, validation, retry, appearance persistence/cancel and folder-picker outcomes passed.");
        }
        finally
        {
            if (Directory.Exists(profile))
                Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task CheckFieldErrorsAsync(MainWindow owner, string profile)
    {
        Config config = Config.LoadConfig(profile);
        int commits = 0;
        SettingsWindow window = new SettingsWindow(config, _ => { commits++; return Task.CompletedTask; });
        Task dialog = window.ShowDialog(owner);
        try
        {
            await WaitUntilAsync(() => window.IsVisible);
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                foreach (string name in new[] { "RecheckTimerInput", "BandwidthInput", "ParallelDownloadsInput", "ReserveInput",
                    "ProductionWorkersInput", "WorkerIdleMinutesInput", "SourceHeapMiBInput", "FilterHeapMiBInput" })
                {
                    window.Editor.CancelChanges();
                    NumericUpDown input = window.Editor.FindControl<NumericUpDown>(name)!;
                    input.Text = "invalid draft";
                    Click(window.Editor.FindControl<Button>("SaveSettingsButton")!);
                    TextBlock error = window.Editor.FindControl<TextBlock>(name + "Error")!;
                    await WaitUntilAsync(() => !window.Editor.IsBusy && error.IsEffectivelyVisible
                        && (input.IsFocused || input.IsKeyboardFocusWithin));
                    Require(commits == 0 && input.Text == "invalid draft", "Invalid field text was committed or corrected automatically.");
                    Require(window.Editor.GetVisualDescendants().OfType<TextBlock>()
                        .Count(text => text.Name?.EndsWith("InputError") == true && text.IsVisible) == 1,
                        "An invalid field marked unrelated settings fields.");
                    if (name == "ReserveInput")
                    {
                        string alternate = language == "en" ? "de" : "en";
                        Texts.SetLanguage(alternate);
                        await WaitUntilAsync(() => error.Text?.Contains(window.Editor.ReserveLabel) == true
                            && Avalonia.Automation.AutomationProperties.GetHelpText(input) == error.Text);
                        Texts.SetLanguage(language);
                    }
                    Require(Avalonia.Automation.AutomationProperties.GetHelpText(input) == error.Text,
                        "The input's accessible help did not identify its validation error.");
                    Point? position = error.TranslatePoint(default, window.Editor.FindControl<ScrollViewer>("SettingsScrollViewer")!);
                    Require(position.HasValue && position.Value.Y >= -1, "The field error stayed above the scroll viewport.");
                }
                window.Editor.CancelChanges();
                await WaitUntilAsync(() => Avalonia.Automation.AutomationProperties.GetHelpText(window.Editor.FindControl<NumericUpDown>("ReserveInput")!)
                    == window.Editor.ReserveInputHelp);
                Require(Avalonia.Automation.AutomationProperties.GetHelpText(window.Editor.FindControl<NumericUpDown>("ReserveInput")!).Contains(window.Editor.ReserveHint),
                    "Discard removed the reserve explanation from accessible help.");
                foreach (string advanced in new[] { "ProductionWorkersInput", "WorkerIdleMinutesInput", "SourceHeapMiBInput", "FilterHeapMiBInput" })
                    Require(Avalonia.Automation.AutomationProperties.GetHelpText(window.Editor.FindControl<NumericUpDown>(advanced)!).Contains(window.Editor.BudgetRestartHint),
                        "Discard or a language change removed the advanced budget restart explanation.");
                Require(!window.Editor.HasError && !window.Editor.HasErrorDetails, "Discard retained validation or technical error state.");
            }
        }
        finally
        {
            Texts.SetLanguage("en");
            if (window.IsVisible) window.Close();
            await dialog;
        }
    }

    private static async Task CheckApplyAsync(MainWindow owner, string profile)
    {
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "downloads");
        config.SaveConfig();
        string original = File.ReadAllText(Path.Combine(profile, "settings.json"));
        TaskCompletionSource pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task applyResult = pending.Task;
        int applies = 0;
        SettingsWindow window = new SettingsWindow(config, async candidate =>
        {
            applies++;
            await applyResult;
            candidate.SaveConfig();
        });
        Task dialog = window.ShowDialog(owner);
        try
        {
            await WaitUntilAsync(() => window.IsVisible && window.Bounds.Width > 0);
            NumericUpDown timer = window.Editor.FindControl<NumericUpDown>("RecheckTimerInput")!;
            Require(timer.Maximum == 4294967, "Settings allowed a timer above the runtime limit.");
            window.Editor.RecheckTimer = 120;
            SetAppearance(window);
            Button save = await WaitForButtonAsync(window, "Save & Close");
            Click(save);
            Require(window.Editor.IsBusy && !save.IsEffectivelyEnabled && applies == 1, "Pending settings apply did not disable the form.");
            Click(save);
            Require(applies == 1, "A duplicate save entered the pending callback twice.");
            Func<WindowCloseReason, bool> requestClose = window.PlatformImpl?.Closing
                ?? throw new InvalidOperationException("Settings native close callback is missing.");
            Require(requestClose(WindowCloseReason.WindowClosing), "Native close was allowed during a pending settings apply.");
            Require(window.IsVisible && !dialog.IsCompleted, "Pending settings dialog closed before apply completed.");
            pending.SetException(new IOException("Synthetic settings apply failure."));
            await WaitUntilAsync(() => !window.Editor.IsBusy && window.Editor.HasError);
            Require(window.IsVisible && !dialog.IsCompleted, "Apply failure closed the settings dialog.");
            Require(window.Editor.HasErrorDetails && window.Editor.ErrorDetails!.Contains("Synthetic settings apply failure.")
                && !window.Editor.FindControl<Expander>("SettingsErrorDetails")!.IsExpanded,
                "Save failure did not retain its technical details behind an explicit expander.");
            Require(config.RecheckTimer == 60, "Apply failure changed the live configuration.");
            Require(config.Theme == "Dark" && config.WorkspaceMode == "Barebones" && config.Language == "en" && !config.CompactDensity,
                "Apply failure changed live appearance preferences.");
            Require(File.ReadAllText(Path.Combine(profile, "settings.json")) == original, "The dialog saved settings before its apply callback committed.");
            await AssertErrorInViewportAsync(window);

            Action[] invalidEdits =
            {
                () => window.Editor.RecheckTimer = null,
                () => window.Editor.RecheckTimer = 60.5m,
                () => window.Editor.MaxParallelDownloads = 1.5m,
                () => window.Editor.BandwidthLimiterMB = 0,
                () => window.Editor.BandwidthLimiterMB = 8796093022208m
            };
            foreach (Action edit in invalidEdits)
            {
                window.Editor.RecheckTimer = 120;
                window.Editor.MaxParallelDownloads = 3;
                window.Editor.BandwidthLimiterMB = 2;
                edit();
                Click(save);
                await WaitUntilAsync(() => !window.Editor.IsBusy);
                Require(window.Editor.HasError && applies == 1, "An invalid setting entered the commit callback.");
                Require(config.RecheckTimer == 60 && config.MaxParallelDownloads == 5, "An invalid setting changed the live configuration.");
            }

            window.Editor.RecheckTimer = 120;
            window.Editor.MaxParallelDownloads = 3;
            window.Editor.BandwidthLimiterMB = 2;
            applyResult = Task.CompletedTask;
            Click(save);
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
            Require(applies == 2 && !window.IsVisible && !window.Editor.IsBusy, "Successful settings retry did not close exactly once.");
            Require(config.RecheckTimer == 120 && config.MaxParallelDownloads == 3, "Successful settings apply did not update the live configuration.");
            Require(Config.LoadConfig(profile).RecheckTimer == 120, "Successful settings apply did not commit the isolated profile.");
            Config saved = Config.LoadConfig(profile);
            Require(config.Theme == "Light" && config.WorkspaceMode == "Simple" && config.Language == "de" && config.CompactDensity
                && saved.Theme == "Light" && saved.WorkspaceMode == "Simple" && saved.Language == "de" && saved.CompactDensity,
                "Successful settings apply did not persist all appearance preferences.");
        }
        finally
        {
            pending.TrySetCanceled();
            if (window.IsVisible)
            {
                await WaitUntilAsync(() => !window.Editor.IsBusy);
                window.Close();
            }
        }
    }

    private static async Task CheckCancelPreferencesAsync(MainWindow owner, string profile)
    {
        Config config = Config.LoadConfig(profile);
        config.SaveConfig();
        string original = File.ReadAllText(Path.Combine(profile, "settings.json"));
        int applies = 0;
        SettingsWindow window = new SettingsWindow(config, candidate =>
        {
            applies++;
            return Task.CompletedTask;
        });
        Task dialog = window.ShowDialog(owner);
        try
        {
            await WaitUntilAsync(() => window.IsVisible);
            SetAppearance(window);
            string originalTitle = window.Title ?? string.Empty;
            Texts.SetLanguage("de");
            await WaitUntilAsync(() => window.Title != originalTitle && window.Editor.SelectedLanguage?.Code == "de");
            Require(window.Editor.SelectedTheme?.Value == "Light" && window.Editor.SelectedWorkspaceMode?.Value == "Simple" && window.Editor.CompactDensity,
                "Changing language while settings were open discarded pending appearance edits.");
            Click(FindButton(window, "Cancel"));
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
            Require(applies == 0 && config.Theme == "Dark" && config.WorkspaceMode == "Barebones"
                && config.Language == "en" && !config.CompactDensity,
                "Cancelling appearance edits changed live settings or invoked apply.");
            Require(File.ReadAllText(Path.Combine(profile, "settings.json")) == original,
                "Cancelling appearance edits changed persisted settings.");
        }
        finally
        {
            Texts.SetLanguage("en");
            if (window.IsVisible)
                window.Close();
        }
    }

    private static void SetAppearance(SettingsWindow window)
    {
        window.Editor.FindControl<ComboBox>("ThemeInput")!.SelectedItem = window.Editor.ThemeChoices.Single(choice => choice.Value == "Light");
        window.Editor.FindControl<ComboBox>("WorkspaceModeInput")!.SelectedItem = window.Editor.WorkspaceChoices.Single(choice => choice.Value == "Simple");
        window.Editor.FindControl<ComboBox>("LanguageInput")!.SelectedItem = window.Editor.LanguageChoices.Single(choice => choice.Code == "de");
        window.Editor.FindControl<CheckBox>("CompactDensityInput")!.IsChecked = true;
        Require(window.Editor.SelectedTheme?.Value == "Light" && window.Editor.SelectedWorkspaceMode?.Value == "Simple"
            && window.Editor.SelectedLanguage?.Code == "de" && window.Editor.CompactDensity,
            "Appearance controls did not update the detached settings edit.");
    }

    private static async Task CheckDirectSaveAsync(MainWindow owner, string profile)
    {
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "downloads");
        Directory.CreateDirectory(Path.Combine(profile, "settings.json"));
        SettingsWindow window = new SettingsWindow(config);
        Task dialog = window.ShowDialog(owner);
        try
        {
            await WaitUntilAsync(() => window.IsVisible);
            window.Editor.RecheckTimer = 120;
            Button save = await WaitForButtonAsync(window, "Save & Close");
            Click(save);
            await WaitUntilAsync(() => !window.Editor.IsBusy && window.Editor.HasError);
            Require(window.IsVisible && config.RecheckTimer == 60, "Direct save failure closed the dialog or changed live settings.");
            Require(Directory.Exists(Path.Combine(profile, "settings.json")), "Direct save failure replaced an unreadable settings path.");
            await AssertErrorInViewportAsync(window);
            Directory.Delete(Path.Combine(profile, "settings.json"));
            Click(save);
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
            Require(config.RecheckTimer == 120 && Config.LoadConfig(profile).RecheckTimer == 120, "Direct save retry failed to commit settings.");
        }
        finally
        {
            if (window.IsVisible)
                window.Close();
        }
    }

    private static async Task CheckOwnerCloseWhileBusyAsync(MainWindow owner, string profile)
    {
        Config config = Config.LoadConfig(profile);
        TaskCompletionSource pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Window parent = new Window { Title = "Settings owner-close fixture", Width = 480, Height = 480 };
        parent.Show(owner);
        SettingsWindow window = new SettingsWindow(config, candidate => pending.Task);
        Task dialog = window.ShowDialog(parent);
        try
        {
            await WaitUntilAsync(() => window.IsVisible);
            window.Editor.RecheckTimer = 120;
            Click(FindButton(window, "Save & Close"));
            Require(window.Editor.IsBusy, "Owner-close fixture did not enter pending settings apply.");
            parent.Close();
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!parent.IsVisible && !window.IsVisible, "A busy settings dialog blocked its owner from closing.");
            pending.SetException(new IOException("Apply finished after its owner closed."));
            await WaitUntilAsync(() => !window.Editor.IsBusy);
            Require(config.RecheckTimer == 60, "An apply failure after owner close changed live settings.");
        }
        finally
        {
            pending.TrySetCanceled();
            await WaitUntilAsync(() => !window.Editor.IsBusy);
            if (window.IsVisible)
                window.Close();
            if (parent.IsVisible)
                parent.Close();
        }
    }

    private static async Task CheckPickerAsync(MainWindow owner, string profile)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        FixtureStorageProvider provider = new FixtureStorageProvider();
        AvaloniaLocator.CurrentMutable.Bind<IStorageProviderFactory>().ToConstant(new FixtureStorageProviderFactory(provider));
        Config config = Config.LoadConfig(profile);
        string originalPath = Path.Combine(profile, "original");
        config.DownloadPath = originalPath;
        SettingsWindow window = new SettingsWindow(config);
        Task dialog = window.ShowDialog(owner);
        bool wrongThread = false;
        window.Editor.PropertyChanged += (object? sender, PropertyChangedEventArgs args) =>
        {
            if (args.PropertyName == nameof(SettingsEditor.DownloadPath) && !Dispatcher.UIThread.CheckAccess())
                wrongThread = true;
        };
        TaskCompletionSource<IReadOnlyList<IStorageFolder>> pending = new TaskCompletionSource<IReadOnlyList<IStorageFolder>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await WaitUntilAsync(() => window.IsVisible);
            Button browse = FindButton(window, "Browse");
            provider.CanPickFolder = false;
            Click(browse);
            Require(window.Editor.HasError && !window.Editor.IsBusy && provider.PickerCalls == 0, "Unavailable picker was not handled without starting a dialog.");
            await AssertErrorInViewportAsync(window);

            provider.CanPickFolder = true;
            provider.Result = Task.FromResult<IReadOnlyList<IStorageFolder>>(Array.Empty<IStorageFolder>());
            Click(browse);
            Require(!window.Editor.IsBusy && !window.Editor.HasError && window.Editor.DownloadPath == originalPath, "Cancelling the folder picker changed settings or reported an error.");

            provider.Result = Task.FromException<IReadOnlyList<IStorageFolder>>(new IOException("Synthetic folder picker failure."));
            Click(browse);
            Require(window.Editor.HasError && !window.Editor.IsBusy && window.Editor.DownloadPath == originalPath, "Folder picker failure was not shown without changing the path.");
            await AssertErrorInViewportAsync(window);

            provider.Result = pending.Task;
            Click(browse);
            Require(window.Editor.IsBusy && !browse.IsEffectivelyEnabled, "Pending folder picker did not disable the form.");
            int calls = provider.PickerCalls;
            Click(browse);
            Require(provider.PickerCalls == calls, "Pending folder picker was opened twice.");
            Require(window.PlatformImpl!.Closing!(WindowCloseReason.WindowClosing), "Pending folder picker allowed its parent dialog to close.");
            string selectedPath = Path.Combine(profile, "selected");
            FixtureStorageFolder selected = new FixtureStorageFolder(new Uri(selectedPath));
            await Task.Run(() => pending.SetResult(new IStorageFolder[] { selected }));
            await WaitUntilAsync(() => !window.Editor.IsBusy);
            Require(window.Editor.DownloadPath == selectedPath && !window.Editor.HasError && selected.Disposed, "Local picker result was not applied and disposed.");
            Require(!wrongThread && config.DownloadPath == originalPath, "Folder picker changed live configuration or raised bindings off the UI thread.");

            FixtureStorageFolder remote = new FixtureStorageFolder(new Uri("content://fixture/nonlocal"));
            provider.Result = Task.FromResult<IReadOnlyList<IStorageFolder>>(new IStorageFolder[] { remote });
            Click(browse);
            Require(window.Editor.HasError && window.Editor.DownloadPath == selectedPath && remote.Disposed, "Nonlocal folder was accepted or not disposed.");
            window.Close();
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            pending.TrySetCanceled();
            if (window.IsVisible)
            {
                await WaitUntilAsync(() => !window.Editor.IsBusy);
                window.Close();
            }
        }
    }

    private static async Task AssertErrorInViewportAsync(SettingsWindow window)
    {
        TextBlock error = window.Editor.FindControl<TextBlock>("SettingsError")!;
        ScrollViewer scroll = window.GetVisualDescendants().OfType<ScrollViewer>().First();
        try
        {
            await WaitUntilAsync(() =>
            {
                Point? point = error.TranslatePoint(new Point(0, 0), scroll);
                return error.IsVisible && error.Bounds.Height > 0 && point.HasValue &&
                    point.Value.Y >= -1 && point.Value.Y + error.Bounds.Height <= scroll.Bounds.Height + 1;
            });
        }
        catch
        {
            window.UpdateLayout();
            Console.WriteLine("SMOKE settings error layout failure: window=" + window.Bounds
                + "; state=" + window.WindowState + "; scale=" + window.RenderScaling);
            Console.WriteLine("SMOKE settings error: text=" + error.Text + "; bounds=" + error.Bounds
                + "; window-origin=" + error.TranslatePoint(new Point(0, 0), window)
                + "; scroll-origin=" + error.TranslatePoint(new Point(0, 0), scroll)
                + "; visible=" + error.IsEffectivelyVisible + "; desired=" + error.DesiredSize);
            Console.WriteLine("SMOKE settings scroll: bounds=" + scroll.Bounds
                + "; window-origin=" + scroll.TranslatePoint(new Point(0, 0), window)
                + "; offset=" + scroll.Offset + "; extent=" + scroll.Extent + "; viewport=" + scroll.Viewport);
            Program.CaptureWindow(window, "smoke-settings-error-failure.png");
            throw;
        }
    }

    private static Button FindButton(SettingsWindow window, string content) => window.Editor.FindControl<Button>(content switch
    {
        "Save & Close" => "SaveSettingsButton",
        "Browse" => "BrowseButton",
        "Cancel" => "CancelSettingsButton",
        _ => throw new ArgumentOutOfRangeException(nameof(content))
    }) ?? throw new InvalidOperationException("The settings action is missing: " + content);

    private static async Task<Button> WaitForButtonAsync(SettingsWindow window, string content)
    {
        Button button = FindButton(window, content);
        await WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            if (!button.IsEffectivelyVisible || !button.IsEffectivelyEnabled || button.Bounds.Width <= 0 || button.Bounds.Height <= 0)
                return false;
            Point? position = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
            Visual? hit = position.HasValue ? window.InputHitTest(position.Value) as Visual : null;
            return ReferenceEquals(hit, button) || ReferenceEquals(hit?.FindAncestorOfType<Button>(), button);
        });
        return button;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Settings smoke condition was not met.");
            await Task.Delay(20);
        }
    }

    internal sealed class FixtureStorageProviderFactory : IStorageProviderFactory
    {
        private readonly IStorageProvider _provider;
        public FixtureStorageProviderFactory(IStorageProvider provider) => _provider = provider;
        public IStorageProvider CreateProvider(TopLevel topLevel) => _provider;
    }

    internal sealed class FixtureStorageProvider : IStorageProvider
    {
        public bool CanOpen => false;
        public bool CanSave => false;
        public bool CanPickFolder { get; set; } = true;
        public int PickerCalls { get; private set; }
        public Task<IReadOnlyList<IStorageFolder>> Result { get; set; } = Task.FromResult<IReadOnlyList<IStorageFolder>>(Array.Empty<IStorageFolder>());
        public Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options)
        {
            Require(!options.AllowMultiple, "Settings picker unexpectedly allowed multiple folders.");
            PickerCalls++;
            return Result;
        }
        public Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
        public Task<OpenFilePickerResult> OpenFilePickerWithResultAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
        public Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options) => throw new NotSupportedException();
        public Task<SaveFilePickerResult> SaveFilePickerWithResultAsync(FilePickerSaveOptions options) => throw new NotSupportedException();
        public Task<IStorageBookmarkFile?> OpenFileBookmarkAsync(string bookmark) => throw new NotSupportedException();
        public Task<IStorageBookmarkFolder?> OpenFolderBookmarkAsync(string bookmark) => throw new NotSupportedException();
        public Task<IStorageFile?> TryGetFileFromPathAsync(Uri filePath) => throw new NotSupportedException();
        public Task<IStorageFolder?> TryGetFolderFromPathAsync(Uri folderPath) => throw new NotSupportedException();
        public Task<IStorageFolder?> TryGetWellKnownFolderAsync(WellKnownFolder wellKnownFolder) => throw new NotSupportedException();
    }

    internal sealed class FixtureStorageFolder : IStorageFolder
    {
        public FixtureStorageFolder(Uri path) => Path = path;
        public string Name => "Fixture folder";
        public Uri Path { get; }
        public bool CanBookmark => false;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public Task<StorageItemProperties> GetBasicPropertiesAsync() => throw new NotSupportedException();
        public Task<string?> SaveBookmarkAsync() => throw new NotSupportedException();
        public Task<IStorageFolder?> GetParentAsync() => throw new NotSupportedException();
        public Task DeleteAsync() => throw new NotSupportedException();
        public Task<IStorageItem?> MoveAsync(IStorageFolder destination) => throw new NotSupportedException();
        public IAsyncEnumerable<IStorageItem> GetItemsAsync() => throw new NotSupportedException();
        public Task<IStorageFolder?> GetFolderAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFile?> GetFileAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFile?> CreateFileAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFolder?> CreateFolderAsync(string name) => throw new NotSupportedException();
    }
}
