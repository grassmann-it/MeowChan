using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
using Serilog;

namespace MeowChan.Desktop;

/// <summary>"New profile…": name, profile folder and its own download folder. The title stands only in the title bar; the
/// footer holds Cancel and "Create and open". Closes with the started result, or null when nothing was started.</summary>
internal sealed class NewProfileDialog : Window, IDisposable
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
    private readonly ProfileFolderKindProbe _kindProbe;
    private string? _errorKey;
    private bool _folderEdited, _downloadsEdited, _syncing, _busy, _closed, _folderIsProfile;
    private bool _disposed;

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
        // Modals plan MD1/MD3: a modal form dialog on the factory; the title stands in the title bar only.
        DialogWindow.Configure(this, DialogSize.L, modal: true);
        // The folder kind is looked up off the UI thread after a pause in typing (never per keystroke on the UI thread).
        _kindProbe = new ProfileFolderKindProbe(ProfileOpenPolicy.Classify, ProfileFolderKindProbe.DefaultDelay,
            action => Dispatcher.UIThread.Post(action), OnFolderKind);
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
        Border footer = DialogFooter.Build(_create, new Control[] { _cancel });
        DialogWindow.Frame(this, DialogFrame.Build(_intro, body, footer, DialogFrame.ErrorStrip(_error, maxHeight: 96)), modal: true, captionName: "NewProfileChrome");

        // The Text property, not TextChanged: Avalonia posts TextChanged after the change, so the _syncing guard would already be
        // cleared and a suggested folder would count as the person's own edit (then it stops following the name).
        _name.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !_syncing) { SuggestFolders(); ClearError(); } };
        _folder.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            if (!_syncing) { _folderEdited = true; ClearError(); }
            RefreshFolderKind();
        };
        _downloads.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !_syncing) { _downloadsEdited = true; ClearError(); } };
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
        // Enter in a text field creates (tunnelled, before the field's own handling); Escape is the factory's policy.
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        DialogEscape.Attach(this, isBusy: () => _busy);
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => { _closed = true; Dispose(); Texts.Changed -= OnLanguageChanged; };
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
        ApplyFolderKind();
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

    /// <summary>The folder text changed: forget what was known about it and, for a complete path, look at it after a pause in typing, off
    /// the UI thread. Nothing here touches the file system on the calling thread.</summary>
    private void RefreshFolderKind()
    {
        string folder = (_folder.Text ?? string.Empty).Trim();
        if (folder.Length > 0 && System.IO.Path.IsPathFullyQualified(folder)) _kindProbe.Request(folder);
        else
        {
            _kindProbe.Cancel();
            _folderIsProfile = false;
            ApplyFolderKind();
        }
    }

    private void OnFolderKind(string folder, ProfileFolderKind kind)
    {
        if (_closed || !string.Equals(folder, (_folder.Text ?? string.Empty).Trim(), StringComparison.Ordinal)) return;
        _folderIsProfile = kind == ProfileFolderKind.Existing;
        ApplyFolderKind();
    }

    private void ApplyFolderKind()
    {
        _existing.IsVisible = _folderIsProfile;
        // An existing profile keeps its own download folder; the field does not apply.
        _downloadsRow.IsEnabled = !_folderIsProfile && !_busy;
        _create.Content = Texts.T(_folderIsProfile ? "profile.new.open" : "profile.new.create");
        AutomationProperties.SetName(_create, (string)_create.Content!);
    }

    private void ClearError() { _errorKey = null; _error.Text = string.Empty; }

    private void ShowError(string key) { _errorKey = key; _error.Text = Texts.T(key); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _kindProbe.Dispose();
    }

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
        if (e.Key == Key.Enter && e.Source is TextBox) { e.Handled = true; _ = CreateAsync(); }
    }

    internal async Task CreateAsync()
    {
        if (_busy || _closed) return;
        string name = (_name.Text ?? string.Empty).Trim(), folder = (_folder.Text ?? string.Empty).Trim(), downloads = (_downloads.Text ?? string.Empty).Trim();
        // The option is on: the folder is checked like "Add folder…" does (it must exist, be readable and be no profile or download
        // folder) before anything is created or started, and it is never created here.
        string? media = _mediaOption.IsChecked == true ? (_media.Text ?? string.Empty).Trim() : null;
        if (media is { Length: 0 }) { ShowError("profile.error.mediaAbsolute"); _media.Focus(); return; }
        SetBusy(true);
        ProfileActionResult result;
        // The checks (folder kind, media folder, profile list) and the creation both read the file system: all of it off the UI thread.
        try { result = await Task.Run(() => _service.Create(new NewProfileRequest(name, folder, downloads, _language, media))); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never the exception's own text: it is English, and it may name a path. The log has it.
            Log.Error(error, "A new profile could not be created.");
            result = new ProfileActionResult(ProfileActionStatus.Failed, "profile.status.prepareFailed");
        }
        finally { SetBusy(false); }
        if (_closed) return;
        if (result.Status == ProfileActionStatus.Started) { Result = result; Close(result); return; }
        ShowError(result.MessageKey);
        if (result.Status != ProfileActionStatus.Refused) return;
        string problem = result.MessageKey;
        (problem.StartsWith("profile.error.name", StringComparison.Ordinal) ? _name : problem.StartsWith("profile.error.downloads", StringComparison.Ordinal) ? _downloads
            : problem.StartsWith("profile.error.media", StringComparison.Ordinal) ? _media : (Control)_folder).Focus();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (Control control in new Control[] { _name, _folder, _chooseFolder, _mediaOption, _media, _chooseMedia, _cancel, _create }) control.IsEnabled = !busy;
        ApplyFolderKind();
    }
}

