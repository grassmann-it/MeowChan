using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan;

public partial class SourcesPage : UserControl
{
    private readonly Func<DiagnosticTarget, bool>? _openWatch;
    private readonly Action<string>? _editRules;
    private readonly Action<string>? _openDiagnostics;
    private readonly Action? _addToDownloads;
    private readonly Action? _openDownloads;
    private IReadOnlyList<SourceListRow> _rows = Array.Empty<SourceListRow>();
    private sealed record SourceCard(SelectionListItem Control, TextBlock Name, WrapPanel Chips, TextBlock Address, TextBlock Observation)
    {
        internal string ChipSignature { get; set; } = string.Empty;
    }
    private sealed record SourceMediaIntegritySaveError(string PluginOwnerId, string SourceId, string Message);
    private SourceMediaIntegritySaveError? _sourceMediaIntegritySaveError;
    private readonly Dictionary<string, SourceCard> _sourceCards = new Dictionary<string, SourceCard>(StringComparer.Ordinal);
    private string? _selectedId;
    private bool _available;
    private bool _busy;
    private bool _ended;
    private bool _showDetails;
    private bool _narrow;
    internal Func<IReadOnlyList<SourceMediaIntegrityPolicyView>>? GetSourceMediaIntegrityPolicies { get; set; }
    internal Func<string, string, bool, long, Task>? SetSourceMediaIntegrityPermission { get; set; }
    /// <summary>The "Sources page: watch rows per source" setting; read on every render so a saved change shows on the next visit.</summary>
    internal Func<int>? GetWatchPreviewRows { get; set; }
    internal bool IsBusy => _busy || SourcePluginManagement.IsBusy;
    internal event EventHandler? BusyChanged;

    public SourcesPage()
    {
        InitializeComponent(); InitializeContextMenus(); InitializeMergedSources();
    }

    /// <param name="openDiagnostics">Opens Status › Diagnostics with the source filter preset to the canonical id of the source.</param>
    /// <param name="addToDownloads">Goes to Downloads and focuses its URL field; the Sources page itself admits nothing.</param>
    /// <param name="openDownloads">Goes to Downloads for the watches beyond the preview. Downloads has no per-source filter, so none is applied.</param>
    internal SourcesPage(Func<DiagnosticTarget, bool> openWatch, Action<string> editRules,
        Action<string> openDiagnostics, Action addToDownloads, Action openDownloads) : this()
    {
        _openWatch = openWatch;
        _editRules = editRules;
        _openDiagnostics = openDiagnostics;
        _addToDownloads = addToDownloads;
        _openDownloads = openDownloads;
    }

    internal void Refresh(IReadOnlyList<ProviderDescriptor> providers, DesktopSessionView view, bool available)
    {
        if (_ended) return;
        _available = available;
        _registeredRows = SourcesSnapshot.From(providers, view, GetSourceMediaIntegrityPolicies?.Invoke());
        RefreshSourceRows();
    }

