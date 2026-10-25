using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan;

public partial class SavedArchiveSearchesDialog : Window
{
    private enum EditKind { None, Create, Rename, Replace, Delete, Apply, Recover, Discard }
    private readonly DesktopSessionHost _host;
    private readonly ArchivePage _page;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IDisposable> _registrations = new();
    private readonly CommandRouter _router;
    private SavedArchiveSearchSnapshot? _snapshot;
    private EditKind _edit;
    private Guid _target;
    private long _revision;
    private ArchiveSearchSpec? _definition;
    private string _initialName = "";
    private bool _working;
    private bool _closed;
    private bool _closingAccepted;
    private bool _discardCloses;
    private bool _initialFocusEligible = true;
    private bool _initialFocusQueued;
    private bool _initialFocusWasActive;
    private Control? _initialFocusTarget;
    private EditKind _initialFocusEdit;
    private readonly ArchiveSearchSpec? _initialDefinition;
    private ListBox List => this.FindControl<ListBox>("SavedSearchList")!;
    private TextBox NameInput => this.FindControl<TextBox>("SavedSearchName")!;
    private SavedArchiveSearch? Selected => List.SelectedItem as SavedArchiveSearch;
    internal bool Applied { get; private set; }

    internal SavedArchiveSearchesDialog(DesktopSessionHost host, ArchivePage page, ArchiveSearchSpec? initialDefinition = null)
    {
        InitializeComponent(); _host = host; _page = page; _initialDefinition = initialDefinition;
        _router = new CommandRouter(this);
        List.ItemTemplate = new FuncDataTemplate<SavedArchiveSearch>((entry, _) =>
        {
            string detail = entry == null ? "" : entry.Definition is { } spec ? SavedArchiveSearchDescription.Describe(spec) : Texts.T("savedSearch.unsupported") + " " + entry.UnsupportedReason;
            var label = new TextBlock { Text = entry?.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Margin = new Avalonia.Thickness(4, 8), Children = { label } };
            AutomationProperties.SetName(panel, entry?.Name + ". " + detail);
            ToolTip.SetTip(panel, entry?.Name + "\n" + detail);
            return panel;
        });
        Wire("SavedSearchCreate", "save-current", () => Begin(EditKind.Create), CanCapture);
        Wire("SavedSearchApply", "apply", () => Begin(EditKind.Apply), () => Selected?.CanApply == true && !_page.IsBusy);
        Wire("SavedSearchRename", "rename", () => Begin(EditKind.Rename), CanEditSelection);
        Wire("SavedSearchReplace", "replace", () => Begin(EditKind.Replace), () => CanEditSelection() && CanCapture());
        Wire("SavedSearchDelete", "delete", () => Begin(EditKind.Delete), CanEditSelection);
        Button("SavedSearchConfirm").Click += async (_, _) => await ConfirmAsync();
        Button("SavedSearchCancelEdit").Click += (_, _) => CancelEdit();
        Button("SavedSearchReload").Click += async (_, _) => await ReloadAsync();
        Button("SavedSearchRecover").Click += (_, _) => Begin(EditKind.Recover);
        Button("SavedSearchClose").Click += (_, _) => Close();
        List.SelectionChanged += (_, _) => Refresh();
        NameInput.TextChanged += (_, _) => Refresh();
        _page.SearchStateChanged += OnPageStateChanged;
        _host.StateChanged += OnHostStateChanged;
        Texts.Changed += OnLanguageChanged;
        KeyDown += (_, e) =>
        {
            if (_router.Route(e)) return;
            if (e.Key == Key.Escape) { e.Handled = true; if (_edit != EditKind.None) CancelEdit(); else Close(); }
        };
        AddHandler(PointerPressedEvent, OnInitialFocusPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnInitialFocusKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, OnInitialFocusChanged, RoutingStrategies.Bubble, handledEventsToo: true);
        Activated += OnInitialFocusActivated;
        Deactivated += OnInitialFocusDeactivated;
        LayoutUpdated += OnInitialFocusLayout;
        PropertyChanged += OnInitialFocusVisibility;
        Opened += async (_, _) =>
        {
            await ReloadAsync();
            if (_closed) return;
            if (_initialDefinition != null && _snapshot?.CanWrite == true)
            {
                Begin(EditKind.Create, _initialDefinition, focusEditor: false);
                PrepareInitialFocus(NameInput, EditKind.Create);
            }
            else PrepareInitialFocus(List, EditKind.None);
        };
        Closing += (_, e) =>
        {
            CancelInitialFocus();
            if (_closingAccepted) return;
            if (_working) { _lifetime.Cancel(); return; }
            if (HasNameChanges()) { e.Cancel = true; _discardCloses = true; ShowDiscard(); return; }
            // Canceling a dialog also cancels its pending apply/write before publication.
            _lifetime.Cancel();
        };
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); CancelInitialFocus();
            _page.SearchStateChanged -= OnPageStateChanged; _host.StateChanged -= OnHostStateChanged; Texts.Changed -= OnLanguageChanged;
            foreach (IDisposable registration in _registrations) registration.Dispose();
            _router.Dispose();
        };
        SizeChanged += (_, _) => ArrangeWorkspace();
        Localize(); Refresh(); ArrangeWorkspace();
    }
    // Initial focus belongs to this opening only. Opened/Reload can complete before
    // native activation or the newly rendered editor's first layout has completed.
    private void PrepareInitialFocus(Control target, EditKind edit)
    {
        if (!_initialFocusEligible) return;
        _initialFocusTarget = target; _initialFocusEdit = edit;
        QueueInitialFocus();
    }
    private void OnInitialFocusPointer(object? sender, PointerPressedEventArgs args) => CancelInitialFocus();
    private void OnInitialFocusKey(object? sender, KeyEventArgs args) => CancelInitialFocus();
    private void OnInitialFocusChanged(object? sender, FocusChangedEventArgs args)
    {
        if (FocusManager?.GetFocusedElement() is Control focused
            && ReferenceEquals(TopLevel.GetTopLevel(focused), this)
            && (_initialFocusTarget == null || !_initialFocusTarget.IsKeyboardFocusWithin))
            CancelInitialFocus();
    }
    private void OnInitialFocusActivated(object? sender, EventArgs args)
    {
        _initialFocusWasActive = true;
        QueueInitialFocus();
    }
    private void OnInitialFocusDeactivated(object? sender, EventArgs args)
    {
        if (_initialFocusWasActive) CancelInitialFocus();
    }
    private void OnInitialFocusLayout(object? sender, EventArgs args) => QueueInitialFocus();
    private void OnInitialFocusVisibility(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty && !IsVisible) CancelInitialFocus();
    }
    private bool InitialFocusReady()
    {
        if (!_initialFocusEligible || _initialFocusTarget is not { } target) return false;
        if (_closed || _lifetime.IsCancellationRequested || !Ready || !IsVisible || _edit != _initialFocusEdit
            || OwnedWindows.Any(window => window.IsVisible))
        {
            CancelInitialFocus(); return false;
        }
        if (target.IsKeyboardFocusWithin) { CancelInitialFocus(); return false; }
        // A newer focus selection in our dialog wins even if a later layout clears it.
        if (FocusManager?.GetFocusedElement() is Control focused
            && ReferenceEquals(TopLevel.GetTopLevel(focused), this))
        {
            CancelInitialFocus(); return false;
        }
        return IsActive && !_working && IsEnabled && IsMeasureValid && IsArrangeValid
            && target.Focusable && target.IsEffectivelyVisible && target.IsEffectivelyEnabled
            && ReferenceEquals(TopLevel.GetTopLevel(target), this)
            && target.IsMeasureValid && target.IsArrangeValid && target.Bounds.Width > 0 && target.Bounds.Height > 0;
    }
    private void QueueInitialFocus()
    {
        if (_initialFocusQueued || !InitialFocusReady()) return;
        _initialFocusQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _initialFocusQueued = false;
            if (!InitialFocusReady()) return;
            Control target = _initialFocusTarget!;
            // Consume before focusing: this can never become a recurring focus trap.
            CancelInitialFocus();
            if (target.Focus() && ReferenceEquals(target, NameInput)) NameInput.SelectAll();
        }, DispatcherPriority.Loaded);
    }
    private void CancelInitialFocus()
    {
        _initialFocusEligible = false; _initialFocusTarget = null;
        RemoveHandler(PointerPressedEvent, OnInitialFocusPointer);
        RemoveHandler(KeyDownEvent, OnInitialFocusKey);
        RemoveHandler(GotFocusEvent, OnInitialFocusChanged);
        Activated -= OnInitialFocusActivated; Deactivated -= OnInitialFocusDeactivated;
        LayoutUpdated -= OnInitialFocusLayout; PropertyChanged -= OnInitialFocusVisibility;
    }
    private void ArrangeWorkspace()
    {
        Grid layout = (Grid)Content!;
        layout.RowDefinitions[1].Height = _edit == EditKind.None ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        layout.RowDefinitions[3].Height = _edit == EditKind.None ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        bool stacked = ClientSize.Width < 580;
        Grid workspace = this.FindControl<Grid>("SavedSearchWorkspace")!;
        workspace.ColumnDefinitions = new(stacked ? "*" : "220,*");
        workspace.RowDefinitions = new(stacked ? "*,*" : "*");
        ScrollViewer detail = this.FindControl<ScrollViewer>("SavedSearchSelection")!;
        Grid.SetColumn(detail, stacked ? 0 : 1); Grid.SetRow(detail, stacked ? 1 : 0);
    }
    internal void CloseForLifetime() { if (_closed) return; _closingAccepted = true; _lifetime.Cancel(); Close(); }
    private Button Button(string name) => this.FindControl<Button>(name)!;
    private void Wire(string name, string suffix, Action execute, Func<bool> canExecute)
    {
        string id = "archive.saved-searches." + suffix;
        bool Available() => !_closed && !_working && _edit == EditKind.None && Ready && canExecute();
        Button button = Button(name);
        button.Command = new DelegateCommand(execute, Available);
        CommandBindings.Apply(button, id);
        _registrations.Add(_router.Register(new CommandRegistration(id, this, () => !_closed && IsVisible,
            Available, execute, Priority: 300)));
    }
    private bool Ready => _host.State == DesktopSessionState.Ready;
    private bool CanCapture() => _snapshot?.CanWrite == true && _page.CanSaveCurrentSearch;
    private bool CanEditSelection() => _snapshot?.CanWrite == true && Selected != null;
    private bool HasNameChanges() => (_edit is EditKind.Create or EditKind.Rename || _edit == EditKind.Discard && (_discardedEdit is EditKind.Create or EditKind.Rename)) && (NameInput.Text ?? "") != _initialName;
    private void Begin(EditKind kind, ArchiveSearchSpec? frozen = null, bool focusEditor = true)
    {
        if (_closed || _working || _edit != EditKind.None || _snapshot == null || !Ready) return;
        if (kind is EditKind.Create or EditKind.Replace)
        {
            frozen ??= _page.CaptureAppliedSearch();
            if (frozen == null || !_snapshot.CanWrite) return;
        }
        if ((kind is EditKind.Apply or EditKind.Rename or EditKind.Replace or EditKind.Delete) && Selected == null) return;
        _edit = kind; _target = kind == EditKind.Create ? Guid.NewGuid() : Selected?.Id ?? Guid.Empty;
        _revision = _snapshot.Revision; _definition = frozen ?? Selected?.Definition;
        _initialName = kind == EditKind.Create ? "" : Selected?.Name ?? "";
        NameInput.Text = _initialName;
        RenderEditor();
        if (kind == EditKind.Apply && !_page.HasUnappliedSearchDraft) { _ = ConfirmAsync(); return; }
        if (!focusEditor) return;
        if (kind is EditKind.Create or EditKind.Rename) { NameInput.Focus(); NameInput.SelectAll(); }
        else Button("SavedSearchConfirm").Focus();
    }
    private void RenderEditor()
    {
        if (_initialFocusTarget != null && _edit != _initialFocusEdit) CancelInitialFocus();
        this.FindControl<StackPanel>("SavedSearchEditor")!.IsVisible = _edit != EditKind.None;
        NameInput.IsVisible = _edit is EditKind.Create or EditKind.Rename;
        Button("SavedSearchConfirm").Classes.Set("danger", _edit is EditKind.Delete or EditKind.Discard);
        Button("SavedSearchConfirm").Classes.Set("primary", _edit is not (EditKind.Delete or EditKind.Discard));
        this.FindControl<TextBlock>("SavedSearchPrompt")!.Text = Texts.T("savedSearch.prompt." + _edit, _initialName);
        this.FindControl<TextBlock>("SavedSearchDefinition")!.Text = _edit == EditKind.Discard ? "" : _definition is { } spec ? SavedArchiveSearchDescription.Describe(spec) : "";
        ToolTip.SetTip(this.FindControl<TextBlock>("SavedSearchDefinition")!, this.FindControl<TextBlock>("SavedSearchDefinition")!.Text);
        Button("SavedSearchConfirm").Content = Texts.T("savedSearch.confirm." + _edit);
        Refresh();
    }
    private void CancelEdit()
    {
        if (_working) { _lifetime.Cancel(); _closingAccepted = true; Close(); return; }
        if (_edit == EditKind.Discard) { _edit = _discardedEdit; _discardCloses = false; RenderEditor(); NameInput.Focus(); return; }
        if (HasNameChanges()) { _discardCloses = false; ShowDiscard(); return; }
        _edit = EditKind.None; RenderEditor(); List.Focus();
    }
    private EditKind _discardedEdit;
    private void ShowDiscard()
    {
        if (_edit != EditKind.Discard) _discardedEdit = _edit;
        _edit = EditKind.Discard; RenderEditor(); Button("SavedSearchConfirm").Focus();
    }
    private async Task ConfirmAsync()
    {
        if (_closed || _working || !Ready || _edit == EditKind.None) return;
        if (_edit == EditKind.Discard) { _edit = EditKind.None; if (_discardCloses) { _closingAccepted = true; _lifetime.Cancel(); Close(); } else { RenderEditor(); List.Focus(); } return; }
        EditKind kind = _edit; Guid target = _target; long revision = _revision;
        string name = NameInput.Text ?? ""; ArchiveSearchSpec? definition = _definition;
        IInputElement? writeOrigin = FocusManager?.GetFocusedElement();
        bool restoreListFocus = false;
        _working = true; Refresh(); Status(Texts.T("savedSearch.working"));
        try
        {
            if (kind == EditKind.Apply)
            {
                if (definition == null) return;
                bool applied = await _page.ApplySearchSpecAsync(definition, _lifetime.Token);
                if (_closed || _lifetime.IsCancellationRequested || !Ready) return;
                if (!applied) { _edit = EditKind.None; RenderEditor(); Status(Texts.T("savedSearch.applyFailed")); return; }
                Applied = true; _closingAccepted = true; Close(); return;
            }
            SavedArchiveSearchSnapshot result = kind switch
            {
                EditKind.Create => await _host.CreateSavedArchiveSearchAsync(target, name, definition!, revision, _lifetime.Token),
                EditKind.Rename => await _host.RenameSavedArchiveSearchAsync(target, name, revision, _lifetime.Token),
                EditKind.Replace => await _host.ReplaceSavedArchiveSearchAsync(target, definition!, revision, _lifetime.Token),
                EditKind.Delete => await _host.DeleteSavedArchiveSearchAsync(target, revision, _lifetime.Token),
                EditKind.Recover => await _host.RecoverSavedArchiveSearchesAsync(revision, _lifetime.Token),
                _ => throw new InvalidOperationException()
            };
            if (_closed || !Ready) return;
            int previous = List.SelectedIndex;
            _snapshot = result; _edit = EditKind.None;
            Render(target, previous); RenderEditor(); Status(Texts.T("savedSearch.saved"));
            restoreListFocus = true;
        }
        catch (SavedSearchRevisionException error)
        {
            if (_closed) return;
            _snapshot = error.Current; Render(target); Status(Texts.T("savedSearch.conflict"));
            // Preserve frozen target/name/definition. Retry is explicitly against the reviewed revision.
            _revision = error.Current.Revision;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) Status(Texts.T("savedSearch.error", error.Message)); }
        finally
        {
            if (!_closed)
            {
                IInputElement? currentFocus = FocusManager?.GetFocusedElement();
                _working = false; Refresh();
                // The list is enabled only now. A newer deliberate focus target wins.
                if (restoreListFocus && (currentFocus == null || ReferenceEquals(currentFocus, writeOrigin)))
                {
                    if (List.ItemCount > 0) List.Focus(); else Button("SavedSearchCreate").Focus();
                }
            }
        }
    }
    private async Task ReloadAsync()
    {
        if (_closed || _working || _edit != EditKind.None) return;
        _working = true; Refresh();
        Guid? selected = Selected?.Id;
        try
        {
            SavedArchiveSearchSnapshot result = await _host.LoadSavedArchiveSearchesAsync(_lifetime.Token);
            if (_closed || !Ready) return;
            _snapshot = result; Render(selected); Status(result.Error == null ? Texts.T("savedSearch.hint") : Texts.T("savedSearch.readonly", result.Error));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!_closed) Status(Texts.T("savedSearch.error", error.Message)); }
        finally { if (!_closed) { _working = false; Refresh(); } }
    }
    private void Render(Guid? selected = null, int previous = -1)
    {
        List.ItemsSource = _snapshot?.Searches;
        List.SelectedItem = _snapshot?.Searches.FirstOrDefault(x => x.Id == selected);
        if (List.SelectedItem == null && List.ItemCount > 0 && previous >= 0) List.SelectedIndex = Math.Min(previous, List.ItemCount - 1);
        Button("SavedSearchRecover").IsVisible = _snapshot?.State == SavedSearchStoreState.RecoveryAvailable;
    }
    private void Refresh()
    {
        this.FindControl<ScrollViewer>("SavedSearchEditorScroll")!.IsVisible = _edit != EditKind.None;
        this.FindControl<Grid>("SavedSearchWorkspace")!.IsVisible = _edit == EditKind.None;
        ArrangeWorkspace();
        this.FindControl<TextBlock>("SavedSearchSelectionName")!.Text = Selected?.Name ?? "";
        this.FindControl<TextBlock>("SavedSearchSelectionDefinition")!.Text = Selected is { } selected
            ? selected.Definition is { } spec ? SavedArchiveSearchDescription.Describe(spec) : Texts.T("savedSearch.unsupported") + " " + selected.UnsupportedReason
            : "";
        foreach (string id in new[] { "SavedSearchCreate", "SavedSearchApply", "SavedSearchRename", "SavedSearchReplace", "SavedSearchDelete" })
        {
            Button(id).IsVisible = _edit == EditKind.None;
            CommandBindings.Refresh(Button(id));
        }
        Button("SavedSearchReload").IsVisible = _edit == EditKind.None;
        Button("SavedSearchRecover").IsVisible = _edit == EditKind.None && _snapshot?.State == SavedSearchStoreState.RecoveryAvailable;
        bool idle = !_working && Ready;
        List.IsEnabled = idle && _edit == EditKind.None;
        NameInput.IsEnabled = idle;
        Button("SavedSearchReload").IsEnabled = idle && _edit == EditKind.None;
        Button("SavedSearchRecover").IsEnabled = idle && _edit == EditKind.None;
        Button("SavedSearchConfirm").IsEnabled = idle && _edit != EditKind.None && (_edit == EditKind.Apply || _edit == EditKind.Discard || _snapshot?.CanWrite == true || _edit == EditKind.Recover && _snapshot?.State == SavedSearchStoreState.RecoveryAvailable);
        Button("SavedSearchCancelEdit").Content = Texts.T(_working ? "savedSearch.cancelOperation" : _edit == EditKind.Discard ? "savedSearch.keepEditing" : "savedSearch.cancel");
    }
    private void Status(string text) => this.FindControl<TextBlock>("SavedSearchStatus")!.Text = text;
    private void OnPageStateChanged() => Dispatcher.UIThread.Post(() => { if (!_closed) Refresh(); });
    private void OnHostStateChanged() => Dispatcher.UIThread.Post(() => { if (!_closed) { if (!Ready) { CloseForLifetime(); } else Refresh(); } });
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!_closed) { Localize(); Render(Selected?.Id); if (_edit != EditKind.None) RenderEditor(); } });
    private void Localize()
    {
        Title = Texts.T("savedSearch.title");
        this.FindControl<TextBlock>("SavedSearchHint")!.Text = Texts.T("savedSearch.hint");
        foreach (var pair in new[] { ("SavedSearchCreate", "save-current"), ("SavedSearchApply", "apply"), ("SavedSearchRename", "rename"), ("SavedSearchReplace", "replace"), ("SavedSearchDelete", "delete") }) CommandBindings.Apply(Button(pair.Item1), "archive.saved-searches." + pair.Item2);
        Button("SavedSearchReload").Content = Texts.T("savedSearch.reload"); Button("SavedSearchRecover").Content = Texts.T("savedSearch.recover"); Button("SavedSearchClose").Content = Texts.T("savedSearch.close");
        AutomationProperties.SetName(List, Texts.T("savedSearch.title")); AutomationProperties.SetName(NameInput, Texts.T("savedSearch.name"));
    }
}