/// <summary>What "Switch profile…" ended with.</summary>
internal sealed record ProfileSwitchOutcome(ProfileActionResult? Result, bool CloseThisWindow, bool NewProfileRequested);

/// <summary>"Switch profile…": the list of known profiles with their state, Open and "Remove from list" per row, the
/// current profile first. Opening starts a new window; this one closes only when the person ticked the option.
/// Nothing here waits for the file system on the UI thread: the rows appear at once with "Checking…", and each folder's answer
/// (missing, open in another window, or "status unknown" when it does not answer) fills its row in as it arrives.</summary>
internal sealed class SwitchProfileDialog : Window, IDisposable
{
    /// <summary>One row: what the list says, what the folder answered so far (null: still being checked) and its controls.</summary>
    private sealed class RowState
    {
        internal RowState(ProfileRegistryEntry entry, bool isCurrent, bool canForget) { Entry = entry; IsCurrent = isCurrent; CanForget = canForget; }
        internal ProfileRegistryEntry Entry { get; }
        internal bool IsCurrent { get; }
        internal bool CanForget { get; }
        internal ProfileRowStatus? Status { get; set; }
        internal TextBlock? StatusText { get; set; }
        internal Button? Open { get; set; }
        internal Grid? Grid { get; set; }
    }

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
    private readonly List<RowState> _model = new();
    private ProfileRegistrySnapshot? _snapshot;
    private CancellationTokenSource? _loadCancel;
    private int _loadVersion;
    private string? _errorKey;
    private bool _busy, _closed, _focusPending;
    private bool _disposed;
    internal StackPanel Rows => _rows;
    internal CheckBox CloseAfterBox => _closeAfter;
    internal TextBlock ErrorText => _error;
    internal TextBlock EmptyText => _empty;
    /// <summary>Completes when the list was read and every folder has answered (or timed out). The rows exist before that.</summary>
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    internal SwitchProfileDialog(ProfileSwitchService service)
    {
        _service = service;
        Name = "SwitchProfileDialog";
        // Modals plan MD1/MD3: a modal list dialog on the factory; the title stands in the title bar only.
        DialogWindow.Configure(this, DialogSize.L, modal: true);
        _intro.Classes.Add("workspace-help");
        _listLabel.Classes.Add("workspace-field-label");
        _listState.Classes.Add("workspace-meta");
        _empty.Classes.Add("workspace-help");
        _newProfile = Commands.CommandBindings.CreateButton("profile.new",
            () => { if (!_busy) Close(new ProfileSwitchOutcome(null, false, NewProfileRequested: true)); }, () => !_busy);
        _newProfile.Name = "SwitchProfileNew";
        _close.Classes.Add("subtle");
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(16, 0, 16, 16), Children = { _listLabel, _listState, _rows, _empty, _closeAfter } };
        Border footer = DialogFooter.Build(_close, new Control[] { _newProfile });
        DialogWindow.Frame(this, DialogFrame.Build(_intro, body, footer, DialogFrame.ErrorStrip(_error, maxHeight: 96)), modal: true, captionName: "SwitchProfileChrome");
        _close.Click += (_, _) => { if (!_busy) Close(); };
        DialogEscape.Attach(this, isBusy: () => _busy);
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => { _closed = true; Dispose(); Texts.Changed -= OnLanguageChanged; };
        Opened += (_, _) => { _focusPending = true; _close.Focus(); if (LoadTask.IsCompleted) FocusFirstOpenWhenPending(); };
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
        // A language change redraws what is known; it never asks the folders again.
        if (_snapshot == null) LoadTask = RefreshAsync(); else Render();
    }

    /// <summary>Reads the list and asks every folder again. The rows are drawn as soon as the list is read, before any folder answers.</summary>
    internal Task RefreshAsync()
    {
        if (_closed) return Task.CompletedTask;
        _loadCancel?.Cancel();
        _loadCancel?.Dispose();
        _loadCancel = new CancellationTokenSource();
        return LoadAsync(++_loadVersion, _loadCancel.Token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadCancel?.Cancel();
        _loadCancel?.Dispose();
        _loadCancel = null;
    }

    private async Task LoadAsync(int version, CancellationToken token)
    {
        ProfileRegistrySnapshot snapshot;
        try { snapshot = await _service.ReadListAsync().WaitAsync(token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Never the exception's own text: it is English, and it may name a path.
            Log.Warning(error, "The profile list could not be read for the switch dialog.");
            if (_closed || version != _loadVersion) return;
            ShowError("profile.status.unexpected");
            snapshot = new ProfileRegistrySnapshot(Array.Empty<ProfileRegistryEntry>(), ProfileRegistryState.Missing);
        }
        if (_closed || version != _loadVersion) return;
        _snapshot = snapshot;
        ProfileRegistryEntry current = snapshot.Entries.FirstOrDefault(entry => ProfileRegistry.SamePath(entry.Path, _service.CurrentProfile))
            ?? new ProfileRegistryEntry(ProfileRegistry.NormalizeName(null, _service.CurrentProfile), _service.CurrentProfile, null, null);
        _model.Clear();
        _model.Add(new RowState(current, isCurrent: true, canForget: false) { Status = ProfileRowStatus.Ready });
        foreach (ProfileRegistryEntry entry in snapshot.Entries.Where(entry => !ProfileRegistry.SamePath(entry.Path, _service.CurrentProfile)))
            _model.Add(new RowState(entry, isCurrent: false, canForget: snapshot.State != ProfileRegistryState.Unsupported));
        Render();

        string[] folders = _model.Where(row => !row.IsCurrent).Select(row => row.Entry.Path).ToArray();
        try
        {
            await ProfileRowProbe.RunAsync(folders, ProfileRowProbe.Probe, ProfileRowProbe.Parallelism, ProfileRowProbe.Timeout,
                (index, status) => Dispatcher.UIThread.Post(() => ApplyStatus(version, index + 1, status)), token);
            // The last answers were posted before this continuation: let them land before the load counts as done.
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { Log.Warning(error, "The profile folders could not be checked for the switch dialog."); }
        if (_closed || version != _loadVersion) return;
        FocusFirstOpenWhenPending();
    }

    private void ApplyStatus(int version, int modelIndex, ProfileRowStatus status)
    {
        if (_closed || version != _loadVersion || modelIndex >= _model.Count) return;
        RowState row = _model[modelIndex];
        row.Status = status;
        ApplyRow(row);
    }

    private void FocusFirstOpenWhenPending()
    {
        if (!_focusPending) return;
        IInputElement? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused != null && !ReferenceEquals(focused, _close) && !ReferenceEquals(focused, _newProfile)) { _focusPending = false; return; }
        if (_rows.GetLogicalDescendantsButtons().FirstOrDefault(button => button.IsEnabled && button.Tag != null) is not { } first) return;
        _focusPending = false;
        first.Focus();
    }

    /// <summary>One row per profile: the current one first, then the list in its order, from what is known now.</summary>
    private void Render()
    {
        if (_closed || _snapshot == null) return;
        _listState.Text = _snapshot.State switch
        {
            ProfileRegistryState.Damaged => Texts.T("profile.switch.listDamaged"),
            ProfileRegistryState.RecoveredFromBackup => Texts.T("profile.switch.listRecovered"),
            ProfileRegistryState.Unsupported => Texts.T("profile.switch.listNewer"),
            _ => string.Empty
        };
        _listState.IsVisible = _listState.Text.Length > 0;
        _rows.Children.Clear();
        foreach (RowState row in _model) _rows.Children.Add(Row(row));
        _empty.IsVisible = _model.Count <= 1;
    }

    private Control Row(RowState state)
    {
        ProfileRegistryEntry entry = state.Entry;
        TextBlock name = new() { Text = entry.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        TextBlock path = new() { Text = entry.Path, TextWrapping = TextWrapping.Wrap };
        path.Classes.Add("workspace-meta");
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
        state.StatusText = status;
        StackPanel text = new() { Spacing = 2, Children = { name, path, status } };
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        state.Grid = row;
        row.Children.Add(text);
        state.Open = null;
        if (!state.IsCurrent)
        {
            Button open = new() { Content = Texts.T("profile.switch.open"), Tag = entry.Path };
            open.Classes.Add("primary");
            AutomationProperties.SetName(open, Texts.T("profile.switch.openName", entry.Name));
            open.Click += async (_, _) => await OpenAsync(entry.Path);
            state.Open = open;
            StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { open } };
            if (state.CanForget)
            {
                Button forget = new() { Content = Texts.T("profile.switch.forget") };
                forget.Classes.Add("subtle");
                AutomationProperties.SetName(forget, Texts.T("profile.switch.forgetName", entry.Name));
                ToolTip.SetTip(forget, Texts.T("profile.switch.forgetName", entry.Name));
                forget.Click += async (_, _) => await ForgetAsync(entry.Path);
                buttons.Children.Add(forget);
            }
            row.Children.Add(buttons); Grid.SetColumn(buttons, 1);
        }
        ApplyRow(state);
        return new Border { Child = row, Padding = new Thickness(12, 8), Classes = { "workspace-card" } };
    }

    /// <summary>Writes a row's state text, its colour and whether it can be opened.</summary>
    private void ApplyRow(RowState state)
    {
        if (state.StatusText == null) return;
        ProfileRegistryEntry entry = state.Entry;
        string text = state.IsCurrent ? Texts.T("profile.state.current") : state.Status switch
        {
            null => Texts.T("profile.state.checking"),
            ProfileRowStatus.Missing => Texts.T("profile.state.missing"),
            ProfileRowStatus.InUse => Texts.T("profile.state.inUse"),
            ProfileRowStatus.Unknown => Texts.T("profile.state.unknown"),
            _ => entry.LastOpenedUtc is { } opened ? Texts.T("profile.state.lastOpened", opened.ToLocalTime().ToString("g", Texts.Culture)) : Texts.T("profile.state.never")
        };
        bool problem = !state.IsCurrent && state.Status is ProfileRowStatus.Missing or ProfileRowStatus.Unknown;
        state.StatusText.Text = text;
        state.StatusText.Classes.Set("workspace-error", problem);
        state.StatusText.Classes.Set("workspace-meta", !problem);
        // Only a folder that answered "fine" can be opened; a row that is still being checked or did not answer cannot.
        if (state.Open != null) state.Open.IsEnabled = state.Status == ProfileRowStatus.Ready;
        if (state.Grid != null) AutomationProperties.SetName(state.Grid, entry.Name + ", " + text);
    }

    private void ShowError(string key) { _errorKey = key; _error.Text = Texts.T(key); }

    private void BeginBusy()
    {
        _busy = true; _errorKey = null; _error.Text = string.Empty;
        SetRowsEnabled(false);
    }

    private void EndBusy() { _busy = false; SetRowsEnabled(true); }

    internal async Task OpenAsync(string folder)
    {
        if (_busy || _closed) return;
        BeginBusy();
        ProfileActionResult result;
        try { result = await Task.Run(() => _service.Open(folder)); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Error(error, "A profile could not be opened from the switch dialog.");
            result = new ProfileActionResult(ProfileActionStatus.Failed, "profile.status.startFailed");
        }
        finally { EndBusy(); }
        if (_closed) return;
        if (result.Status == ProfileActionStatus.Started) { Close(new ProfileSwitchOutcome(result, _closeAfter.IsChecked == true, false)); return; }
        ShowError(result.MessageKey);
        LoadTask = RefreshAsync();
    }

    /// <summary>"Remove from list": the list's lock file may be held by another window for a few seconds, so the removal runs off the UI
    /// thread while the dialog is busy; afterwards the list is read again (it may have changed meanwhile).</summary>
    internal async Task ForgetAsync(string folder)
    {
        if (_busy || _closed) return;
        BeginBusy();
        ProfileForgetOutcome outcome;
        try { outcome = await _service.ForgetAsync(folder); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Log.Error(error, "A profile could not be removed from the list in the switch dialog.");
            outcome = ProfileForgetOutcome.Failed;
        }
        finally { EndBusy(); }
        if (_closed) return;
        if (outcome == ProfileForgetOutcome.Failed) ShowError("profile.status.forgetFailed");
        _focusPending = true; // the focused row's button is gone after the redraw
        LoadTask = RefreshAsync();
    }

    private void SetRowsEnabled(bool enabled) { _rows.IsEnabled = enabled; _closeAfter.IsEnabled = enabled; _close.IsEnabled = enabled; Commands.CommandBindings.Refresh(_newProfile); }
}

internal static class ProfileDialogVisuals
{
    internal static IEnumerable<Button> GetLogicalDescendantsButtons(this Panel panel) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(panel).OfType<Button>();
}