    private void RefreshSourceRows()
    {
        if (_ended) return;
        _rows = SourceListRows.Merge(_registeredRows, SourcePluginManagement.CatalogEntries);
        if (!_rows.Any(row => row.Id == _selectedId)) SetSelectedSource(_rows.FirstOrDefault()?.Id);
        SourcePluginManagement.SetSourceCount(_rows.Count, _rows.Count(row => row.IsActive));
        this.FindControl<TextBlock>("SourcesEmpty")!.IsVisible = _rows.Count == 0;
        this.FindControl<Desktop.Controls.WorkspaceEmptyState>("SourcesEmptyState")!.IsVisible = _rows.Count == 0;
        SelectionList cards = this.FindControl<SelectionList>("SourceCards")!;
        _projectingSources = true;
        try
        {
            foreach (string removed in _sourceCards.Keys.Where(id => !_rows.Any(row => row.Id == id)).ToArray())
            {
                cards.Items.Remove(_sourceCards[removed].Control);
                _sourceCards.Remove(removed);
            }
            foreach (SourceListRow row in _rows)
            {
                if (!_sourceCards.TryGetValue(row.Id, out SourceCard? card))
                {
                    TextBlock name = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; name.Classes.Add("section-title");
                    TextBlock address = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; address.Classes.Add("workspace-meta");
                    TextBlock observation = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; observation.Classes.Add("workspace-help");
                    WrapPanel chips = new WrapPanel { ItemSpacing = 8, LineSpacing = 4, IsVisible = false };
                    StackPanel labels = new StackPanel { Spacing = 4 };
                    labels.Children.Add(name); labels.Children.Add(chips); labels.Children.Add(address); labels.Children.Add(observation);
                    SelectionListItem select = new PluginSelectionItem { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                    select.Classes.Add("workspace-list-card");
                    string sourceId = row.Id;
                    select.PointerReleased += (_, args) =>
                    {
                        if (args.InitialPressMouseButton is not (MouseButton.Left or MouseButton.None)) return;
                        if (_selectedId != sourceId) SetActionError(string.Empty);
                        SetSelectedSource(sourceId);
                        _showDetails = true;
                        RenderDetails();
                        FocusSourceControlAfterLayout(this.FindControl<TextBlock>("SourceHeading")!);
                    };
                    select.Content = labels;
                    AttachSourceContextMenu(select, sourceId);
                    card = new SourceCard(select, name, chips, address, observation);
                    _sourceCards.Add(row.Id, card); cards.Items.Add(select);
                }
                card.Name.Text = row.Name;
                RenderChips(card, row);
                card.Address.Text = row.Address;
                card.Observation.Text = row.Summary;
                card.Control.IsEnabled = !IsBusy;
                AutomationProperties.SetName(card.Control, row.Name + ". " + SourceWording.DescribeMeta(row.Badges) + ". " + row.Summary);
            }
            // Active first, then maturity/name; a change of maturity or name moves the card, it never rebuilds it.
            for (int index = 0; index < _rows.Count; index++)
            {
                SelectionListItem control = _sourceCards[_rows[index].Id].Control;
                int current = cards.Items.IndexOf(control);
                if (current != index) { cards.Items.Remove(control); cards.Items.Insert(index, control); }
            }
            cards.SelectedItem = _selectedId != null ? _sourceCards.GetValueOrDefault(_selectedId)?.Control : null;
        }
        finally { _projectingSources = false; }
        RenderDetails();
    }

    /// <summary>A chip only where the source deviates from the norm; a source that is fine shows no chip at all.</summary>
    private void RenderChips(SourceCard card, SourceListRow row)
    {
        SourceBadge[] deviating = row.Badges.Where(badge => badge.Deviates).ToArray();
        bool problem = row.Plugin is { } plugin && SourcePluginManagement.SourcePluginNeedsAttention(plugin);
        string signature = (problem ? "problem|" : string.Empty) + string.Join("|", deviating.Select(badge => (badge.Warning ? "!" : "") + badge.ChipText));
        card.Chips.IsVisible = deviating.Length > 0 || problem;
        if (signature == card.ChipSignature) return;
        card.ChipSignature = signature;
        card.Chips.Children.Clear();
        if (problem)
        {
            Border attention = Desktop.Controls.WorkspaceChips.CreateBadge("SourcePluginProblem", Texts.T("plugins.problem"), compact: true);
            attention.Classes.Add("chip-warning"); card.Chips.Children.Add(attention);
        }
        foreach (SourceBadge badge in deviating)
        {
            Border chip = Desktop.Controls.WorkspaceChips.CreateBadge("SourceChip", badge.ChipText, compact: true);
            if (badge.Warning) chip.Classes.Add("chip-warning");
            card.Chips.Children.Add(chip);
        }
    }

    private void SetSelectedSource(string? sourceId)
    {
        if (_selectedId == sourceId) return;
        _selectedId = sourceId;
        if (!_projectingSources && sourceId != null && _sourceCards.TryGetValue(sourceId, out var card))
            this.FindControl<SelectionList>("SourceCards")!.SelectedItem = card.Control;
    }

    private SourceListRow? Selected => _rows.FirstOrDefault(row => row.Id == _selectedId);
    private void RenderDetails()
    {
        RenderOperationFeedback();
        foreach (KeyValuePair<string, SourceCard> source in _sourceCards)
        {
            source.Value.Control.Classes.Set("selected", source.Key == _selectedId);
            source.Value.Control.IsEnabled = !IsBusy;
        }
        SourceListRow? row = Selected;
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
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceBackButton")!, "sources.back", CommandCatalog.Label("sources.back"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceRulesButton")!, "rules.edit-scope", Texts.T("sources.rules"));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceDiagnosticsButton")!, "navigation.diagnostics", Texts.T("sources.diagnostics"));
        Button addToDownloads = this.FindControl<Button>("SourceAddToDownloadsButton")!;
        CommandBindings.ApplyLabeled(addToDownloads, "input.focus-url", Texts.T("sources.addInDownloads"));
        // The explanation belongs to the hover, not to a sentence on the page; the shortcut stays visible.
        ToolTip.SetTip(addToDownloads, Texts.T("sources.addInDownloadsHint")
            + (CommandCatalog.ShortcutDisplay("input.focus-url") is string shortcut ? " (" + shortcut + ")" : string.Empty));
        CommandBindings.ApplyLabeled(this.FindControl<Button>("SourceOpenWatchButton")!, "watch.show-in-downloads", Texts.T("diagnostics.openWatch"));
        Button emptyAdd = this.FindControl<Button>("SourcesEmptyAdd")!;
        emptyAdd.Content = PluginsPage.IconAndLabel("plus", Texts.T("plugins.add")); AutomationProperties.SetName(emptyAdd, Texts.T("plugins.add")); ToolTip.SetTip(emptyAdd, Texts.T("plugins.add.help"));
        if (row == null) return;
        RenderSourcePluginBlock();
        this.FindControl<TextBlock>("SourceHeading")!.Text = row.Name;
        RenderSourceMediaIntegrity(row);
        this.FindControl<TextBlock>("SourceIdentity")!.Text = Texts.T("sources.identity", row.Id, row.Address);
        Button website = this.FindControl<Button>("SourceWebsiteButton")!;
        // The address is in the card and under "Technical details"; the label stays one short verb phrase, the address is the hover and the help text.
        CommandBindings.ApplyLabeled(website, "location.open-source", Texts.T("sources.website"));
        ToolTip.SetTip(website, row.Address + (CommandCatalog.ShortcutDisplay("location.open-source") is string websiteShortcut ? " · " + websiteShortcut : string.Empty));
        AutomationProperties.SetHelpText(website, row.Address);
        website.IsEnabled = !IsBusy;
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
        ListBox watchList = this.FindControl<ListBox>("SourceWatchesList")!;
        int previewRows = Math.Clamp(GetWatchPreviewRows?.Invoke() ?? Config.SourcesWatchPreviewRowsDefault,
            Config.SourcesWatchPreviewRowsMinimum, Config.SourcesWatchPreviewRowsMaximum);
        IReadOnlyList<SourceWatchRow> shownWatches = row.Watches.Count <= previewRows ? row.Watches : row.Watches.Take(previewRows).ToArray();
        if (watchList.ItemsSource is not IReadOnlyList<SourceWatchRow> displayed || !displayed.SequenceEqual(shownWatches))
        {
            DiagnosticTarget? selected = (watchList.SelectedItem as SourceWatchRow)?.Target;
            watchList.ItemsSource = shownWatches;
            watchList.SelectedItem = shownWatches.FirstOrDefault(watch => watch.Target == selected);
        }
        watchList.IsVisible = shownWatches.Count > 0;
        Button allWatches = this.FindControl<Button>("SourceAllWatchesButton")!;
        allWatches.IsVisible = row.Watches.Count > shownWatches.Count;
        CommandBindings.ApplyLabeled(allWatches, "navigation.downloads", Texts.T("sources.showAll", row.Watches.Count));
        allWatches.IsEnabled = _available && !IsBusy && _openDownloads != null;
        foreach (string name in new[] { "SourceRulesButton", "SourceDiagnosticsButton", "SourceAddToDownloadsButton" })
            this.FindControl<Button>(name)!.IsEnabled = _available && !IsBusy;
        RefreshWatchSelection();
        RefreshWorkViewPresentation();
    }

    private void RenderSourceMediaIntegrity(SourceListRow row)
    {
        SourceMediaIntegrityPolicyView? policy = row.MediaIntegrityPolicy;
        TextBlock classificationText = this.FindControl<TextBlock>("SourceExperimental")!;
        // The meta line is always complete; the sentence below explains "Experimental" and is there only for it.
        TextBlock metaText = this.FindControl<TextBlock>("SourceMeta")!;
        metaText.Text = SourceWording.DescribeMeta(row.Badges);
        AutomationProperties.SetName(metaText, metaText.Text);
        bool experimental = row.Classification?.Maturity == SourceWordingMaturity.Experimental;
        string classification = experimental ? Texts.T("sources.experimentalNote") : string.Empty;
        classificationText.IsVisible = experimental;
        classificationText.Text = classification;
        AutomationProperties.SetName(classificationText, classification);
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
        permission.IsEnabled = _available && !IsBusy && SetSourceMediaIntegrityPermission != null && (policy.IsActive || policy.IsGranted);
        this.FindControl<TextBlock>("SourceMediaIntegrityScope")!.Text = Texts.T("sources.providerMismatchScope", row.Name);
        this.FindControl<TextBlock>("SourceMediaIntegrityStatus")!.Text = Texts.T(!policy.IsActive
            ? "sources.providerMismatchInactive" : policy.IsGranted ? "sources.providerMismatchAllowed" : "sources.providerMismatchStrict");
    }

    private async void OnSourceMediaPermissionClick(object? sender, RoutedEventArgs e)
    {
        if (_ended || !_available || IsBusy || SetSourceMediaIntegrityPermission is not { } save ||
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
        this.FindControl<MeowChan.Desktop.Controls.WorkspaceBanner>("SourceMediaIntegrityErrorBanner")!.IsVisible = belongsToSelection;
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
            MeowChan.Desktop.Controls.WorkspaceBanner banner = this.FindControl<MeowChan.Desktop.Controls.WorkspaceBanner>("SourceMediaIntegrityErrorBanner")!;
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

    private SourceWatchRow? SelectedWatch => this.FindControl<ListBox>("SourceWatchesList")!.SelectedItem as SourceWatchRow;
    private void OnWatchSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshWatchSelection();
    private void RefreshWatchSelection() =>
        this.FindControl<Button>("SourceOpenWatchButton")!.IsEnabled = _available && !IsBusy && SelectedWatch != null;
    // Double click and Enter on a selected row are the same action as the button below the list.
    private void OnWatchDoubleTapped(object? sender, TappedEventArgs e) { if (SelectedWatch != null) OnOpenWatchClick(sender, e); }
    private void OnWatchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || SelectedWatch == null) return;
        e.Handled = true;
        OnOpenWatchClick(sender, e);
    }
    private async void OnSourcesEmptyAddClick(object? sender, RoutedEventArgs e) { if (!IsBusy) await SourcePluginManagement.AddPluginAsync(); }
    private void OnAllWatchesClick(object? sender, RoutedEventArgs e) { if (_available && !IsBusy) _openDownloads?.Invoke(); }
    private void OnPluginClick(object? sender, RoutedEventArgs e)
    {
        if (!IsBusy && Selected?.Plugin is { } plugin)
        {
            string key = SourcePluginManagement.Selected is { } selected && selected.PluginId == plugin.PluginId ? selected.Key : plugin.Key;
            SourcePluginManagement.SelectSourcePlugin(key, details: true);
        }
    }
    internal void SelectSource(string sourceId)
    {
        if (IsBusy || SourcePluginManagement.IsDiscardConfirmationOpen || !_rows.Any(row => row.Id == sourceId)) return;
        SourcePluginManagement.SetFilter(SourcesPluginsFilter.Sources);
        SetSelectedSource(sourceId);
        if (_sourceCards.TryGetValue(sourceId, out var card)) this.FindControl<SelectionList>("SourceCards")!.SelectedItem = card.Control;
        _showDetails = true;
        RenderDetails();
        FocusSourceControlAfterLayout(this.FindControl<TextBlock>("SourceHeading")!);
    }
    private void OnRulesClick(object? sender, RoutedEventArgs e) { if (_available && !IsBusy && Selected is SourceListRow row) _editRules?.Invoke(row.Id); }
    private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // The detail grid needs 520 DIP after list, gutters and panel padding.
        bool narrow = Bounds.Width - 40 - 280 - 14 - 32 < 520;
        if (_narrow == narrow) return;
        _narrow = narrow;
        Grid split = this.FindControl<Grid>("SourcesSplit")!;
        // Preserve the track collection while SizeChanged runs inside layout.
        split.ColumnDefinitions[0].Width = new GridLength(narrow ? 0 : 280);
        split.ColumnSpacing = narrow ? 0 : 16;
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
        if (_ended || IsBusy || Selected is not SourceListRow row) return;
        try { await ExternalLauncher.OpenAsync(row.Address); }
        catch (Exception exception) { if (!_ended && _selectedId == row.Id) SetActionError(exception.Message); }
    }
    private void OnDiagnosticsClick(object? sender, RoutedEventArgs e) { if (_available && !IsBusy && Selected is SourceListRow row) _openDiagnostics?.Invoke(row.Id); }
    private void OnOpenWatchClick(object? sender, RoutedEventArgs e)
    {
        if (!_available || IsBusy || SelectedWatch == null) return;
        if (_openWatch?.Invoke(SelectedWatch.Target) != true)
            SetActionError(Texts.T("diagnostics.removed"));
    }
    private void OnAddToDownloadsClick(object? sender, RoutedEventArgs e) { if (_available && !IsBusy) _addToDownloads?.Invoke(); }
    internal void EndLifetime()
    {
        _ended = true; _available = false; BusyChanged = null; SourcePluginManagement.EndLifetime();
    }
}
