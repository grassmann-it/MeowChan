using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using MeowChan.Desktop.Commands;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ursa.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan;

public partial class PluginsPage : UserControl
{
    private PluginManagementServices? _services;
    private PluginCatalogSnapshot? _catalog;
    private readonly Dictionary<string, PluginConfigurationDraft> _drafts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SelectionListItem> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginSettingsEditor> _editors = new(StringComparer.Ordinal);
    private sealed record PluginListContent(TextBlock Name, TextBlock Description, TextBlock Origin, TextBlock State, TextBlock Identity);
    private readonly Dictionary<string, PluginListContent> _cardContent = new(StringComparer.Ordinal);
    private bool _projectingSelection;
    private readonly Dictionary<string, string?> _actionReasons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DescriptionsItem Item, SelectableTextBlock Value)> _identityRows = new(StringComparer.Ordinal);
    private string? _selectedKey;
    private string? _renderedKey;
    private bool _narrow;
    private bool _showDetails;
    private bool _ended;
    private bool _refreshing;
    private long _refreshGeneration;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _statusReadPending;
    private string? _renderedGrantsKey;
    private IReadOnlyList<PluginGrantSnapshot> _displayedGrants = Array.Empty<PluginGrantSnapshot>();
    private bool _busy;
    private string? _operationKey;
    private long _operationGeneration;
    private CancellationTokenSource? _operation;
    private readonly CancellationTokenSource _lifetime = new();
    internal Func<PluginEditorOptions>? GetEditorOptions { get; set; }
    internal Func<PluginEditorOptions, Task>? SaveEditorOptionsAsync { get; set; }
    internal Action<string>? OpenSourceWatches { get; set; }
    internal event EventHandler? BusyChanged;
    internal bool IsBusy => _busy;
    internal PluginCatalogEntry? Selected => _catalog?.Entries.FirstOrDefault(entry => entry.Key == _selectedKey);
    internal PluginConfigurationDraft? CurrentDraft => _selectedKey != null ? _drafts.GetValueOrDefault(_selectedKey) : null;

    public PluginsPage()
    {
        InitializeComponent();
        InitializePluginSections();
        InitializePluginDetails();
        InitializePluginActions();
        KeyDown += (_, args) =>
        {
            if (args.Handled || args.Key != Key.Escape) return;
            if (PluginActionMenu?.IsOpen == true) { ClosePluginActionMenu(restoreFocus: true); args.Handled = true; return; }
            if (_busy) { OnCancelClick(this, new RoutedEventArgs()); args.Handled = true; }
            else if (_narrow && _showDetails) { OnBackClick(this, new RoutedEventArgs()); args.Handled = true; }
        };
        C<SelectionList>("PluginCards").AddHandler(KeyDownEvent, OnPluginListKeyDown, RoutingStrategies.Tunnel);
        Texts.Changed += OnLanguageChanged;
        _statusTimer.Tick += OnStatusTick;
        Localize();
    }
    internal void Attach(PluginManagementServices services) { _services = services; C<TextBlock>("PluginDirectory").Text = services.PluginDirectory; }
    internal async Task ActivateAsync(string? sourceId = null)
    {
        if (_ended) return;
        _renderedGrantsKey = null;
        _statusTimer.Start();
        await RefreshAsync();
        if (_ended) return;
        if (sourceId != null)
        {
            C<ComboBox>("PluginCategory").SelectedIndex = 1;
            PluginCatalogEntry? entry = _catalog?.Entries.FirstOrDefault(entry => entry.Description?.Sources.Any(source => source.SourceId == sourceId) == true);
            if (entry != null) Select(entry.Key);
        }
        else C<ComboBox>("PluginCategory").Focus();
    }
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;
    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!_ended) { Localize(); RenderList(); RenderDetails(); } });
    private void Localize()
    {
        foreach ((string name, string key) in new[]
        {
            ("PluginsTitle","nav.plugins"),("PluginsSubtitle","plugins.subtitle"),("PluginsEmpty","plugins.empty"),
            ("PluginTestHint","plugins.testHint"),("PluginEditorHint","plugins.editorHint"),
            ("PluginLocalStatusHint","plugins.localStatusHint"),("PluginChooseHint","plugins.choosePlugin"),
            ("PluginSourceToolsHeading","plugins.sourceTools")
        }) C<TextBlock>(name).Text = name == "PluginEditorHint" ? Texts.T(key, string.Empty) : Texts.T(key);
        foreach ((string name, string key) in new[]
        {
            ("PluginsRefresh","plugins.refresh"),("PluginsFolder","plugins.folder"),("PluginBack","plugins.back"),
            ("PluginSelectCandidate","plugins.selectCandidate"),("PluginRollback","plugins.rollback"),("PluginWatches","plugins.watches"),
            ("PluginSave","plugins.save"),("PluginDiscard","plugins.discard"),("PluginEdit","plugins.edit"),
            ("PluginDefaultEditor","plugins.defaultEditor"),("PluginBuild","plugins.build"),("PluginCancel","plugins.cancel"),
            ("PluginProbe","plugins.probe"),("PluginEditorSave","plugins.saveEditor"),
            ("PluginCopyTechnicalDetails","plugins.copyTechnicalDetails")
        })
        {
            Button button = C<Button>(name);
            if (key is "plugins.refresh" or "plugins.folder" or "plugins.back" or "plugins.save" or "plugins.discard" or "plugins.edit" or "plugins.build" or "plugins.cancel" or "plugins.probe")
                CommandBindings.ApplyLabeled(button, key, Texts.T(key));
            else { button.Content = Texts.T(key); AutomationProperties.SetName(button, Texts.T(key)); }
        }
        C<TextBlock>("PluginInventoryHeader").Text = Texts.T("plugins.inventoryProblems");
        AutomationProperties.SetName(C<SelectionList>("PluginCards"), Texts.T("nav.plugins"));
        C<Expander>("PluginInfoExpander").Header = Texts.T("plugins.information");
        C<Expander>("PluginGrantExpander").Header = Texts.T("plugins.grants");
        C<Expander>("PluginDirectoryExpander").Header = Texts.T("plugins.directoryDetails");
        C<Expander>("PluginDeveloperExpander").Header = Texts.T("plugins.developerDetails");
        C<Expander>("PluginEditorExpander").Header = Texts.T("plugins.editorConfiguration");
        C<TextBox>("PluginProbeUrl").PlaceholderText = Texts.T("plugins.probeUrl");
        AutomationProperties.SetName(C<TextBox>("PluginProbeUrl"), Texts.T("plugins.probeUrl"));
        AutomationProperties.SetName(C<TextBox>("PluginEditorExecutable"), Texts.T("plugins.editorExecutable"));
        AutomationProperties.SetName(C<TextBox>("PluginEditorArguments"), Texts.T("plugins.editorArguments"));
        SetTestStatus(_testStatusKey, _testStatusArguments);
        if (Selected is { } localizedEntry) { RebuildLocalizedSettings(localizedEntry); RenderInformation(localizedEntry); RenderGrants(localizedEntry); }
        _refreshing = true;
        int selected = C<ComboBox>("PluginCategory").SelectedIndex;
        C<ComboBox>("PluginCategory").ItemsSource = new[] { Texts.T("plugins.all"), Texts.T("plugins.sources"), Texts.T("plugins.filters") };
        C<ComboBox>("PluginCategory").SelectedIndex = selected < 0 ? 0 : selected;
        AutomationProperties.SetName(C<ComboBox>("PluginCategory"), Texts.T("plugins.category"));
        _refreshing = false;
    }
    internal async Task RefreshAsync()
    {
        if (_ended || _services == null) { if (!_ended) Error(Texts.T("plugins.unavailable")); return; }
        long refreshGeneration = ++_refreshGeneration;
        try
        {
            PluginCatalogSnapshot catalog = await _services.ReadCatalogAsync(_lifetime.Token);
            if (_ended || refreshGeneration != _refreshGeneration) return;
            _catalog = catalog;
            if (_selectedKey != null && !catalog.Entries.Any(entry => entry.Key == _selectedKey))
            {
                CancelOperation(); Error(null); SetTestStatus(null);
            }
            foreach (string key in _drafts.Where(pair => !pair.Value.Dirty && pair.Value.ExpectedRevision != catalog.Profile.Revision).Select(pair => pair.Key).ToArray())
            {
                _drafts.Remove(key);
                if (key == _selectedKey) _renderedKey = null;
            }
            RenderList(); RenderDetails();
        }
        catch (OperationCanceledException) when (_ended) { }
        catch (Exception exception) { if (!_ended && refreshGeneration == _refreshGeneration) Error(exception.Message); }
    }
    private bool Included(PluginCatalogEntry entry) => C<ComboBox>("PluginCategory").SelectedIndex switch { 1 => entry.IsSource, 2 => entry.IsFilter, _ => true };
    private void RenderList()
    {
        SelectionList list = C<SelectionList>("PluginCards");
        Control? focusedItem = _cards.Values.FirstOrDefault(item => item.IsFocused);
        var listOffset = C<ScrollViewer>("PluginListScroll").Offset;
        PluginCatalogEntry[] entries = _catalog?.Entries.Where(Included).ToArray() ?? Array.Empty<PluginCatalogEntry>();
        var productionWorkers = _services?.GetProductionWorkerSnapshots?.Invoke();
        _projectingSelection = true;
        try
        {
            foreach (string removed in _cards.Keys.Where(key => !entries.Any(entry => entry.Key == key)).ToArray())
            {
                list.Items.Remove(_cards[removed]);
                if (_catalog?.Entries.Any(entry => entry.Key == removed) != true)
                { _cards.Remove(removed); _cardContent.Remove(removed); }
            }
            for (int index = 0; index < entries.Length; index++)
            {
                PluginCatalogEntry entry = entries[index];
                if (!_cards.TryGetValue(entry.Key, out SelectionListItem? item))
                {
                    var title = new TextBlock { Name = "PluginListName", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                    var purpose = new TextBlock { Name = "PluginListDescription", TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                    var origin = new TextBlock { Name = "PluginListDescriptionOrigin", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                    var state = new TextBlock { Name = "PluginListState", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                    var identity = new TextBlock { Name = "PluginListIdentity", TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
                    origin.Classes.Add("muted"); state.Classes.Add("muted"); identity.Classes.Add("muted");
                    _cardContent.Add(entry.Key, new(title, purpose, origin, state, identity));
                    item = new PluginSelectionItem { HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        Padding = new Thickness(12), Content = new StackPanel { Spacing = 5, Children = { title, purpose, origin, state, identity } } };
                    string key = entry.Key;
                    item.PointerReleased += (_, args) =>
                    {
                        if (args.InitialPressMouseButton is not (MouseButton.Left or MouseButton.None)) return;
                        Select(key); args.Handled = true;
                    };
                    _cards.Add(key, item);
                }
                if (index >= list.Items.Count || !ReferenceEquals(list.Items[index], item))
                { list.Items.Remove(item); list.Items.Insert(index, item); }
                PluginEntryPresentation presentation = PluginEntryPresentation.Create(entry, productionWorkers);
                PluginListContent content = _cardContent[entry.Key];
                content.Name.Text = presentation.Name;
                content.Description.Text = PluginContentPresentation.DescribeDescription(presentation);
                content.Origin.Text = presentation.HasRetainedDescription ? Texts.T("plugins.lastDescriptionShort") : string.Empty;
                content.Origin.IsVisible = presentation.HasRetainedDescription;
                content.State.Text = PluginContentPresentation.DescribeType(presentation.DescriptionMetadata) + " · " + PluginContentPresentation.DescribeStatus(presentation);
                PluginCatalogEntry[] similar = entries.Where(other => string.Equals(other.Name, entry.Name, StringComparison.CurrentCultureIgnoreCase)).ToArray();
                content.Identity.IsVisible = similar.Length > 1;
                if (similar.Length > 1)
                {
                    string fileName = System.IO.Path.GetFileName(entry.LoaderPath) ?? entry.CandidateId ?? entry.Key;
                    content.Identity.Text = Texts.T("plugins.candidatePosition", Array.FindIndex(similar, other => other.Key == entry.Key) + 1, similar.Length)
                        + " · " + fileName + (presentation.CandidateVersion is { } version ? " · " + Texts.T("plugins.fileVersion", version) : string.Empty);
                    ToolTip.SetTip(content.Identity, entry.LoaderPath ?? entry.CandidateId ?? entry.Key);
                }
                AutomationProperties.SetName(item, string.Join(". ", new[] { presentation.Name, content.Description.Text, content.Origin.Text, content.State.Text,
                    content.Identity.IsVisible ? content.Identity.Text : null }.Where(value => !string.IsNullOrEmpty(value))));
            }
            list.SelectedItem = _selectedKey != null && entries.Any(entry => entry.Key == _selectedKey) ? _cards.GetValueOrDefault(_selectedKey) : null;
        }
        finally { _projectingSelection = false; }
        if (focusedItem != null && list.Items.Contains(focusedItem) && !focusedItem.IsFocused) focusedItem.Focus();
        C<ScrollViewer>("PluginListScroll").Offset = listOffset;
        if (Selected == null || !Included(Selected)) _showDetails = false;
        C<TextBlock>("PluginsEmpty").IsVisible = _catalog != null && entries.Length == 0;
        C<TextBlock>("PluginsEmpty").Text = Texts.T(_catalog?.Entries.Count == 0 ? "plugins.noPlugins" : C<ComboBox>("PluginCategory").SelectedIndex switch
        { 1 => "plugins.emptySources", 2 => "plugins.emptyFilters", _ => "plugins.empty" });
        int count = _catalog?.Diagnostics.Count ?? 0;
        C<Expander>("PluginInventoryProblems").IsVisible = count > 0;
        C<Badge>("PluginInventoryBadge").Header = count;
        AutomationProperties.SetName(C<Expander>("PluginInventoryProblems"), Texts.T("plugins.inventoryCount", count));
        C<TextBlock>("PluginInventoryDiagnostics").Text = string.Join("\n", _catalog?.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message + "\n" + diagnostic.FilePath) ?? Array.Empty<string>());
        ApplyPaneVisibility();
        RefreshPluginActions();
    }
    private void OnPluginSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_projectingSelection) return;
        if (C<SelectionList>("PluginCards").SelectedItem is SelectionListItem item &&
            _cards.FirstOrDefault(pair => ReferenceEquals(pair.Value, item)).Key is { } key)
            SelectCore(key, openDetails: false);
    }
    private void OnPluginListKeyDown(object? sender, KeyEventArgs e)
    {
        if (TryHandlePluginActionKeyDown(e)) return;
        SelectionList list = C<SelectionList>("PluginCards");
        if (e.Key is Key.Enter or Key.Space)
        { if (_selectedKey != null && list.SelectedItem != null && _cards.ContainsKey(_selectedKey)) { Select(_selectedKey); e.Handled = true; } return; }
        if (e.Key is not (Key.Up or Key.Down or Key.Home or Key.End) || list.Items.Count == 0) return;
        int index = e.Key switch { Key.Home => 0, Key.End => list.Items.Count - 1,
            Key.Up => Math.Max(0, list.SelectedIndex - 1), _ => Math.Min(list.Items.Count - 1, list.SelectedIndex + 1) };
        list.SelectedIndex = index;
        if (list.Items[index] is Control item) { item.Focus(); item.BringIntoView(); }
        e.Handled = true;
    }
    internal void Select(string key) => SelectCore(key, openDetails: true);
    private void SelectCore(string key, bool openDetails)
    {
        if (_ended || _catalog?.Entries.Any(entry => entry.Key == key) != true) return;
        if (_selectedKey != key) { ClosePluginActionMenu(); CancelDiscardConfirmation(); ++_refreshGeneration; CancelOperation(); SetTestStatus(null); Error(null); }
        _selectedKey = key; _showDetails = openDetails;
        RenderList(); RenderDetails();
        if (_narrow && openDetails) C<Button>("PluginBack").Focus();
    }
    private void RenderDetails()
    {
        PluginCatalogEntry? entry = Selected;
        ApplyPaneVisibility();
        if (entry == null) return;
        PluginEntryPresentation presentation = PluginEntryPresentation.Create(entry,
            _services?.GetProductionWorkerSnapshots?.Invoke(), CurrentDraft, _catalog?.Profile.Revision);
        C<TextBlock>("PluginHeading").Text = presentation.Name;
        RenderTechnicalIdentity(entry);
        C<TextBlock>("PluginDescription").Text = PluginContentPresentation.DescribeDescription(presentation);
        C<TextBlock>("PluginDescriptionOrigin").Text = PluginContentPresentation.DescribeDescriptionOrigin(presentation.DescriptionOrigin);
        C<TextBlock>("PluginDescriptionOrigin").IsVisible = presentation.HasRetainedDescription;
        string? author = presentation.DescriptionMetadata?.Author;
        C<TextBlock>("PluginAuthor").Text = string.IsNullOrWhiteSpace(author) ? string.Empty : Texts.T("plugins.author", author);
        C<TextBlock>("PluginAuthor").IsVisible = !string.IsNullOrWhiteSpace(author);
        C<TextBlock>("PluginType").Text = PluginContentPresentation.DescribeType(presentation.DescriptionMetadata);
        C<Border>("PluginVersionChip").IsVisible = !string.IsNullOrEmpty(presentation.CandidateVersion);
        C<TextBlock>("PluginVersion").Text = presentation.CandidateVersion is { } version ? Texts.T("plugins.fileVersion", version) : string.Empty;
        string? pluginId = presentation.PluginId ?? presentation.DescriptionMetadata?.PluginId;
        C<TextBlock>("PluginCompactId").Text = pluginId == null ? string.Empty : Texts.T("plugins.pluginIdCompact", pluginId);
        C<Border>("PluginIdChip").IsVisible = pluginId != null;
        ToolTip.SetTip(C<TextBlock>("PluginCompactId"), C<TextBlock>("PluginCompactId").Text);
        SourceCapabilities capabilities = presentation.Sources.Aggregate(SourceCapabilities.None, (flags, source) => flags | source.Capabilities);
        string functions = string.Join(" · ", new[] { presentation.Sources.Count > 0 ? PluginContentPresentation.DescribeCapabilities(capabilities) : string.Empty,
            string.Join(" · ", presentation.Filters.Select(filter => filter.Name)) }.Where(value => !string.IsNullOrEmpty(value)));
        C<TextBlock>("PluginCapabilities").Text = functions;
        C<TextBlock>("PluginCapabilities").IsVisible = functions.Length > 0;
        C<TextBlock>("PluginStatus").Text = PluginContentPresentation.DescribeStatus(presentation);
        string hint = PluginContentPresentation.DescribeStatusHint(presentation);
        C<TextBlock>("PluginStatusHint").Text = hint;
        C<TextBlock>("PluginStatusHint").IsVisible = hint.Length > 0;
        C<TextBlock>("PluginCandidateDiagnostics").Text = string.Join("\n", entry.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message + "\n" + diagnostic.FilePath));
        C<TextBlock>("PluginCandidateDiagnostics").IsVisible = entry.Diagnostics.Count > 0;
        C<StackPanel>("PluginSourceExpander").IsVisible = entry.SourcePath != null;
        C<TextBlock>("PluginSourcePath").Text = entry.SourcePath;
        if (_renderedKey != entry.Key)
        {
            _renderedKey = entry.Key;
            BuildSettings(entry); RenderInformation(entry); RenderGrants(entry);
            RestorePluginToolState(entry);
        }
        if (entry.Description != null && CurrentDraft == null) { BuildSettings(entry); RenderInformation(entry); }
        PluginGrantSnapshot[] currentGrants = _catalog!.Profile.Grants.Where(grant => grant.PluginId == entry.PluginId).ToArray();
        if (_renderedGrantsKey != entry.Key || !_displayedGrants.SequenceEqual(currentGrants)) RenderGrants(entry);
        UpdateDraftState(); UpdateTestButtons(); UpdateGrantActions(); RefreshPluginSections(); RefreshPluginActions();
    }
    private void UpdateDraftState()
    {
        PluginConfigurationDraft? draft = CurrentDraft;
        bool stale = draft != null && draft.ExpectedRevision != _catalog?.Profile.Revision;
        C<TextBlock>("PluginDraftStatus").Text = Texts.T(stale ? "plugins.stale" : draft?.Dirty == true ? "plugins.draft" : "plugins.settingsHint");
        SetAction("PluginSave", stale ? "plugins.stale" : "plugins.reason.unchanged", draft?.Dirty == true && !stale);
        SetAction("PluginDiscard", "plugins.reason.noDraft", draft != null);
        ShowReasons("PluginDraftReasons", "PluginSave", "PluginDiscard");
        RefreshPluginActions();
    }
    private void RenderInformation(PluginCatalogEntry entry)
    {
        StackPanel panel = C<StackPanel>("PluginInformation"); panel.Children.Clear();
        C<Expander>("PluginInfoExpander").IsVisible = entry.Description is { } description &&
            (description.Sources.Count > 0 || description.Filters.Count > 0 || description.Information.Count > 0);
        if (entry.Description == null) return;
        foreach (SourceDescription source in entry.Description.Sources)
            panel.Children.Add(Label(source.Name + ": " + PluginContentPresentation.DescribeCapabilities(source.Capabilities) + "\n" + string.Join(", ", source.HttpHosts.Select(host => host.Host + " (" + host.Purpose + ")"))));
        foreach (FilterDescription filter in entry.Description.Filters)
            panel.Children.Add(Label(filter.Name + ": " + filter.Description + "\n" + Texts.T("plugins.filterFormats", string.Join(", ", filter.SupportedMimeTypes))));
        foreach (InformationEntry info in entry.Description.Information)
            panel.Children.Add(Label(info.Label + ": " + (info.Kind == InformationKind.List ? string.Join(", ", info.Items ?? Array.Empty<string>()) : info.Value)
                + (info.AsOf is { } at ? "\n" + Texts.T("plugins.asOf", at.ToLocalTime(), info.SourceId ?? entry.PluginId) : string.Empty)));
    }
    private void RenderGrants(PluginCatalogEntry entry)
    {
        StackPanel panel = C<StackPanel>("PluginGrants"); panel.Children.Clear();
        _renderedGrantsKey = entry.Key;
        _displayedGrants = _catalog!.Profile.Grants.Where(grant => grant.PluginId == entry.PluginId).ToArray();
        foreach (FilterDescription filter in entry.Description?.Filters ?? Array.Empty<FilterDescription>())
            panel.Children.Add(Label(filter.Name + ": " + Texts.T("plugins.requiredAccess", filter.RequiredAccess) + "\n" + Texts.T("plugins.grantHint")));
        foreach (PluginGrantSnapshot grant in _catalog!.Profile.Grants.Where(grant => grant.PluginId == entry.PluginId))
        {
            panel.Children.Add(Label(grant.FilterKey + " · " + PluginGrantPresentation.Describe(grant,
                _services?.GetRuleScopes?.Invoke() ?? Array.Empty<MeowChan.Rules.MediaRuleScope>(), effective: false)));
            Button revoke = new() { Content = Texts.T("plugins.revoke"), Tag = grant };
            revoke.Click += async (_, _) => await CommandAsync((services, revision, token) => services.ProfileStore.SetGrantAsync(grant.PluginId, grant.FilterKey, grant.Scope, FilterDataAccess.None, revision, token));
            panel.Children.Add(revoke);
            panel.Children.Add(new SelectableTextBlock { Focusable = true, TextWrapping = TextWrapping.Wrap });
        }
        C<Expander>("PluginGrantExpander").IsVisible = entry.Description?.Filters.Count > 0;
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private void ApplyPaneVisibility()
    {
        bool hasSelection = Selected != null && Included(Selected);
        C<Grid>("PluginListPane").IsVisible = !_narrow || !_showDetails || !hasSelection;
        C<ScrollViewer>("PluginDetailsPane").IsVisible = hasSelection && (!_narrow || _showDetails);
        C<Button>("PluginBack").IsVisible = _narrow;
        C<Border>("PluginNoSelection").IsVisible = !_narrow && !hasSelection && _catalog?.Entries.Count > 0;
    }
    private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Control? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        bool ContainsFocus(Control pane) => focused != null &&
            (ReferenceEquals(focused, pane) || focused.GetVisualAncestors().Contains(pane));
        bool listHadFocus = ContainsFocus(C<Grid>("PluginListPane"));
        bool detailsHadFocus = ContainsFocus(C<ScrollViewer>("PluginDetailsPane"));
        bool narrow = Bounds.Width < 850;
        if (narrow && !_narrow)
        {
            // The pane the user is working in takes precedence over a previous
            // explicit-open flag. A breakpoint must never hide that input.
            if (detailsHadFocus) _showDetails = true;
            else if (listHadFocus) _showDetails = false;
        }
        _narrow = narrow;
        C<Grid>("PluginSplit").ColumnDefinitions[0].Width = new GridLength(_narrow ? 0 : 240);
        C<Grid>("PluginSplit").ColumnSpacing = _narrow ? 0 : 14;
        Grid.SetColumn(C<Grid>("PluginListPane"), _narrow ? 1 : 0); ApplyPaneVisibility();
        // Widening hides Back itself. Repair only a focus owned by one of our
        // panes, and only if its current control became unavailable.
        if ((listHadFocus || detailsHadFocus) && focused?.IsEffectivelyVisible != true)
        {
            if (C<ScrollViewer>("PluginDetailsPane").IsVisible)
            {
                if (_narrow) C<Button>("PluginBack").Focus();
                else
                {
                    C<TextBlock>("PluginHeading").Focusable = true;
                    C<TextBlock>("PluginHeading").Focus();
                }
            }
            else C<ComboBox>("PluginCategory").Focus();
        }
    }
    private void OnBackClick(object? sender, RoutedEventArgs e)
    { _showDetails = false; ApplyPaneVisibility(); if (_selectedKey != null && _cards.TryGetValue(_selectedKey, out SelectionListItem? card) && C<SelectionList>("PluginCards").Items.Contains(card)) { card.BringIntoView(); card.Focus(); } else C<ComboBox>("PluginCategory").Focus(); }
    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e) { if (!_refreshing) { ClosePluginActionMenu(); CancelDiscardConfirmation(); _showDetails = false; RenderList(); } }
    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnFolderClick(object? sender, RoutedEventArgs e)
    { try { if (_services != null) await ExternalLauncher.OpenAsync(_services.PluginDirectory); } catch (Exception error) { if (!_ended) Error(error.Message); } }
    private void OnWatchesClick(object? sender, RoutedEventArgs e)
    { _ = ExecutePluginControlActionAsync("PluginWatches"); }
    private async void OnEnabledClick(object? sender, RoutedEventArgs e)
    { await ExecutePluginControlActionAsync("PluginEnabled"); }
    private async void OnSelectCandidateClick(object? sender, RoutedEventArgs e)
    { await ExecutePluginControlActionAsync("PluginSelectCandidate"); }
    private async void OnRollbackClick(object? sender, RoutedEventArgs e)
    { await ExecutePluginControlActionAsync("PluginRollback"); }
    private async Task CommandAsync(Func<PluginManagementServices, long, CancellationToken, Task<PluginProfileSnapshot>> command)
    {
        if (_ended || _busy || _services == null || _catalog == null) return;
        (long generation, CancellationToken token) = BeginOperation();
        try
        {
            await command(_services, _catalog.Profile.Revision, token);
            if (!Current(generation)) return;
            await RefreshAsync(); if (Current(generation) && Selected is { } selected) RenderGrants(selected);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) Error(exception.Message); }
        finally { FinishOperation(generation); }
    }
    private void OnDiscardClick(object? sender, RoutedEventArgs e)
    { RequestDiscardSelectedDraft(); }
    private void Error(string? text)
    {
        TextBlock summary = C<TextBlock>("PluginPageError");
        summary.Text = text;
        summary.Focusable = !string.IsNullOrEmpty(text);
        AutomationProperties.SetName(summary, text);
        C<Ursa.Controls.Banner>("PluginErrorBanner").IsVisible = !string.IsNullOrEmpty(text);
    }
    private (long Generation, CancellationToken Token) BeginOperation()
    {
        ClosePluginActionMenu();
        CancelDiscardConfirmation();
        CancelOperation(); _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationKey = _selectedKey;
        _busy = true; BusyChanged?.Invoke(this, EventArgs.Empty); Error(null); RenderDetails();
        return (_operationGeneration, _operation.Token);
    }
    private bool Current(long generation) => !_ended && generation == _operationGeneration && _operationKey == _selectedKey && _operation?.IsCancellationRequested == false;
    private void FinishOperation(long generation)
    { if (generation != _operationGeneration) return; _operation?.Dispose(); _operation = null; _operationKey = null; _busy = false; if (!_ended) { RenderDetails(); BusyChanged?.Invoke(this, EventArgs.Empty); } }
    private void CancelOperation()
    {
        ++_operationGeneration; _operation?.Cancel(); _operation?.Dispose(); _operation = null; _operationKey = null; _busy = false;
        C<Ursa.Controls.LoadingIcon>("PluginOperationLoading").IsLoading = false;
        C<Ursa.Controls.LoadingIcon>("PluginOperationLoading").IsVisible = false;
    }
    private async void OnStatusTick(object? sender, EventArgs e)
    {
        if (_ended || _statusReadPending || _busy || !IsEffectivelyVisible) return;
        _statusReadPending = true;
        try { await RefreshAsync(); } finally { _statusReadPending = false; }
    }
    internal void Deactivate() { ClosePluginActionMenu(); CancelDiscardConfirmation(); _statusTimer.Stop(); ++_refreshGeneration; CancelOperation(); if (!_ended) RenderDetails(); }
    internal void EndLifetime()
    { if (_ended) return; ClosePluginActionMenu(); CancelDiscardConfirmation(); _ended = true; _pluginSectionNavigation?.Dispose(); IsEnabled = false; _statusTimer.Stop(); _statusTimer.Tick -= OnStatusTick; Texts.Changed -= OnLanguageChanged; _lifetime.Cancel(); CancelOperation(); _lifetime.Dispose(); }
}
