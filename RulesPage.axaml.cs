using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Commands;
using MeowChan.Rules;

namespace MeowChan;

public sealed record RuleScopeChoice(MediaRuleScope Scope, string Label);

public partial class RulesPage : UserControl, INotifyPropertyChanged
{
    private const decimal BytesPerMegabyte = 1_000_000m;
    private readonly Func<MediaRuleSet> _getRules;
    private readonly Func<IEnumerable<MediaRule>, string, Task<MediaRuleSet>> _saveRules;
    private readonly Dictionary<MediaRuleScope, RuleDraft> _drafts = new Dictionary<MediaRuleScope, RuleDraft>();
    private MediaRuleSet _confirmed = MediaRuleSet.Default;
    private RuleDraft _draft = new RuleDraft(MediaRuleScope.Global, MediaRuleSet.Default);
    private RuleScopeChoice? _selectedScope;
    private bool _refreshingScopeChoices;
    private bool _refreshingChoiceLabels;
    private EffectiveMediaRule? _preview;
    private bool _hasConfirmed;
    private bool _available;
    private bool _busy;
    private bool _ended;
    private string? _validationKey;
    private string? _operationErrorKey;
    private string? _operationErrorDetail;
    private string? _statusKey;
    private int _newScopeKind;
    private string _newSourceId = string.Empty;
    private string _newBoardId = string.Empty;
    private string _newThreadId = string.Empty;

    public new event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? BusyChanged;

    public RulesPage() : this(() => MediaRuleSet.Default,
        (_, _) => Task.FromException<MediaRuleSet>(new InvalidOperationException("No rule store is attached."))) { }

