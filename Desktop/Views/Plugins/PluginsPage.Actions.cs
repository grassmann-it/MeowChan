using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using Ursa.Controls;

namespace MeowChan;

public partial class PluginsPage
{
    internal static readonly string[] PluginActionCommandIds =
    {
        "plugins.enable", "plugins.disable", "plugins.details", "plugins.show-settings", "plugins.watches",
        "plugins.select-candidate", "plugins.rollback", "plugins.show-grants", "plugins.show-developer-tools", "plugins.actions"
    };

    private sealed record PluginActionInvocation(string CommandId, PluginActionTarget Target, long OperationGeneration, string? SourceId = null);
    private PluginActionInvocation CapturePluginActionInvocation(string id, PluginActionTarget target, string? sourceId = null) =>
        new(id, target, _operationGeneration, sourceId);
    internal ContextMenu? PluginActionMenu { get; private set; }
    private PluginActionTarget? _pluginMenuTarget;
    private bool _pluginMenuWasBusy;
    private bool _pluginMenuReturnFocus;
    private string? _pluginMenuLanguage;

    private void InitializePluginActions()
    {
        var list = C<SelectionList>("PluginCards");
        list.AddHandler(PointerPressedEvent, OnPluginActionPointerPressed, RoutingStrategies.Tunnel);
        list.AddHandler(ContextRequestedEvent, OnPluginActionContextRequested, RoutingStrategies.Tunnel);
    }

    private PluginCatalogEntry? PluginActionEntry(PluginActionTarget target) =>
        _catalog?.Entries.FirstOrDefault(entry => entry.Key == target.Key);

    private PluginActionTarget? CaptureSelectedPluginActionTarget() =>
        Selected is { } entry && _catalog != null && Included(entry)
            ? PluginActionTarget.Capture(entry, _catalog.Profile.Revision) : null;

    internal bool CanExecutePluginAction(string id) => CaptureSelectedPluginActionTarget() is { } target &&
        PluginActionUnavailableReason(CapturePluginActionInvocation(id, target)) == null;

    internal string? PluginActionDisabledReason(string id) => CaptureSelectedPluginActionTarget() is { } target
        ? PluginActionUnavailableReason(CapturePluginActionInvocation(id, target)) : Texts.T("plugins.reason.noActivation");

    private string? PluginActionUnavailableReason(PluginActionInvocation action)
    {
        if (_ended || !IsEffectivelyVisible || !IsEffectivelyEnabled || IsDiscardConfirmationOpen)
            return Texts.T("plugins.unavailable");
        if (action.CommandId == "plugins.actions")
            return PluginActionEntry(action.Target) is { } menuEntry && Included(menuEntry) ? null : Texts.T("plugins.reason.targetChanged");
        if (_busy) return Texts.T("plugins.reason.busy");
        if (action.OperationGeneration != _operationGeneration || _catalog == null || PluginActionEntry(action.Target) is not { } entry || !Included(entry)
            || _selectedKey != action.Target.Key || !action.Target.Matches(entry, _catalog.Profile.Revision))
            return Texts.T("plugins.reason.targetChanged");
        PluginEntryPresentation presentation = PluginEntryPresentation.Create(entry);
        return action.CommandId switch
        {
            "plugins.enable" => _services == null || entry.PluginId == null || entry.Activation == null ? Texts.T("plugins.reason.noActivation") :
                entry.Activation.Enabled ? Texts.T("plugins.reason.alreadyEnabled") : null,
            "plugins.disable" => _services == null || entry.PluginId == null || entry.Activation == null ? Texts.T("plugins.reason.noActivation") :
                !entry.Activation.Enabled ? Texts.T("plugins.reason.alreadyDisabled") : null,
            "plugins.select-candidate" => _services == null || entry.PluginId == null || !presentation.CanSelectCandidate ? Texts.T("plugins.reason.noCandidate") :
                !presentation.CandidateChoiceRequired ? Texts.T("plugins.reason.alreadySelected") : null,
            "plugins.rollback" => _services == null || entry.PluginId == null || entry.Activation?.LastGood == null ? Texts.T("plugins.reason.noRollback") : null,
            "plugins.show-settings" => HasPluginSettings(entry) ? null : Texts.T("plugins.reason.noSettings"),
            "plugins.watches" => OpenSourceWatches == null || presentation.Sources.Count == 0
                || action.SourceId != null && !presentation.Sources.Any(source => source.SourceId == action.SourceId)
                    ? Texts.T("plugins.reason.noWatches") : null,
            "plugins.show-grants" => presentation.Filters.Count > 0 ? null : Texts.T("plugins.reason.noGrants"),
            "plugins.show-developer-tools" or "plugins.details" => null,
            _ => Texts.T("plugins.unavailable")
        };
    }

