using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Supplements existing picker-status tests with path draft/focus/persistence transactions.
// Selection is injected: these checks do not open or claim coverage of native dialogs.
internal static class SettingsPathInputChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-path-input-" + Guid.NewGuid().ToString("N"));
        string language = Texts.Language;
        try
        {
            await SettingsChecks.CheckPickerAsync(owner, Path.Combine(profile, "existing-picker-lifetime"));
            foreach (string current in new[] { "en", "de" })
            {
                Texts.SetLanguage(current);
                await CheckRejectedDraftAndDiscard(owner, Path.Combine(profile, current));
            }
            Texts.SetLanguage("en");
            await CheckSave(owner, Path.Combine(profile, "manual-save"), select: false);
            await CheckSave(owner, Path.Combine(profile, "selected-save"), select: true);
            await CheckDestinationFailures(owner, Path.Combine(profile, "destination-failures"));
            Console.WriteLine("SMOKE path input: injected EN/DE rejection/raw/focus/discard and manual/selected save-failure/retry/reload gates passed; native dialogs and native keystrokes not exercised.");
        }
        finally
        {
            Texts.SetLanguage(language);
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task CheckRejectedDraftAndDiscard(MainWindow owner, string profile)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        var provider = new SettingsChecks.FixtureStorageProvider();
        AvaloniaLocator.CurrentMutable.Bind<IStorageProviderFactory>().ToConstant(new SettingsChecks.FixtureStorageProviderFactory(provider));
        Config config = Seed(profile);
        string originalPath = config.DownloadPath;
        string file = Path.Combine(profile, "settings.json");
        byte[] original = File.ReadAllBytes(file);
        var window = new SettingsWindow(config);
        Task dialog = window.ShowDialog(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            await SettingsSectionChecks.OpenAsync(window.Editor, "settings.downloads");
            var path = window.Editor.FindControl<TextBox>("DownloadPathInput")!;
            var browse = window.Editor.FindControl<Button>("BrowseButton")!;
            string raw = "relative unfinished folder ü 日本 " + new string('x', 100);
            window.UpdateLayout();
            path.BringIntoView();
            await Program.WaitUntilAsync(() => path.IsEffectivelyVisible && path.Bounds.Height > 0);
            Require(path.Focus(), "Path editor could not receive focus.");
            path.Text = raw;
            await Program.WaitUntilAsync(() => window.Editor.DownloadPath == raw);
            var peer = ControlAutomationPeer.CreatePeerForElement(path)!;
            Require(peer.GetName() == window.Editor.DownloadPathLabel
                && peer.GetLabeledBy()?.GetName() == window.Editor.DownloadPathLabel
                && peer.GetHelpText() == window.Editor.DownloadPathHelp,
                "Actual path editor lost localized name, label relation or help.");
            Preserve(raw);
            provider.CanPickFolder = false;
            await Pick("unavailable", Texts.T("settings.pickerUnavailable"));
            Require(provider.PickerCalls == 0, "Unavailable picker opened a dialog.");
            provider.CanPickFolder = true;
            provider.Result = Task.FromResult<IReadOnlyList<IStorageFolder>>(Array.Empty<IStorageFolder>());
            await Pick("cancelled", null);
            provider.Result = Task.FromException<IReadOnlyList<IStorageFolder>>(new IOException("Synthetic path input failure"));
            await Pick("failed", Texts.T("settings.pickerFailed", "Synthetic path input failure"));
            var remote = new SettingsChecks.FixtureStorageFolder(new Uri("content://fixture/remote-path"));
            provider.Result = Task.FromResult<IReadOnlyList<IStorageFolder>>(new IStorageFolder[] { remote });
            await Pick("nonlocal", Texts.T("settings.pickerLocalOnly"));
            Require(remote.Disposed, "Rejected nonlocal folder escaped disposal.");

            string selectedPath = Path.Combine(profile, "selected folder ü 日本");
            var selected = new SettingsChecks.FixtureStorageFolder(new Uri(selectedPath));
            provider.Result = Task.FromResult<IReadOnlyList<IStorageFolder>>(new IStorageFolder[] { selected });
            Click(browse);
            await Program.WaitUntilAsync(() => !window.Editor.IsBusy && path.Text == selectedPath && browse.IsFocused);
            Require(selected.Disposed && !window.Editor.HasError, "Local selection failed or escaped disposal.");
            Preserve(selectedPath);
            Click(window.Editor.FindControl<Button>("CancelSettingsButton")!);
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
            Require(config.DownloadPath == originalPath && Config.LoadConfig(profile).DownloadPath == originalPath
                && File.ReadAllBytes(file).SequenceEqual(original), "Discard persisted the selected path.");
            Console.WriteLine($"SMOKE path input {Texts.Language}: raw rejection, localization, return focus, selection and discard passed.");

            async Task Pick(string step, string? error)
            {
                path.BringIntoView();
                Require(path.Focus(), "Could not prepare path focus for " + step);
                Click(browse);
                await Program.WaitUntilAsync(() => !window.Editor.IsBusy && browse.IsFocused);
                Require(window.Editor.ErrorMessage == error && window.Editor.HasError == (error != null),
                    "Picker error was not localized or cancellation reported an error: " + step);
                Preserve(raw);
            }
            void Preserve(string expected)
            {
                Require(path.Text == expected && window.Editor.DownloadPath == expected,
                    "Picker replaced the path raw text/draft.");
                Require(config.DownloadPath == originalPath && File.ReadAllBytes(file).SequenceEqual(original),
                    "Editing or selecting a path changed active or persisted settings before Save.");
            }
        }
        finally
        {
            if (window.IsVisible) window.Close();
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task CheckSave(MainWindow owner, string profile, bool select)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        var provider = new SettingsChecks.FixtureStorageProvider();
        AvaloniaLocator.CurrentMutable.Bind<IStorageProviderFactory>().ToConstant(new SettingsChecks.FixtureStorageProviderFactory(provider));
        Config config = Seed(profile);
        string originalPath = config.DownloadPath;
        string file = Path.Combine(profile, "settings.json");
        string backup = file + ".fixture";
        byte[] original = File.ReadAllBytes(file);
        string expected = Path.Combine(profile, "chosen folder ü 日本");
        string raw = select ? expected : Path.GetRelativePath(config.DownloadPathBaseDirectory, expected);
        Directory.CreateDirectory(expected); // private fixture setup, never product validation
        string retained = Path.Combine(expected, "existing.tmp");
        File.WriteAllText(retained, "unchanged private fixture");
        var window = new SettingsWindow(config);
        Task dialog = window.ShowDialog(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            await SettingsSectionChecks.OpenAsync(window.Editor, "settings.downloads");
            var path = window.Editor.FindControl<TextBox>("DownloadPathInput")!;
            var save = window.Editor.FindControl<Button>("SaveSettingsButton")!;
            window.Editor.MinimumFreeSpaceGiB = 0;
            window.Editor.FindControl<NumericUpDown>("ReserveInput")!.Value = 0;
            if (select)
            {
                var folder = new SettingsChecks.FixtureStorageFolder(new Uri(raw));
                provider.Result = Task.FromResult<IReadOnlyList<IStorageFolder>>(new IStorageFolder[] { folder });
                Click(window.Editor.FindControl<Button>("BrowseButton")!);
                await Program.WaitUntilAsync(() => !window.Editor.IsBusy && folder.Disposed && path.Text == raw);
            }
            else
            {
                path.Text = string.Empty;
                await Program.WaitUntilAsync(() => window.Editor.DownloadPath == string.Empty);
                Click(save);
                await Program.WaitUntilAsync(() => !window.Editor.IsBusy && window.Editor.HasError && path.IsFocused);
                TextBlock error = window.Editor.FindControl<TextBlock>("DownloadPathInputError")!;
                Require(path.Text == string.Empty && window.Editor.DownloadPath == string.Empty
                    && error.IsEffectivelyVisible && error.Text == Texts.T("settings.pathInvalid")
                    && AutomationProperties.GetHelpText(path) == error.Text
                    && config.DownloadPath == originalPath && File.ReadAllBytes(file).SequenceEqual(original),
                    "Invalid path Save lost raw text, failed to focus/name its error or changed settings.");
                path.Text = raw;
                await Program.WaitUntilAsync(() => window.Editor.DownloadPath == raw);
            }
            Require(config.DownloadPath == originalPath && File.ReadAllBytes(file).SequenceEqual(original),
                "Path input committed before Save.");
            // A real filesystem write failure, confined to the generated profile.
            File.Move(file, backup);
            Directory.CreateDirectory(file);
            Click(save);
            await Program.WaitUntilAsync(() => !window.Editor.IsBusy && window.Editor.HasError);
            Require(window.IsVisible && window.Editor.HasErrorDetails && config.DownloadPath == originalPath
                && path.Text == raw && window.Editor.DownloadPath == raw
                && Directory.Exists(file) && File.ReadAllBytes(backup).SequenceEqual(original),
                "Save failure lost path raw text/draft, closed the editor or changed the active/file configuration.");
            Directory.Delete(file);
            File.Move(backup, file);
            Click(save);
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
            Config reloaded = Config.LoadConfig(profile);
            Require(!window.Editor.HasError && config.DownloadPath == expected && reloaded.DownloadPath == expected,
                "Successful path save/reload did not preserve existing relative/local path semantics.");
            Require(Directory.Exists(expected) && File.ReadAllText(retained) == "unchanged private fixture"
                && Directory.GetFiles(expected).SequenceEqual(new[] { retained }),
                "Check/Apply changed an existing file or left an owned probe behind.");
            Console.WriteLine($"SMOKE path input {(select ? "selected" : "manual relative")}: save failure retains raw draft and active config; retry/reload passed.");
        }
        finally
        {
            if (Directory.Exists(file)) Directory.Delete(file);
            if (File.Exists(backup)) File.Move(backup, file, overwrite: true);
            if (window.IsVisible) window.Close();
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task CheckDestinationFailures(MainWindow owner, string profile)
    {
        Config config = Seed(profile);
        byte[] original = File.ReadAllBytes(Path.Combine(profile, "settings.json"));
        string oldTarget = config.DownloadPath;
        var window = new SettingsWindow(config);
        Task dialog = window.ShowDialog(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            await SettingsSectionChecks.OpenAsync(window.Editor, "settings.downloads");
            TextBox path = window.Editor.FindControl<TextBox>("DownloadPathInput")!;
            Button check = window.Editor.FindControl<Button>("CheckDestinationButton")!;
            string missing = Path.Combine(profile, "does-not-exist");
            path.Text = missing;
            await Program.WaitUntilAsync(() => window.Editor.DownloadPath == missing);
            Require(window.Editor.NormalizedDestination.Contains(missing), "Absolute path preview is missing.");
            Click(check);
            await Program.WaitUntilAsync(() => !window.Editor.IsBusy && window.Editor.HasError && path.IsFocused);
            Require(window.Editor.DestinationStatusText == Texts.T("destination.Missing") && !Directory.Exists(missing)
                && path.Text == missing && path.IsFocused, "Missing folder check created a directory or lost the draft/field error.");
            string file = Path.Combine(profile, "existing-file.tmp");
            File.WriteAllText(file, "preserve");
            path.Text = file;
            await Program.WaitUntilAsync(() => window.Editor.DownloadPath == file);
            Click(window.Editor.FindControl<Button>("SaveSettingsButton")!);
            await Program.WaitUntilAsync(() => !window.Editor.IsBusy && window.Editor.HasError);
            Require(window.Editor.DestinationStatusText == Texts.T("destination.NotDirectory") && File.ReadAllText(file) == "preserve"
                && config.DownloadPath == oldTarget && File.ReadAllBytes(Path.Combine(profile, "settings.json")).SequenceEqual(original),
                "File target was applied, modified, or reported as a directory.");
            Console.WriteLine("SMOKE destination: explicit Check missing / Apply file target; raw draft, focused field, originals and config preserved; no mkdir.");
        }
        finally
        {
            if (window.IsVisible) window.Close();
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static Config Seed(string profile)
    {
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "original");
        config.SaveConfig();
        return config;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
