using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>"New profile…": name, profile folder and its own download folder. The title stands only in the title bar; the
/// footer holds Cancel and "Create and open". Closes with the started result, or null when nothing was started.</summary>
internal sealed class NewProfileDialog : Window
{
    private readonly ProfileSwitchService _service;
    private readonly string? _language;
    private readonly TextBlock _intro = new() { Name = "NewProfileIntro", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _nameLabel = new() { Name = "NewProfileNameLabel" };
    private readonly TextBox _name = new() { Name = "NewProfileName", MaxLength = ProfileRegistry.MaximumNameLength };
    private readonly TextBlock _folderLabel = new() { Name = "NewProfileFolderLabel" };
    private readonly TextBox _folder = new() { Name = "NewProfileFolder", MaxLength = ProfileRegistry.MaximumPathLength };
    private readonly Button _chooseFolder = new() { Name = "NewProfileChooseFolder" };
    private readonly TextBlock _folderHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _existing = new() { Name = "NewProfileExisting", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _downloadsLabel = new() { Name = "NewProfileDownloadsLabel" };
    private readonly TextBox _downloads = new() { Name = "NewProfileDownloads", MaxLength = ProfileRegistry.MaximumPathLength };
    private readonly Button _chooseDownloads = new() { Name = "NewProfileChooseDownloads" };
    private readonly TextBlock _downloadsHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _mediaOption = new() { Name = "NewProfileMediaOption" };
    private readonly TextBlock _mediaLabel = new() { Name = "NewProfileMediaLabel" };
    private readonly TextBox _media = new() { Name = "NewProfileMedia", MaxLength = ProfileRegistry.MaximumPathLength };
    private readonly Button _chooseMedia = new() { Name = "NewProfileChooseMedia" };
    private readonly TextBlock _mediaHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _mediaRow = new() { Spacing = 4, IsVisible = false };
    private readonly TextBlock _error = new() { Name = "NewProfileError", TextWrapping = TextWrapping.Wrap };
    private readonly Button _cancel = new() { Name = "NewProfileCancel" };
    private readonly Button _create = new() { Name = "NewProfileCreate" };
    private readonly StackPanel _downloadsRow = new() { Spacing = 4 };
    private string? _errorKey;
    private bool _folderEdited, _downloadsEdited, _syncing, _busy, _closed;

    internal ProfileActionResult? Result { get; private set; }
    internal TextBox NameBox => _name;
    internal TextBox FolderBox => _folder;
    internal TextBox DownloadsBox => _downloads;
    internal Button CreateButton => _create;
    internal CheckBox MediaOption => _mediaOption;
    internal TextBox MediaBox => _media;
    internal TextBlock ErrorText => _error;
    internal TextBlock ExistingNotice => _existing;
    /// <summary>Folder picker seam (smokes): returns the chosen folder or null.</summary>
    internal Func<string, Task<string?>>? ChooseFolder { get; set; }

    internal NewProfileDialog(ProfileSwitchService service, string? language)
    {
        _service = service; _language = language;
        Name = "NewProfileDialog";
        Width = 600; Height = 680; MinWidth = 380; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _intro.Classes.Add("workspace-help");
        foreach (TextBlock label in new[] { _nameLabel, _folderLabel, _downloadsLabel, _mediaLabel }) label.Classes.Add("workspace-field-label");
        _folderHint.Classes.Add("workspace-help"); _downloadsHint.Classes.Add("workspace-help"); _mediaHint.Classes.Add("workspace-help"); _existing.Classes.Add("workspace-meta");
        AutomationProperties.SetLiveSetting(_existing, AutomationLiveSetting.Polite);
        AutomationProperties.SetLabeledBy(_name, _nameLabel);
        AutomationProperties.SetLabeledBy(_folder, _folderLabel);
        AutomationProperties.SetLabeledBy(_downloads, _downloadsLabel);
        AutomationProperties.SetLabeledBy(_media, _mediaLabel);
        _cancel.Classes.Add("subtle"); _create.Classes.Add("primary");

        Grid FolderRow(TextBox box, Button choose)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(box); row.Children.Add(choose); Grid.SetColumn(choose, 1);
            return row;
        }
        _downloadsRow.Children.Add(_downloadsLabel); _downloadsRow.Children.Add(FolderRow(_downloads, _chooseDownloads)); _downloadsRow.Children.Add(_downloadsHint);
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(16, 0, 16, 16) };
        StackPanel nameRow = new() { Spacing = 4, Children = { _nameLabel, _name } };
        StackPanel folderRow = new() { Spacing = 4, Children = { _folderLabel, FolderRow(_folder, _chooseFolder), _folderHint, _existing } };
        _mediaRow.Children.Add(_mediaLabel); _mediaRow.Children.Add(FolderRow(_media, _chooseMedia)); _mediaRow.Children.Add(_mediaHint);
        body.Children.Add(nameRow); body.Children.Add(folderRow); body.Children.Add(_downloadsRow);
        body.Children.Add(_mediaOption); body.Children.Add(_mediaRow);
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel, _create } };
        Content = WindowChrome.Frame(this, DialogFrame.Build(_intro, body, actions, DialogFrame.ErrorStrip(_error, maxHeight: 96)), WindowChromeKind.Dialog);

        _name.TextChanged += (_, _) => { if (!_syncing) { SuggestFolders(); ClearError(); } };
        _folder.TextChanged += (_, _) => { if (!_syncing) { _folderEdited = true; ClearError(); } RefreshFolderKind(); };
        _downloads.TextChanged += (_, _) => { if (!_syncing) { _downloadsEdited = true; ClearError(); } };
        _chooseFolder.Click += async (_, _) => await ChooseAsync(_folder, "profile.new.chooseFolder");
        _chooseDownloads.Click += async (_, _) => await ChooseAsync(_downloads, "profile.new.chooseDownloads");
        _chooseMedia.Click += async (_, _) => await ChooseAsync(_media, "profile.new.chooseMedia");
        _media.TextChanged += (_, _) => ClearError();
        _mediaOption.IsCheckedChanged += (_, _) =>
        {
            _mediaRow.IsVisible = _mediaOption.IsChecked == true;
            ClearError();
            if (_mediaOption.IsChecked == true) _media.Focus();
        };
        _cancel.Click += (_, _) => { if (!_busy) Close(); };
        _create.Click += async (_, _) => await CreateAsync();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => { _closed = true; Texts.Changed -= OnLanguageChanged; };
        Opened += (_, _) => _name.Focus();
        Texts.Changed += OnLanguageChanged;
        ApplyTexts();
        SuggestFolders();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ApplyTexts);

    private void ApplyTexts()
    {
        if (_closed) return;
        Title = Texts.T("profile.new.title");
        _intro.Text = Texts.T("profile.new.intro");
        _nameLabel.Text = Texts.T("profile.new.name");
        _folderLabel.Text = Texts.T("profile.new.folder");
        _folderHint.Text = Texts.T("profile.new.folderHint");
        _existing.Text = Texts.T("profile.new.existing");
        _downloadsLabel.Text = Texts.T("profile.new.downloads");
        _downloadsHint.Text = Texts.T("profile.new.downloadsHint");
        _mediaOption.Content = Texts.T("profile.new.media.option");
        _mediaLabel.Text = Texts.T("profile.new.media.folder");
        _mediaHint.Text = Texts.T("profile.new.media.hint");
        _chooseFolder.Content = _chooseDownloads.Content = _chooseMedia.Content = Texts.T("profile.new.choose");
        AutomationProperties.SetName(_chooseMedia, Texts.T("profile.new.chooseMedia"));
        AutomationProperties.SetName(_chooseFolder, Texts.T("profile.new.chooseFolder"));
        AutomationProperties.SetName(_chooseDownloads, Texts.T("profile.new.chooseDownloads"));
        _cancel.Content = Texts.T("profile.new.cancel");
        RefreshFolderKind();
        _error.Text = _errorKey == null ? string.Empty : Texts.T(_errorKey);
    }

    /// <summary>Until the person edits a folder, both follow the name: <c>.MeowChan-profiles/&lt;name&gt;</c> and
    /// <c>MeowChan-&lt;name&gt;</c> beside this window's download folder, each the first that does not exist yet.</summary>
    private void SuggestFolders()
    {
        string slug = ProfileOpenPolicy.Slug(_name.Text ?? string.Empty);
        _syncing = true;
        try
        {
            if (!_folderEdited && ProfileOpenPolicy.DefaultProfilesParent() is { } parent)
                _folder.Text = ProfileOpenPolicy.FreeFolder(parent, slug);
            if (!_downloadsEdited) _downloads.Text = ProfileOpenPolicy.SuggestDownloadFolder(_service.CurrentDownloadPath, slug);
        }
        finally { _syncing = false; }
    }

    private void RefreshFolderKind()
    {
        string folder = (_folder.Text ?? string.Empty).Trim();
        bool existing = folder.Length > 0 && System.IO.Path.IsPathFullyQualified(folder)
            && ProfileOpenPolicy.Classify(folder) == ProfileFolderKind.Existing;
        _existing.IsVisible = existing;
        // An existing profile keeps its own download folder; the field does not apply.
        _downloadsRow.IsEnabled = !existing && !_busy;
        _create.Content = Texts.T(existing ? "profile.new.open" : "profile.new.create");
        AutomationProperties.SetName(_create, (string)_create.Content!);
    }

    private void ClearError() { _errorKey = null; _error.Text = string.Empty; }

    private void ShowError(string key) { _errorKey = key; _error.Text = Texts.T(key); }

    private async Task ChooseAsync(TextBox target, string titleKey)
    {
        if (_busy) return;
        string? chosen;
        if (ChooseFolder != null) chosen = await ChooseFolder(titleKey);
        else
        {
            LocalFolderPickResult result = await LocalFolderPicker.PickAsync(StorageProvider, Texts.T(titleKey));
            chosen = result.Status == LocalFolderPickStatus.Selected ? result.Path : null;
        }
        if (_closed || string.IsNullOrWhiteSpace(chosen)) return;
        target.Text = chosen;
        target.Focus();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; if (!_busy) Close(); }
        else if (e.Key == Key.Enter && e.Source is TextBox) { e.Handled = true; _ = CreateAsync(); }
    }

    internal async Task CreateAsync()
    {
        if (_busy || _closed) return;
        string name = (_name.Text ?? string.Empty).Trim(), folder = (_folder.Text ?? string.Empty).Trim(), downloads = (_downloads.Text ?? string.Empty).Trim();
        // The option is on: the folder is checked like "Add folder…" does (it must exist, be readable and be no profile or download
        // folder) before anything is created or started, and it is never created here.
        string? media = _mediaOption.IsChecked == true ? (_media.Text ?? string.Empty).Trim() : null;
        string? problem = media is { Length: 0 } ? "profile.error.mediaAbsolute" : _service.Validate(name, folder, downloads, media);
        if (problem != null)
        {
            ShowError(problem);
            (problem.StartsWith("profile.error.name", StringComparison.Ordinal) ? _name : problem.StartsWith("profile.error.downloads", StringComparison.Ordinal) ? _downloads
                : problem.StartsWith("profile.error.media", StringComparison.Ordinal) ? _media : (Control)_folder).Focus();
            return;
        }
        SetBusy(true);
        ProfileActionResult result;
        try { result = await Task.Run(() => _service.Create(new NewProfileRequest(name, folder, downloads, _language, media))); }
        finally { SetBusy(false); }
        if (_closed) return;
        if (result.Status == ProfileActionStatus.Started) { Result = result; Close(result); return; }
        ShowError(result.MessageKey);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (Control control in new Control[] { _name, _folder, _chooseFolder, _mediaOption, _media, _chooseMedia, _cancel, _create }) control.IsEnabled = !busy;
        RefreshFolderKind();
    }
}

