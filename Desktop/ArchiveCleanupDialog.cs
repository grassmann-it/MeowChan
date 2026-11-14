using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>One explicit local cleanup, with no filesystem inventory or deletion in the UI.</summary>
internal sealed class ArchiveCleanupDialog : Window
{
    private readonly Func<IReadOnlyCollection<ResourceId>, CancellationToken, Task<ArchiveCleanupPreview>> _preview;
    private readonly Func<ArchiveCleanupPreview, IReadOnlyCollection<ResourceId>, CancellationToken, IReadOnlyCollection<ResourceId>?, Task<ArchiveCleanupExecutionPlan>> _prepare;
    private readonly Func<ArchiveCleanupExecutionPlan, CancellationToken, IReadOnlyCollection<ResourceId>?, Task<ArchiveCleanupExecutionResult>> _execute;
    private readonly Func<IReadOnlyCollection<ResourceId>> _openThreads;
    private readonly Action<string>? _openFolder;
    private readonly CancellationTokenSource _lifetime;
    private readonly ArchiveCleanupSelection _selection = new();
    private readonly TextBlock _scope = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _selectionSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly MeowChan.Desktop.Controls.SearchBox _search = new();
    private readonly ComboBox _filter = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _list = new();
    private readonly StackPanel _inventoryControls = new() { Spacing = 8 };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52, MaxHeight = 100 };
    private readonly Expander _detailsSection = new() { HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false };
    private readonly TextBlock _candidatesHeading = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _lossHeading = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _loss = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _ack = new();
    private readonly StackPanel _confirmation = new() { Spacing = 8, IsVisible = false };
    private readonly StackPanel _outcome = new() { Spacing = 8 };
    private readonly Button _refresh = new(), _selectShown = new(), _clear = new(), _review = new();
    private readonly Button _confirm = new(), _back = new(), _cancel = new(), _close = new(), _folder = new();
    private ArchiveCleanupExecutionPlan? _plan;
    private CancellationTokenSource? _operation;
    private bool _busy, _executing, _closed, _closeWhenIdle, _localizing;
    private int _generation, _focusGeneration;

    internal bool ArchiveChanged { get; private set; }

    internal ArchiveCleanupDialog(DesktopSessionHost host, Func<IReadOnlyCollection<ResourceId>> openThreads,
        Action<string>? openFolder = null, CancellationToken lifetime = default)
        : this((ids, token) => host.PrepareArchiveCleanupAsync(ids, token),
            (preview, ids, token) => host.PrepareArchiveCleanupExecutionAsync(preview, ids, token),
            (plan, token) => host.ExecuteArchiveCleanupAsync(plan, token), openThreads, openFolder, lifetime)
    {
        _prepare = host.PrepareArchiveCleanupExecutionAsync;
        _execute = host.ExecuteArchiveCleanupAsync;
        void Changed() => Dispatcher.UIThread.Post(() =>
        {
            if (!_closed && host.State != DesktopSessionState.Ready) InvalidatePreview();
        });
        host.StateChanged += Changed;
        Closed += (_, _) => host.StateChanged -= Changed;
    }

    // Delegates let acceptance tests use private fixtures and controlled cancellation boundaries.
    internal ArchiveCleanupDialog(
        Func<IReadOnlyCollection<ResourceId>, CancellationToken, Task<ArchiveCleanupPreview>> preview,
        Func<ArchiveCleanupPreview, IReadOnlyCollection<ResourceId>, CancellationToken, Task<ArchiveCleanupExecutionPlan>> prepare,
        Func<ArchiveCleanupExecutionPlan, CancellationToken, Task<ArchiveCleanupExecutionResult>> execute,
        Func<IReadOnlyCollection<ResourceId>> openThreads, Action<string>? openFolder = null,
        CancellationToken lifetime = default)
    {
        _preview = preview;
        _prepare = (snapshot, ids, token, _) => prepare(snapshot, ids, token);
        _execute = (plan, token, _) => execute(plan, token);
        _openThreads = openThreads; _openFolder = openFolder;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        // Modals plan MD2: the list step of the dialog factory (modal, fitted to the screen); the title stands in the title bar only.
        Name = "ArchiveCleanupDialog";
        DialogWindow.Configure(this, DialogSize.L, modal: true);
        NameScope names = new(); NameScope.SetNameScope(this, names);
        T Named<T>(T control, string suffix) where T : Control
        { string name = "ArchiveCleanup" + suffix; control.Name = name; names.Register(name, control); return control; }
        Named(_scope, "Scope"); Named(_selectionSummary, "SelectionSummary"); Named(_status, "Status"); Named(_error, "Error");
        Named(_search, "Search"); Named(_filter, "Filter"); Named(_list, "List"); Named(_details, "Details");
        Named(_loss, "LossSummary"); Named(_ack, "ConfirmAcknowledgement"); Named(_refresh, "Refresh");
        Named(_selectShown, "SelectShown"); Named(_clear, "ClearSelection"); Named(_review, "Review");
        Named(_confirm, "Confirm"); Named(_back, "Back"); Named(_cancel, "Cancel"); Named(_close, "Close"); Named(_folder, "OpenFolder");
        _error.Classes.Add("workspace-error");
        _hint.Classes.Add("workspace-help"); _scope.Classes.Add("workspace-meta"); _loss.Classes.Add("workspace-warning");
        _candidatesHeading.Classes.Add("section-title");
        _lossHeading.Classes.Add("section-title"); _selectionSummary.Classes.Add("section-title");
        _review.Classes.Add("primary"); _confirm.Classes.Add("danger");
        // I1b: the secondary tools carry a catalog icon before their text (primary and danger actions stay text).
        MeowChan.Desktop.Controls.IconButtons.WithText(_refresh, "refresh-cw"); MeowChan.Desktop.Controls.IconButtons.WithText(_selectShown, "check-square"); MeowChan.Desktop.Controls.IconButtons.WithText(_clear, "x");
        _back.Classes.Add("subtle"); _close.Classes.Add("subtle");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _search.TextChanged += (_, _) => RenderRows();
        _filter.SelectionChanged += (_, _) => { if (!_localizing) RenderRows(); };
        _list.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        { Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) } });
        _list.SelectionChanged += (_, _) => UpdateDetails();
        _list.ItemTemplate = new FuncDataTemplate<ArchiveCleanupPreviewRow>((row, _) => row == null ? new TextBlock() : BuildRow(row));
        _refresh.Click += async (_, _) => await RefreshAsync();
        _selectShown.Click += (_, _) =>
        {
            foreach (ArchiveCleanupPreviewRow row in VisibleRows()) _selection.SetSelected(row.Id, true);
            RenderRows(); _selectShown.Focus();
        };
        _clear.Click += (_, _) => { _selection.Clear(); RenderRows(); _clear.Focus(); };
        _review.Click += async (_, _) => await ReviewAsync();
        _back.Click += (_, _) => EndConfirmation();
        _ack.IsCheckedChanged += (_, _) => UpdateActions();
        _confirm.Click += async (_, _) => await ExecuteAsync();
        _cancel.Click += (_, _) => CancelOperation();
        _close.Click += (_, _) => RequestClose();
        _folder.Click += (_, _) =>
        {
            if (_busy || _list.SelectedItem is not ArchiveCleanupPreviewRow row) return;
            try
            {
                string directory = row.Summary.MediaDirectory;
                if (row.Media.SavedAssetCount == null)
                    directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)) ?? directory;
                _openFolder?.Invoke(directory);
            }
            catch (Exception error) { _error.Text = Texts.T("archive.cleanup.error.openFolder") + "\n" + Texts.T("error.detail", error.Message); }
        };
        Named(_hint, "Hint");
        // Fixed header: task, protection rules and archive scope stay in view while the inventory scrolls.
        StackPanel header = new() { Spacing = 12 };
        header.Children.Add(_hint); header.Children.Add(_scope);
        _inventoryControls.Children.Add(_candidatesHeading);
        _inventoryControls.Children.Add(_search);
        _inventoryControls.Children.Add(_filter);
        WrapPanel choose = new() { ItemSpacing = 8, LineSpacing = 8 };
        choose.Children.Add(_selectShown); choose.Children.Add(_clear); _inventoryControls.Children.Add(choose);
        StackPanel detailsContent = new() { Spacing = 8 };
        detailsContent.Children.Add(_details); detailsContent.Children.Add(_folder);
        _detailsSection.Content = detailsContent;
        StackPanel bottom = new() { Spacing = 12 };
        bottom.Children.Add(_selectionSummary); bottom.Children.Add(_detailsSection);
        _confirmation.Children.Add(_lossHeading); _confirmation.Children.Add(_loss); _confirmation.Children.Add(_ack);
        Border warning = new() { Child = _confirmation };
        warning.Classes.Add("workspace-warning");
        // Visibility follows the confirmation itself, without a blank warning surface in the preview.
        _confirmation.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) warning.IsVisible = _confirmation.IsVisible; };
        warning.IsVisible = false; bottom.Children.Add(warning);
        _outcome.Children.Add(_status);
        // The outcome keeps the full width above the actions; the confirm step's action stands right-most (review and confirm never show together).
        Border footer = DialogFooter.Build(_confirm, new Control[] { _refresh, _close, _cancel, _back, _review },
            new ScrollViewer { Content = _outcome, MaxHeight = 200, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, statusAbove: true);
        // Inventory, details and loss summary scroll; the next safe action stays in the fixed footer.
        Grid body = new() { Margin = new Thickness(16, 0, 16, 16), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 16 };
        body.Children.Add(_inventoryControls); body.Children.Add(_list); Grid.SetRow(_list, 1);
        body.Children.Add(bottom); Grid.SetRow(bottom, 2);
        _list.MinHeight = 100; _list.Height = 240;
        DialogWindow.Frame(this, DialogFrame.Build(header, body, footer, DialogFrame.ErrorStrip(_error)), modal: true);
        names.Complete();
        Localize();
        // Escape cancels the running operation, then the confirmation step, then closes (RequestClose defers while busy).
        DialogEscape.Attach(this, () =>
        {
            if (_busy) { CancelOperation(); return true; }
            if (_plan == null) return false;
            EndConfirmation(); return true;
        }, close: RequestClose);
        Closing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true; CancelOperation();
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _operation?.Cancel(); Texts.Changed -= OnLanguageChanged; _lifetime.Dispose(); };
        Texts.Changed += OnLanguageChanged;
        Opened += async (_, _) => { _refresh.Focus(); await RefreshAsync(); };
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Localize); return; }
        Localize();
    }
    private void Localize()
    {
        if (_closed) return;
        Title = ArchiveCleanupText.Title;
        _candidatesHeading.Text = Texts.T("archive.cleanup.inventory");
        _detailsSection.Header = Texts.T("archive.cleanup.details.heading");
        _lossHeading.Text = Texts.T("archive.cleanup.loss.heading");
        _hint.Text = Texts.T("archive.cleanup.hint");
        _search.PlaceholderText = Texts.T("archive.cleanup.search");
        AutomationProperties.SetName(_search, _search.PlaceholderText);
        AutomationProperties.SetName(_filter, Texts.T("archive.cleanup.filter.label"));
        AutomationProperties.SetName(_list, Texts.T("archive.cleanup.list.label"));
        AutomationProperties.SetName(_details, Texts.T("archive.cleanup.details.label"));
        int filter = Math.Max(0, _filter.SelectedIndex);
        _localizing = true;
        try
        {
            _filter.ItemsSource = new[] { Texts.T("archive.cleanup.filter.all"), Texts.T("archive.cleanup.filter.candidates"),
                Texts.T("archive.cleanup.state.protected"), Texts.T("archive.cleanup.filter.active"),
                Texts.T("archive.cleanup.filter.unknown"), Texts.T("archive.cleanup.filter.kept") };
            _filter.SelectedIndex = filter;
        }
        finally { _localizing = false; }
        _refresh.Content = Texts.T("archive.cleanup.refresh");
        _selectShown.Content = Texts.T("archive.cleanup.selectShown");
        _clear.Content = Texts.T("archive.cleanup.clearSelection");
        _review.Content = Texts.T("archive.cleanup.review");
        _confirm.Content = Texts.T("archive.cleanup.confirm");
        _back.Content = Texts.T("archive.cleanup.back");
        _cancel.Content = Texts.T("archive.cleanup.cancelOperation"); _close.Content = Texts.T("action.close");
        _folder.Content = Texts.T("archive.cleanup.openFolder");
        _ack.Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Texts.T("archive.cleanup.acknowledge") };
        RenderRows(); UpdateSummary(); UpdateLoss(); UpdateActions();
    }

    private IReadOnlyList<ArchiveCleanupPreviewRow> VisibleRows()
    {
        string query = _search.Text?.Trim() ?? string.Empty;
        return _selection.Preview?.Rows.Where(row => (_filter.SelectedIndex switch
        {
            1 => row.Eligibility == ArchiveCleanupEligibility.Candidate,
            2 => row.Eligibility == ArchiveCleanupEligibility.Protected,
            3 => row.Eligibility == ArchiveCleanupEligibility.Active,
            4 => row.Eligibility == ArchiveCleanupEligibility.Unknown,
            5 => row.Eligibility == ArchiveCleanupEligibility.Kept,
            _ => true
        }) && (query.Length == 0 || ArchiveCleanupText.Identity(row.Id).Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Title.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray() ?? Array.Empty<ArchiveCleanupPreviewRow>();
    }

    private Control BuildRow(ArchiveCleanupPreviewRow row)
    {
        CheckBox choice = new() { Name = "ArchiveCleanupChoose", VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = _selection.CanSelect(row), IsChecked = _selection.IsSelected(row.Id) };
        AutomationProperties.SetName(choice, ArchiveCleanupText.Identity(row.Id) + " · " + ArchiveCleanupText.State(row.Eligibility) + " · " + ArchiveCleanupText.Media(row));
        choice.IsCheckedChanged += (_, _) => { _selection.SetSelected(row.Id, choice.IsChecked == true); UpdateSummary(); UpdateActions(); };
        StackPanel text = new() { Spacing = 4 };
        text.Children.Add(new TextBlock { Text = row.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        TextBlock identity = new() { Text = ArchiveCleanupText.Identity(row.Id), TextWrapping = TextWrapping.Wrap };
        identity.Classes.Add("workspace-meta"); text.Children.Add(identity);
        StackPanel state = new() { Spacing = 4 };
        state.Children.Add(new TextBlock { Text = row.Media.SavedAssetCount is int count
            ? Texts.Count("archive.cleanup.row.versions", count)
            : Texts.T("archive.cleanup.row.versionsUnknown"), TextWrapping = TextWrapping.Wrap });
        TextBlock protection = new() { Text = ArchiveCleanupText.State(row.Eligibility)
            + (row.Reasons.Count > 0 ? " · " + ArchiveCleanupText.Reasons(row) : ""), TextWrapping = TextWrapping.Wrap };
        protection.Classes.Add("workspace-meta"); state.Children.Add(protection);
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("Auto,2*,*"), ColumnSpacing = 12, Margin = new Thickness(0, 8) };
        content.Children.Add(choice); content.Children.Add(text); Grid.SetColumn(text, 1);
        content.Children.Add(state); Grid.SetColumn(state, 2);
        return content;
    }

    private void RenderRows()
    {
        if (_closed || _localizing) return;
        ResourceId? lead = (_list.SelectedItem as ArchiveCleanupPreviewRow)?.Id;
        var rows = VisibleRows(); _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(row => row.Id == lead);
        UpdateDetails(); UpdateSummary(); UpdateActions();
    }
    private void UpdateDetails()
    {
        ArchiveCleanupPreviewRow? row = _list.SelectedItem as ArchiveCleanupPreviewRow;
        _details.Text = row == null ? "" : ArchiveCleanupText.Identity(row.Id) + "\n" + row.Title + "\n"
            + ArchiveCleanupText.Media(row) + "\n" + ArchiveCleanupText.Reasons(row) + "\n" + row.Summary.MediaDirectory
            + (row.Media.Error == null ? "" : "\n" + row.Media.Error);
        _detailsSection.IsVisible = row != null;
        _details.IsVisible = row != null;
        _folder.MaxWidth = 300;
        _folder.Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = row?.Media.SavedAssetCount == null
            ? Texts.T("archive.cleanup.openParentFolder")
            : Texts.T("archive.cleanup.openFolder") };
        _folder.IsVisible = row != null && _openFolder != null; UpdateActions();
    }
    private void UpdateSummary()
    {
        var preview = _selection.Preview;
        _scope.Text = preview == null ? Texts.T("archive.cleanup.scope.unchecked")
            : Texts.T("archive.cleanup.scope.whole", preview.Rows.Count,
                preview.Rows.Count(row => row.Eligibility == ArchiveCleanupEligibility.Candidate))
                + (!preview.Complete ? Texts.T("archive.cleanup.scope.incomplete")
                    : _selection.Invalidated || preview.IsStale ? ArchiveCleanupText.Stale : Texts.T("archive.cleanup.scope.nothingSelected"));
        var selected = _selection.SelectedRows;
        _selectionSummary.Text = Texts.T("archive.cleanup.selection.summary", selected.Count,
            selected.Sum(row => (long)row.PostCount), selected.Sum(row => (long)(row.Media.SavedAssetCount ?? 0)),
            selected.Sum(row => row.Media.VerifiedBytes));
    }
    private void UpdateActions()
    {
        bool confirming = _plan != null;
        bool choosing = !_busy && !confirming && !_closed;
        // The exact loss summary replaces the inventory during confirmation. Back preserves its selection.
        _hint.IsVisible = _inventoryControls.IsVisible = _list.IsVisible = !confirming;
        _detailsSection.IsVisible = _details.IsVisible = !confirming && _list.SelectedItem is ArchiveCleanupPreviewRow;
        _folder.IsVisible = _details.IsVisible && _openFolder != null;
        _list.IsEnabled = choosing && _selection.CanChoose;
        _search.IsEnabled = _filter.IsEnabled = choosing;
        _refresh.IsVisible = !confirming;
        _refresh.IsEnabled = !_busy && _plan == null;
        _selectShown.IsEnabled = choosing && _selection.CanChoose && VisibleRows().Any(_selection.CanSelect);
        _clear.IsEnabled = choosing && _selection.SelectedRows.Count > 0;
        _review.IsEnabled = choosing && _selection.CanChoose && _selection.SelectedRows.Count > 0;
        _review.IsVisible = _plan == null;
        _confirm.IsVisible = _back.IsVisible = _confirmation.IsVisible = _plan != null;
        _confirm.IsEnabled = !_busy && _plan?.CanExecute == true && _ack.IsChecked == true && _selection.CanChoose;
        _back.IsEnabled = _ack.IsEnabled = !_busy;
        _cancel.IsVisible = _busy; _cancel.IsEnabled = _busy && _operation?.IsCancellationRequested == false;
        _close.IsEnabled = !_busy; _folder.IsEnabled = !_busy;
    }
    private void UpdateLoss()
    {
        if (_plan == null) return;
        _loss.Text = Texts.T("archive.cleanup.loss.summary", _plan.SelectedIds.Count,
            _plan.PostCount, _plan.AssetCount, _plan.OwnedBytes)
            + string.Join("\n", _plan.SelectedIds.Select(ArchiveCleanupText.Identity)) + "\n"
            + Texts.T("archive.cleanup.loss.remains");
    }

    private CancellationTokenSource BeginOperation(bool executing = false)
    {
        ++_focusGeneration;
        _busy = true; _executing = executing; _error.Text = "";
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); UpdateActions();
        return _operation;
    }
    private void EndOperation(CancellationTokenSource operation)
    {
        if (ReferenceEquals(_operation, operation)) _operation = null;
        operation.Dispose(); _busy = _executing = false;
        if (_closed) return;
        UpdateSummary(); UpdateActions();
        if (_closeWhenIdle) Close();
    }
    private async Task RefreshAsync()
    {
        if (_busy || _closed || _plan != null) return;
        int generation = ++_generation;
        _selection.Invalidate(); _plan = null; _ack.IsChecked = false; RenderRows();
        var operation = BeginOperation();
        _status.Text = Texts.T("archive.cleanup.status.checking");
        try
        {
            ResourceId[] open = _openThreads().Distinct().ToArray();
            var preview = await Task.Run(() => _preview(open, operation.Token), operation.Token);
            if (_closed || generation != _generation) return;
            operation.Token.ThrowIfCancellationRequested();
            _selection.Load(preview); RenderRows();
            _error.Text = string.Join("\n", preview.Errors);
            _status.Text = preview.IsStale ? ArchiveCleanupText.Stale : Texts.T("archive.cleanup.status.checked");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { if (!_closed) _status.Text = Texts.T("archive.cleanup.status.checkCanceled"); }
        catch (Exception error)
        { if (!_closed) _error.Text = Texts.T("archive.cleanup.error.inventory") + "\n" + Texts.T("error.detail", error.Message); }
        finally { EndOperation(operation); if (!_closed) { RenderRows(); FocusAfterLayout(_refresh, _outcome); } }
    }
    private async Task ReviewAsync()
    {
        if (_busy || _closed || _plan != null || !_selection.CanChoose || _selection.SelectedRows.Count == 0) return;
        ArchiveCleanupPreview preview = _selection.Preview!;
        ResourceId[] ids = _selection.FreezeSelected();
        var operation = BeginOperation();
        _status.Text = Texts.T("archive.cleanup.status.rechecking");
        try
        {
            ResourceId[] open = _openThreads().Distinct().ToArray();
            if (!open.ToHashSet().SetEquals(preview.OpenThreadIds))
            { InvalidatePreview(); return; }
            var plan = await Task.Run(() => _prepare(preview, ids, operation.Token, open), operation.Token);
            if (_closed) return;
            operation.Token.ThrowIfCancellationRequested();
            if (!plan.CanExecute || !plan.SelectedIds.ToHashSet().SetEquals(ids) || plan.SelectedIds.Count != ids.Length)
            {
                _selection.Invalidate(); _status.Text = ArchiveCleanupText.Stale;
                _error.Text = string.Join("\n", plan.Reasons); RenderRows(); return;
            }
            _plan = plan; _ack.IsChecked = false; UpdateLoss();
            _status.Text = Texts.T("archive.cleanup.status.reviewLoss");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { if (!_closed) { _selection.Invalidate(); _status.Text = Texts.T("archive.cleanup.status.reviewCanceled"); } }
        catch (Exception error)
        { if (!_closed) { _selection.Invalidate(); _status.Text = ArchiveCleanupText.Stale; _error.Text = Texts.T("archive.cleanup.error.preview", error.Message); } }
        finally
        {
            EndOperation(operation);
            if (!_closed) { RenderRows(); FocusAfterLayout(_plan != null ? _back : _refresh, _plan != null ? null : _outcome); }
        }
    }
    private void EndConfirmation()
    {
        if (_busy) return;
        _plan = null; _ack.IsChecked = false; UpdateActions(); FocusAfterLayout(_review);
    }
    private async Task ExecuteAsync()
    {
        if (_busy || _closed || _plan is not { CanExecute: true } plan || _ack.IsChecked != true || !_selection.CanChoose) return;
        var operation = BeginOperation(executing: true);
        _status.Text = Texts.T("archive.cleanup.status.deleting");
        // A confirmation can be consumed only once, including failed or canceled attempts.
        _plan = null; _ack.IsChecked = false;
        try
        {
            ResourceId[] open = _openThreads().Distinct().ToArray();
            if (_selection.Preview == null || !open.ToHashSet().SetEquals(_selection.Preview.OpenThreadIds))
            { InvalidatePreview(); return; }
            var result = await Task.Run(() => _execute(plan, operation.Token, open));
            if (_closed) return;
            ArchiveChanged |= result.DeletedThreads.Count > 0 || result.DeletedFileCount > 0;
            _status.Text = Texts.T("archive.cleanup.status.deleted", result.DeletedThreads.Count,
                result.DeletedFileCount, result.DeletedBytes, result.RemainingThreads.Count)
                + (result.Canceled ? Texts.T("archive.cleanup.status.operationCanceled") : "")
                + (result.RequiresNewPreview ? ArchiveCleanupText.Stale : Texts.T("archive.cleanup.status.checkAgain"));
            _error.Text = string.Join("\n", result.Errors);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { if (!_closed) _status.Text = Texts.T("archive.cleanup.status.deleteCanceled"); }
        catch (Exception error)
        {
            ArchiveChanged = true;
            if (!_closed) { _status.Text = Texts.T("archive.cleanup.status.deleteUnfinished"); _error.Text = Texts.T("archive.cleanup.error.delete", error.Message); }
        }
        finally
        {
            _selection.Invalidate(); EndOperation(operation);
            if (!_closed) { RenderRows(); FocusAfterLayout(_refresh, _outcome); }
        }
    }
    private void FocusAfterLayout(Control target, Control? reveal = null)
    {
        int request = ++_focusGeneration;
        bool Current() => !_closed && !_busy && request == _focusGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (!Current()) return;
            UpdateLayout();
            if (!target.IsEffectivelyVisible || !target.IsEffectivelyEnabled) return;
            target.Focus(); (reveal ?? target).BringIntoView();
            Dispatcher.UIThread.Post(() =>
            {
                if (Current() && ReferenceEquals(FocusManager?.GetFocusedElement(), target))
                    (reveal ?? target).BringIntoView(); // Reveal again after the outer scroll viewport has remeasured.
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }
    internal void InvalidatePreview()
    {
        if (_closed) return;
        ++_generation; _selection.Invalidate(); _plan = null; _ack.IsChecked = false;
        _operation?.Cancel(); _status.Text = ArchiveCleanupText.Stale; RenderRows();
    }
    private void CancelOperation()
    {
        if (!_busy || _operation == null) return;
        _operation.Cancel();
        _status.Text = _executing ? Texts.T("archive.cleanup.status.cancelingCommitted")
            : Texts.T("archive.cleanup.status.canceling");
        UpdateActions();
    }
    internal void RequestClose()
    {
        if (_busy) { _closeWhenIdle = true; CancelOperation(); }
        else Close();
    }
}