    private static bool HasPluginSettings(PluginCatalogEntry entry) =>
        PluginEntryPresentation.Create(entry).DescriptionMetadata is { } description &&
        (description.Settings.Fields.Count > 0 || description.Sources.Any(source => source.Settings.Fields.Count > 0));

    private void RefreshPluginActions()
    {
        if (PluginActionMenu != null && (_pluginMenuTarget == null || _catalog == null
            || PluginActionEntry(_pluginMenuTarget) is not { } menuEntry || !Included(menuEntry)
            || !_pluginMenuTarget.Matches(menuEntry, _catalog.Profile.Revision)
            || _pluginMenuWasBusy != _busy || _pluginMenuLanguage != Texts.Language || !IsEffectivelyEnabled
            || !_busy && _selectedKey != _pluginMenuTarget.Key))
            ClosePluginActionMenu();

        PluginActionTarget? target = CaptureSelectedPluginActionTarget();
        foreach (string name in new[] { "PluginMoreActions", "PluginListActions" })
        {
            if (this.FindControl<Button>(name) is not { } button) continue;
            button.Content = Texts.T("plugins.actions");
            AutomationProperties.SetName(button, target == null ? Texts.T("plugins.actions") : Texts.T("plugins.actionsFor", Selected!.Name));
            CommandBindings.BindExisting(button, "plugins.actions");
            button.IsEnabled = target != null && !IsDiscardConfirmationOpen && !_ended;
        }
        if (target == null) return;
        PluginCatalogEntry entry = Selected!;
        BindAction("PluginEnabled", entry.Activation?.Enabled == true ? "plugins.disable" : "plugins.enable");
        BindAction("PluginSelectCandidate", "plugins.select-candidate");
        BindAction("PluginRollback", "plugins.rollback");
        BindAction("PluginWatches", "plugins.watches");
        if (this.FindControl<Button>("PluginSettingsAction") != null) BindAction("PluginSettingsAction", "plugins.show-settings");
        PluginEntryPresentation view = PluginEntryPresentation.Create(entry);
        C<Button>("PluginSelectCandidate").IsVisible = view.CandidateChoiceRequired;
        C<Button>("PluginWatches").IsVisible = view.Sources.Count > 0;
        if (view.CandidateChoiceRequired) ShowReasons("PluginActivationReasons", "PluginEnabled", "PluginSelectCandidate");
        else ShowReasons("PluginActivationReasons", "PluginEnabled");
        if (this.FindControl<SelectableTextBlock>("PluginRecoveryReasons") != null)
            ShowReasons("PluginRecoveryReasons", "PluginRollback");
        if (this.FindControl<SelectableTextBlock>("PluginActionHint") is { } hint)
        {
            hint.Text = Texts.T("plugins.actionsDirtyHint");
            hint.IsVisible = _drafts.Values.Any(draft => draft.Dirty);
            AutomationProperties.SetName(hint, hint.Text);
        }

        void BindAction(string controlName, string id)
        {
            Button button = C<Button>(controlName);
            var action = CapturePluginActionInvocation(id, target);
            string? reason = PluginActionUnavailableReason(action);
            button.Tag = action;
            CommandBindings.ApplyLabeled(button, id, CommandCatalog.Label(id));
            button.IsEnabled = reason == null;
            _actionReasons[controlName] = reason;
            AutomationProperties.SetHelpText(button, CommandBindings.Tooltip(id, reason) + " · " + Texts.T("plugins.lifecycleHint")
                + (_drafts.Values.Any(draft => draft.Dirty) ? " · " + Texts.T("plugins.actionsDirtyHint") : string.Empty));
        }
    }

