using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Import;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>One preview row: name, size, kind and status as text.</summary>
internal sealed record ManualImportRow(ManualImportCandidate Item)
{
    internal string Text => string.Join(" · ", new[] { Item.Name, Item.Length > 0 ? ManualImportSession.Size(Item.Length) : string.Empty,
        ManualImportSession.KindText(Item.Kind), ManualImportSession.StatusText(Item) }.Where(part => part.Length > 0));
}

/// <summary>
/// Imports own files into the media library (contract manual-import-v1, phase 1): choose or drop files and folders, the
/// selection is checked right away (SHA-256, duplicates against the library), the preview names what will be copied and
/// what is only linked, optional tags go to the new files, and the summary offers to show the batch on the Media page.
/// Copies only; never moves or deletes a source file. Esc stops a running step first and closes the dialog otherwise.
/// </summary>
internal sealed class ManualImportDialog : Window
{
    private readonly ManualImportSession _session;
    private readonly Func<PrivateTagStoreSnapshot?> _tagSnapshot;
    private readonly TextBlock _intro = new() { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly TextBlock _target = new() { Name = "ManualImportTarget", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly TextBlock _rootsLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _roots = new() { Name = "ManualImportRoots", Spacing = 4 };
    private readonly Button _addFiles = new() { Name = "ManualImportAddFiles" };
    private readonly Button _addFolder = new() { Name = "ManualImportAddFolder" };
    private readonly CheckBox _subfolders = new() { Name = "ManualImportSubfolders" };
    private readonly CheckBox _otherFiles = new() { Name = "ManualImportOtherFiles" };
    private readonly ProgressBar _progress = new() { Name = "ManualImportProgress", Minimum = 0, Maximum = 1, IsVisible = false, Height = 6 };
    private readonly TextBlock _progressText = new() { Name = "ManualImportProgressText", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _counts = new() { Name = "ManualImportCounts", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _duplicateHint = new() { Name = "ManualImportDuplicateHint", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private readonly ListBox _list = new() { Name = "ManualImportList", MaxHeight = 240, MinHeight = 80 };
    private readonly TextBlock _nameLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _name = new() { Name = "ManualImportBatchName", MaxLength = ManualImportCatalogStore.MaximumBatchNameLength };
    private readonly TextBlock _tagsLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _tagChips = new() { Name = "ManualImportTagChips", ItemSpacing = 6, LineSpacing = 6 };
    private readonly IdentitySuggestionBox _tagInput = new() { Name = "ManualImportTagInput" };
    private readonly Button _tagAdd = new() { Name = "ManualImportTagAdd" };
    private readonly TextBlock _status = new() { Name = "ManualImportStatus", TextWrapping = TextWrapping.Wrap };
    private readonly Button _import = new() { Name = "ManualImportStart", Classes = { "primary" } };
    private readonly Button _cancel = new() { Name = "ManualImportCancel" };
    private readonly Button _showBatch = new() { Name = "ManualImportShowBatch", Classes = { "primary" }, IsVisible = false };
    private readonly Button _close = new() { Name = "ManualImportClose" };
    private readonly SourceTagSuggestions _sources = new();
    private readonly Grid _tagRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
    private bool _closed;
    private bool _syncing;

    internal ManualImportSession Session => _session;
    internal Button ImportButton => _import;
    internal Button CancelButton => _cancel;
    internal Button ShowBatchButton => _showBatch;
    internal Button CloseButton => _close;
    internal TextBlock Status => _status;
    internal TextBlock Counts => _counts;
    internal ListBox List => _list;
    internal TextBox BatchNameBox => _name;
    internal IdentitySuggestionBox TagInput => _tagInput;
    internal Button TagAddButton => _tagAdd;
    /// <summary>Set by the owner: "show this batch" on the Media page after a finished import.</summary>
    internal Action<ManualImportBatch>? ShowBatch { get; set; }
    /// <summary>Test seams for the pickers; the defaults use this window's storage provider.</summary>
    internal Func<Task<IReadOnlyList<string>>>? ChooseFiles { get; set; }
    internal Func<Task<string?>>? ChooseFolder { get; set; }

    internal ManualImportDialog(ManualImportSession session, string targetFolder, Func<PrivateTagStoreSnapshot?> tagSnapshot,
        Func<MeowChan.Archive.BooruCatalogSnapshot?>? readSourceTags = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _tagSnapshot = tagSnapshot ?? throw new ArgumentNullException(nameof(tagSnapshot));
        _sources.Read = readSourceTags;
        _target.Tag = targetFolder;
        Name = "ManualImportDialog";
        // Modals plan MD2: a modeless assistant on the list step (it keeps minimize and its taskbar entry), fitted to the
        // screen; the title stands in the title bar only.
        DialogWindow.Configure(this, DialogSize.L, modal: false);
        this[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        this[!ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_progressText, AutomationLiveSetting.Polite);
        _list.ItemTemplate = new FuncDataTemplate<ManualImportRow>((row, _) =>
        {
            TextBlock text = new() { TextWrapping = TextWrapping.Wrap, Text = row?.Text };
            if (row != null) AutomationProperties.SetName(text, row.Text);
            return text;
        });
        // A long selection is a virtualized list, never one control per file.
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);

        WrapPanel pick = new() { ItemSpacing = 6, LineSpacing = 6, Children = { _addFiles, _addFolder } };
        WrapPanel switches = new() { ItemSpacing = 12, LineSpacing = 4, Children = { _subfolders, _otherFiles } };
        Grid tagRow = _tagRow;
        tagRow.Children.Add(_tagInput); tagRow.Children.Add(_tagAdd); Grid.SetColumn(_tagAdd, 1);
        StackPanel body = new() { Spacing = 10, Margin = new Thickness(16, 0, 16, 16) };
        foreach (Control control in new Control[] { _intro, _target, _rootsLabel, _roots, pick, switches, _progress, _progressText,
            _counts, _duplicateHint, _list, _nameLabel, _name, _tagsLabel, _tagChips, tagRow, _status })
            body.Children.Add(control);
        // The body scrolls between the title bar and the fixed footer; Import is the primary action (right-most, like Show batch).
        Border footer = DialogFooter.Build(_import, new Control[] { _cancel, _close, _showBatch });
        DialogWindow.Frame(this, DialogFrame.Build(header: null, body, footer), modal: false, captionName: "ManualImportChrome");

        _subfolders.IsCheckedChanged += (_, _) => { if (!_syncing) _session.IncludeSubfolders = _subfolders.IsChecked == true; };
        _otherFiles.IsCheckedChanged += (_, _) => { if (!_syncing) _session.IncludeOtherFiles = _otherFiles.IsChecked == true; };
        _name.TextChanged += (_, _) => { if (!_syncing) { _session.BatchName = _name.Text ?? string.Empty; Refresh(); } };
        _addFiles.Click += OnAddFiles; _addFolder.Click += OnAddFolder;
        _import.Click += OnImport; _cancel.Click += OnCancel; _close.Click += OnClose; _showBatch.Click += OnShowBatch;
        _tagAdd.Click += OnTagAdd;
        TagSuggestionInput.Attach(_tagInput, () => _session.Busy ? null : _tagSnapshot(), AssignedTags, () => OnTagAdd(null, new RoutedEventArgs()), _sources);
        _tagInput.AddHandler(KeyDownEvent, OnTagKey, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        // Escape stops a running step; otherwise it closes (an open suggestion list takes it first, see OnKey).
        DialogEscape.Attach(this, () =>
        {
            if (!_session.Busy) return false;
            _session.Cancel(); return true;
        });
        // The open dialog is itself a drop target: more dropped files join the selection.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += OnOpened; Closed += OnClosed;
        _session.Updated += OnSessionUpdated; Texts.Changed += OnLanguageChanged;
        Refresh();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _sources.Load();
        (_session.Roots.Count == 0 ? _addFiles : (Control)_name).Focus();
    }

    private void OnSessionUpdated() => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private IReadOnlySet<TagId> AssignedTags()
    {
        HashSet<TagId> chosen = new();
        if (_tagSnapshot() is not { } snapshot) return chosen;
        foreach (string tag in _session.Tags)
            if (PrivateTagName.TryCreate(tag, out PrivateTagName? name, out _) && snapshot.Tags.FirstOrDefault(item => item.Name == name) is { } known)
                chosen.Add(known.Id);
        return chosen;
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<string> files = ChooseFiles != null ? await ChooseFiles() : await PickFilesAsync();
            if (!_closed && files.Count > 0) _session.AddRoots(files);
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "import.pick.files")) { }
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = ChooseFolder != null ? await ChooseFolder()
                : (await LocalFolderPicker.PickAsync(StorageProvider, Texts.T("import.addFolder"))) is { Status: LocalFolderPickStatus.Selected } picked ? picked.Path : null;
            if (!_closed && folder != null) _session.AddRoots(new[] { folder });
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "import.pick.folder")) { }
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        if (!StorageProvider.CanOpen) return Array.Empty<string>();
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Texts.T("import.addFiles"), AllowMultiple = true
        });
        try { return files.Select(file => file.TryGetLocalPath()).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).ToArray(); }
        finally { foreach (IStorageFile file in files) file.Dispose(); }
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        try { await _session.ImportAsync(); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "import.run")) { }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => _session.Cancel();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnShowBatch(object? sender, RoutedEventArgs e)
    {
        if (_session.Outcome is { } outcome) ShowBatch?.Invoke(outcome.Batch);
        Close();
    }

    private void OnTagAdd(object? sender, RoutedEventArgs e)
    {
        string text = _tagInput.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_session.AddTag(text)) _tagInput.Text = string.Empty;
    }

    private void OnTagKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        e.Handled = true;
        _tagInput.Dismiss();
        OnTagAdd(null, new RoutedEventArgs());
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        // Escape closes an open suggestion list first; then DialogEscape stops a running step, and only then closes the dialog.
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        if (TagSuggestionInput.Route(_tagInput, e) != TagFieldKey.CloseList) return;
        e.Handled = true; _tagInput.Dismiss();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        DroppedContent content = SafeRead(e.DataTransfer);
        e.DragEffects = content.Kind == DroppedContentKind.Files && !_session.Busy && _session.Stage != ManualImportStage.Done
            ? DroppedLinks.AcceptedEffect(e.DragEffects) : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DroppedContent content = SafeRead(e.DataTransfer);
        e.Handled = true;
        if (content.Kind != DroppedContentKind.Files || _session.Busy || _session.Stage == ManualImportStage.Done)
        { e.DragEffects = DragDropEffects.None; return; }
        e.DragEffects = DroppedLinks.AcceptedEffect(e.DragEffects);
        _session.AddRoots(content.Paths);
    }

    private static DroppedContent SafeRead(IDataTransfer transfer)
    {
        try { return DroppedLinks.Read(transfer); }
        catch (Exception) { return DroppedContent.Empty; }
    }

    private void Refresh()
    {
        if (_closed) return;
        Title = Texts.T("import.title");
        _intro.Text = Texts.T("import.intro");
        _target.Text = Texts.T("import.target", _target.Tag as string ?? string.Empty);
        _rootsLabel.Text = _session.Roots.Count == 0 ? Texts.T("import.roots.empty") : Texts.T("import.roots", _session.Roots.Count);
        _addFiles.Content = Texts.T("import.addFiles"); AutomationProperties.SetName(_addFiles, Texts.T("import.addFiles"));
        _addFolder.Content = Texts.T("import.addFolder"); AutomationProperties.SetName(_addFolder, Texts.T("import.addFolder"));
        _subfolders.Content = Texts.T("import.subfolders");
        _otherFiles.Content = Texts.T("import.otherFiles"); ToolTip.SetTip(_otherFiles, Texts.T("import.otherFiles.tip"));
        _nameLabel.Text = Texts.T("import.batch.name");
        AutomationProperties.SetName(_name, Texts.T("import.batch.name"));
        _tagsLabel.Text = Texts.T("import.tags");
        TagSuggestionInput.Localize(_tagInput);
        _tagAdd.Content = Texts.T("tags.add"); AutomationProperties.SetName(_tagAdd, Texts.T("tags.add"));
        _cancel.Content = Texts.T(_session.Stage == ManualImportStage.Importing ? "import.cancel.run" : "import.cancel.check");
        ToolTip.SetTip(_cancel, _session.Stage == ManualImportStage.Importing ? Texts.T("import.cancel.run.tip") : null);
        _close.Content = Texts.T("action.close");
        _showBatch.Content = Texts.T("import.showBatch");

        _syncing = true;
        try
        {
            _subfolders.IsChecked = _session.IncludeSubfolders;
            _otherFiles.IsChecked = _session.IncludeOtherFiles;
            if (_name.Text != _session.BatchName) _name.Text = _session.BatchName;
        }
        finally { _syncing = false; }

        bool editable = _session.Stage is not (ManualImportStage.Importing or ManualImportStage.Done);
        _roots.Children.Clear();
        for (int index = 0; index < _session.Roots.Count; index++)
        {
            int at = index;
            string root = _session.Roots[index];
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            TextBlock text = new() { Text = root, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Button remove = new() { IsEnabled = editable };
            MeowChan.Desktop.Controls.WorkspaceIcons.MakeSquare(remove, MeowChan.Desktop.Controls.WorkspaceIcons.Close, Texts.T("import.roots.remove", System.IO.Path.GetFileName(root)), subtle: false);
            remove.Click += (_, _) => _session.RemoveRoot(at);
            row.Children.Add(text); row.Children.Add(remove); Grid.SetColumn(remove, 1);
            _roots.Children.Add(row);
        }
        _addFiles.IsEnabled = _addFolder.IsEnabled = _subfolders.IsEnabled = _otherFiles.IsEnabled = editable;
        _name.IsEnabled = _tagInput.IsEnabled = _tagAdd.IsEnabled = editable;
        // A finished batch has nothing left to edit: the name and tag fields give way so the summary stays in view.
        bool finished = _session.Stage == ManualImportStage.Done;
        _nameLabel.IsVisible = _name.IsVisible = _tagRow.IsVisible = !finished;
        if (!_tagInput.IsEnabled) _tagInput.Dismiss();

        _tagChips.Children.Clear();
        foreach (string tag in _session.Tags)
        {
            string name = tag;
            Button chip = new() { Content = MeowChan.Desktop.Controls.WorkspaceChips.RemovableContent(name), IsEnabled = editable };
            string hint = Texts.T("import.tags.remove", name);
            AutomationProperties.SetName(chip, hint); ToolTip.SetTip(chip, hint);
            chip.Click += (_, _) => _session.RemoveTag(name);
            _tagChips.Children.Add(chip);
        }
        if (_tagChips.Children.Count == 0)
            _tagChips.Children.Add(new TextBlock { Text = Texts.T("import.tags.none"), Classes = { "muted" } });

        RefreshProgress();
        ManualImportPlan? plan = _session.Plan;
        _counts.IsVisible = plan != null;
        _counts.Text = plan == null ? string.Empty : Texts.T("import.counts", _session.NewCount, _session.LinkedCount,
            _session.UnsupportedCount, _session.UnreadableCount);
        AutomationProperties.SetName(_counts, _counts.Text);
        _duplicateHint.IsVisible = plan != null && (!plan.DuplicateCheckComplete || _session.LinkedCount > 0);
        _duplicateHint.Text = plan == null ? string.Empty : !plan.DuplicateCheckComplete ? Texts.T("import.dedupe.incomplete") : Texts.T("import.dedupe.linked");
        IEnumerable<ManualImportCandidate> rows = plan?.Items ?? Enumerable.Empty<ManualImportCandidate>();
        // Group by status: new first, then linked, then what is left out.
        _list.ItemsSource = rows.OrderBy(item => (int)item.Disposition).Select(item => new ManualImportRow(item)).ToArray();
        _list.IsVisible = plan is { Items.Count: > 0 };
        AutomationProperties.SetName(_list, Texts.T("import.list"));

        bool done = _session.Stage == ManualImportStage.Done;
        _import.IsVisible = !done;
        _import.IsEnabled = _session.CanImport;
        _import.Content = _session.NewCount > 0 ? Texts.Count("import.start", _session.NewCount) : Texts.T("import.start.linkOnly");
        AutomationProperties.SetName(_import, _import.Content as string ?? string.Empty);
        _cancel.IsVisible = _session.Busy;
        _showBatch.IsVisible = done && _session.Outcome is { Batch: var batch } && batch.Count(ManualImportItemState.Committed) > 0 && ShowBatch != null;

        _status.Text = done ? Summary(_session.Outcome!) : _session.MessageKey != null
            ? ManualImportSession.MessageText(_session.MessageKey, _session.MessageArguments) : !_session.BatchNameValid ? Texts.T("import.batch.invalid") : string.Empty;
        _status.IsVisible = _status.Text.Length > 0;
    }

    private void RefreshProgress()
    {
        if (_session.Stage == ManualImportStage.Checking)
        {
            ManualImportCheckProgress? progress = _session.CheckProgress;
            _progress.IsVisible = true;
            _progress.IsIndeterminate = progress == null || progress.BytesTotal == 0;
            _progress.Value = progress is { BytesTotal: > 0 } ? (double)progress.BytesDone / progress.BytesTotal : 0;
            _progressText.Text = progress == null ? Texts.T("import.check.listing")
                : Texts.T("import.check.progress", progress.FilesDone, progress.FilesTotal,
                    ManualImportSession.Size(progress.BytesDone), ManualImportSession.Size(progress.BytesTotal));
        }
        else if (_session.Stage == ManualImportStage.Importing)
        {
            ManualImportCopyProgress? progress = _session.CopyProgress;
            _progress.IsVisible = true;
            _progress.IsIndeterminate = progress == null || progress.FilesTotal == 0;
            _progress.Value = progress is { FilesTotal: > 0 } ? (double)progress.FilesDone / progress.FilesTotal : 0;
            _progressText.Text = progress == null ? Texts.T("import.run.starting")
                : Texts.T("import.run.progress", progress.FilesDone, progress.FilesTotal);
        }
        else
        {
            _progress.IsVisible = false;
            _progressText.Text = string.Empty;
        }
        _progressText.IsVisible = _progressText.Text.Length > 0;
    }

    internal static string Summary(ManualImportOutcome outcome)
    {
        ManualImportBatch batch = outcome.Batch;
        List<string> parts = new()
        {
            Texts.Count("import.done", batch.Count(ManualImportItemState.Committed), batch.Count(ManualImportItemState.Committed), batch.Name)
        };
        if (batch.Count(ManualImportItemState.Duplicate) is > 0 and int linked) parts.Add(Texts.Count("import.done.linked", linked));
        if (batch.Count(ManualImportItemState.Failed) is > 0 and int failed) parts.Add(Texts.Count("import.done.failed", failed));
        if (batch.Cancelled) parts.Add(Texts.T("import.done.cancelled"));
        if (outcome.TagsRequested && batch.Count(ManualImportItemState.Committed) > 0)
            parts.Add(Texts.T(outcome.TagsApplied ? "import.done.tags" : "import.done.tagsFailed", batch.Tags.Count));
        return string.Join(" ", parts);
    }

    internal void CloseForLifetime() { if (!_closed) Close(); }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        // A running check stops; a running copy stops after the current file (finished files stay).
        _session.Cancel();
        _session.Updated -= OnSessionUpdated; Texts.Changed -= OnLanguageChanged;
        _tagInput.EndSuggestions(); _sources.End();
        _addFiles.Click -= OnAddFiles; _addFolder.Click -= OnAddFolder;
        _import.Click -= OnImport; _cancel.Click -= OnCancel; _close.Click -= OnClose; _showBatch.Click -= OnShowBatch; _tagAdd.Click -= OnTagAdd;
        _tagInput.RemoveHandler(KeyDownEvent, OnTagKey); RemoveHandler(KeyDownEvent, OnKey);
        RemoveHandler(DragDrop.DragOverEvent, OnDragOver); RemoveHandler(DragDrop.DropEvent, OnDrop);
        Opened -= OnOpened; Closed -= OnClosed;
    }
}