/// <summary>What "Switch profile…" ended with.</summary>
internal sealed record ProfileSwitchOutcome(ProfileActionResult? Result, bool CloseThisWindow, bool NewProfileRequested);

/// <summary>"Switch profile…": the list of known profiles with their state, Open and "Remove from list" per row, the
/// current profile first. Opening starts a new window; this one closes only when the person ticked the option.</summary>
internal sealed class SwitchProfileDialog : Window
{
    private readonly ProfileSwitchService _service;
    private readonly TextBlock _intro = new() { Name = "SwitchProfileIntro", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _listLabel = new() { Name = "SwitchProfileListLabel" };
    private readonly TextBlock _listState = new() { Name = "SwitchProfileListState", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _rows = new() { Name = "SwitchProfileRows", Spacing = 8 };
    private readonly TextBlock _empty = new() { Name = "SwitchProfileEmpty", TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _closeAfter = new() { Name = "SwitchProfileCloseAfter" };
    private readonly TextBlock _error = new() { Name = "SwitchProfileError", TextWrapping = TextWrapping.Wrap };
    private readonly Button _newProfile;
    private readonly Button _close = new() { Name = "SwitchProfileClose" };
    private string? _errorKey;
    private bool _busy, _closed;
    internal StackPanel Rows => _rows;
    internal CheckBox CloseAfterBox => _closeAfter;
    internal TextBlock ErrorText => _error;
    internal TextBlock EmptyText => _empty;

    internal SwitchProfileDialog(ProfileSwitchService service)
    {
        _service = service;
        Name = "SwitchProfileDialog";
        Width = 640; Height = 560; MinWidth = 400; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _intro.Classes.Add("workspace-help");
        _listLabel.Classes.Add("workspace-field-label");
        _listState.Classes.Add("workspace-meta");
        _empty.Classes.Add("workspace-help");
        _newProfile = Commands.CommandBindings.CreateButton("profile.new",
            () => { if (!_busy) Close(new ProfileSwitchOutcome(null, false, NewProfileRequested: true)); }, () => !_busy);
        _newProfile.Name = "SwitchProfileNew";
        _close.Classes.Add("subtle");
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(16, 0, 16, 16), Children = { _listLabel, _listState, _rows, _empty, _closeAfter } };
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _newProfile, _close } };
        Content = WindowChrome.Frame(this, DialogFrame.Build(_intro, body, actions, DialogFrame.ErrorStrip(_error, maxHeight: 96)), WindowChromeKind.Dialog);
        _close.Click += (_, _) => { if (!_busy) Close(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (!_busy) Close(); } };
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => { _closed = true; Texts.Changed -= OnLanguageChanged; };
        Opened += (_, _) => (_rows.GetLogicalDescendantsButtons().FirstOrDefault(button => button.IsEnabled) ?? (Control)_close).Focus();
        Texts.Changed += OnLanguageChanged;
        ApplyTexts();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ApplyTexts);