    private Task ExecutePluginControlActionAsync(string controlName) => this.FindControl<Button>(controlName)?.Tag is PluginActionInvocation action
        ? ExecutePluginActionAsync(action, C<Button>(controlName)) : Task.CompletedTask;

    internal Task ExecuteSelectedPluginActionAsync(string id) => CaptureSelectedPluginActionTarget() is { } target
        ? ExecutePluginActionAsync(CapturePluginActionInvocation(id, target), PluginActionOrigin(target.Key)) : Task.CompletedTask;

    private async Task ExecutePluginActionAsync(PluginActionInvocation action, Control origin)
    {
        string? reason = PluginActionUnavailableReason(action);
        if (reason != null) { ClosePluginActionMenu(); Error(reason); return; }
        // Do not let a detached popup or a background window invoke a stale command.
        if (!CommandRouter.IsRoutingContextActive(this, this)) return;
        if (action.CommandId == "plugins.actions")
        { OpenPluginActionMenu(action.Target.Key, origin, details: _showDetails); return; }
        if (action.CommandId == "plugins.watches" && action.SourceId == null
            && PluginActionEntry(action.Target) is { } sourceEntry && PluginEntryPresentation.Create(sourceEntry).Sources.Count > 1)
        { OpenPluginSourcesMenu(action.Target, origin); return; }

        ClosePluginActionMenu();
        switch (action.CommandId)
        {
            case "plugins.enable":
            case "plugins.disable":
                // The captured command is the intent. Never invert a later mutable Enabled value.
                bool enabled = action.CommandId == "plugins.enable";
                await CommandAsync((services, revision, token) => services.ProfileStore.SetEnabledAsync(action.Target.PluginId!, enabled, revision, token));
                RestorePluginActionFocus(action.Target, origin);
                break;
            case "plugins.select-candidate":
                await CommandAsync((services, revision, token) => services.ProfileStore.SelectCandidateAsync(
                    action.Target.PluginId!, action.Target.CandidateId!, revision, token));
                RestorePluginActionFocus(action.Target, origin);
                break;
            case "plugins.rollback":
                await CommandAsync((services, revision, token) => services.ProfileStore.SelectRollbackAsync(action.Target.PluginId!, revision, token));
                RestorePluginActionFocus(action.Target, origin);
                break;
            case "plugins.details": Select(action.Target.Key); break;
            case "plugins.show-settings":
                Select(action.Target.Key);
                if (_editors.Values.FirstOrDefault(editor => editor.Draft.Schema.Fields.Count > 0) is { } editor)
                    RevealPluginActionSection(action.Target, editor.GetVisualAncestors().OfType<Expander>().FirstOrDefault() ?? (Control)editor);
                break;
            case "plugins.show-grants":
                Select(action.Target.Key); RevealPluginActionSection(action.Target, C<Expander>("PluginGrantExpander")); break;
            case "plugins.show-developer-tools":
                Select(action.Target.Key); RevealPluginActionSection(action.Target, C<Expander>("PluginDeveloperExpander")); break;
            case "plugins.watches":
                var sources = PluginEntryPresentation.Create(PluginActionEntry(action.Target)!).Sources;
                string? sourceId = action.SourceId ?? (sources.Count == 1 ? sources[0].SourceId : null);
                if (sourceId != null) OpenSourceWatches?.Invoke(sourceId);
                break;
        }
    }

