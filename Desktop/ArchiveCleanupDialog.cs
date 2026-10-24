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
    private readonly TextBox _search = new();
    private readonly ComboBox _filter = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _list = new();
    private readonly StackPanel _inventoryControls = new() { Spacing = 8 };
    private readonly TextBox _details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52, MaxHeight = 100 };
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
        Name = "ArchiveCleanupDialog"; Width = 820; Height = 700; MinWidth = 360; MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        NameScope names = new(); NameScope.SetNameScope(this, names);
        T Named<T>(T control, string suffix) where T : Control
        { string name = "ArchiveCleanup" + suffix; control.Name = name; names.Register(name, control); return control; }
        Named(_scope, "Scope"); Named(_selectionSummary, "SelectionSummary"); Named(_status, "Status"); Named(_error, "Error");
        Named(_search, "Search"); Named(_filter, "Filter"); Named(_list, "List"); Named(_details, "Details");
        Named(_loss, "LossSummary"); Named(_ack, "ConfirmAcknowledgement"); Named(_refresh, "Refresh");
        Named(_selectShown, "SelectShown"); Named(_clear, "ClearSelection"); Named(_review, "Review");
        Named(_confirm, "Confirm"); Named(_back, "Back"); Named(_cancel, "Cancel"); Named(_close, "Close"); Named(_folder, "OpenFolder");
        _error.Classes.Add("error");
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
            catch (Exception error) { _error.Text = T("Could not open the storage folder.", "Speicherordner konnte nicht geöffnet werden.") + "\n" + error.Message; }
        };
        Named(_hint, "Hint");
        StackPanel header = new() { Spacing = 8 };
        header.Children.Add(_hint); header.Children.Add(_scope);
        _inventoryControls.Children.Add(_search);
        _inventoryControls.Children.Add(_filter);
        WrapPanel choose = new() { ItemSpacing = 8, LineSpacing = 6 };
        choose.Children.Add(_selectShown); choose.Children.Add(_clear); _inventoryControls.Children.Add(choose);
        header.Children.Add(_inventoryControls);
        StackPanel bottom = new() { Spacing = 8 };
        bottom.Children.Add(_selectionSummary); bottom.Children.Add(_details); bottom.Children.Add(_folder);
        _confirmation.Children.Add(_loss); _confirmation.Children.Add(_ack); bottom.Children.Add(_confirmation);
        // Keep the completion text beside the next safe action, including at minimum window size.
        _outcome.Children.Add(_status); _outcome.Children.Add(_error); bottom.Children.Add(_outcome);
        WrapPanel actions = new() { HorizontalAlignment = HorizontalAlignment.Right, ItemSpacing = 8, LineSpacing = 6 };
        foreach (Button button in new[] { _refresh, _close, _cancel, _back, _review, _confirm }) actions.Children.Add(button);
        _outcome.Children.Add(actions);
        // One scrolling body keeps all actions reachable at the supported narrow/minimum size.
        Grid body = new() { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        body.Children.Add(header); body.Children.Add(_list); Grid.SetRow(_list, 1);
        body.Children.Add(bottom); Grid.SetRow(bottom, 2);
        _list.MinHeight = 100; _list.Height = 240;
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        KeyboardNavigation.SetTabNavigation(body, KeyboardNavigationMode.Cycle);
        names.Complete();
        Localize();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_busy) CancelOperation(); else if (_plan != null) EndConfirmation(); else RequestClose();
        };
        Closing += (_, e) =>
        {
            if (!_busy) return;
            e.Cancel = true; CancelOperation();
        };
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _operation?.Cancel(); Texts.Changed -= OnLanguageChanged; _lifetime.Dispose(); };
        Texts.Changed += OnLanguageChanged;
        Opened += async (_, _) => { _refresh.Focus(); await RefreshAsync(); };
    }

    private static string T(string en, string de) => ArchiveCleanupText.Get(en, de);
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Localize); return; }
        Localize();
    }
    private void Localize()
    {
        if (_closed) return;
        Title = ArchiveCleanupText.Title;
        _hint.Text = T("Review the whole archive. Candidates have 0–2 verified saved asset versions, including history and variants. Favorites, ratings, bookmarks, groups, reading positions and unfinished work are protected. Unknown storage is never treated as empty.",
            "Prüft das gesamte Archiv. Kandidaten haben 0–2 nachgewiesene gespeicherte Assetfassungen, einschließlich Historie und Varianten. Favoriten, Bewertungen, Lesezeichen, Gruppen, Lesestellen und unerledigte Aufträge sind geschützt. Unbekannter Speicher gilt niemals als leer.");
        _search.PlaceholderText = T("Search identity or title", "Identität oder Titel suchen");
        AutomationProperties.SetName(_search, _search.PlaceholderText);
        AutomationProperties.SetName(_filter, T("Preview filter", "Vorschaufilter"));
        AutomationProperties.SetName(_list, T("Archive inventory — use the checkboxes to select candidates", "Archivbestand — Kandidaten über Kontrollkästchen auswählen"));
        AutomationProperties.SetName(_details, T("Thread details and storage location", "Threaddetails und Speicherort"));
        int filter = Math.Max(0, _filter.SelectedIndex);
        _localizing = true;
        try
        {
            _filter.ItemsSource = new[] { T("All archive threads", "Alle Archivthreads"), T("Candidates", "Kandidaten"),
                T("Protected", "Geschützt"), T("Unfinished work", "Unerledigte Aufträge"),
                T("Unknown or unconfirmed storage", "Unbekannt oder Speicherort unbestätigt"), T("Keep: more than 2", "Behalten: mehr als 2") };
            _filter.SelectedIndex = filter;
        }
        finally { _localizing = false; }
        _refresh.Content = T("Check again", "Neu prüfen");
        _selectShown.Content = T("Select shown candidates", "Sichtbare Kandidaten auswählen");
        _clear.Content = T("Clear selection", "Auswahl leeren");
        _review.Content = T("Review deletion…", "Löschung prüfen…");
        _confirm.Content = T("Permanently delete locally", "Lokal endgültig löschen");
        _back.Content = T("Back to selection", "Zurück zur Auswahl");
        _cancel.Content = T("Cancel operation", "Vorgang abbrechen"); _close.Content = T("Close", "Schließen");
        _folder.Content = T("Open storage folder", "Speicherordner öffnen");
        _ack.Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = T("I understand: the selected local archive data will be permanently deleted, without a recycle bin.",
            "Ich verstehe: Die ausgewählten lokalen Archivdaten werden endgültig ohne Papierkorb gelöscht.") };
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
        StackPanel text = new() { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = ArchiveCleanupText.Identity(row.Id) + " · " + row.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = ArchiveCleanupText.Media(row), TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = ArchiveCleanupText.State(row.Eligibility) + (row.Reasons.Count > 0 ? " · " + ArchiveCleanupText.Reasons(row) : ""), TextWrapping = TextWrapping.Wrap });
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Margin = new Thickness(0, 4) };
        content.Children.Add(choice); content.Children.Add(text); Grid.SetColumn(text, 1);
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
            + ArchiveCleanupText.Reasons(row) + "\n" + row.Summary.MediaDirectory
            + (row.Media.Error == null ? "" : "\n" + row.Media.Error);
        _details.IsVisible = row != null;
        _folder.MaxWidth = 300;
        _folder.Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = row?.Media.SavedAssetCount == null
            ? T("Open parent storage folder", "Übergeordneten Speicherordner öffnen")
            : T("Open storage folder", "Speicherordner öffnen") };
        _folder.IsVisible = row != null && _openFolder != null; UpdateActions();
    }
    private void UpdateSummary()
    {
        var preview = _selection.Preview;
        _scope.Text = preview == null ? T("Inventory has not been checked.", "Bestand noch nicht geprüft.")
            : T($"Whole archive: {preview.Rows.Count:N0} threads · {preview.Rows.Count(row => row.Eligibility == ArchiveCleanupEligibility.Candidate):N0} candidates. ",
                $"Gesamtes Archiv: {preview.Rows.Count:N0} Threads · {preview.Rows.Count(row => row.Eligibility == ArchiveCleanupEligibility.Candidate):N0} Kandidaten. ")
                + (!preview.Complete ? T("Incomplete inventory — selection is locked.", "Bestand unvollständig — Auswahl gesperrt.")
                    : _selection.Invalidated || preview.IsStale ? ArchiveCleanupText.Stale : T("Nothing is selected automatically.", "Es wird nichts automatisch ausgewählt."));
        var selected = _selection.SelectedRows;
        _selectionSummary.Text = T($"Selected across all filters: {selected.Count:N0} threads · {selected.Sum(row => (long)row.PostCount):N0} posts · {selected.Sum(row => (long)(row.Media.SavedAssetCount ?? 0)):N0} saved versions · {selected.Sum(row => row.Media.VerifiedBytes):N0} verified media bytes.",
            $"Über alle Filter ausgewählt: {selected.Count:N0} Threads · {selected.Sum(row => (long)row.PostCount):N0} Beiträge · {selected.Sum(row => (long)(row.Media.SavedAssetCount ?? 0)):N0} gespeicherte Fassungen · {selected.Sum(row => row.Media.VerifiedBytes):N0} nachgewiesene Medienbytes.");
    }
    private void UpdateActions()
    {
        bool confirming = _plan != null;
        bool choosing = !_busy && !confirming && !_closed;
        // The exact loss summary replaces the inventory during confirmation. Back preserves its selection.
        _hint.IsVisible = _inventoryControls.IsVisible = _list.IsVisible = !confirming;
        _details.IsVisible = !confirming && _list.SelectedItem is ArchiveCleanupPreviewRow;
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
        _loss.Text = T($"Permanently remove {_plan.SelectedIds.Count:N0} selected threads, {_plan.PostCount:N0} archived posts and {_plan.AssetCount:N0} saved asset versions. The confirmed owned file plan contains {_plan.OwnedBytes:N0} bytes, including archive housekeeping.\n",
            $"{_plan.SelectedIds.Count:N0} ausgewählte Threads, {_plan.PostCount:N0} archivierte Beiträge und {_plan.AssetCount:N0} gespeicherte Assetfassungen endgültig entfernen. Der bestätigte Dateiplan umfasst {_plan.OwnedBytes:N0} Bytes einschließlich Archivverwaltungsdateien.\n")
            + string.Join("\n", _plan.SelectedIds.Select(ArchiveCleanupText.Identity)) + "\n"
            + T("Exports and unrelated files remain. Download and watch jobs remain unchanged.",
                "Exporte und fremde Dateien bleiben erhalten. Download- und Beobachtungsaufträge bleiben unverändert.");
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
        _status.Text = T("Checking local archive and protection data…", "Lokales Archiv und Schutzdaten werden geprüft…");
        try
        {
            ResourceId[] open = _openThreads().Distinct().ToArray();
            var preview = await Task.Run(() => _preview(open, operation.Token), operation.Token);
            if (_closed || generation != _generation) return;
            operation.Token.ThrowIfCancellationRequested();
            _selection.Load(preview); RenderRows();
            _error.Text = string.Join("\n", preview.Errors);
            _status.Text = preview.IsStale ? ArchiveCleanupText.Stale : T("Inventory checked. Choose the candidates you want to remove.", "Bestand geprüft. Gewünschte Kandidaten bewusst auswählen.");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { if (!_closed) _status.Text = T("Check canceled. Check again before selecting.", "Prüfung abgebrochen. Vor der Auswahl neu prüfen."); }
        catch (Exception error)
        { if (!_closed) _error.Text = T("The inventory could not be verified. Nothing is selected.", "Bestand konnte nicht geprüft werden. Es ist nichts ausgewählt.") + "\n" + error.Message; }
        finally { EndOperation(operation); if (!_closed) { RenderRows(); FocusAfterLayout(_refresh, _outcome); } }
    }
    private async Task ReviewAsync()
    {
        if (_busy || _closed || _plan != null || !_selection.CanChoose || _selection.SelectedRows.Count == 0) return;
        ArchiveCleanupPreview preview = _selection.Preview!;
        ResourceId[] ids = _selection.FreezeSelected();
        var operation = BeginOperation();
        _status.Text = T("Rechecking this exact selection before confirmation…", "Genau diese Auswahl wird vor der Bestätigung erneut geprüft…");
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
            _status.Text = T("Review the loss summary. Deletion needs your explicit confirmation.", "Verlustübersicht prüfen. Erst die bewusste Bestätigung erlaubt das Löschen.");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { if (!_closed) { _selection.Invalidate(); _status.Text = T("Review canceled. Check again.", "Prüfung abgebrochen. Neu prüfen."); } }
        catch (Exception error)
        { if (!_closed) { _selection.Invalidate(); _status.Text = ArchiveCleanupText.Stale; _error.Text = error.Message; } }
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
        _status.Text = T("Rechecking and deleting the confirmed local selection…", "Bestätigte lokale Auswahl wird erneut geprüft und gelöscht…");
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
            _status.Text = T($"Deleted {result.DeletedThreads.Count:N0} threads and {result.DeletedFileCount:N0} owned files ({result.DeletedBytes:N0} bytes). {result.RemainingThreads.Count:N0} selected threads remain. ",
                $"{result.DeletedThreads.Count:N0} Threads und {result.DeletedFileCount:N0} zugeordnete Dateien ({result.DeletedBytes:N0} Bytes) gelöscht. {result.RemainingThreads.Count:N0} ausgewählte Threads verbleiben. ")
                + (result.Canceled ? T("The operation was canceled. ", "Der Vorgang wurde abgebrochen. ") : "")
                + (result.RequiresNewPreview ? ArchiveCleanupText.Stale : T("Check again before another selection.", "Vor einer weiteren Auswahl neu prüfen."));
            _error.Text = string.Join("\n", result.Errors);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { if (!_closed) _status.Text = T("Deletion canceled. Recheck the inventory; any already removed data remains removed.", "Löschung abgebrochen. Bestand neu prüfen; bereits entfernte Daten bleiben entfernt."); }
        catch (Exception error)
        {
            ArchiveChanged = true;
            if (!_closed) { _status.Text = T("Deletion did not finish. Check the inventory again; some files may already be removed.", "Löschung nicht abgeschlossen. Bestand neu prüfen; einzelne Dateien können bereits entfernt sein."); _error.Text = error.Message; }
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
        _status.Text = _executing ? T("Canceling; already committed deletions will be reported…", "Abbruch läuft; bereits abgeschlossene Löschungen werden gemeldet…")
            : T("Canceling…", "Wird abgebrochen…");
        UpdateActions();
    }
    internal void RequestClose()
    {
        if (_busy) { _closeWhenIdle = true; CancelOperation(); }
        else Close();
    }
}
