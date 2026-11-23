using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed class StartupFolderWindow : Window
{
    private readonly StartupFolderSession _session;
    private readonly Action<string, bool> _open;
    private readonly ComboBox _profiles = new() { Name = "StartupFolderProfiles", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _adopt = new() { Name = "StartupFolderAdopt", IsEnabled = false };
    private readonly Button _browse = new() { Name = "StartupFolderBrowse" };
    private readonly Button _temporary = new() { Name = "StartupFolderTemporary" };
    private readonly Button _cancel = new() { Name = "StartupFolderCancel" };
    private readonly TextBlock _error = new();
    private string[] _protectedFolders = Array.Empty<string>();
    private bool _busy;
    private bool _applying;
    private bool _handingOff;

    internal StartupFolderWindow(StartupFolderSession session, Action<string, bool> open)
    {
        _session = session;
        _open = open;
        Name = "StartupFolderWindow";
        Title = Texts.T("profile.folderSession.title");
        DialogWindow.Configure(this, DialogSize.M, modal: true, heightToContent: true);
        // This is the process's first window and has no owner to keep it reachable.
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _adopt.Content = Texts.T("profile.folderSession.adopt");
        _adopt.Classes.Add("primary");
        _temporary.Content = Texts.T("profile.folderSession.temporary");
        _browse.Content = Texts.T("profile.new.choose");
        _cancel.Content = Texts.T("profile.new.cancel");
        _profiles.PlaceholderText = Texts.T("profile.switch.list");
        AutomationProperties.SetName(_browse, Texts.T("profile.new.chooseFolder"));
        TextBlock label = new() { Text = Texts.T("profile.new.folder") };
        label.Classes.Add("workspace-field-label");
        AutomationProperties.SetLabeledBy(_profiles, label);
        Grid profileRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { _profiles, _browse } };
        Grid.SetColumn(_browse, 1);
        StackPanel body = new()
        {
            Spacing = 8,
            Margin = new Thickness(16, 0, 16, 16),
            Children =
            {
                label, profileRow,
                Help("profile.folderSession.adoptHint"),
                Help("profile.folderSession.temporaryHint")
            }
        };
        DialogWindow.Frame(this, DialogFrame.Build(new TextBlock { Text = session.Source, TextWrapping = TextWrapping.Wrap },
            body, DialogFooter.Build(_adopt, new Control[] { _cancel, _temporary }), DialogFrame.ErrorStrip(_error)), modal: true);
        DialogEscape.Attach(this, isBusy: () => _applying);
        Closing += (_, e) => e.Cancel = _applying && !_handingOff;
        Opened += async (_, _) =>
        {
            await RunAsync(LoadProfilesAsync);
            if (IsVisible) _cancel.Focus();
        };
        _cancel.Click += (_, _) => Close();
        _profiles.SelectionChanged += (_, _) => RefreshButtons();
        _browse.Click += async (_, _) => await RunAsync(BrowseAsync);
        _adopt.Click += async (_, _) => await RunAsync(() => StartAsync((string?)_profiles.SelectedItem));
        _temporary.Click += async (_, _) => await RunAsync(() => StartAsync(profile: null));
    }

    private static TextBlock Help(string key)
    {
        TextBlock text = new() { Text = Texts.T(key), TextWrapping = TextWrapping.Wrap };
        text.Classes.Add("workspace-help");
        return text;
    }

    private async Task LoadProfilesAsync()
    {
        (_protectedFolders, string[] profiles) = await Task.Run(ReadProfiles);
        if (!IsVisible) return;
        _profiles.ItemsSource = profiles;
        string? problem = await Task.Run(() => _session.SourceProblem(_protectedFolders));
        if (IsVisible) ShowError(problem);
    }

    private static (string[] Protected, string[] Profiles) ReadProfiles()
    {
        string? registryPath = ProfileRegistry.DefaultFilePath();
        IEnumerable<string> known = registryPath == null ? Array.Empty<string>() :
            new ProfileRegistry(registryPath).Read().Entries.OrderByDescending(entry => entry.LastOpenedUtc).Select(entry => entry.Path);
        List<string> profiles = known.ToList();
        try { profiles.Add(Config.GetConfigDirectory()); }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            FolderSourceErrorText.Log(error, "default profile selection");
        }
        string[] protectedFolders = (registryPath == null ? profiles : profiles.Append(Path.GetDirectoryName(registryPath)!)).ToArray();
        return (protectedFolders, profiles.Distinct(ProfilePathComparer()).Where(Directory.Exists).ToArray());
    }

    private static StringComparer ProfilePathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private async Task BrowseAsync()
    {
        LocalFolderPickResult choice = await LocalFolderPicker.PickAsync(StorageProvider, Texts.T("profile.new.chooseFolder"));
        if (!IsVisible) return;
        if (choice is { Status: LocalFolderPickStatus.Selected, Path: { } path })
        {
            IEnumerable<string> profiles = _profiles.ItemsSource as IEnumerable<string> ?? Array.Empty<string>();
            StringComparer comparer = ProfilePathComparer();
            string[] items = profiles.Append(path).Distinct(comparer).ToArray();
            _profiles.ItemsSource = items;
            _profiles.SelectedItem = items.First(item => comparer.Equals(item, path));
        }
        else if (choice.Status != LocalFolderPickStatus.Cancelled) ShowError("profile.error.folderUnusable");
    }

    private async Task StartAsync(string? profile)
    {
        string language = Texts.Language;
        string? problem = await Task.Run(() => profile == null
            ? _session.SourceProblem(_protectedFolders) : _session.ProfileProblem(profile, _protectedFolders));
        if (!IsVisible) return;
        if (problem != null)
        {
            ShowError(problem);
            return;
        }
        _applying = true;
        RefreshButtons();
        string target = profile ?? await Task.Run(() => _session.PrepareTemporaryProfile(language, _protectedFolders));
        _open(target, profile == null);
        _handingOff = true;
        Close();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        ShowError(null);
        RefreshButtons();
        try { await action(); }
        catch (SettingsRecoveryRequiredException recovery)
        {
            if (IsVisible) _error.Text = recovery.LocalizedMessage();
        }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException or InvalidOperationException
            or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            FolderSourceErrorText.Log(error, "folder session");
            _session.RemoveTemporaryProfile();
            if (IsVisible) ShowError("folderSource.error.io");
        }
        finally
        {
            _busy = false;
            _applying = false;
            if (IsVisible) RefreshButtons();
        }
    }

    private void RefreshButtons()
    {
        _adopt.IsEnabled = !_busy && _profiles.SelectedItem is string;
        _cancel.IsEnabled = !_applying;
        _browse.IsEnabled = _temporary.IsEnabled = _profiles.IsEnabled = !_busy;
    }

    private void ShowError(string? key) => _error.Text = key == null ? string.Empty : Texts.T(key);
}