    private void RevealPluginActionSection(PluginActionTarget target, Control section)
    {
        _pluginSectionNavigation?.CancelPendingNavigation();
        if (section is Expander expander) expander.IsExpanded = true;
        foreach (Expander parent in section.GetVisualAncestors().OfType<Expander>()) parent.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || _selectedKey != target.Key || !section.IsAttachedToVisualTree() || !section.IsEffectivelyVisible) return;
            section.Focusable = true; section.BringIntoView(); section.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OnPluginMoreActionsClick(object? sender, RoutedEventArgs e)
    { if (_selectedKey is { } key) OpenPluginActionMenu(key, C<Button>("PluginMoreActions"), details: true); }
    private void OnPluginListActionsClick(object? sender, RoutedEventArgs e)
    { if (_selectedKey is { } key) OpenPluginActionMenu(key, C<Button>("PluginListActions")); }
    private async void OnPluginSettingsActionClick(object? sender, RoutedEventArgs e) => await ExecutePluginControlActionAsync("PluginSettingsAction");

    private Control PluginActionOrigin(string key) => _cards.TryGetValue(key, out var card) && card.IsEffectivelyVisible
        ? card : this.FindControl<Button>("PluginMoreActions") ?? (Control)C<ComboBox>("PluginCategory");

    private void OnPluginActionPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var list = C<SelectionList>("PluginCards");
        if (!e.GetCurrentPoint(list).Properties.IsRightButtonPressed || !MainWindow.IsListCommandSource(e.Source, list)) return;
        e.Handled = true;
        SelectionListItem? row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<SelectionListItem>().FirstOrDefault();
        string? key = row == null ? null : _cards.FirstOrDefault(pair => ReferenceEquals(pair.Value, row)).Key;
        if (key == null) { ClosePluginActionMenu(); return; }
        OpenPluginActionMenu(key, row!, anchor: e.GetPosition(row!));
    }

    private void OnPluginActionContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var list = C<SelectionList>("PluginCards");
        if (!MainWindow.IsListCommandSource(e.Source, list)) return;
        e.Handled = true;
        // Pointer requests were resolved against a real row in PointerPressed, never the old selection.
        if (e.TryGetPosition(list, out _)) return;
        if (_selectedKey is { } key && _cards.TryGetValue(key, out var row) && list.Items.Contains(row))
            OpenPluginActionMenu(key, row);
    }

    private bool TryHandlePluginActionKeyDown(KeyEventArgs e)
    {
        if (e.Handled || !MainWindow.IsListCommandSource(e.Source, C<SelectionList>("PluginCards"))) return false;
        if (CommandBindings.Matches(e, "menu.context-open"))
        {
            e.Handled = true;
            if (_selectedKey is { } key && C<SelectionList>("PluginCards").SelectedItem != null)
                OpenPluginActionMenu(key, PluginActionOrigin(key));
            return true;
        }
        // Menu remains native. A removed/rebound Shift+F10 must not invoke its old native accelerator.
        if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift) { e.Handled = true; return true; }
        return false;
    }

    internal void OpenPluginActionMenu(string key, Control origin, bool details = false, Point? anchor = null)
    {
        if (_ended || IsDiscardConfirmationOpen || !IsEffectivelyVisible || _catalog == null
            || _catalog.Entries.FirstOrDefault(entry => entry.Key == key) is not { } entry || !Included(entry)) return;
        ClosePluginActionMenu();
        // Inspecting another row while busy must not call SelectCore, which cancels the operation.
        if (!_busy && _selectedKey != key) SelectCore(key, openDetails: false);
        var target = PluginActionTarget.Capture(entry, _catalog.Profile.Revision);
        var menu = new ContextMenu();
        var reasons = new List<string>();
        if (!details) Add("plugins.details");
        Add(entry.Activation?.Enabled == true ? "plugins.disable" : "plugins.enable");
        menu.Items.Add(new Separator());
        if (HasPluginSettings(entry)) Add("plugins.show-settings");
        if (PluginEntryPresentation.Create(entry).Sources.Count > 0) AddWatches(menu, target, origin, reasons);
        PluginEntryPresentation view = PluginEntryPresentation.Create(entry);
        if (view.CandidateChoiceRequired || details)
        { menu.Items.Add(new Separator()); if (view.CandidateChoiceRequired) Add("plugins.select-candidate"); }
        if (details)
        {
            Add("plugins.rollback");
            menu.Items.Add(new Separator());
            if (view.Filters.Count > 0) Add("plugins.show-grants");
            Add("plugins.show-developer-tools");
        }
        if (_drafts.Values.Any(draft => draft.Dirty)) AddPluginMenuInformation(menu, Texts.T("plugins.actionsDirtyHint"));
        AddPluginMenuReasons(menu, reasons);
        OpenPreparedPluginMenu(menu, target, origin, anchor);

        void Add(string id)
        {
            var action = CapturePluginActionInvocation(id, target);
            menu.Items.Add(CreatePluginActionMenuItem(action, origin));
            if (PluginActionUnavailableReason(action) is { } why) reasons.Add(CommandCatalog.Label(id) + ": " + why);
        }
    }

    private MenuItem CreatePluginActionMenuItem(PluginActionInvocation action, Control origin)
    {
        PluginCatalogEntry? entry = PluginActionEntry(action.Target);
        string? version = action.CommandId switch
        {
            "plugins.select-candidate" => entry?.Candidate?.Description?.PluginVersion,
            "plugins.rollback" => entry?.Activation?.LastGood?.Description.PluginVersion,
            _ => null
        };
        string Label() => CommandCatalog.Label(action.CommandId) + (string.IsNullOrEmpty(version) ? string.Empty : " · " + version);
        var item = CommandBindings.CreateMenuItem(action.CommandId, () => _ = ExecutePluginActionAsync(action, origin),
            () => PluginActionUnavailableReason(action) == null, () => PluginActionUnavailableReason(action), Label);
        item.Tag = action.CommandId;
        return item;
    }

    private MenuItem CreatePluginSourceMenuItem(PluginActionInvocation action, Control origin, string label)
    {
        // The general Watches shortcut opens this chooser; individual sources have no separate gesture.
        var item = new MenuItem
        {
            Header = label, Icon = CommandBindings.Icon("plugins.watches"), Tag = action.CommandId,
            Command = new DelegateCommand(() => _ = ExecutePluginActionAsync(action, origin),
                () => PluginActionUnavailableReason(action) == null),
            IsEnabled = PluginActionUnavailableReason(action) == null
        };
        AutomationProperties.SetName(item, label);
        AutomationProperties.SetHelpText(item, PluginActionUnavailableReason(action) ?? label);
        return item;
    }

    private void AddWatches(ContextMenu menu, PluginActionTarget target, Control origin, List<string> reasons)
    {
        var action = CapturePluginActionInvocation("plugins.watches", target);
        MenuItem item = CreatePluginActionMenuItem(action, origin);
        var sources = PluginEntryPresentation.Create(PluginActionEntry(target)!).Sources;
        if (sources.Count > 1)
        {
            item.Command = null;
            foreach (var source in sources)
            {
                MenuItem choice = CreatePluginSourceMenuItem(action with { SourceId = source.SourceId }, origin, source.Name + " · " + source.SourceId);
                item.Items.Add(choice);
            }
        }
        menu.Items.Add(item);
        if (PluginActionUnavailableReason(action) is { } why) reasons.Add(CommandCatalog.Label(action.CommandId) + ": " + why);
    }

    private void OpenPluginSourcesMenu(PluginActionTarget target, Control origin)
    {
        ClosePluginActionMenu();
        var menu = new ContextMenu();
        foreach (var source in PluginEntryPresentation.Create(PluginActionEntry(target)!).Sources)
        {
            MenuItem item = CreatePluginSourceMenuItem(CapturePluginActionInvocation("plugins.watches", target, source.SourceId), origin, source.Name + " · " + source.SourceId);
            menu.Items.Add(item);
        }
        OpenPreparedPluginMenu(menu, target, origin);
    }

    private static void AddPluginMenuInformation(ContextMenu menu, string text)
    {
        var item = new MenuItem { Header = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 } };
        AutomationProperties.SetName(item, text); AutomationProperties.SetHelpText(item, text);
        menu.Items.Add(item);
    }

    private static void AddPluginMenuReasons(ContextMenu menu, IReadOnlyList<string> reasons)
    {
        if (reasons.Count == 0) return;
        var info = new MenuItem { Header = Texts.T("plugins.unavailableActions"), Icon = CommandBindings.Icon("thread.details") };
        AutomationProperties.SetName(info, Texts.T("plugins.unavailableActions"));
        AutomationProperties.SetHelpText(info, string.Join(Environment.NewLine, reasons));
        foreach (string reason in reasons)
        {
            var explanation = new MenuItem { Header = new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 } };
            AutomationProperties.SetName(explanation, reason); info.Items.Add(explanation);
        }
        menu.Items.Add(new Separator()); menu.Items.Add(info);
    }

    private void OpenPreparedPluginMenu(ContextMenu menu, PluginActionTarget target, Control origin, Point? anchor = null)
    {
        PluginActionMenu = menu; _pluginMenuTarget = target; _pluginMenuLanguage = Texts.Language;
        _pluginMenuReturnFocus = true; _pluginMenuWasBusy = _busy;
        string? returnKey = _selectedKey;
        Control returnOrigin = _busy && returnKey != target.Key
            ? TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control ?? C<SelectionList>("PluginCards")
            : origin;
        origin.BringIntoView();
        menu.Placement = PlacementMode.AnchorAndGravity;
        menu.PlacementTarget = origin;
        menu.PlacementRect = new Rect(anchor ?? new Point(0, origin.Bounds.Height), new Size(1, 1));
        menu.PlacementAnchor = PopupAnchor.TopLeft; menu.PlacementGravity = PopupGravity.BottomRight;
        // Long translated Next-start labels and version strings must fit beside the shortcut column.
        void WrapLabels()
        {
            double headerWidth = Math.Clamp((TopLevel.GetTopLevel(this)?.Bounds.Width ?? 640) - 180, 160, 400);
            void Wrap(System.Collections.IEnumerable items)
            {
                foreach (MenuItem item in items.OfType<MenuItem>())
                {
                    if (item.Header is string label) item.Header = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = headerWidth };
                    Wrap(item.Items);
                }
            }
            Wrap(menu.Items);
        }
        WrapLabels();
        ShortcutService.Changed += WrapLabels;
        menu.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            args.Handled = true; ClosePluginActionMenu(restoreFocus: true);
        };
        menu.Closed += (_, _) =>
        {
            ShortcutService.Changed -= WrapLabels;
            if (!ReferenceEquals(PluginActionMenu, menu)) return;
            bool restore = _pluginMenuReturnFocus;
            PluginActionMenu = null; _pluginMenuTarget = null; _pluginMenuLanguage = null;
            if (restore) RestorePluginMenuFocus(returnKey, returnOrigin);
        };
        // Explicit Open bypasses Opening in Avalonia 12: all items and targets are ready now.
        menu.Open(origin);
    }

    private void ClosePluginActionMenu(bool restoreFocus = false)
    {
        _pluginMenuReturnFocus = restoreFocus;
        PluginActionMenu?.Close();
    }

    private void RestorePluginActionFocus(PluginActionTarget target, Control origin)
        => RestorePluginMenuFocus(target.Key, origin);

    private void RestorePluginMenuFocus(string? key, Control origin)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || !IsEffectivelyVisible || IsDiscardConfirmationOpen) return;
            if (_selectedKey == key && origin.IsAttachedToVisualTree() && origin.IsEffectivelyVisible && origin.IsEffectivelyEnabled)
            { origin.Focus(); return; }
            if (_selectedKey == key && key != null && _cards.TryGetValue(key, out var row) && row.IsEffectivelyVisible)
            { row.BringIntoView(); row.Focus(); return; }
            if (Selected == null) C<ComboBox>("PluginCategory").Focus();
        }, DispatcherPriority.Loaded);
    }
}