    private void ApplyTexts()
    {
        if (_closed) return;
        Title = Texts.T("profile.switch.title");
        _intro.Text = Texts.T("profile.switch.intro");
        _listLabel.Text = Texts.T("profile.switch.list");
        _empty.Text = Texts.T("profile.switch.empty");
        _closeAfter.Content = Texts.T("profile.switch.closeAfter");
        _close.Content = Texts.T("profile.switch.close");
        _error.Text = _errorKey == null ? string.Empty : Texts.T(_errorKey);
        Refresh();
    }

    /// <summary>One row per profile: the current one first, then the list in its order. States are read now.</summary>
    internal void Refresh()
    {
        if (_closed) return;
        ProfileRegistrySnapshot snapshot = _service.ReadList();
        _listState.Text = snapshot.State switch
        {
            ProfileRegistryState.Damaged => Texts.T("profile.switch.listDamaged"),
            ProfileRegistryState.RecoveredFromBackup => Texts.T("profile.switch.listRecovered"),
            ProfileRegistryState.Unsupported => Texts.T("profile.switch.listNewer"),
            _ => string.Empty
        };
        _listState.IsVisible = _listState.Text.Length > 0;
        ProfileRegistryEntry current = snapshot.Entries.FirstOrDefault(entry => ProfileRegistry.SamePath(entry.Path, _service.CurrentProfile))
            ?? new ProfileRegistryEntry(ProfileRegistry.NormalizeName(null, _service.CurrentProfile), _service.CurrentProfile, null, null);
        _rows.Children.Clear();
        _rows.Children.Add(Row(current, isCurrent: true, canForget: false));
        int others = 0;
        foreach (ProfileRegistryEntry entry in snapshot.Entries.Where(entry => !ProfileRegistry.SamePath(entry.Path, _service.CurrentProfile)))
        {
            _rows.Children.Add(Row(entry, isCurrent: false, canForget: snapshot.State != ProfileRegistryState.Unsupported));
            others++;
        }
        _empty.IsVisible = others == 0;
    }