    internal RulesPage(Func<MediaRuleSet> getRules, Func<IEnumerable<MediaRule>, string, Task<MediaRuleSet>> saveRules)
    {
        _getRules = getRules ?? throw new ArgumentNullException(nameof(getRules));
        _saveRules = saveRules ?? throw new ArgumentNullException(nameof(saveRules));
        InitializeComponent();
        InitializeRuleSuggestions();
        _drafts.Add(_draft.Scope, _draft);
        RuleExtensionsInput.Edited += DraftChanged;
        _discardConfirmation = new(DiscardRuleButton);
        KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key != Avalonia.Input.Key.Escape || e.KeyModifiers != Avalonia.Input.KeyModifiers.None
                || Desktop.Commands.ShortcutService.IsCapturing) return;
            if (e.Source is Avalonia.Visual source && source.GetSelfAndVisualAncestors().Any(v =>
                v is ComboBox { IsDropDownOpen: true } or Ursa.Controls.AutoCompleteBox)) return;
            if (TryDismissRuleDecision()) e.Handled = true;
        };
        RefreshChoiceLabels();
        RefreshConfirmedRules();
        DataContext = this;
        ApplyCommandIcons();
        SynchronizeModeSelections();
        Texts.Changed += OnLanguageChanged;
    }

    public bool IsAvailable
    {
        get => _available;
        set
        {
            if (_ended || _available == value) return;
            _available = value;
            if (value) { RefreshConfirmedRules(); _ = LoadTemplatesAsync(); }
            RaiseAll();
        }
    }
    public bool IsBusy => _busy;
    public bool IsEditable => !_ended && _available && _hasConfirmed && !_busy;
    public bool CanSave => IsEditable && _draft.Dirty && !_draft.Stale && _preview != null;
    public bool HasPreview => _preview != null && !_draft.Stale;
    public bool HasValidationError => _draft.Stale || _validationKey != null;
    public string? ValidationMessage => _draft.Stale ? Texts.T("rules.stale") : _validationKey == null ? null : Texts.T(_validationKey);
    public string? ErrorMessage => _operationErrorKey == null ? null : Texts.T(_operationErrorKey, _operationErrorDetail);
    public bool HasError => ErrorMessage != null;
    public bool HasStatus => !HasError;
    public string StatusMessage => Texts.T(!_available || !_hasConfirmed ? "rules.unavailable" : _busy ? "rules.saving"
        : _draft.Stale ? "rules.stale" : _statusKey ?? (_draft.Dirty ? "rules.unsaved" :
            FindRule(_confirmed, _draft.Scope) == null ? "rules.inherited" : "rules.confirmed"));

    public IReadOnlyList<RuleScopeChoice> ScopeChoices { get; private set; } = Array.Empty<RuleScopeChoice>();
    public IReadOnlyList<string> ScopeKindChoices { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> TypesModeChoices { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<string> SizeModeChoices { get; private set; } = Array.Empty<string>();
    public RuleScopeChoice? SelectedScopeChoice
    {
        get => _selectedScope;
        set
        {
            if (_refreshingScopeChoices || ReferenceEquals(_selectedScope, value)) return;
            _selectedScope = value;
            Raise(nameof(SelectedScopeChoice));
        }
    }

    public int NewScopeKind
    {
        get => _newScopeKind;
        set { if (_refreshingChoiceLabels || value < 0 || value > 3 || _newScopeKind == value) return; _newScopeKind = value; RaiseAll(); }
    }
    public string NewSourceId { get => _newSourceId; set { _newSourceId = value ?? string.Empty; Raise(nameof(NewSourceId)); } }
    public string NewBoardId { get => _newBoardId; set { _newBoardId = value ?? string.Empty; Raise(nameof(NewBoardId)); } }
    public string NewThreadId { get => _newThreadId; set { _newThreadId = value ?? string.Empty; Raise(nameof(NewThreadId)); } }
    public bool NeedsSource => NewScopeKind >= 1;
    public bool NeedsBoard => NewScopeKind >= 2;
    public bool NeedsThread => NewScopeKind >= 3;
    public bool RuleEnabled { get => _draft.Enabled; set { if (_draft.Enabled == value) return; _draft.Enabled = value; DraftChanged(); } }
    public int TypesMode
    {
        get => (int)_draft.TypesMode;
        set
        {
            if (_refreshingChoiceLabels || value < 0 || value > 2 || TypesMode == value) return;
            _draft.TypesMode = (MediaTypeSelectionMode)value;
            DraftChanged();
        }
    }
    public string ExtensionsText
    {
        get => string.Join(", ", _draft.ExtensionInput.Tags.Concat(string.IsNullOrEmpty(_draft.ExtensionInput.Rest) ? Array.Empty<string>() : new[] { _draft.ExtensionInput.Rest }));
        set { string text = value ?? string.Empty; if (ExtensionsText == text) return; _draft.ExtensionInput.Tags.Clear(); _draft.ExtensionInput.Rest = text; DraftChanged(); }
    }
    public int SizeMode
    {
        get => (int)_draft.SizeMode;
        set
        {
            if (_refreshingChoiceLabels || value < 0 || value > 2 || SizeMode == value) return;
            _draft.SizeMode = (MediaSizeLimitMode)value;
            DraftChanged();
        }
    }
    public string MegabytesText
    {
        get => _draft.Megabytes;
        set
        {
            string text = value ?? string.Empty;
            if (_draft.Megabytes == text) return;
            _draft.Megabytes = text;
            _draft.NumberCulture = Texts.Culture;
            DraftChanged();
        }
    }
    public string ExtensionsError => _draft.TypesMode == MediaTypeSelectionMode.Selected && _validationKey == "rules.invalidTypes" ? Texts.T("rules.invalidTypes") : string.Empty;
    public bool HasTypeSelection => _draft.TypesMode == MediaTypeSelectionMode.Selected;
    public bool HasSizeLimit => _draft.SizeMode == MediaSizeLimitMode.Limited;

    public string Title => Texts.T("nav.rules");
    public string Subtitle => Texts.T("rules.subtitle");
    public string SavedScopesLabel => Texts.T("rules.savedScopes");
    public string ScopeCountLabel => Texts.T("rules.changedScopes", _confirmed.Rules.Count);
    public string OpenLabel => Texts.T("rules.open");
    public string NewScopeLabel => Texts.T("rules.newScope");
    public string ScopeKindLabel => Texts.T("rules.scopeKind");
    public string SourceIdLabel => Texts.T("rules.sourceId");
    public string BoardIdLabel => Texts.T("rules.boardId");
    public string ThreadIdLabel => Texts.T("rules.threadId");
    public string IdentityHint => Texts.T("rules.identityHint");
    public string EditingLabel => Texts.T("rules.editing", FormatScope(_draft.Scope));
    public string EnabledLabel => Texts.T("rules.enabled");
    public string EnabledHint => Texts.T("rules.enabledHint");
    public string TypesLabel => Texts.T("rules.types");
    public string ExtensionsLabel => Texts.T("rules.extensions");
    public string ExtensionsHint => Texts.T("rules.extensionsHint");
    public string SizeLabel => Texts.T("rules.size");
    public string MegabytesLabel => Texts.T("rules.megabytes");
    public string MegabytesHint => Texts.T("rules.megabytesHint");
    public string FutureHint => Texts.T("rules.futureHint");
    public string UnknownHint => Texts.T("rules.unknownHint");
    public string PreviewTitle => Texts.T("r08d.rules.preview");
    public string DiscardLabel => Texts.T("rules.discard");
    public string SaveLabel => Texts.T("rules.save");
    public string EffectiveTypesLabel => _preview == null ? string.Empty : _preview.Types.Mode == MediaTypeSelectionMode.All
        ? Texts.T("rules.allTypes") : _preview.Types.Extensions.Count == 0 ? Texts.T("rules.noMedia") : string.Join(", ", _preview.Types.Extensions);
    public string EffectiveSizeLabel => _preview == null ? string.Empty : _preview.SizeLimit.Mode == MediaSizeLimitMode.Unlimited
        ? Texts.T("rules.unlimited") : Texts.T("rules.sizeValue", FormatMegabytes(_preview.SizeLimit.Bytes!.Value, Texts.Culture));
    public string TypesOriginLabel => _preview == null ? string.Empty : Texts.T("rules.origin", FormatOrigin(_preview.TypesOrigin));
    public string SizeOriginLabel => _preview == null ? string.Empty : Texts.T("rules.origin", FormatOrigin(_preview.SizeLimitOrigin));

    public MediaRuleScope ActiveScope => _draft.Scope;
    public MediaRuleScope? ParentScope => _draft.Scope.Kind switch
    {
        MediaRuleScopeKind.Thread => MediaRuleScope.ForBoard(_draft.Scope.Source!, _draft.Scope.Board!),
        MediaRuleScopeKind.Board => MediaRuleScope.ForSource(_draft.Scope.Source!),
        MediaRuleScopeKind.Source => MediaRuleScope.Global,
        _ => null
    };
    public bool CanOpenParent => IsEditable && ParentScope != null;
    public bool CanOpenTypesOrigin => IsEditable && _preview?.TypesOrigin.Scope != null;
    public bool CanOpenSizeOrigin => IsEditable && _preview?.SizeLimitOrigin.Scope != null;
    public string ParentScopeLabel => CommandCatalog.Label("rules.open-parent")
        + (ParentScope is MediaRuleScope parent ? " · " + FormatScope(parent) : string.Empty);
    public string OpenTypesOriginLabel => OriginActionLabel(TypesLabel, _preview?.TypesOrigin.Scope);
    public string OpenSizeOriginLabel => OriginActionLabel(SizeLabel, _preview?.SizeLimitOrigin.Scope);

    private static string OriginActionLabel(string field, MediaRuleScope? scope) =>
        CommandCatalog.Label("rules.open-origin") + " · " + field + " · "
        + (scope == null ? Texts.T("rules.builtin") : FormatScope(scope));

    private void OnOpenParentScope(object? sender, RoutedEventArgs e)
    {
        if (CanOpenParent && ParentScope is MediaRuleScope scope) OpenNavigatedScope(scope);
    }
    private void OnOpenTypesOrigin(object? sender, RoutedEventArgs e)
    {
        if (CanOpenTypesOrigin && _preview?.TypesOrigin.Scope is MediaRuleScope scope) OpenNavigatedScope(scope);
    }
    private void OnOpenSizeOrigin(object? sender, RoutedEventArgs e)
    {
        if (CanOpenSizeOrigin && _preview?.SizeLimitOrigin.Scope is MediaRuleScope scope) OpenNavigatedScope(scope);
    }

    private void OpenNavigatedScope(MediaRuleScope scope)
    {
        EditScope(scope);
        if (!IsEditable || _draft.Scope != scope) return;
        RuleEnabledInput.Focus();
        RuleEnabledInput.BringIntoView();
    }

    public void EndLifetime()
    {
        if (_ended) return;
        _discardConfirmation.Close(); _templateDeleteConfirmation?.Close(); _templateEditorConfirmation?.Close();
        _ended = true;
        EndRuleSuggestions();
        IsEnabled = false;
        _available = false;
        Texts.Changed -= OnLanguageChanged;
        _filterLifetime.Cancel(); _filterLifetime.Dispose();
    }

    /// <summary>Refreshes confirmed values while preserving dirty drafts and detecting conflicting edits.</summary>
    public void RefreshConfirmedRules()
    {
        if (_ended || _busy) return;
        try
        {
            AcceptConfirmed(_getRules());
            if (_operationErrorKey == "rules.loadFailed") ClearOperationError();
        }
        catch (Exception exception)
        {
            _hasConfirmed = false;
            _operationErrorKey = "rules.loadFailed";
            _operationErrorDetail = exception.Message;
        }
        UpdatePreview();
        RefreshScopeChoices();
        RaiseAll();
    }

    /// <summary>Opens the same retained editor from a thread, board or source context action.</summary>
    public void EditScope(MediaRuleScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (_ended || _busy) return;
        RefreshConfirmedRules();
        if (!_hasConfirmed) return;
        if (!_drafts.TryGetValue(scope, out RuleDraft? draft))
        {
            draft = new RuleDraft(scope, _confirmed);
            _drafts.Add(scope, draft);
        }
        DeactivateRuleDecisions();
        _draft = draft;
        _selectedScope = null;
        _statusKey = null;
        ClearOperationError();
        UpdatePreview();
        RefreshScopeChoices();
        RaiseAll();
    }

    private void AcceptConfirmed(MediaRuleSet confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        _confirmed = confirmed;
        _hasConfirmed = true;
        MediaRuleScope active = _draft.Scope;
        foreach (KeyValuePair<MediaRuleScope, RuleDraft> entry in _drafts.ToArray())
        {
            RuleDraft draft = entry.Value;
            if (draft.Baseline.Revision != confirmed.Revision && draft.TemplateUndo != null) { draft.TemplateUndo = null; draft.TemplateUndoInvalidated = true; }
            // A preview captures this draft's identity. Re-reading the same immutable
            // revision must not invalidate a clean target; a new revision still does.
            if (!draft.Dirty && draft.Baseline.Revision != confirmed.Revision) _drafts[entry.Key] = new RuleDraft(entry.Key, confirmed);
            else if (!RulesEqual(FindRule(draft.Baseline, entry.Key), FindRule(confirmed, entry.Key))) draft.Stale = true;
            else { draft.Baseline = confirmed; draft.Stale = false; }
        }
        _draft = _drafts[active];
    }

    private void DraftChanged()
    {
        if (_ended) return;
        _discardConfirmation.Close();
        _templatePreview = null;
        _draft.Generation++;
        if (_draft.TemplateUndo != null) _draft.TemplateUndoInvalidated = true;
        _draft.TemplateUndo = null;
        bool wasDirty = _draft.Dirty;
        _draft.Dirty = true;
        _statusKey = null;
        ClearOperationError();
        UpdatePreview();
        if (!wasDirty) RefreshScopeChoices();
        RaiseAll();
    }

    private void UpdatePreview()
    {
        _preview = null;
        _validationKey = null;
        if (!_hasConfirmed || _draft.Stale) return;
        try
        {
            MediaRule rule = BuildDraft();
            MediaRuleSet proposed = new MediaRuleSet(_draft.Baseline.Revision,
                _draft.Baseline.Rules.Where(item => item.Scope != _draft.Scope).Append(rule));
            _preview = new MediaRuleResolver(proposed).Resolve(_draft.Scope);
        }
        catch (RuleInputException exception) { _validationKey = exception.Key; }
    }

    private MediaRule BuildDraft() => BuildDraft(_draft);
    private MediaRule BuildDraft(RuleDraft draft)
    {
        MediaTypeSelection types;
        try
        {
            types = draft.ExtensionInput.Build(draft.TypesMode);
        }
        catch (ArgumentException) { throw new RuleInputException("rules.invalidTypes"); }
        long? maximum = null;
        if (draft.SizeMode == MediaSizeLimitMode.Limited)
        {
            if (!TryReadMegabytes(draft.Megabytes, draft.NumberCulture, out long bytes))
                throw new RuleInputException("rules.invalidSize");
            maximum = bytes;
        }
        return new MediaRule(draft.Scope, draft.Enabled, types, new MediaSizeLimit(draft.SizeMode, maximum)) { ImageFilter = BuildImageFilter(draft) };
    }

    private static bool TryReadMegabytes(string text, CultureInfo culture, out long bytes)
    {
        bytes = 0;
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, culture, out decimal megabytes) || megabytes < 0)
            return false;
        try
        {
            decimal value = megabytes * BytesPerMegabyte;
            if (value > long.MaxValue || decimal.Truncate(value) != value) return false;
            bytes = decimal.ToInt64(value);
            return true;
        }
        catch (OverflowException) { return false; }
    }

    internal async Task SaveCurrentAsync()
    {
        if (!IsEditable) return;
        RuleExtensionsInput.FlushPendingText();
        RefreshConfirmedRules();
        if (!IsEditable || _draft.Stale) return;
        MediaRule rule;
        try { rule = BuildDraft(); }
        catch (RuleInputException exception) { _validationKey = exception.Key; RaiseAll(); if (exception.Key == "plugins.invalidFilterSettings") FocusFilterSettingError(); else if (exception.Key == "rules.invalidTypes") RuleExtensionsInput.FocusInput(); return; }
        if (!_draft.Dirty) return;
        MediaRule[] proposed = _draft.Baseline.Rules.Where(item => item.Scope != _draft.Scope).Append(rule).ToArray();
        string revision = _draft.Baseline.Revision;
        MediaRuleScope scope = _draft.Scope;
        ClearOperationError();
        _statusKey = null;
        SetBusy(true);
        try
        {
            if (!await ValidateRuleFilterAsync(rule)) return;
            MediaRuleSet saved = await _saveRules(proposed, revision);
            if (_ended) return;
            _drafts[scope] = new RuleDraft(scope, saved);
            _draft = _drafts[scope];
            AcceptConfirmed(saved);
            _statusKey = "rules.saved";
        }
        catch (Exception exception)
        {
            if (_ended) return;
            _operationErrorKey = "rules.saveFailed";
            _operationErrorDetail = exception.Message;
            try { AcceptConfirmed(_getRules()); } catch (Exception) { }
        }
        finally
        {
            SetBusy(false);
            if (!_ended) { UpdatePreview(); RefreshScopeChoices(); RaiseAll(); }
        }
    }

    internal void DiscardCurrentDraft()
    {
        if (!IsEditable) return;
        try
        {
            MediaRuleScope scope = _draft.Scope;
            AcceptConfirmed(_getRules());
            _drafts[scope] = new RuleDraft(scope, _confirmed);
            _draft = _drafts[scope];
            _statusKey = null;
            ClearOperationError();
            UpdatePreview();
            RefreshScopeChoices();
        }
        catch (Exception exception) { _operationErrorKey = "rules.loadFailed"; _operationErrorDetail = exception.Message; }
        RaiseAll();
    }

    private async void OnSaveRule(object? sender, RoutedEventArgs e) => await SaveCurrentAsync();
    private void OnDiscardDraft(object? sender, RoutedEventArgs e) => RequestDiscardCurrentDraft();
    private void OnOpenSavedScope(object? sender, RoutedEventArgs e)
    {
        if (IsEditable && _selectedScope != null) EditScope(_selectedScope.Scope);
    }
    private void OnOpenNewScope(object? sender, RoutedEventArgs e)
    {
        if (!IsEditable) return;
        try
        {
            string source = _newSourceId.Trim();
            string board = _newBoardId.Trim();
            string thread = _newThreadId.Trim();
            if ((NeedsSource && InvalidScopePart(source)) || (NeedsBoard && InvalidScopePart(board)) || (NeedsThread && InvalidScopePart(thread)))
                throw new ArgumentException();
            MediaRuleScope scope = new MediaRuleScope((MediaRuleScopeKind)_newScopeKind,
                NeedsSource ? source : null, NeedsBoard ? board : null, NeedsThread ? thread : null);
            EditScope(scope);
            NewRuleScopeExpander.IsExpanded = false;
            if (FindRule(_confirmed, scope) == null) DraftChanged();
        }
        catch (ArgumentException) { _operationErrorKey = "rules.invalidScope"; _operationErrorDetail = null; RaiseAll(); }
    }

    private static bool InvalidScopePart(string value) => string.IsNullOrWhiteSpace(value) || value.Contains("://", StringComparison.Ordinal)
        || value.Any(char.IsControl);

    private void RefreshChoiceLabels()
    {
        // Replacing translated label arrays resets ComboBox selections. Publish every
        // ItemsSource before reasserting the unchanged indices, and ignore temporary
        // TwoWay feedback so a language change cannot alter the rule or scope draft.
        _refreshingChoiceLabels = true;
        try
        {
            ScopeKindChoices = new[] { Texts.T("rules.global"), Texts.T("rules.source"), Texts.T("rules.board"), Texts.T("rules.thread") };
            TypesModeChoices = new[] { Texts.T("rules.inherit"), Texts.T("rules.allTypes"), Texts.T("rules.selectedTypes") };
            SizeModeChoices = new[] { Texts.T("rules.inherit"), Texts.T("rules.unlimited"), Texts.T("rules.limited") };
            Raise(nameof(ScopeKindChoices));
            Raise(nameof(TypesModeChoices));
            Raise(nameof(SizeModeChoices));
            Raise(nameof(NewScopeKind));
            Raise(nameof(TypesMode));
            Raise(nameof(SizeMode));
        }
        finally { _refreshingChoiceLabels = false; }
        RefreshScopeChoices();
    }

    private void RefreshScopeChoices()
    {
        RebuildRuleSuggestions();
        MediaRuleScope selected = _selectedScope?.Scope ?? _draft.Scope;
        RuleScopeChoice[] choices = _confirmed.Rules.Select(rule => rule.Scope).Concat(_drafts.Keys).Append(MediaRuleScope.Global).Distinct()
            .OrderBy(scope => scope.Kind).ThenBy(scope => scope.Source, StringComparer.Ordinal)
            .ThenBy(scope => scope.Board, StringComparer.Ordinal).ThenBy(scope => scope.Thread, StringComparer.Ordinal)
            .Select(scope => new RuleScopeChoice(scope, FormatScope(scope) + " · " + Texts.T(
                _drafts.TryGetValue(scope, out RuleDraft? draft) && draft.Dirty ? "rules.unsaved"
                : FindRule(_confirmed, scope) == null ? "rules.notSaved" : "rules.confirmed"))).ToArray();
        // Replacing ItemsSource can synchronously write a transient selection back through
        // the ComboBox's TwoWay binding. Publish the collection first, then its matching
        // selected item, while retaining the chosen scope identity throughout that reset.
        _refreshingScopeChoices = true;
        try
        {
            ScopeChoices = choices;
            Raise(nameof(ScopeChoices));
            _selectedScope = choices.FirstOrDefault(choice => choice.Scope == selected) ?? choices[0];
            Raise(nameof(SelectedScopeChoice));
        }
        finally { _refreshingScopeChoices = false; }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        void Refresh()
        {
            if (_ended) return;
            foreach (RuleDraft draft in _drafts.Values)
            {
                draft.ReformatInitialNumber(Texts.Culture);
                if (TryReadMegabytes(draft.Megabytes, draft.NumberCulture, out long bytes))
                {
                    draft.Megabytes = FormatMegabytes(bytes, Texts.Culture);
                    draft.NumberCulture = Texts.Culture;
                }
            }
            ReformatTemplateEditor();
            RefreshChoiceLabels();
            _filterRenderedDraft = null;
            UpdatePreview();
            RaiseAll();
            ApplyCommandIcons();
        }
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void ApplyCommandIcons()
    {
        CommandBindings.ApplyLabeled(OpenSavedRuleButton, "rules.open-scope", OpenLabel);
        CommandBindings.ApplyLabeled(OpenNewRuleScopeButton, "rules.open-scope", OpenLabel);
        CommandBindings.ApplyLabeled(DiscardRuleButton, "rules.discard", DiscardLabel);
        CommandBindings.ApplyLabeled(SaveRuleButton, "rules.save", SaveLabel);
        CommandBindings.ApplyLabeled(OpenParentRuleButton, "rules.open-parent", CommandCatalog.Label("rules.open-parent"));
        CommandBindings.ApplyLabeled(OpenTypesOriginButton, "rules.open-origin", CommandCatalog.Label("rules.open-origin"));
        CommandBindings.ApplyLabeled(OpenSizeOriginButton, "rules.open-origin", CommandCatalog.Label("rules.open-origin"));
        RefreshOriginActionLabels();
    }

    private void OnRulesSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        double width = Math.Min(880, Math.Max(0, e.NewSize.Width - 60));
        RulesContent.Width = width;
        RulesHeader.Width = width;
        bool narrow = width < 700;
        foreach (Grid row in RulesContent.GetVisualDescendants().OfType<Grid>().Where(row => row.Classes.Contains("rule-row")))
        {
            row.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 280);
            row.ColumnSpacing = narrow ? 0 : 32;
            row.RowSpacing = narrow ? 6 : 0;
            Control editor = row.Children[1];
            Grid.SetColumn(editor, narrow ? 0 : 1);
            Grid.SetRow(editor, narrow ? 1 : 0);
        }
        Grid.SetColumn(RulesFooterActions, narrow ? 0 : 1);
        Grid.SetRow(RulesFooterActions, narrow ? 1 : 0);
        Grid.SetColumnSpan(RulesFooterActions, narrow ? 2 : 1);
        Grid.SetColumnSpan(RulesFooter.Children[0], narrow ? 2 : 1);
    }

    private void SetBusy(bool busy)
    {
        if (_busy == busy) return;
        if (busy) { _discardConfirmation.Close(); _templatePreview = null; }
        _busy = busy;
        RaiseAll();
        if (!_ended) BusyChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ClearOperationError() { _operationErrorKey = null; _operationErrorDetail = null; }
    private void RaiseAll()
    {
        Raise(string.Empty);
        SynchronizeModeSelections();
        RefreshOriginActionLabels();
        RefreshFilterEditor();
        RuleExtensionsInput.Bind(_draft.ExtensionInput);
        RefreshBreadcrumb();
        RefreshTemplatesPanel();
    }
    private void RefreshOriginActionLabels()
    {
        if (_ended) return;
        Avalonia.Automation.AutomationProperties.SetName(OpenParentRuleButton, ParentScopeLabel);
        Avalonia.Automation.AutomationProperties.SetName(OpenTypesOriginButton, OpenTypesOriginLabel);
        Avalonia.Automation.AutomationProperties.SetName(OpenSizeOriginButton, OpenSizeOriginLabel);
        ToolTip.SetTip(OpenParentRuleButton, ParentScopeLabel);
        ToolTip.SetTip(OpenTypesOriginButton, OpenTypesOriginLabel);
        ToolTip.SetTip(OpenSizeOriginButton, OpenSizeOriginLabel);
    }
    private void SynchronizeModeSelections()
    {
        if (_ended || !ReferenceEquals(DataContext, this)) return;
        // A translated ItemsSource may clear SelectedIndex even when the bound enum
        // value did not change. The binding can suppress that unchanged value, so
        // restore the control state after notifications without replacing its binding.
        bool refreshing = _refreshingChoiceLabels;
        _refreshingChoiceLabels = true;
        try
        {
            NewRuleScopeKindInput.SetCurrentValue(ComboBox.SelectedIndexProperty, NewScopeKind);
            RuleTypesModeInput.SetCurrentValue(ComboBox.SelectedIndexProperty, TypesMode);
            RuleSizeModeInput.SetCurrentValue(ComboBox.SelectedIndexProperty, SizeMode);
        }
        finally { _refreshingChoiceLabels = refreshing; }
    }
    private void Raise(string property) { if (!_ended) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property)); }
    private static string FormatMegabytes(long bytes, CultureInfo culture) => (bytes / BytesPerMegabyte).ToString("0.######", culture);
    private static string FormatOrigin(MediaRuleOrigin origin) => origin.Scope == null ? Texts.T("rules.builtin") : FormatScope(origin.Scope);
    private static string FormatScope(MediaRuleScope scope) => MeowChan.Desktop.Plugins.PluginGrantPresentation.ScopeLabel(scope);
    private static MediaRule? FindRule(MediaRuleSet set, MediaRuleScope scope) => set.Rules.FirstOrDefault(rule => rule.Scope == scope);
    private static bool RulesEqual(MediaRule? first, MediaRule? second) => ReferenceEquals(first, second) || first != null && second != null
        && first.Enabled == second.Enabled && first.Scope == second.Scope && first.Types.Mode == second.Types.Mode
        && first.Types.Extensions.SequenceEqual(second.Types.Extensions) && first.SizeLimit == second.SizeLimit
        && ImageFiltersEqual(first.ImageFilter, second.ImageFilter);

    private sealed class RuleInputException(string key) : Exception { internal string Key { get; } = key; }

    internal sealed partial class RuleDraft
    {
        internal MediaRuleScope Scope { get; }
        internal MediaRuleSet Baseline { get; set; }
        internal bool Enabled { get; set; }
        internal MediaTypeSelectionMode TypesMode { get; set; }
        internal MeowChan.Desktop.Controls.ExtensionTagDraft ExtensionInput { get; set; } = new();
        internal MediaSizeLimitMode SizeMode { get; set; }
        internal string Megabytes { get; set; }
        internal CultureInfo NumberCulture { get; set; }
        internal MediaImageFilterRule ImageFilter { get; set; }
        internal bool FilterSchemaAcknowledged { get; set; }
        internal System.Collections.Generic.IReadOnlyList<MeowChan.PluginSdk.FieldValidationError> FilterValidationErrors { get; set; } = Array.Empty<MeowChan.PluginSdk.FieldValidationError>();
        internal MediaImageFilterRule? LastPluginFilter { get; set; }
        internal MeowChan.Desktop.Plugins.PluginSettingsDraft? FilterParameters { get; set; }
        internal bool Dirty { get; set; }
        internal bool Stale { get; set; }

        internal RuleDraft(MediaRuleScope scope, MediaRuleSet baseline)
        {
            Scope = scope;
            Baseline = baseline;
            MediaRule rule = FindRule(baseline, scope) ?? new MediaRule(scope);
            Enabled = rule.Enabled;
            ImageFilter = rule.ImageFilter;
            TypesMode = rule.Types.Mode;
            ExtensionInput.Tags.AddRange(rule.Types.Extensions);
            SizeMode = rule.SizeLimit.Mode;
            NumberCulture = Texts.Culture;
            Megabytes = rule.SizeLimit.Bytes.HasValue ? FormatMegabytes(rule.SizeLimit.Bytes.Value, NumberCulture) : "10";
            CaptureInitial();
        }
    }
}
