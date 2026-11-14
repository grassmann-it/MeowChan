using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using Serilog;

namespace MeowChan;

// Profiles (PLAN-POWER-TAGGEN PR1): "⋯ › Profile ▸ New profile… / Switch profile… / Open profile folder". Another profile
// always opens in a new process (one window per profile, the profile lease keeps it so); this window never changes its
// profile. The list of known profiles lives outside every profile (ProfileRegistry).
public partial class MainWindow
{
    private MenuItem? _profileMenu;
    private Window? _profileDialog;

    /// <summary>Starts the other window. Smokes replace it with a recording fake; nothing else does.</summary>
    internal IProfileLauncher ProfileLauncher { get; set; } = new ProcessProfileLauncher();
    /// <summary>The profile list file; smokes point it into their private folder. Null: the per-user location.</summary>
    internal string? ProfileRegistryPath { get; set; }
    internal Window? ProfileDialogWindow => _profileDialog;

    private bool CanUseProfiles => _restoreCompleted && !_isClosing && IsEnabled && _session.State == DesktopSessionState.Ready
        && !WorkspaceCommandBusy && !HasVisibleShellDialog() && _profileDialog == null;

    private void InitializeProfileMenu()
    {
        if (_profileMenu != null) return;
        _profileMenu = new MenuItem { Name = "ProfileMenuItem" };
        MenuItem create = CommandBindings.CreateMenuItem("profile.new", () => _ = OpenNewProfileDialogAsync(), () => CanUseProfiles);
        create.Name = "ProfileNewMenuItem";
        MenuItem change = CommandBindings.CreateMenuItem("profile.switch", () => _ = OpenSwitchProfileDialogAsync(), () => CanUseProfiles);
        change.Name = "ProfileSwitchMenuItem";
        MenuItem folder = CommandBindings.CreateMenuItem("profile.open-folder", OpenProfileFolder, () => !_isClosing);
        folder.Name = "ProfileFolderMenuItem";
        _profileMenu.Items.Add(create);
        _profileMenu.Items.Add(change);
        _profileMenu.Items.Add(new Separator());
        _profileMenu.Items.Add(folder);
        // Application group, in front of Settings: … View, Profile, Settings, Keyboard shortcuts, About.
        int at = WorkspaceMenuItem.Items.IndexOf(SettingsMenuItem);
        WorkspaceMenuItem.Items.Insert(at < 0 ? WorkspaceMenuItem.Items.Count : at, _profileMenu);
        _profileMenu.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem item in new[] { create, change, folder })
            {
                (item.Command as DelegateCommand)?.RaiseCanExecuteChanged();
                item.IsEnabled = item.Command?.CanExecute(null) ?? true;
            }
        };
        _shortcutRouter?.Register(new CommandRegistration("profile.new", this, () => true, () => CanUseProfiles, () => _ = OpenNewProfileDialogAsync()));
        _shortcutRouter?.Register(new CommandRegistration("profile.switch", this, () => true, () => CanUseProfiles, () => _ = OpenSwitchProfileDialogAsync()));
        _shortcutRouter?.Register(new CommandRegistration("profile.open-folder", this, () => true, () => !_isClosing, OpenProfileFolder));
        Texts.Changed += OnProfileMenuLanguageChanged;
        Closed += (_, _) => Texts.Changed -= OnProfileMenuLanguageChanged;
        RefreshProfileMenu();
        // An ordinary start records this profile in the list once the session is up (never for environment-chosen
        // profiles, tests or smokes: ProfileStartup.RecordsInProfileList).
        if (ProfileStartup.RecordsInProfileList) Dispatcher.UIThread.Post(() => _ = RecordProfileAfterRestoreAsync(), DispatcherPriority.Background);
    }

    private void OnProfileMenuLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(RefreshProfileMenu);

    private void RefreshProfileMenu()
    {
        if (_profileMenu == null) return;
        _profileMenu.Header = Texts.T("profile.menu");
        _profileMenu.Icon = CommandBindings.IconFromKey("folder");
        AutomationProperties.SetName(_profileMenu, Texts.T("profile.menu"));
    }

    internal ProfileSwitchService CreateProfileService()
    {
        string? path = ProfileRegistryPath ?? ProfileRegistry.DefaultFilePath();
        return new ProfileSwitchService(path == null ? null : new ProfileRegistry(path), ProfileLauncher, _session.ProfileDirectory,
            _config.DownloadPath, () => _config.ProfileRegistryLimit);
    }

    private void OpenProfileFolder() { if (!_isClosing) OpenDirectory(_session.ProfileDirectory); }

    internal async Task OpenNewProfileDialogAsync()
    {
        if (!CanUseProfiles) return;
        NewProfileDialog dialog = new(CreateProfileService(), _config.Language);
        _profileDialog = dialog;
        ProfileActionResult? result;
        try { result = await dialog.ShowDialog<ProfileActionResult?>(this); }
        finally { if (ReferenceEquals(_profileDialog, dialog)) _profileDialog = null; }
        if (result != null) ReportProfileStarted(result);
    }

    internal async Task OpenSwitchProfileDialogAsync()
    {
        if (!CanUseProfiles) return;
        SwitchProfileDialog dialog = new(CreateProfileService());
        _profileDialog = dialog;
        ProfileSwitchOutcome? outcome;
        try { outcome = await dialog.ShowDialog<ProfileSwitchOutcome?>(this); }
        finally { if (ReferenceEquals(_profileDialog, dialog)) _profileDialog = null; }
        if (outcome == null || _isClosing) return;
        if (outcome.NewProfileRequested) { await OpenNewProfileDialogAsync(); return; }
        if (outcome.Result is not { } result) return;
        ReportProfileStarted(result);
        if (outcome.CloseThisWindow) _ = CloseApplicationAsync();
    }

    private void ReportProfileStarted(ProfileActionResult result)
    {
        if (_isClosing) return;
        string message = Texts.T(result.MessageKey);
        if (result.ListNoteKey is { } note)
            message += " " + (note == "profile.note.listFull"
                ? Texts.T(note, result.ListNoteArgument, Texts.T("budgets.profileRegistry"), Texts.T("settings.storageLimits"))
                : Texts.T(note));
        ShowNotification(Texts.T("profile.menu"), message,
            result.ListNoteKey == null ? NotificationType.Information : NotificationType.Warning, operation: "profile.open");
    }

    private async Task RecordProfileAfterRestoreAsync()
    {
        try
        {
            await _restoreTask;
            if (_isClosing || _session.State != DesktopSessionState.Ready) return;
            ProfileSwitchService service = CreateProfileService();
            DesktopSessionHost session = _session;
            await Task.Run(() => service.RecordCurrent(session.EnsureProfileIdentity()?.Id));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The list is a convenience: a failure never reaches the person.
            Log.Warning(error, "This profile could not be recorded in the profile list.");
        }
    }
}