    private Control Row(ProfileRegistryEntry entry, bool isCurrent, bool canForget)
    {
        bool missing = !isCurrent && !System.IO.Directory.Exists(entry.Path);
        bool inUse = !isCurrent && !missing && ProfileLease.IsInUse(entry.Path);
        string state = isCurrent ? Texts.T("profile.state.current") : missing ? Texts.T("profile.state.missing")
            : inUse ? Texts.T("profile.state.inUse") : entry.LastOpenedUtc is { } opened
            ? Texts.T("profile.state.lastOpened", opened.ToLocalTime().ToString("g", Texts.Culture)) : Texts.T("profile.state.never");
        TextBlock name = new() { Text = entry.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        TextBlock path = new() { Text = entry.Path, TextWrapping = TextWrapping.Wrap };
        path.Classes.Add("workspace-meta");
        TextBlock status = new() { Text = state, TextWrapping = TextWrapping.Wrap };
        status.Classes.Add(missing ? "workspace-error" : "workspace-meta");
        StackPanel text = new() { Spacing = 2, Children = { name, path, status } };
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(text);
        AutomationProperties.SetName(row, entry.Name + ", " + state);
        if (!isCurrent)
        {
            Button open = new() { Content = Texts.T("profile.switch.open"), IsEnabled = !missing && !inUse, Tag = entry.Path };
            open.Classes.Add("primary");
            AutomationProperties.SetName(open, Texts.T("profile.switch.openName", entry.Name));
            open.Click += async (_, _) => await OpenAsync(entry.Path);
            StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { open } };
            if (canForget)
            {
                Button forget = new() { Content = Texts.T("profile.switch.forget") };
                forget.Classes.Add("subtle");
                AutomationProperties.SetName(forget, Texts.T("profile.switch.forgetName", entry.Name));
                ToolTip.SetTip(forget, Texts.T("profile.switch.forgetName", entry.Name));
                forget.Click += (_, _) => { if (!_busy && _service.Forget(entry.Path)) Refresh(); };
                buttons.Children.Add(forget);
            }
            row.Children.Add(buttons); Grid.SetColumn(buttons, 1);
        }
        return new Border { Child = row, Padding = new Thickness(12, 8), Classes = { "workspace-card" } };
    }

    internal async Task OpenAsync(string folder)
    {
        if (_busy || _closed) return;
        _busy = true; _errorKey = null; _error.Text = string.Empty;
        SetRowsEnabled(false);
        ProfileActionResult result;
        try { result = await Task.Run(() => _service.Open(folder)); }
        finally { _busy = false; SetRowsEnabled(true); }
        if (_closed) return;
        if (result.Status == ProfileActionStatus.Started) { Close(new ProfileSwitchOutcome(result, _closeAfter.IsChecked == true, false)); return; }
        _errorKey = result.MessageKey; _error.Text = Texts.T(result.MessageKey);
        Refresh();
    }

    private void SetRowsEnabled(bool enabled) { _rows.IsEnabled = enabled; _closeAfter.IsEnabled = enabled; _close.IsEnabled = enabled; Commands.CommandBindings.Refresh(_newProfile); }
}

internal static class ProfileDialogVisuals
{
    internal static IEnumerable<Button> GetLogicalDescendantsButtons(this Panel panel) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(panel).OfType<Button>();
}
