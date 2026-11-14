using System;
using System.IO;
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
using MeowChan.FolderSources;

namespace MeowChan.Desktop;

/// <summary>What the folder source dialog needs from the host.</summary>
internal sealed record FolderSourceDialogServices(Func<string, FolderSourceError> Check,
    Func<string, string, bool, Task<FolderSourceRoot>> Add, Func<Guid, string, Task<FolderSourceRoot>> Rename);

/// <summary>
/// "Add folder…" and "Rename…" of a read-only folder source (PR3): name, folder (Add only) and "Include subfolders". The title
/// stands only in the title bar; the footer holds Cancel and the action. Closes with the added or renamed root, or null.
/// </summary>
internal sealed class FolderSourceDialog : Window
{
    private readonly FolderSourceDialogServices _services;
    private readonly FolderSourceRoot? _renaming;
    private readonly TextBlock _intro = new() { Name = "FolderSourceIntro", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _nameLabel = new() { Name = "FolderSourceNameLabel" };
    private readonly TextBox _name = new() { Name = "FolderSourceName", MaxLength = FolderSourceCatalog.MaximumNameLength };
    private readonly TextBlock _folderLabel = new() { Name = "FolderSourceFolderLabel" };
    private readonly TextBox _folder = new() { Name = "FolderSourceFolder", MaxLength = FolderSourceCatalog.MaximumPathLength };
    private readonly Button _choose = new() { Name = "FolderSourceChoose" };
    private readonly TextBlock _folderHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _subfolders = new() { Name = "FolderSourceSubfolders", IsChecked = true };
    private readonly TextBlock _error = new() { Name = "FolderSourceError", TextWrapping = TextWrapping.Wrap };
    private readonly Button _cancel = new() { Name = "FolderSourceCancel" };
    private readonly Button _confirm = new() { Name = "FolderSourceConfirm" };
    private string? _errorKey;
    private bool _nameEdited, _syncing, _busy, _closed;

    internal TextBox NameBox => _name;
    internal TextBox FolderBox => _folder;
    internal CheckBox SubfoldersBox => _subfolders;
    internal Button ConfirmButton => _confirm;
    internal TextBlock ErrorText => _error;
    internal FolderSourceRoot? Result { get; private set; }
    /// <summary>Folder picker seam (smokes): returns the chosen folder or null.</summary>
    internal Func<Task<string?>>? ChooseFolder { get; set; }

    internal FolderSourceDialog(FolderSourceDialogServices services, FolderSourceRoot? renaming = null)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _renaming = renaming;
        Name = "FolderSourceDialog";
        Width = 580; Height = renaming == null ? 480 : 320; MinWidth = 380; MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _intro.Classes.Add("workspace-help");
        _nameLabel.Classes.Add("workspace-field-label"); _folderLabel.Classes.Add("workspace-field-label");
        _folderHint.Classes.Add("workspace-help");
        AutomationProperties.SetLabeledBy(_name, _nameLabel);
        AutomationProperties.SetLabeledBy(_folder, _folderLabel);
        _cancel.Classes.Add("subtle"); _confirm.Classes.Add("primary");

        Grid folderRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        folderRow.Children.Add(_folder); folderRow.Children.Add(_choose); Grid.SetColumn(_choose, 1);
        StackPanel body = new() { Spacing = 12, Margin = new Thickness(16, 0, 16, 16) };
        if (renaming == null)
            body.Children.Add(new StackPanel { Spacing = 4, Children = { _folderLabel, folderRow, _folderHint } });
        body.Children.Add(new StackPanel { Spacing = 4, Children = { _nameLabel, _name } });
        if (renaming == null) body.Children.Add(_subfolders);
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel, _confirm } };
        Content = WindowChrome.Frame(this, DialogFrame.Build(_intro, body, actions, DialogFrame.ErrorStrip(_error, maxHeight: 96)), WindowChromeKind.Dialog);

        if (renaming != null) _name.Text = renaming.Name;
        _name.TextChanged += (_, _) => { if (!_syncing) { _nameEdited = true; ClearError(); } };
        _folder.TextChanged += (_, _) => { if (!_syncing) { SuggestName(); ClearError(); } };
        _choose.Click += async (_, _) => await ChooseAsync();
        _cancel.Click += (_, _) => { if (!_busy) Close(); };
        _confirm.Click += async (_, _) => await ConfirmAsync();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => { _closed = true; Texts.Changed -= OnLanguageChanged; };
        Opened += (_, _) => (renaming == null ? _folder : _name).Focus();
        Texts.Changed += OnLanguageChanged;
        ApplyTexts();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ApplyTexts);

    private void ApplyTexts()
    {
        if (_closed) return;
        Title = Texts.T(_renaming == null ? "folderSource.add.title" : "folderSource.rename.title");
        _intro.Text = Texts.T(_renaming == null ? "folderSource.add.intro" : "folderSource.rename.intro");
        _nameLabel.Text = Texts.T("folderSource.add.name");
        _folderLabel.Text = Texts.T("folderSource.add.folder");
        _folderHint.Text = Texts.T("folderSource.add.folderHint");
        _choose.Content = Texts.T("folderSource.add.choose");
        AutomationProperties.SetName(_choose, Texts.T("folderSource.add.chooseFolder"));
        _subfolders.Content = Texts.T("folderSource.add.subfolders");
        _cancel.Content = Texts.T("folderSource.add.cancel");
        _confirm.Content = Texts.T(_renaming == null ? "folderSource.add.confirm" : "folderSource.rename.confirm");
        AutomationProperties.SetName(_confirm, (string)_confirm.Content!);
        _error.Text = _errorKey == null ? string.Empty : Texts.T(_errorKey);
    }

    // Until the person edits the name, it follows the folder's own name.
    private void SuggestName()
    {
        if (_nameEdited || _renaming != null) return;
        string folder = (_folder.Text ?? string.Empty).Trim();
        string suggested;
        try { suggested = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)); }
        catch (ArgumentException) { suggested = string.Empty; }
        _syncing = true;
        try { _name.Text = suggested.Length > FolderSourceCatalog.MaximumNameLength ? suggested[..FolderSourceCatalog.MaximumNameLength] : suggested; }
        finally { _syncing = false; }
    }

    private void ClearError() { _errorKey = null; _error.Text = string.Empty; }

    private void ShowError(string key) { _errorKey = key; _error.Text = Texts.T(key); }

    /// <summary>The text key that explains an error to the person (never a path).</summary>
    internal static string ErrorKey(FolderSourceError error) => error switch
    {
        FolderSourceError.TooManyRoots => "folderSource.error.tooManyRoots",
        FolderSourceError.FolderUnavailable => "folderSource.error.unavailable",
        FolderSourceError.FolderOverlaps => "folderSource.error.overlaps",
        FolderSourceError.FolderIsProfile => "folderSource.error.profile",
        FolderSourceError.FolderIsDownloads => "folderSource.error.downloads",
        FolderSourceError.InvalidName => "folderSource.error.name",
        FolderSourceError.CatalogFull => "folderSource.error.full",
        FolderSourceError.CatalogNewer => "folderSource.error.newer",
        FolderSourceError.UnknownRoot => "folderSource.error.unknown",
        _ => "folderSource.error.damaged"
    };

    private async Task ChooseAsync()
    {
        if (_busy) return;
        string? chosen;
        if (ChooseFolder != null) chosen = await ChooseFolder();
        else
        {
            LocalFolderPickResult result = await LocalFolderPicker.PickAsync(StorageProvider, Texts.T("folderSource.add.chooseFolder"));
            chosen = result.Status == LocalFolderPickStatus.Selected ? result.Path : null;
        }
        if (_closed || string.IsNullOrWhiteSpace(chosen)) return;
        _folder.Text = chosen;
        _folder.Focus();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; if (!_busy) Close(); }
        else if (e.Key == Key.Enter && e.Source is TextBox) { e.Handled = true; _ = ConfirmAsync(); }
    }

    internal async Task ConfirmAsync()
    {
        if (_busy || _closed) return;
        string name = (_name.Text ?? string.Empty).Trim(), folder = (_folder.Text ?? string.Empty).Trim();
        if (name.Length == 0) { ShowError("folderSource.error.name"); _name.Focus(); return; }
        if (_renaming == null)
        {
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) { ShowError("folderSource.error.unavailable"); _folder.Focus(); return; }
            FolderSourceError check = await Task.Run(() => _services.Check(folder));
            if (_closed) return;
            if (check != FolderSourceError.None) { ShowError(ErrorKey(check)); _folder.Focus(); return; }
        }
        SetBusy(true);
        try
        {
            FolderSourceRoot root = _renaming == null
                ? await _services.Add(folder, name, _subfolders.IsChecked == true)
                : await _services.Rename(_renaming.Id, name);
            Result = root;
            SetBusy(false);
            if (!_closed) Close(root);
        }
        catch (FolderSourceException error) { SetBusy(false); if (!_closed) ShowError(ErrorKey(error.Error)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException
            or Microsoft.Data.Sqlite.SqliteException)
        { SetBusy(false); if (!_closed) ShowError("folderSource.error.damaged"); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (Control control in new Control[] { _name, _folder, _choose, _subfolders, _cancel, _confirm }) control.IsEnabled = !busy;
    }
}
