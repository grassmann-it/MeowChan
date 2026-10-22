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
    private readonly Dictionary<string, Button> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginSettingsEditor> _editors = new(StringComparer.Ordinal);
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
        KeyDown += (_, args) =>
        {
            if (args.Handled || args.Key != Key.Escape) return;
            if (_busy) { OnCancelClick(this, new RoutedEventArgs()); args.Handled = true; }
            else if (_narrow && _showDetails) { OnBackClick(this, new RoutedEventArgs()); args.Handled = true; }
        };
        Texts.Changed += OnLanguageChanged;
        _statusTimer.Tick += OnStatusTick;
        Localize();
    }
    internal void Attach(PluginManagementServices services) { _services = services; C<TextBlock>("PluginDirectory").Text = services.PluginDirectory; }
    internal async Task ActivateAsync(string? sourceId = null)
    {
        if (_ended) return;
        _statusTimer.Start();
        await RefreshAsync();
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
            ("PluginTestHint","plugins.testHint"),("PluginEditorHint","plugins.editorHint")
        }) C<TextBlock>(name).Text = name == "PluginEditorHint" ? Texts.T(key, string.Empty) : Texts.T(key);
        foreach ((string name, string key) in new[]
        {
            ("PluginsRefresh","plugins.refresh"),("PluginsFolder","plugins.folder"),("PluginBack","plugins.back"),
            ("PluginSelectCandidate","plugins.selectCandidate"),("PluginRollback","plugins.rollback"),("PluginWatches","plugins.watches"),
            ("PluginSave","plugins.save"),("PluginDiscard","plugins.discard"),("PluginEdit","plugins.edit"),
            ("PluginDefaultEditor","plugins.defaultEditor"),("PluginBuild","plugins.build"),("PluginCancel","plugins.cancel"),
            ("PluginProbe","plugins.probe"),("PluginEditorSave","plugins.saveEditor")
        })
        {
            Button button = C<Button>(name);
            if (key is "plugins.refresh" or "plugins.folder" or "plugins.back" or "plugins.save" or "plugins.discard" or "plugins.edit" or "plugins.build" or "plugins.cancel" or "plugins.probe")
                CommandBindings.ApplyLabeled(button, key, Texts.T(key));
            else { button.Content = Texts.T(key); AutomationProperties.SetName(button, Texts.T(key)); }
        }
        C<Expander>("PluginInventoryProblems").Header = Texts.T("plugins.inventoryProblems");
        C<Expander>("PluginInfoExpander").Header = Texts.T("plugins.information");
        C<Expander>("PluginGrantExpander").Header = Texts.T("plugins.grants");
        C<Expander>("PluginSourceExpander").Header = Texts.T("plugins.sourceTools");
        C<Expander>("PluginEditorExpander").Header = Texts.T("plugins.editorConfiguration");
        C<TextBox>("PluginProbeUrl").PlaceholderText = Texts.T("plugins.probeUrl");
        AutomationProperties.SetName(C<TextBox>("PluginProbeUrl"), Texts.T("plugins.probeUrl"));
        AutomationProperties.SetName(C<TextBox>("PluginEditorExecutable"), Texts.T("plugins.editorExecutable"));
        AutomationProperties.SetName(C<TextBox>("PluginEditorArguments"), Texts.T("plugins.editorArguments"));
        SetTestStatus(_testStatusKey, _testStatusArguments);
        if (Selected is { } localizedEntry) { BuildSettings(localizedEntry); RenderInformation(localizedEntry); RenderGrants(localizedEntry); }
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
            foreach (string key in _drafts.Where(pair => !pair.Value.Dirty && pair.Value.ExpectedRevision != catalog.Profile.Revision).Select(pair => pair.Key).ToArray())
            {
                _drafts.Remove(key);
                if (key == _selectedKey) _renderedKey = null;
            }
            RenderList(); RenderDetails();
        }
        catch (OperationCanceledException) when (_ended) { }
        catch (Exception exception) { if (!_ended) Error(exception.Message); }
    }
    private bool Included(PluginCatalogEntry entry) => C<ComboBox>("PluginCategory").SelectedIndex switch { 1 => entry.IsSource, 2 => entry.IsFilter, _ => true };
    private void RenderList()
    {
        StackPanel list = C<StackPanel>("PluginCards");
        PluginCatalogEntry[] entries = _catalog?.Entries.Where(Included).ToArray() ?? Array.Empty<PluginCatalogEntry>();
        foreach (string removed in _cards.Keys.Where(key => !entries.Any(entry => entry.Key == key)).ToArray())
        { list.Children.Remove(_cards[removed]); _cards.Remove(removed); }
        foreach (PluginCatalogEntry entry in entries)
        {
            if (!_cards.TryGetValue(entry.Key, out Button? button))
            {
                button = new Button { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Padding = new Thickness(12) };
                string key = entry.Key; button.Click += (_, _) => Select(key);
                _cards.Add(key, button); list.Children.Add(button);
            }
            button.Content = new TextBlock { Text = entry.Name + "\n" + EntryState(entry), TextWrapping = TextWrapping.Wrap };
            button.Classes.Set("selected", entry.Key == _selectedKey);
            AutomationProperties.SetName(button, entry.Name + ". " + EntryState(entry));
        }
        C<TextBlock>("PluginsEmpty").IsVisible = entries.Length == 0;
        C<Expander>("PluginInventoryProblems").IsVisible = _catalog?.Diagnostics.Count > 0;
        C<TextBlock>("PluginInventoryDiagnostics").Text = string.Join("\n", _catalog?.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message + "\n" + diagnostic.FilePath) ?? Array.Empty<string>());
        // Filtering never silently selects another component.
        ApplyPaneVisibility();
    }
    private static string EntryState(PluginCatalogEntry entry)
    {
        if (entry.Activation is not { } state) return Texts.T("plugins.notLoaded");
        if (entry.Candidate?.Problem != null || state.Problem != null && state.SelectedCandidateId == entry.CandidateId) return Texts.T("plugins.problem");
        if (state.Active?.CandidateId == entry.CandidateId) return Texts.T(state.Enabled ? "plugins.active" : "plugins.disabled");
        if (state.Pending?.CandidateId == entry.CandidateId) return Texts.T("plugins.pending");
        return Texts.T(state.SelectedCandidateId == entry.CandidateId ? "plugins.unavailable" : "plugins.notSelected");
    }
    internal void Select(string key)
    {
        if (_ended || _catalog?.Entries.Any(entry => entry.Key == key) != true) return;
        if (_selectedKey != key) CancelOperation();
        _selectedKey = key; _showDetails = true;
        Error(null); RenderList(); RenderDetails();
        if (_narrow) C<Button>("PluginBack").Focus();
    }
    private void RenderDetails()
    {
        PluginCatalogEntry? entry = Selected;
        ApplyPaneVisibility();
        if (entry == null) return;
        PluginActivationSnapshot? activation = entry.Activation;
        C<TextBlock>("PluginHeading").Text = entry.Name;
        C<TextBlock>("PluginIdentity").Text = entry.Description is { } description
            ? Texts.T("plugins.identity", description.PluginId, description.PluginVersion, description.SdkVersion) : entry.LoaderPath ?? entry.Key;
        C<TextBlock>("PluginDescription").Text = entry.Description?.Description;
        PluginHost.Contracts.PluginWorkerSnapshot? workerState = _services?.GetWorkerSnapshots?.Invoke()
            .FirstOrDefault(worker => worker.Identity.ExecutionRevision == activation?.Active?.ExecutionRevision);
        C<TextBlock>("PluginStatus").Text = string.Join("\n", new[]
        {
            EntryState(entry), activation?.Problem, entry.Candidate?.Problem,
            workerState != null ? Texts.T("plugins.workerState", Texts.T("plugins.worker." + workerState.State), workerState.FailureMessage ?? string.Empty) : null,
            activation?.Active != null ? Texts.T("plugins.activeRevision", RevisionLabel(activation.Active)) : null,
            activation?.Pending != null ? Texts.T("plugins.pendingRevision", RevisionLabel(activation.Pending)) : null,
            activation?.Pinned != null ? Texts.T("plugins.pinnedRevision", RevisionLabel(activation.Pinned)) : null,
            activation?.LastGood != null ? Texts.T("plugins.rollbackTarget", RevisionLabel(activation.LastGood)) : null,
            activation?.Candidates.Count(candidate => candidate.IsPresent) > 1 ? Texts.T("plugins.candidateConflict") : null,
            entry.Candidate?.IsPresent == false ? Texts.T("plugins.missing") : null,
            entry.Candidate?.Problem != null && entry.Description != null ? Texts.T("plugins.lastDescription") : null
        }.Where(value => !string.IsNullOrEmpty(value)));
        C<TextBlock>("PluginCandidateDiagnostics").Text = string.Join("\n", entry.Diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message + "\n" + diagnostic.FilePath));
        C<TextBlock>("PluginCandidateDiagnostics").IsVisible = entry.Diagnostics.Count > 0;
        C<Button>("PluginEnabled").Content = Texts.T(activation?.Enabled == true ? "plugins.disable" : "plugins.enable");
        C<Button>("PluginEnabled").IsEnabled = !_busy && activation != null;
        C<Button>("PluginSelectCandidate").IsEnabled = !_busy && entry.Candidate?.Artifact != null && entry.Candidate.Problem == null;
        C<Button>("PluginRollback").IsEnabled = !_busy && activation?.LastGood != null;
        C<Button>("PluginWatches").IsVisible = entry.Description?.Sources.Count > 0;
        C<Expander>("PluginSourceExpander").IsVisible = entry.SourcePath != null;
        C<TextBlock>("PluginSourcePath").Text = entry.SourcePath;
        if (_renderedKey != entry.Key)
        {
            _renderedKey = entry.Key;
            BuildSettings(entry); RenderInformation(entry); RenderGrants(entry);
            PluginEditorOptions options = GetEditorOptions?.Invoke() ?? new(null, new[] { "{file}" });
            C<TextBox>("PluginEditorExecutable").Text = options.Executable;
            C<TextBox>("PluginEditorArguments").Text = string.Join(Environment.NewLine, options.Arguments);
            SetTestStatus(null);
            C<TextBox>("PluginProbeResult").Text = string.Empty;
            C<StackPanel>("PluginBuildDiagnostics").Children.Clear();
        }
        if (entry.Description != null && CurrentDraft == null) { BuildSettings(entry); RenderInformation(entry); }
        PluginGrantSnapshot[] currentGrants = _catalog!.Profile.Grants.Where(grant => grant.PluginId == entry.PluginId).ToArray();
        if (_renderedGrantsKey != entry.Key || !_displayedGrants.SequenceEqual(currentGrants)) RenderGrants(entry);
        UpdateDraftState(); UpdateTestButtons();
    }
    private void BuildSettings(PluginCatalogEntry entry)
    {
        StackPanel settings = C<StackPanel>("PluginSettings"); settings.Children.Clear(); _editors.Clear();
        if (entry.Description == null || entry.CandidateId == null) return;
        if (!_drafts.TryGetValue(entry.Key, out PluginConfigurationDraft? draft))
        {
            draft = new(entry, _catalog!.Profile.Revision); _drafts[entry.Key] = draft;
            draft.Plugin.Changed += UpdateDraftState;
            foreach (PluginSettingsDraft source in draft.Sources.Values) source.Changed += UpdateDraftState;
        }
        if (draft.NeedsMigrationAcknowledgement)
        {
            settings.Children.Add(Label(Texts.T("plugins.removedSettings") + "\n" + string.Join("\n", draft.RemovedSettings.Select(issue => issue.Path))));
            Button acknowledge = new() { Content = Texts.T("plugins.acceptSchema") };
            acknowledge.Click += (_, _) => { draft.AcknowledgeMigration(); BuildSettings(entry); UpdateDraftState(); };
            settings.Children.Add(acknowledge);
        }
        AddSettings(Texts.T("plugins.pluginSettings"), string.Empty, draft.Plugin);
        foreach ((string sourceId, PluginSettingsDraft source) in draft.Sources)
            AddSettings(Texts.T("plugins.sourceSettings", draft.Description.Sources.First(value => value.SourceId == sourceId).Name), sourceId, source);
    }
    private void AddSettings(string label, string scope, PluginSettingsDraft draft)
    {
        PluginSettingsEditor editor = new(draft); _editors.Add(scope, editor);
        C<StackPanel>("PluginSettings").Children.Add(new Expander { Header = label, Content = editor, IsExpanded = draft.Schema.Fields.Count > 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch });
    }
    private void UpdateDraftState()
    {
        PluginConfigurationDraft? draft = CurrentDraft;
        bool stale = draft != null && draft.ExpectedRevision != _catalog?.Profile.Revision;
        C<TextBlock>("PluginDraftStatus").Text = Texts.T(stale ? "plugins.stale" : draft?.Dirty == true ? "plugins.draft" : "plugins.settingsHint");
        C<Button>("PluginSave").IsEnabled = !_busy && draft?.Dirty == true && !stale;
        C<Button>("PluginDiscard").IsEnabled = !_busy && draft != null;
    }
    private void RenderInformation(PluginCatalogEntry entry)
    {
        StackPanel panel = C<StackPanel>("PluginInformation"); panel.Children.Clear();
        if (entry.Description == null) return;
        if (entry.Activation?.Active is { } active) panel.Children.Add(Label(Texts.T("plugins.activeRevision", active.ExecutionRevision)));
        if (entry.LoaderPath != null) panel.Children.Add(Label(entry.LoaderPath));
        foreach (SourceDescription source in entry.Description.Sources)
            panel.Children.Add(Label(source.Name + ": " + source.Capabilities + "\n" + string.Join(", ", source.HttpHosts.Select(host => host.Host + " (" + host.Purpose + ")"))));
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
            panel.Children.Add(Label(grant.FilterKey + " · " + grant.Scope.SourceId + (grant.Scope.BoardId != null ? "/" + grant.Scope.BoardId : string.Empty)
                + (grant.Scope.RuleId != null ? " · " + grant.Scope.RuleId : string.Empty) + " · " + grant.Access));
            Button revoke = new() { Content = Texts.T("plugins.revoke"), IsEnabled = !_busy && grant.Access != FilterDataAccess.None };
            revoke.Click += async (_, _) => await CommandAsync((services, revision, token) => services.ProfileStore.SetGrantAsync(grant.PluginId, grant.FilterKey, grant.Scope, FilterDataAccess.None, revision, token));
            panel.Children.Add(revoke);
        }
        C<Expander>("PluginGrantExpander").IsVisible = entry.Description?.Filters.Count > 0;
    }
    private static string RevisionLabel(PluginReleaseSnapshot release) => release.Description.PluginVersion + " · " + release.ExecutionRevision[..Math.Min(12, release.ExecutionRevision.Length)];
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private void ApplyPaneVisibility()
    {
        bool hasSelection = Selected != null && Included(Selected);
        C<Grid>("PluginListPane").IsVisible = !_narrow || !_showDetails || !hasSelection;
        C<ScrollViewer>("PluginDetailsPane").IsVisible = hasSelection && (!_narrow || _showDetails);
        C<Button>("PluginBack").IsVisible = _narrow;
    }
    private void OnPageSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _narrow = Bounds.Width < 850;
        C<Grid>("PluginSplit").ColumnDefinitions[0].Width = new GridLength(_narrow ? 0 : 240);
        C<Grid>("PluginSplit").ColumnSpacing = _narrow ? 0 : 14;
        Grid.SetColumn(C<Grid>("PluginListPane"), _narrow ? 1 : 0); ApplyPaneVisibility();
    }
    private void OnBackClick(object? sender, RoutedEventArgs e)
    { _showDetails = false; ApplyPaneVisibility(); if (_selectedKey != null && _cards.TryGetValue(_selectedKey, out Button? card)) card.Focus(); }
    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e) { if (!_refreshing) { _showDetails = false; RenderList(); } }
    private async void OnRefreshClick(object? sender, RoutedEventArgs e) => await RefreshAsync();
    private async void OnFolderClick(object? sender, RoutedEventArgs e)
    { try { if (_services != null) await ExternalLauncher.OpenAsync(_services.PluginDirectory); } catch (Exception error) { Error(error.Message); } }
    private void OnWatchesClick(object? sender, RoutedEventArgs e)
    { if (Selected?.Description?.Sources.FirstOrDefault() is { } source) OpenSourceWatches?.Invoke(source.SourceId); }
    private async void OnEnabledClick(object? sender, RoutedEventArgs e)
    { if (Selected?.Activation is { } state) await CommandAsync((services, revision, token) => services.ProfileStore.SetEnabledAsync(state.PluginId, !state.Enabled, revision, token)); }
    private async void OnSelectCandidateClick(object? sender, RoutedEventArgs e)
    { if (Selected is { PluginId: { } plugin, CandidateId: { } candidate }) await CommandAsync((services, revision, token) => services.ProfileStore.SelectCandidateAsync(plugin, candidate, revision, token)); }
    private async void OnRollbackClick(object? sender, RoutedEventArgs e)
    { if (Selected?.PluginId is { } plugin) await CommandAsync((services, revision, token) => services.ProfileStore.SelectRollbackAsync(plugin, revision, token)); }
    private async Task CommandAsync(Func<PluginManagementServices, long, CancellationToken, Task<PluginProfileSnapshot>> command)
    {
        if (_ended || _busy || _services == null || _catalog == null) return;
        (long generation, CancellationToken token) = BeginOperation();
        try
        {
            await command(_services, _catalog.Profile.Revision, token);
            if (!Current(generation)) return;
            await RefreshAsync(); RenderGrants(Selected!);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (Current(generation)) Error(exception.Message); }
        finally { FinishOperation(generation); }
    }
    private void OnDiscardClick(object? sender, RoutedEventArgs e)
    { if (_selectedKey == null || _busy) return; _drafts.Remove(_selectedKey); _renderedKey = null; Error(null); RenderDetails(); }
    private void Error(string? text)
    { C<TextBlock>("PluginPageError").Text = text; C<Ursa.Controls.Banner>("PluginErrorBanner").IsVisible = !string.IsNullOrEmpty(text); }
    private (long Generation, CancellationToken Token) BeginOperation()
    {
        CancelOperation(); _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _busy = true; BusyChanged?.Invoke(this, EventArgs.Empty); Error(null); RenderDetails();
        return (_operationGeneration, _operation.Token);
    }
    private bool Current(long generation) => !_ended && generation == _operationGeneration && _operation?.IsCancellationRequested == false;
    private void FinishOperation(long generation)
    { if (generation != _operationGeneration) return; _operation?.Dispose(); _operation = null; _busy = false; if (!_ended) { RenderDetails(); BusyChanged?.Invoke(this, EventArgs.Empty); } }
    private void CancelOperation()
    { ++_operationGeneration; _operation?.Cancel(); _operation?.Dispose(); _operation = null; _busy = false; }
    private async void OnStatusTick(object? sender, EventArgs e)
    {
        if (_ended || _statusReadPending || _busy || !IsEffectivelyVisible) return;
        _statusReadPending = true;
        try { await RefreshAsync(); } finally { _statusReadPending = false; }
    }
    internal void Deactivate() { _statusTimer.Stop(); ++_refreshGeneration; CancelOperation(); if (!_ended) RenderDetails(); }
    internal void EndLifetime()
    { if (_ended) return; _ended = true; IsEnabled = false; _statusTimer.Stop(); _statusTimer.Tick -= OnStatusTick; Texts.Changed -= OnLanguageChanged; _lifetime.Cancel(); CancelOperation(); _lifetime.Dispose(); }
}
