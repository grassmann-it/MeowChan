using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan;

public partial class SourcesPage : UserControl
{
    private readonly Func<DiagnosticTarget, bool>? _openWatch;
    private readonly Action<string>? _editRules;
    private readonly Action? _openDiagnostics;
    private readonly Action? _openDownloads;
    private readonly Func<string, Action<Window>, Task<bool>>? _submit;
    private IReadOnlyList<SourceRow> _rows = Array.Empty<SourceRow>();
    private sealed record SourceCard(Button Control, TextBlock Name, TextBlock Address, TextBlock Observation);
    private sealed record SourceMediaIntegritySaveError(string PluginOwnerId, string SourceId, string Message);
    private SourceMediaIntegritySaveError? _sourceMediaIntegritySaveError;
    private readonly Dictionary<string, SourceCard> _sourceCards = new Dictionary<string, SourceCard>(StringComparer.Ordinal);
    private string? _selectedId;
    private bool _available;
    private bool _busy;
    private bool _ended;
    private bool _hasUrlResult;
    private long _sourceSelectionRevision;
    private Action? _cancelSubmitFocusRestore;
    private bool _showDetails;
    private bool _narrow;
    internal Action<string>? OpenPluginSettings { get; set; }
    internal Func<IReadOnlyList<SourceMediaIntegrityPolicyView>>? GetSourceMediaIntegrityPolicies { get; set; }
    internal Func<string, string, bool, long, Task>? SetSourceMediaIntegrityPermission { get; set; }
    internal bool IsBusy => _busy;
    internal event EventHandler? BusyChanged;

    public SourcesPage()
    {
        InitializeComponent(); InitializeContextMenus();
        DetachedFromVisualTree += (_, _) => _cancelSubmitFocusRestore?.Invoke();
    }
    internal SourcesPage(Func<DiagnosticTarget, bool> openWatch, Action<string> editRules,
        Action openDiagnostics, Action openDownloads, Func<string, Task<bool>> submit)
        : this(openWatch, editRules, openDiagnostics, openDownloads, (input, _) => submit(input)) { }

    internal SourcesPage(Func<DiagnosticTarget, bool> openWatch, Action<string> editRules,
        Action openDiagnostics, Action openDownloads, Func<string, Action<Window>, Task<bool>> submit) : this()
    {
        _openWatch = openWatch;
        _editRules = editRules;
        _openDiagnostics = openDiagnostics;
        _openDownloads = openDownloads;
        _submit = submit;
    }

    internal void Refresh(IReadOnlyList<ProviderDescriptor> providers, DesktopSessionView view, bool available)
    {
        if (_ended) return;
        if (view.State != DesktopSessionState.Ready) _cancelSubmitFocusRestore?.Invoke();
        _available = available;
        _rows = SourcesSnapshot.From(providers, view, GetSourceMediaIntegrityPolicies?.Invoke());
        if (!_rows.Any(row => row.Id == _selectedId)) SetSelectedSource(_rows.FirstOrDefault()?.Id);
        this.FindControl<TextBlock>("SourceCount")!.Text = Texts.T("sources.count", _rows.Count);
        this.FindControl<TextBlock>("SourcesEmpty")!.IsVisible = _rows.Count == 0;
        StackPanel cards = this.FindControl<StackPanel>("SourceCards")!;
        foreach (string removed in _sourceCards.Keys.Where(id => !_rows.Any(row => row.Id == id)).ToArray())
        {
            cards.Children.Remove(_sourceCards[removed].Control);
            _sourceCards.Remove(removed);
        }
        foreach (SourceRow row in _rows)
        {
            if (!_sourceCards.TryGetValue(row.Id, out SourceCard? card))
            {
                TextBlock name = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; name.Classes.Add("section-title");
                TextBlock address = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; address.Classes.Add("workspace-meta");
                TextBlock observation = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; observation.Classes.Add("workspace-help");
                StackPanel labels = new StackPanel { Spacing = 4 };
                labels.Children.Add(name); labels.Children.Add(address); labels.Children.Add(observation);
                Button select = new Button { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Padding = new Thickness(14) };
                string sourceId = row.Id;
                select.Click += (_, _) =>
                {
                    if (_selectedId != sourceId) SetActionError(string.Empty);
                    SetSelectedSource(sourceId);
                    _showDetails = true;
                    RenderDetails();
                    if (_narrow) FocusSourceControlAfterLayout(this.FindControl<Button>("SourceBackButton")!);
                };
                select.Content = labels;
                AttachSourceContextMenu(select, sourceId);
                card = new SourceCard(select, name, address, observation);
                _sourceCards.Add(row.Id, card); cards.Children.Add(select);
            }
            card.Name.Text = (row.Id == _selectedId ? "● " : "") + SourceDisplayName(row);
            card.Address.Text = row.Address;
            card.Observation.Text = row.Summary;
            card.Control.IsEnabled = !_busy;
        }
        RenderDetails();
    }

