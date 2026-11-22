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
    private sealed record PluginListContent(TextBlock Name, WrapPanel Chips, TextBlock Description, TextBlock Origin, TextBlock State, TextBlock Identity, Desktop.Controls.StatFooterRow Footer)
    {
        internal string ChipSignature { get; set; } = string.Empty;
    }
    private readonly Dictionary<string, PluginListContent> _cardContent = new(StringComparer.Ordinal);
    private bool _projectingSelection;
    private readonly Dictionary<string, string?> _actionReasons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DescriptionsItem Item, SelectableTextBlock Value)> _identityRows = new(StringComparer.Ordinal);
    private string? _selectedKey;
    // Plugin whose configuration was just saved: its status line says when the saved values apply.
    private string? _savedNoticeKey;
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
    private PluginErrorState? _errorState;

    // Only host-owned messages carry translation keys. Worker/compiler/exception
    // text remains opaque, even when it happens to look like a translation key.
    // A failure pairs a localized sentence (what happened, what to do next) with the original
    // technical text, which is kept only as a detail (tooltip and accessible help), never as the message.
    internal sealed class PluginErrorState
    {
        private readonly object?[] _arguments;
        internal string? Key { get; }
        internal string? RawText { get; }
        internal string? Detail { get; }

        private PluginErrorState(string? key, string? rawText, object?[] arguments, string? detail = null)
        { Key = key; RawText = rawText; _arguments = (object?[])arguments.Clone(); Detail = detail; }

        internal static PluginErrorState Failure(string key, string? detail) =>
            new(key, null, Array.Empty<object?>(), string.IsNullOrWhiteSpace(detail) ? null : detail);

        internal static PluginErrorState Localized(string key, params object?[] arguments) =>
            new(key, null, arguments);
        internal static PluginErrorState Raw(string text) =>
            new(null, text, Array.Empty<object?>());
        internal string Render() => Key is null ? RawText! : Texts.T(Key, _arguments);
    }
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
        // Every key of the plugin management runs in the "plugins" context (S3 P2-4): a default that a saved binding took away only elsewhere (a partial
        // yield) stays active here (EffectiveShortcutBindings.EnterKeyContext). Inside the Sources page this host is the nearer one and wins.
        MeowChan.Desktop.Controls.KeyContextHost.Install(this, _ => "plugins");
        InitializeComponent();
        InitializePluginDetails();
        InitializePluginActions();
        KeyDown += (_, args) =>
        {
            if (args.Handled || args.Key != Key.Escape) return;
            if (PluginActionMenu?.IsOpen == true) { ClosePluginActionMenu(restoreFocus: true); args.Handled = true; return; }
            if (_busy) { OnCancelClick(this, new RoutedEventArgs()); args.Handled = true; }
            else if (ShowingSourcePluginDetails) { ReturnToSourceOverview(); args.Handled = true; }
            else if (_narrow && _showDetails) { OnBackClick(this, new RoutedEventArgs()); args.Handled = true; }
        };
        C<SelectionList>("PluginCards").AddHandler(KeyDownEvent, OnPluginListKeyDown, RoutingStrategies.Tunnel);
        AttachTypeAhead();
        Texts.Changed += OnLanguageChanged;
        // Layout only: an empty status row (no text, no spinner) takes no height and no gap; it is never hidden, so the live
        // region stays in the tree and announces the next status as before.
        C<TextBlock>("PluginTestStatus").PropertyChanged += (_, args) => { if (args.Property == TextBlock.TextProperty) FitStatusRow(); };
        C<Ursa.Controls.LoadingIcon>("PluginOperationLoading").PropertyChanged += (_, args) => { if (args.Property == IsVisibleProperty) FitStatusRow(); };
        FitStatusRow();
        _statusTimer.Tick += OnStatusTick;
        Localize();
    }
    internal void Attach(PluginManagementServices services)
    {
        _services = services;
        ToolTip.SetTip(C<Button>("PluginsFolder"), services.PluginDirectory);
        C<Button>("PluginsLocalTagger").IsVisible = services.LocalTaggerModels != null;
    }
    internal async Task ActivateAsync(string? sourceId = null)
    {
        if (_ended) return;
        _renderedGrantsKey = null;
        _statusTimer.Start();
        await RefreshAsync();
        if (_ended) return;
        if (sourceId != null)
        {
            SetFilter(SourcesPluginsFilter.Sources);
            PluginCatalogEntry? entry = _catalog?.Entries.FirstOrDefault(entry => entry.Description?.Sources.Any(source => source.SourceId == sourceId) == true);
            if (entry != null) { Select(entry.Key); FocusPluginHeading(); }
        }
        else FocusListOrFilter();
    }
    private T C<T>(string name) where T : Control => this.FindControl<T>(name)!;
    /// <summary>The content of an icon-and-text action button (a catalog icon, then the label), as "Add plugin…" in the header.</summary>
    internal static Control IconAndLabel(string icon, string label) => new StackPanel
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
        Children = { CommandBindings.NamedIcon(icon), new TextBlock { Text = label } }
    };

    /// <summary>
    /// Where focus goes when nothing is selected (after activation or Back): the list itself, never the "Show" filter in front of
    /// it. A source list is the Sources page's own (its selected card); the plugin list focuses its selected or first card. The
    /// dropdown is only the last resort, when no card exists.
    /// </summary>
    private void FocusListOrFilter()
    {
        if (SourceContent != null && Filter == SourcesPluginsFilter.Sources && FocusSourceCard?.Invoke() == true) return;
        SelectionList list = C<SelectionList>("PluginCards");
        if (list.IsEffectivelyVisible)
        {
            Control? card = list.SelectedItem as Control ?? list.Items.OfType<Control>().FirstOrDefault(item => item.IsEffectivelyVisible);
            if (card != null) { card.BringIntoView(); card.Focus(); return; }
        }
        C<ComboBox>("PluginCategory").Focus();
    }
    private void FitStatusRow()
    {
        bool empty = string.IsNullOrEmpty(C<TextBlock>("PluginTestStatus").Text) && !C<Ursa.Controls.LoadingIcon>("PluginOperationLoading").IsVisible;
        DockPanel row = C<DockPanel>("PluginStatusRow");
        row.MaxHeight = empty ? 0 : double.PositiveInfinity;
        row.Margin = empty ? default : new Thickness(0, 8, 0, 0);
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Invalidate consent synchronously, including same-language catalog reloads.
        Interlocked.Increment(ref _migrationCatalogEpoch);
        _migrationApproval?.Stop();
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended) return;
            CloseBundledRepairPreview();
            // These transient host-owned surfaces materialize localized text. Do not leave
            // a copied-status or discard confirmation in the previous language while the
            // selected plugin/draft itself is retained.
            C<TextBlock>("PluginTechnicalCopyStatus").Text = string.Empty;
            ShowPluginIdCopyStatus(null);
            CancelDiscardConfirmation();
            // Texts.Changed also reloads catalogs without changing Texts.Language.
            // The menu contains materialized labels/reasons, so its language-code
            // guard alone cannot invalidate that old catalog snapshot.
            ClosePluginActionMenu();
            Localize(); RenderError(); RenderList(); RenderDetails(); RenderPluginProbeResult();
        });
    }
    private void Localize()
    {
        foreach ((string name, string key) in new[]
        {
            ("PluginsTitle","nav.sources"),("PluginsSubtitle","sources.subtitle"),("PluginsEmpty","plugins.empty"),
            ("PluginTestHint","plugins.testHint"),("PluginEditorHint","plugins.editorHint"),
            ("PluginSourceToolsHeading","plugins.sourceTools")
        }) C<TextBlock>(name).Text = name == "PluginEditorHint" ? Texts.T(key, string.Empty) : Texts.T(key);
        foreach ((string name, string key) in new[]
        {
            ("PluginsLocalTagger","localTagger.title"),("PluginsRefresh","plugins.refresh"),("PluginsFolder","plugins.folder"),("PluginBack","plugins.back"),
            ("PluginsReviewRepairs","plugins.repair.review"),
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
        // Actions that carried a word only: catalog icons before the (still string) label.
        CommandBindings.EnsureIconBeforeText(C<Button>("PluginCopyTechnicalDetails"), "copy");
        CommandBindings.EnsureIconBeforeText(C<Button>("PluginDefaultEditor"), "external-link");
        CommandBindings.EnsureIconBeforeText(C<Button>("PluginEditorSave"), "save");
        Button add = C<Button>("PluginsAdd");
        add.Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { CommandBindings.NamedIcon("plus"), new TextBlock { Text = Texts.T("plugins.add") } } }; AutomationProperties.SetName(add, Texts.T("plugins.add")); ToolTip.SetTip(add, Texts.T("plugins.add.help"));
        Button emptyAdd = C<Button>("PluginsEmptyAdd");
        emptyAdd.Content = IconAndLabel("plus", Texts.T("plugins.add")); AutomationProperties.SetName(emptyAdd, Texts.T("plugins.add")); ToolTip.SetTip(emptyAdd, Texts.T("plugins.add.help"));
        C<Button>("PluginsReviewRepairs").Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { CommandBindings.NamedIcon("check"), new TextBlock { Text = Texts.T("plugins.repair.review") } } };
        C<TextBlock>("PluginInventoryHeader").Text = Texts.T("plugins.inventoryProblems");
        AutomationProperties.SetName(C<SelectionList>("PluginCards"), Texts.T("nav.plugins"));
        C<Expander>("PluginInfoExpander").Header = Texts.T("plugins.information");
        C<Expander>("PluginGrantExpander").Header = Texts.T("plugins.grants");
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
        C<ComboBox>("PluginCategory").ItemsSource = new[] { Texts.T("plugins.sources"), Texts.T("plugins.filters"), Texts.T("plugins.all") };
        C<ComboBox>("PluginCategory").SelectedIndex = selected < 0 ? 2 : selected;
        AutomationProperties.SetName(C<ComboBox>("PluginCategory"), Texts.T("sources.show"));
        C<TextBlock>("SourcesShowLabel").Text = Texts.T("sources.show");
        if (_services != null) ToolTip.SetTip(C<Button>("PluginsFolder"), CommandBindings.Tooltip("plugins.folder") + "\n" + _services.PluginDirectory);
        _refreshing = false;
    }
    internal async Task RefreshAsync()
    {
        if (_ended || _services == null) { if (!_ended) ErrorLocalized("plugins.unavailable"); return; }
        long refreshGeneration = ++_refreshGeneration;
        try
        {
            PluginCatalogSnapshot catalog = await _services.ReadCatalogAsync(_lifetime.Token);
            if (_ended || refreshGeneration != _refreshGeneration) return;
            _catalog = catalog;
            CatalogChanged?.Invoke(this, EventArgs.Empty);
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
        catch (Exception exception) { if (!_ended && refreshGeneration == _refreshGeneration) ErrorFailure("plugins.error.refresh", exception); }
    }
    private bool Included(PluginCatalogEntry entry) => SourcesPluginsFilterPolicy.Includes(entry, Filter);
    private void RenderList()
    {
        SelectionList list = C<SelectionList>("PluginCards");
        Control? focusedItem = _cards.Values.FirstOrDefault(item => item.IsFocused);
        var listOffset = C<ScrollViewer>("PluginListScroll").Offset;
        // One card per plugin (its files are chosen in the detail), active plugins first.
        PluginCatalogEntry[] entries = CardEntries();
        var productionWorkers = _services?.GetProductionWorkerSnapshots?.Invoke();
        if (SourceContent == null || Filter != SourcesPluginsFilter.Sources)
        {
            if (Selected == null || !Included(Selected))
            {
                string? first = entries.FirstOrDefault()?.Key;
                if (_selectedKey != first) { CancelDiscardConfirmation(); CancelOperation(); _selectedKey = first; _renderedKey = null; }
            }
        }
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
                    // The card of the Sources list: name, chips only where a source deviates, then quiet meta lines.
                    var title = new TextBlock { Name = "PluginListName", TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                    var chips = new WrapPanel { Name = "PluginListChips", ItemSpacing = 8, LineSpacing = 4, IsVisible = false };
                    var purpose = new TextBlock { Name = "PluginListDescription", TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                    var origin = new TextBlock { Name = "PluginListDescriptionOrigin", TextWrapping = TextWrapping.Wrap };
                    var state = new TextBlock { Name = "PluginListState", TextWrapping = TextWrapping.Wrap };
                    var identity = new TextBlock { Name = "PluginListIdentity", TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
                    title.Classes.Add("section-title");
                    purpose.Classes.Add("workspace-meta"); origin.Classes.Add("workspace-meta"); state.Classes.Add("workspace-meta"); identity.Classes.Add("workspace-meta");
                    // The counts close every card the same way (also a disabled or unused plugin: zeros, never blank).
                    var footer = new Desktop.Controls.StatFooterRow();
                    _cardContent.Add(entry.Key, new(title, chips, purpose, origin, state, identity, footer));
                    item = new PluginSelectionItem { HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                        Classes = { "workspace-list-card" }, Content = new StackPanel { Spacing = 4, Children = { title, chips, purpose, origin, state, identity, footer } } };
                    string key = entry.Key;
                    item.PointerReleased += (_, args) =>
                    {
                        if (args.InitialPressMouseButton is not (MouseButton.Left or MouseButton.None)) return;
                        // The card stands for all files of its plugin: a click on the shown plugin keeps the file already shown.
                        Select(_selectedKey != null && CardKey(_selectedKey) == key ? _selectedKey : key); args.Handled = true;
                    };
                    _cards.Add(key, item);
                }
                if (index >= list.Items.Count || !ReferenceEquals(list.Items[index], item))
                { list.Items.Remove(item); list.Items.Insert(index, item); }
                PluginEntryPresentation presentation = PluginEntryPresentation.Create(entry, productionWorkers, sdkMismatch: _services?.HasSdkMismatch);
                PluginListContent content = _cardContent[entry.Key];
                content.Name.Text = presentation.Name;
                content.Description.Text = PluginContentPresentation.DescribeDescription(presentation);
                // Name, description and identity stop after two lines with an ellipsis in a narrow list: the full text stays the tooltip.
                ToolTip.SetTip(content.Name, string.IsNullOrEmpty(presentation.Name) ? null : presentation.Name);
                ToolTip.SetTip(content.Description, string.IsNullOrEmpty(content.Description.Text) ? null : content.Description.Text);
                content.Origin.Text = presentation.HasRetainedDescription ? Texts.T("plugins.lastDescriptionShort") : string.Empty;
                content.Origin.IsVisible = presentation.HasRetainedDescription;
                string sourceClassifications = PluginContentPresentation.DescribeSourceClassifications(presentation);
                // The classification is shown as chips (as on the Sources list); the line keeps type and status.
                // The item's accessible name below still carries the complete former line, classification included.
                string type = PluginContentPresentation.DescribeType(presentation.DescriptionMetadata);
                string status = PluginContentPresentation.DescribeStatus(presentation);
                content.State.Text = type + " · " + status;
                RenderListChips(content, presentation, GroupNeedsAttention(entry));
                // Several files of one plugin share this card ("Files: 2"); another plugin of the same name is told apart by its ID.
                int fileCount = GroupMembers(entry).Count(Included);
                bool sameName = entries.Count(other => string.Equals(other.Name, entry.Name, StringComparison.CurrentCultureIgnoreCase)) > 1;
                string identityText = string.Join(" · ", new[] { fileCount > 1 ? Texts.T("plugins.filesCount", fileCount) : null,
                    sameName && presentation.PluginId != null ? Texts.T("plugins.pluginIdCompact", presentation.PluginId) : null }.Where(value => value != null));
                content.Identity.IsVisible = identityText.Length > 0;
                content.Identity.Text = identityText;
                ToolTip.SetTip(content.Identity, identityText.Length > 0 ? identityText : null);
                content.Footer.Update(PluginCardStats.Items(presentation, fileCount, PluginActivityTracker.IsBusy(presentation)));
                string fullState = type + (sourceClassifications.Length > 0 ? " · " + sourceClassifications : string.Empty) + " · " + status;
                AutomationProperties.SetName(item, string.Join(". ", new[] { presentation.Name, content.Description.Text, content.Origin.Text, fullState,
                    content.Identity.IsVisible ? content.Identity.Text : null }.Where(value => !string.IsNullOrEmpty(value))));
            }
            string? shownCard = _selectedKey != null && Selected is { } selectedEntry && Included(selectedEntry) ? CardKey(_selectedKey) : null;
            list.SelectedItem = shownCard != null && entries.Any(entry => entry.Key == shownCard) ? _cards.GetValueOrDefault(shownCard) : null;
        }
        finally { _projectingSelection = false; }
        if (focusedItem != null && list.Items.Contains(focusedItem) && !focusedItem.IsFocused) focusedItem.Focus();
        C<ScrollViewer>("PluginListScroll").Offset = listOffset;
        if (Selected == null || !Included(Selected)) _showDetails = false;
        bool showEmpty = _catalog != null && entries.Length == 0;
        C<TextBlock>("PluginsEmpty").IsVisible = showEmpty;
        C<Desktop.Controls.WorkspaceEmptyState>("PluginsEmptyState").IsVisible = showEmpty;
        C<TextBlock>("PluginsEmpty").Text = Texts.T(_catalog?.Entries.Count == 0 ? "plugins.noPlugins" : C<ComboBox>("PluginCategory").SelectedIndex switch
        { 0 => "plugins.emptySources", 1 => "plugins.emptyFilters", _ => "plugins.empty" });
        // The header count, as on the Sources page: every plugin of the profile, whatever the category shows.
        C<TextBlock>("PluginsCount").Text = _catalog == null ? string.Empty : Texts.T("plugins.count", GroupCount);
        C<TextBlock>("PluginsCount").IsVisible = _catalog != null;
        if (SourceContent != null && Filter == SourcesPluginsFilter.Sources)
        { C<TextBlock>("PluginsCount").Text = _sourceCountText; C<TextBlock>("PluginsCount").IsVisible = true; }
        int count = _catalog?.Diagnostics.Count ?? 0;
        C<Expander>("PluginInventoryProblems").IsVisible = count > 0;
        C<Badge>("PluginInventoryBadge").Header = count;
        AutomationProperties.SetName(C<Expander>("PluginInventoryProblems"), Texts.T("plugins.inventoryCount", count));
        C<TextBlock>("PluginInventoryDiagnostics").Text = string.Join("\n", _catalog?.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message + "\n" + diagnostic.FilePath) ?? Array.Empty<string>());
        ApplyPaneVisibility();
        RefreshPluginActions();
    }
    /// <summary>
    /// The Sources list rule for the plugin's described sources: a chip only where a source deviates from the norm
    /// ("Maintenance unverified", "Experimental" in the warning tone), each once. The source status chip is left out: on the
    /// Sources page it means the source's own activation, which the plugin list does not show.
    /// </summary>
    private static void RenderListChips(PluginListContent content, PluginEntryPresentation presentation, bool needsAttention)
    {
        SourceBadge[] deviating = presentation.SourceClassifications
            .SelectMany(classification => SourceWording.Badges(classification, isActive: true, isBuiltIn: false))
            .Where(badge => badge.Deviates && badge.Kind != SourceBadgeKind.Status)
            .OrderBy(badge => badge.Kind)
            .DistinctBy(badge => badge.ChipText).ToArray();
        string signature = (needsAttention ? "A|" : "") + Texts.Language + "|" + string.Join("|", deviating.Select(badge => (badge.Warning ? "!" : "") + badge.ChipText));
        content.Chips.IsVisible = deviating.Length > 0 || needsAttention;
        if (signature == content.ChipSignature) return;
        content.ChipSignature = signature;
        content.Chips.Children.Clear();
        if (needsAttention)
        {
            // A plugin that needs attention is never hidden by the ordering: its card says so.
            Border attention = Desktop.Controls.WorkspaceChips.CreateBadge("PluginListChip", Texts.T("plugins.problem"), compact: true);
            attention.Classes.Add("chip-warning");
            content.Chips.Children.Add(attention);
        }
        foreach (SourceBadge badge in deviating)
        {
            Border chip = Desktop.Controls.WorkspaceChips.CreateBadge("PluginListChip", badge.ChipText, compact: true);
            if (badge.Warning) chip.Classes.Add("chip-warning");
            content.Chips.Children.Add(chip);
        }
    }
    private void OnPluginSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_projectingSelection) return;
        if (C<SelectionList>("PluginCards").SelectedItem is SelectionListItem item &&
            _cards.FirstOrDefault(pair => ReferenceEquals(pair.Value, item)).Key is { } key)
        {
            // The card stands for all files of its plugin: choosing the card again keeps the file already shown.
            if (_selectedKey != null && CardKey(_selectedKey) == key) return;
            SelectCore(key, openDetails: false);
        }
    }
    private void OnPluginListKeyDown(object? sender, KeyEventArgs e)
    {
        if (TryHandlePluginActionKeyDown(e)) return;
        SelectionList list = C<SelectionList>("PluginCards");
        if (e.Key is Key.Enter or Key.Space)
        { if (_selectedKey != null && list.SelectedItem != null && _cards.ContainsKey(CardKey(_selectedKey))) { Select(_selectedKey); e.Handled = true; } return; }
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
        C<ComboBox>("PluginCategory").IsEnabled = !_busy && !IsDiscardConfirmationOpen;
        if (entry == null) { UpdateTestButtons(); RefreshPluginActions(); PresentationChanged?.Invoke(this, EventArgs.Empty); return; }
        PluginEntryPresentation presentation = PluginEntryPresentation.Create(entry,
            _services?.GetProductionWorkerSnapshots?.Invoke(), CurrentDraft, _catalog?.Profile.Revision, sdkMismatch: _services?.HasSdkMismatch);
        C<TextBlock>("PluginHeading").Text = presentation.Name;
        RenderTechnicalIdentity(entry);
        C<TextBlock>("PluginDescription").Text = PluginContentPresentation.DescribeDescription(presentation);
        C<TextBlock>("PluginDescriptionOrigin").Text = PluginContentPresentation.DescribeDescriptionOrigin(presentation.DescriptionOrigin);
        C<TextBlock>("PluginDescriptionOrigin").IsVisible = presentation.HasRetainedDescription || presentation.HasSourceClassifications;
        string? author = presentation.DescriptionMetadata?.Author;
        C<TextBlock>("PluginAuthor").Text = string.IsNullOrWhiteSpace(author) ? string.Empty : Texts.T("plugins.author", author);
        C<TextBlock>("PluginAuthor").IsVisible = !string.IsNullOrWhiteSpace(author);
        C<TextBlock>("PluginType").Text = PluginContentPresentation.DescribeType(presentation.DescriptionMetadata);
        string classifications = PluginContentPresentation.DescribeSourceClassifications(presentation);
        C<Border>("PluginExperimentalChip").IsVisible = presentation.HasSourceClassifications;
        // The warning tone of the Sources "Experimental" chip; the words say it too.
        C<Border>("PluginExperimentalChip").Classes.Set("chip-warning", presentation.HasExperimentalSources);
        TextBlock classificationText = C<TextBlock>("PluginExperimental");
        classificationText.Text = classifications;
        AutomationProperties.SetName(classificationText, classifications);
        List<string> classificationHints =
            PluginContentPresentation.DescribeDetailedSourceClassifications(presentation)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (presentation.HasExperimentalSources)
            classificationHints.Add(Texts.T("plugins.experimentalHint", string.Join(", ", presentation.SourceClassifications
                .Where(source => source.Maturity == SourceWordingMaturity.Experimental).Select(source => source.SourceName))));
        C<TextBlock>("PluginExperimentalHint").IsVisible = classificationHints.Count > 0;
        string classificationHint = string.Join("\n", classificationHints);
        C<TextBlock>("PluginExperimentalHint").Text = classificationHint;
        AutomationProperties.SetName(C<TextBlock>("PluginExperimentalHint"),
            Texts.T("plugins.classificationTitle") + ": " + classificationHint);
        C<Border>("PluginVersionChip").IsVisible = !string.IsNullOrEmpty(presentation.CandidateVersion);
        C<TextBlock>("PluginVersion").Text = presentation.CandidateVersion is { } version ? Texts.T("plugins.fileVersion", version) : string.Empty;
        string? pluginId = presentation.PluginId ?? presentation.DescriptionMetadata?.PluginId;
        C<TextBlock>("PluginCompactId").Text = pluginId == null ? string.Empty : Texts.T("plugins.pluginIdCompact", pluginId);
        _copyablePluginId = pluginId;
        C<Button>("PluginIdChip").IsVisible = pluginId != null;
        // The chip copies the full ID (its text may be trimmed); the tooltip and the accessible name say so and show the ID.
        string copyName = pluginId == null ? string.Empty : Texts.T("plugins.copyPluginId") + ": " + pluginId;
        ToolTip.SetTip(C<Button>("PluginIdChip"), copyName);
        AutomationProperties.SetName(C<Button>("PluginIdChip"), copyName);
        SourceCapabilities capabilities = presentation.Sources.Aggregate(SourceCapabilities.None, (flags, source) => flags | source.Capabilities);
        string functions = string.Join(" · ", new[] { presentation.Sources.Count > 0 ? PluginContentPresentation.DescribeCapabilities(capabilities) : string.Empty,
            string.Join(" · ", presentation.Filters.Select(filter => filter.Name)) }.Where(value => !string.IsNullOrEmpty(value)));
        C<TextBlock>("PluginCapabilities").Text = functions;
        C<TextBlock>("PluginCapabilities").IsVisible = functions.Length > 0;
        C<TextBlock>("PluginStatus").Text = PluginContentPresentation.DescribeStatus(presentation);
        RenderLifecycle(entry, presentation);
        RenderFileChoices(entry);
        // Problems belong beside the normal actions. Paths, fingerprints and full
        // compiler diagnostics remain available in the optional developer details.
        string attention = string.Join("\n", new[] { presentation.HasSdkMismatch ? PluginContentPresentation.SdkMismatchHint() : null,
            presentation.CandidateProblem, presentation.VisibleActivationProblem,
            presentation.HasWorkerProblem ? presentation.Worker?.FailureMessage : null }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
        C<TextBlock>("PluginAttention").Text = attention;
        C<TextBlock>("PluginAttention").IsVisible = attention.Length > 0;
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
        UpdateDraftState(); UpdateTestButtons(); UpdateGrantActions(); RefreshWorkViewPresentation(); RefreshPluginActions();
        C<ComboBox>("PluginCategory").IsEnabled = !_busy && !IsDiscardConfirmationOpen;
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }
    private void UpdateDraftState()
    {
        PluginConfigurationDraft? draft = CurrentDraft;
        bool stale = draft != null && draft.ExpectedRevision != _catalog?.Profile.Revision;
        if (_savedNoticeKey != _selectedKey || draft?.Dirty == true) _savedNoticeKey = null;
        C<TextBlock>("PluginDraftStatus").Text = Texts.T(stale ? "plugins.stale" : draft?.Dirty == true ? "plugins.draft"
            : _savedNoticeKey != null ? "plugins.savedNextStart" : "plugins.settingsHint");
        C<TextBlock>("PluginDraftStatus").Classes.Set("workspace-error", stale);
        TextBlock draftSummary = C<TextBlock>("PluginDraftSummary");
        draftSummary.Text = C<TextBlock>("PluginDraftStatus").Text;
        draftSummary.IsVisible = stale || draft?.Dirty == true;
        draftSummary.Classes.Set("workspace-error", stale);
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
        bool sources = SourceContent != null && Filter == SourcesPluginsFilter.Sources;
        C<ContentControl>("SourceContentHost").IsVisible = sources && !ShowingSourcePluginDetails;
        if (sources)
        {
            C<Grid>("PluginListPane").IsVisible = false;
            C<ScrollViewer>("PluginDetailsPane").IsVisible = hasSelection && ShowingSourcePluginDetails;
        }
        Grid.SetColumn(C<ScrollViewer>("PluginDetailsPane"), sources ? 0 : 1);
        Grid.SetColumnSpan(C<ScrollViewer>("PluginDetailsPane"), sources ? 2 : 1);
        C<Button>("PluginBack").IsVisible = _narrow || ShowingSourcePluginDetails;
        if (ShowingSourcePluginDetails) CommandBindings.ApplyLabeled(C<Button>("PluginBack"), "sources.back", Texts.T("sources.backToSource"));
        else CommandBindings.ApplyLabeled(C<Button>("PluginBack"), "plugins.back", Texts.T("plugins.back"));
    }
    private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        Control? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        bool ContainsFocus(Control pane) => focused != null &&
            (ReferenceEquals(focused, pane) || focused.GetVisualAncestors().Contains(pane));
        bool listHadFocus = ContainsFocus(C<Grid>("PluginListPane"));
        bool detailsHadFocus = ContainsFocus(C<ScrollViewer>("PluginDetailsPane"));
        // The Sources page's switch point: the detail needs 520 DIP beside the 280 DIP list, gutters and panel padding.
        bool narrow = Bounds.Width - 40 - 280 - 14 - 32 < 520;
        if (narrow && !_narrow)
        {
            // The pane the user is working in takes precedence over a previous
            // explicit-open flag. A breakpoint must never hide that input.
            if (detailsHadFocus) _showDetails = true;
            else if (listHadFocus) _showDetails = false;
        }
        _narrow = narrow;
        // The page head places itself (PageHeader): actions beside the title while they fit, under it otherwise and below 900 dip.
        C<Grid>("PluginSplit").ColumnDefinitions[0].Width = new GridLength(_narrow ? 0 : 280);
        C<Grid>("PluginSplit").ColumnSpacing = _narrow ? 0 : 16;
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
    { if (ShowingSourcePluginDetails) { ReturnToSourceOverview(); return; } _showDetails = false; ApplyPaneVisibility(); if (_selectedKey != null && _cards.TryGetValue(CardKey(_selectedKey), out SelectionListItem? card) && C<SelectionList>("PluginCards").Items.Contains(card)) { card.BringIntoView(); card.Focus(); } else FocusListOrFilter(); }
    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        ClosePluginActionMenu(); CancelDiscardConfirmation(); _showDetails = false;
        RenderList(); RenderDetails();
        if (Filter == SourcesPluginsFilter.Sources) SourceOverviewRequested?.Invoke(this, EventArgs.Empty);
    }
    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnFolderClick(object? sender, RoutedEventArgs e)
    { try { if (_services != null) await ExternalLauncher.OpenAsync(_services.PluginDirectory); } catch (Exception error) { if (!_ended) ErrorFailure("plugins.error.folder", error); } }
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
        catch (Exception exception) { if (Current(generation)) ErrorFailure("plugins.error.change", exception); }
        finally { FinishOperation(generation); }
    }
    private void OnDiscardClick(object? sender, RoutedEventArgs e)
    { RequestDiscardSelectedDraft(); }
    // Existing raw callers (including field-validation and probe failures) keep
    // their original text. Null retains the existing explicit clear semantics.
    private void Error(string? text)
    {
        _errorState = text is null ? null : PluginErrorState.Raw(text);
        RenderError();
    }
    // An exception from a host operation: a localized sentence with the next step; the original text is only the detail.
    private void ErrorFailure(string key, Exception exception)
    {
        _errorState = PluginErrorState.Failure(key, exception.Message);
        RenderError();
    }
    private void ErrorFailure(string key, string? detail)
    {
        _errorState = PluginErrorState.Failure(key, detail);
        RenderError();
    }
    private void ErrorLocalized(string key, params object?[] arguments)
    {
        _errorState = PluginErrorState.Localized(key, arguments);
        RenderError();
    }
    private void RenderError()
    {
        string? text = _errorState?.Render();
        TextBlock summary = C<TextBlock>("PluginPageError");
        summary.Text = text;
        string? detail = _errorState?.Detail;
        ToolTip.SetTip(summary, detail);
        AutomationProperties.SetHelpText(summary, detail ?? string.Empty);
        summary.Focusable = !string.IsNullOrEmpty(text);
        AutomationProperties.SetName(summary, text);
        C<MeowChan.Desktop.Controls.WorkspaceBanner>("PluginErrorBanner").IsVisible = !string.IsNullOrEmpty(text);
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
    internal void Deactivate() { CloseBundledRepairPreview(); ClosePluginActionMenu(); CancelDiscardConfirmation(); _statusTimer.Stop(); ++_refreshGeneration; CancelOperation(); if (!_ended) RenderDetails(); }
    internal void EndLifetime()
    { if (_ended) return; CloseBundledRepairPreview(); ClosePluginActionMenu(); CancelDiscardConfirmation(); _ended = true; IsEnabled = false; _statusTimer.Stop(); _statusTimer.Tick -= OnStatusTick; Texts.Changed -= OnLanguageChanged; _lifetime.Cancel(); CancelOperation(); _lifetime.Dispose(); }
}