    private static string SourceDisplayName(SourceRow row) => row.Name +
        (row.MediaIntegrityPolicy?.Maturity == SourceMaturity.Experimental ? " · " + Texts.T("sources.experimental") : string.Empty);

    private void SetSelectedSource(string? sourceId)
    {
        if (_selectedId == sourceId) return;
        _cancelSubmitFocusRestore?.Invoke();
        _selectedId = sourceId;
        _sourceSelectionRevision++;
    }

    private SourceRow? Selected => _rows.FirstOrDefault(row => row.Id == _selectedId);
    private void RenderDetails()
    {
        RenderOperationFeedback();
        foreach (KeyValuePair<string, SourceCard> source in _sourceCards)
        {
            source.Value.Name.Text = (source.Key == _selectedId ? "● " : "")
                + (_rows.FirstOrDefault(row => row.Id == source.Key) is { } sourceRow ? SourceDisplayName(sourceRow) : source.Key);
            source.Value.Control.Classes.Set("selected", source.Key == _selectedId);
            source.Value.Control.IsEnabled = !_busy;
        }
        SourceRow? row = Selected;
        Border details = this.FindControl<Border>("SourceDetailsPanel")!;
        StackPanel list = this.FindControl<StackPanel>("SourcesListPane")!;
        Button back = this.FindControl<Button>("SourceBackButton")!;
        bool listHadFocus = list.IsKeyboardFocusWithin;
        bool detailsHadFocus = details.IsKeyboardFocusWithin;
        bool backHadFocus = back.IsKeyboardFocusWithin;
        details.IsVisible = row != null && (!_narrow || _showDetails);
        list.IsVisible = !_narrow || !_showDetails || row == null;
        back.IsVisible = _narrow;
        if (listHadFocus && !list.IsVisible)
            FocusSourceControlAfterLayout(back);
        else if (detailsHadFocus && !details.IsVisible && _selectedId is string id && _sourceCards.TryGetValue(id, out SourceCard? selectedCard))
            FocusSourceControlAfterLayout(selectedCard.Control);
        else if (backHadFocus && !back.IsVisible && details.IsVisible)
            FocusSourceControlAfterLayout(this.FindControl<Button>("SourceWebsiteButton")!);
        foreach (Control control in this.GetLogicalDescendants().OfType<Control>())
        {
            if (control.Tag is not string key || !control.Classes.Contains("localized")) continue;
            if (control is TextBlock text) text.Text = Texts.T(key);
            if (control is Button button && !button.Classes.Contains("command-icon")) button.Content = Texts.T(key);
        }
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceBackButton")!, "sources.back", Texts.T("nav.sources"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceRulesButton")!, "rules.edit-scope", Texts.T("sources.rules"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceDiagnosticsButton")!, "navigation.diagnostics", Texts.T("sources.diagnostics"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceDownloadsButton")!, "navigation.downloads", Texts.T("nav.downloads"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceAddButton")!, "watch.add-url", Texts.T("action.add"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceOpenWatchButton")!, "watch.show-in-downloads", Texts.T("diagnostics.openWatch"));
        if (row == null) return;
        this.FindControl<Button>("SourcePluginButton")!.Content = Texts.T("plugins.manageSource");
        this.FindControl<Button>("SourcePluginButton")!.IsEnabled = OpenPluginSettings != null;
        this.FindControl<TextBlock>("SourceHeading")!.Text = row.Name;
        RenderSourceMediaIntegrity(row);
        this.FindControl<TextBlock>("SourceIdentity")!.Text = Texts.T("sources.identity", row.Id, row.Address);
        Button website = this.FindControl<Button>("SourceWebsiteButton")!;
        CommandBindings.ApplyLabeled(website, "location.open-source", Texts.T("sources.website", row.Address));
        website.IsEnabled = !_busy;
        this.FindControl<TextBlock>("SourceHosts")!.Text = Texts.T("sources.hosts", row.Descriptor.SupportedHosts.Count > 0
            ? string.Join(", ", row.Descriptor.SupportedHosts) : Texts.T("sources.unspecified"));
        this.FindControl<TextBlock>("SourceVersion")!.Text = Texts.T("sources.version", row.Descriptor.AdapterVersion ?? Texts.T("sources.unspecified"));
        this.FindControl<Expander>("AdapterDetails")!.Header = Texts.T("sources.details");
        WrapPanel capabilities = this.FindControl<WrapPanel>("CapabilityCards")!;
        capabilities.Children.Clear();
        foreach ((ProviderCapabilities capability, string label) in new[]
        {
            (ProviderCapabilities.ThreadWatch, "sources.threads"), (ProviderCapabilities.BoardCatalog, "sources.catalog"),
            (ProviderCapabilities.PostCapture, "sources.capture"), (ProviderCapabilities.OriginalMedia, "sources.originals"),
            (ProviderCapabilities.Previews, "sources.previews")
        })
        {
            string state = row.Descriptor.Capabilities is not ProviderCapabilities supported ? "sources.unspecified"
                : supported.HasFlag(capability) ? "sources.supported" : "sources.unsupported";
            StackPanel labels = new StackPanel { Spacing = 4 };
            TextBlock capabilityLabel = new() { Text = Texts.T(label) }; capabilityLabel.Classes.Add("workspace-field-label"); labels.Children.Add(capabilityLabel);
            TextBlock value = new TextBlock { Text = Texts.T(state) }; value.Classes.Add("workspace-meta"); labels.Children.Add(value);
            Border card = new Border { Child = labels, Padding = new Thickness(0, 4, 12, 4), Width = 205 };
            capabilities.Children.Add(card);
        }
        this.FindControl<TextBlock>("SourceCaptureAvailability")!.Text = Texts.T(row.Descriptor.CaptureAvailable
            ? "sources.captureAvailable" : "sources.captureUnavailable");
        this.FindControl<TextBlock>("SourceWatchSummary")!.Text = row.Summary;
        this.FindControl<TextBox>("SourceUrlInput")!.PlaceholderText = Texts.T("downloads.urlPlaceholder");
        this.FindControl<TextBlock>("SourceUrlResult")!.Text = _hasUrlResult ? Texts.T("sources.urlResult") : string.Empty;
        DataGrid grid = this.FindControl<DataGrid>("SourceWatchesGrid")!;
        if (grid.ItemsSource is not IReadOnlyList<SourceWatchRow> displayed || !displayed.SequenceEqual(row.Watches))
        {
            DiagnosticTarget? selected = (grid.SelectedItem as SourceWatchRow)?.Target;
            grid.ItemsSource = row.Watches;
            grid.SelectedItem = row.Watches.FirstOrDefault(watch => watch.Target == selected);
        }
        grid.Columns[0].Header = Texts.T("diagnostics.resource");
        grid.Columns[1].Header = Texts.T("sources.work");
        grid.Columns[2].Header = Texts.T("sources.observation");
        foreach (string name in new[] { "SourceRulesButton", "SourceDiagnosticsButton", "SourceDownloadsButton", "SourceAddButton" })
            this.FindControl<Button>(name)!.IsEnabled = _available && !_busy;
        this.FindControl<TextBox>("SourceUrlInput")!.IsEnabled = _available && !_busy;
        RefreshWatchSelection();
    }

    private void RenderSourceMediaIntegrity(SourceRow row)
    {
        SourceMediaIntegrityPolicyView? policy = row.MediaIntegrityPolicy;
        TextBlock experimental = this.FindControl<TextBlock>("SourceExperimental")!;
        experimental.IsVisible = policy?.Maturity == SourceMaturity.Experimental;
        experimental.Text = Texts.T("sources.experimental");
        this.FindControl<StackPanel>("SourceMediaIntegrityPanel")!.IsVisible = policy != null
            && !ExperimentalSourceActivationPolicy.IsStrictExperimentalSource(policy.Snapshot.PluginOwnerId, policy.Snapshot.SourceId);
        RenderSourceMediaIntegrityError(policy);
        TextBlock retryHint = this.FindControl<TextBlock>("SourceMediaIntegrityRetryHint")!;
        retryHint.IsVisible = policy is { IsActive: true, IsGranted: true };
        retryHint.Text = Texts.T("sources.providerMismatchRetryHint");
        if (policy == null) return;
        this.FindControl<TextBlock>("SourceMediaIntegrityExplanation")!.Text = Texts.T("sources.providerMismatchExplanation");
        this.FindControl<TextBlock>("SourceAllowProviderMismatchLabel")!.Text = Texts.T("sources.allowProviderMismatch");
        CheckBox permission = this.FindControl<CheckBox>("SourceAllowProviderMismatch")!;
        permission.IsChecked = policy.IsGranted;
        permission.IsEnabled = _available && !_busy && SetSourceMediaIntegrityPermission != null && (policy.IsActive || policy.IsGranted);
        this.FindControl<TextBlock>("SourceMediaIntegrityScope")!.Text = Texts.T("sources.providerMismatchScope", row.Name);
        this.FindControl<TextBlock>("SourceMediaIntegrityStatus")!.Text = Texts.T(!policy.IsActive
            ? "sources.providerMismatchInactive" : policy.IsGranted ? "sources.providerMismatchAllowed" : "sources.providerMismatchStrict");
    }

    private async void OnSourceMediaPermissionClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_available || _busy || SetSourceMediaIntegrityPermission is not { } save ||
            Selected?.MediaIntegrityPolicy is not { } policy) return;
        // Capture the authority, revision and intent before any await or selection refresh.
        string ownerId = policy.Snapshot.PluginOwnerId;
        string sourceId = policy.Snapshot.SourceId;
        long revision = policy.Snapshot.Revision;
        bool allow = this.FindControl<CheckBox>("SourceAllowProviderMismatch")!.IsChecked == true;
        _sourceMediaIntegritySaveError = null;
        SourceMediaIntegritySaveError? saveError = null;
        _busy = true; BusyChanged?.Invoke(this, EventArgs.Empty); RenderDetails();
        try { await save(ownerId, sourceId, allow, revision); }
        catch (Exception error)
        {
            if (!_ended && Selected?.Id == sourceId && Selected.MediaIntegrityPolicy?.Snapshot.PluginOwnerId == ownerId)
                _sourceMediaIntegritySaveError = saveError = new(ownerId, sourceId, error.Message);
        }
        finally
        {
            _busy = false;
            if (!_ended)
            {
                if (GetSourceMediaIntegrityPolicies?.Invoke() is { } current)
                    _rows = _rows.Select(row => row with { MediaIntegrityPolicy = SourcesSnapshot.FindPolicy(row.Id, current) }).ToArray();
                BusyChanged?.Invoke(this, EventArgs.Empty); RenderDetails();
                if (saveError != null) RevealSourceMediaIntegrityErrorAfterLayout(saveError);
            }
        }
    }

    private void RenderSourceMediaIntegrityError(SourceMediaIntegrityPolicyView? policy)
    {
        SourceMediaIntegritySaveError? error = _sourceMediaIntegritySaveError;
        bool belongsToSelection = error != null && policy is { } selected
            && selected.Snapshot.PluginOwnerId == error.PluginOwnerId && selected.Snapshot.SourceId == error.SourceId;
        if (!belongsToSelection) _sourceMediaIntegritySaveError = null;
        this.FindControl<Ursa.Controls.Banner>("SourceMediaIntegrityErrorBanner")!.IsVisible = belongsToSelection;
        this.FindControl<TextBlock>("SourceMediaIntegrityError")!.Text = belongsToSelection
            ? Texts.T("sources.providerMismatchSaveFailed", error!.Message) : string.Empty;
    }

    private void RevealSourceMediaIntegrityErrorAfterLayout(SourceMediaIntegritySaveError expected)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || !IsEffectivelyVisible || TopLevel.GetTopLevel(this) == null ||
                !ReferenceEquals(_sourceMediaIntegritySaveError, expected) || Selected?.MediaIntegrityPolicy is not { } policy ||
                policy.Snapshot.PluginOwnerId != expected.PluginOwnerId || policy.Snapshot.SourceId != expected.SourceId) return;
            Ursa.Controls.Banner banner = this.FindControl<Ursa.Controls.Banner>("SourceMediaIntegrityErrorBanner")!;
            if (banner.IsEffectivelyVisible) banner.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void FocusSourceControlAfterLayout(Control target)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || !IsEffectivelyVisible || !target.IsEffectivelyVisible || !target.IsEffectivelyEnabled) return;
            target.BringIntoView();
            target.Focus();
        }, DispatcherPriority.Loaded);
    }

    private SourceWatchRow? SelectedWatch => this.FindControl<DataGrid>("SourceWatchesGrid")!.SelectedItem as SourceWatchRow;
    private void OnWatchSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshWatchSelection();
    private void RefreshWatchSelection()
    {
        this.FindControl<Button>("SourceOpenWatchButton")!.IsEnabled = _available && !_busy && SelectedWatch != null;
        this.FindControl<TextBox>("SourceWatchDetails")!.Text = SelectedWatch is SourceWatchRow watch
            ? watch.Resource + Environment.NewLine + watch.Work + Environment.NewLine + watch.Observation : string.Empty;
    }
    private void OnPluginClick(object? sender, RoutedEventArgs e) { if (Selected is SourceRow row) OpenPluginSettings?.Invoke(row.Id); }
    internal void SelectSource(string sourceId)
    {
        if (!_rows.Any(row => row.Id == sourceId)) return;
        SetSelectedSource(sourceId);
        _showDetails = true;
        RenderDetails();
        if (_narrow) FocusSourceControlAfterLayout(this.FindControl<Button>("SourceBackButton")!);
    }
    private void OnRulesClick(object? sender, RoutedEventArgs e) { if (_available && !_busy && Selected is SourceRow row) _editRules?.Invoke(row.Id); }
    private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // The detail grid needs 520 DIP after list, gutters and panel padding.
        bool narrow = Bounds.Width - 40 - 280 - 14 - 32 < 520;
        if (_narrow == narrow) return;
        _narrow = narrow;
        Grid split = this.FindControl<Grid>("SourcesSplit")!;
        // Preserve the track collection while SizeChanged runs inside layout.
        split.ColumnDefinitions[0].Width = new GridLength(narrow ? 0 : 280);
        split.ColumnSpacing = narrow ? 0 : 14;
        Grid.SetColumn(this.FindControl<StackPanel>("SourcesListPane")!, narrow ? 1 : 0);
        RenderDetails();
    }
    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        _showDetails = false;
        RenderDetails();
        if (_selectedId is string id && _sourceCards.TryGetValue(id, out SourceCard? card)) card.Control.Focus();
    }
    private async void OnWebsiteClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || _busy || Selected is not SourceRow row) return;
        try { await ExternalLauncher.OpenAsync(row.Address); }
        catch (Exception exception) { if (!_ended && _selectedId == row.Id) SetActionError(exception.Message); }
    }
    private void OnDiagnosticsClick(object? sender, RoutedEventArgs e) { if (_available && !_busy) _openDiagnostics?.Invoke(); }
    private void OnDownloadsClick(object? sender, RoutedEventArgs e) { if (_available && !_busy) _openDownloads?.Invoke(); }
    private void OnOpenWatchClick(object? sender, RoutedEventArgs e)
    {
        if (!_available || _busy || SelectedWatch == null) return;
        if (_openWatch?.Invoke(SelectedWatch.Target) != true)
            SetActionError(Texts.T("diagnostics.removed"));
    }
    private async void OnAddClick(object? sender, RoutedEventArgs e) => await SubmitAsync();
    private async void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SubmitAsync();
    }
    private async Task SubmitAsync()
    {
        if (_ended || !_available || _busy || _submit == null) return;
        _cancelSubmitFocusRestore?.Invoke();
        TextBox editor = this.FindControl<TextBox>("SourceUrlInput")!;
        string input = editor.Text ?? string.Empty;
        Button submitButton = this.FindControl<Button>("SourceAddButton")!;
        // A real Add click may focus the button before its busy disable.
        TopLevel? owner = editor.IsKeyboardFocusWithin || submitButton.IsKeyboardFocusWithin
            ? TopLevel.GetTopLevel(editor) : null;
        long selectionRevision = _sourceSelectionRevision;
        bool accepted = false;
        bool newerInput = false;
        bool observing = owner != null;
        Window? ownerWindow = owner as Window;
        Window? admissionModal = null;
        bool sawAdmissionModal = false;
        bool modalOwnerDeactivated = false;
        bool awaitingAdmissionOwnerActivation = false;
        TaskCompletionSource? ownerActivation = null;
        List<Action> stopModalObservers = new();
        void StopObserving()
        {
            if (!observing) return;
            observing = false;
            owner?.RemoveHandler(GotFocusEvent, OnNewFocus);
            owner?.RemoveHandler(PointerPressedEvent, OnNewPointer);
            owner?.RemoveHandler(KeyDownEvent, OnNewKey);
            if (ownerWindow != null)
            {
                ownerWindow.Deactivated -= OnOwnerDeactivated;
                ownerWindow.Activated -= OnOwnerActivated;
                ownerWindow.Closing -= OnOwnerClosing;
                ownerWindow.PropertyChanged -= OnOwnerPropertyChanged;
            }
            editor.PropertyChanged -= OnEditorPropertyChanged;
            foreach (Action stop in stopModalObservers) stop();
            stopModalObservers.Clear();
            if (_cancelSubmitFocusRestore == CancelFocusRestore) _cancelSubmitFocusRestore = null;
        }
        void CancelFocusRestore()
        {
            newerInput = true;
            ownerActivation?.TrySetResult();
            StopObserving();
        }
        void OnOwnerActivated(object? sender, EventArgs e)
        {
            // Close can raise a managed owner activation before Modal.Closed,
            // followed by a native deactivation/activation bounce. Only the
            // first activation after the exact normal Closed ends its return.
            if (admissionModal == null) awaitingAdmissionOwnerActivation = false;
            ownerActivation?.TrySetResult();
        }
        void OnOwnerDeactivated(object? sender, EventArgs e)
        {
            if (admissionModal != null) modalOwnerDeactivated = true;
            else if (!awaitingAdmissionOwnerActivation) CancelFocusRestore();
        }
        void OnOwnerClosing(object? sender, WindowClosingEventArgs e) => CancelFocusRestore();
        void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == IsVisibleProperty && ownerWindow is { IsVisible: false }
                || e.Property == Window.WindowStateProperty && ownerWindow is { WindowState: WindowState.Minimized })
                CancelFocusRestore();
        }
        void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBox.TextProperty) CancelFocusRestore();
        }
        void ObserveAdmissionModal(Window modal)
        {
            // This callback belongs to this exact submit operation. Cancellation
            // must never attach new observers or revive a stale focus claim.
            if (!observing) return;
            sawAdmissionModal = true;
            admissionModal = modal;
            modalOwnerDeactivated = false;
            awaitingAdmissionOwnerActivation = false;
            WindowClosingEventArgs? closing = null;
            void OnOpened(object? sender, EventArgs e)
            {
                if (!ReferenceEquals(modal.Owner, ownerWindow)) CancelFocusRestore();
            }
            void OnClosing(object? sender, WindowClosingEventArgs e) => closing = e;
            void OnDeactivated(object? sender, EventArgs e)
            {
                // Keep the original args so a later handler's canceled Closing
                // cannot make an external activation look like a normal close.
                if (closing is not { Cancel: false }) CancelFocusRestore();
            }
            void OnClosed(object? sender, EventArgs e)
            {
                if (!observing || !ReferenceEquals(admissionModal, modal)) return;
                admissionModal = null;
                // No timer or blanket deactivation exemption: this one normal
                // owned-modal return lasts only until its next owner activation.
                awaitingAdmissionOwnerActivation = closing is { Cancel: false } && modalOwnerDeactivated;
            }
            modal.Opened += OnOpened;
            modal.Closing += OnClosing;
            modal.Deactivated += OnDeactivated;
            modal.Closed += OnClosed;
            stopModalObservers.Add(() =>
            {
                modal.Opened -= OnOpened;
                modal.Closing -= OnClosing;
                modal.Deactivated -= OnDeactivated;
                modal.Closed -= OnClosed;
            });
        }
        void OnNewFocus(object? sender, FocusChangedEventArgs e) => CancelFocusRestore();
        void OnNewPointer(object? sender, PointerPressedEventArgs e) => CancelFocusRestore();
        void OnNewKey(object? sender, KeyEventArgs e) => CancelFocusRestore();
        // Disabling the busy editor clears native focus. Remember later choices,
        // including focus that subsequently moves to null, until completion.
        owner?.AddHandler(GotFocusEvent, OnNewFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        owner?.AddHandler(PointerPressedEvent, OnNewPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner?.AddHandler(KeyDownEvent, OnNewKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (ownerWindow != null)
        {
            ownerWindow.Deactivated += OnOwnerDeactivated;
            ownerWindow.Activated += OnOwnerActivated;
            ownerWindow.Closing += OnOwnerClosing;
            ownerWindow.PropertyChanged += OnOwnerPropertyChanged;
        }
        if (observing)
        {
            editor.PropertyChanged += OnEditorPropertyChanged;
            _cancelSubmitFocusRestore = CancelFocusRestore;
        }
        try
        {
            SetActionError(null);
            _busy = true; BusyChanged?.Invoke(this, EventArgs.Empty); RenderDetails();
            try
            {
                accepted = await _submit(input, ObserveAdmissionModal);
                if (_ended) return;
                _hasUrlResult = true;
                if (accepted && editor.Text == input) editor.Text = string.Empty;
            }
            catch (Exception exception)
            {
                if (!_ended) SetActionError(exception.Message);
            }
            finally
            {
                _busy = false;
                if (!_ended) { BusyChanged?.Invoke(this, EventArgs.Empty); RenderDetails(); }
            }
            if (!accepted && owner != null)
            {
                bool RestoreInputOrAwaitOwnerActivation()
                {
                    if (_ended || _busy || !_available || newerInput || selectionRevision != _sourceSelectionRevision
                        || editor.Text != input || !IsEffectivelyVisible || !editor.IsEffectivelyVisible
                        || !editor.IsEffectivelyEnabled || !ReferenceEquals(TopLevel.GetTopLevel(editor), owner)
                        || owner is Window { IsVisible: false } or Window { IsEnabled: false }) return false;
                    if (owner is Window { IsActive: false })
                    {
                        if (!sawAdmissionModal || !awaitingAdmissionOwnerActivation) return false;
                        // Recheck at Loaded: the owner may have become inactive
                        // since submit completion. Preserve only this exact return
                        // until its first activation or permanent cancellation.
                        ownerActivation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        return true;
                    }
                    var focus = owner.FocusManager?.GetFocusedElement();
                    if (focus != null && !ReferenceEquals(focus, editor)) return false;
                    editor.BringIntoView();
                    editor.Focus();
                    return false;
                }
                bool awaitingOwner = await Dispatcher.UIThread.InvokeAsync(RestoreInputOrAwaitOwnerActivation, DispatcherPriority.Loaded);
                if (awaitingOwner)
                {
                    await ownerActivation!.Task;
                    // The event clears the one-shot return or cancellation sets
                    // newerInput. This callback cannot start another owner wait.
                    await Dispatcher.UIThread.InvokeAsync(() => { RestoreInputOrAwaitOwnerActivation(); }, DispatcherPriority.Loaded);
                }
            }
        }
        finally { StopObserving(); }
    }
    internal void EndLifetime()
    {
        _ended = true; _available = false; BusyChanged = null;
        _cancelSubmitFocusRestore?.Invoke();
    }
}
