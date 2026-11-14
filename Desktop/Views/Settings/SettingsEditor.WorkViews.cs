using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

/// <summary>
/// Work views on the settings page (WV8). The pages keep their editors and draft; what a view reduces is a set of
/// collapsible groups ("More settings", one expander per group with a policy row settings.group.&lt;name&gt;). Barebones and
/// Simple show them collapsed with a counter, Advanced shows their content directly. A changed value or a field with an
/// error opens its group in every view, and the settings search opens whatever it jumps to. The editor finds the groups by
/// their registered id, so another group (for example Reader settings) only needs an expander and a policy row.
/// </summary>
public partial class SettingsEditor
{
    private sealed class GroupSlot
    {
        public required string Id;
        public required Expander Expander;
        public required Control Body;
        public required Panel Parent;
        public ShortcutEditor? Shortcuts;
        public Control[] Inputs = [];
        public TextBlock[] Errors = [];
        public string[] Baseline = [];
        public int Count;
        public bool Flat;
        public int LastChanged;
        public bool HadError;
    }

    private List<GroupSlot>? _groupSlots;
    private WorkView _currentWorkView = WorkView.Barebones;
    private bool _groupSweepQueued;

    /// <summary>The view this editor last applied (the saved setting of its profile until the window tells otherwise).</summary>
    internal WorkView CurrentWorkView => _currentWorkView;

    /// <summary>One group's live state, for the work-views smoke and the tests of the page.</summary>
    internal sealed record GroupState(string Id, bool Expanded, bool Flat, int Count, int Changed, bool HasError, string? Header);

    internal IReadOnlyList<GroupState> GroupStates() => (_groupSlots ?? [])
        .Select(slot => new GroupState(slot.Id, slot.Expander.IsExpanded, slot.Flat, slot.Count, ChangedCount(slot), HasGroupError(slot),
            slot.Expander.Header as string)).ToArray();

    // Called once at the end of the constructor, when every input shows the loaded values.
    private void InitializeWorkViews()
    {
        _groupSlots = this.GetLogicalDescendants().OfType<Expander>()
            .Where(expander => WorkViewElement.GetId(expander) is { } id && id.StartsWith(WorkViewPolicy.SettingsGroupPrefix, StringComparison.Ordinal))
            .Select(CreateSlot).OfType<GroupSlot>().ToList();
        ResetGroupBaselines();
        PropertyChanged += (_, _) => QueueGroupSweep();
        ShortcutsEditor.DraftEdited += (_, _) => QueueGroupSweep();
        ApplyWorkView(WorkViewSetting.Parse(_config.WorkspaceMode), viewChanged: true);
    }

    private static GroupSlot? CreateSlot(Expander expander)
    {
        if (expander.Content is not Control body || expander.GetLogicalParent() is not Panel parent) return null;
        ShortcutEditor? shortcuts = expander.FindLogicalAncestorOfType<ShortcutEditor>();
        // Template parts are not settings; the hidden compatibility switch of the gallery overlay is not one either.
        Control[] inputs = shortcuts != null ? [] : body.GetSelfAndLogicalDescendants().OfType<Control>()
            .Where(control => IsSettingInput(control) && control.TemplatedParent == null && (control.IsVisible || control.IsTabStop)).ToArray();
        TextBlock[] errors = body.GetSelfAndLogicalDescendants().OfType<TextBlock>()
            .Where(block => block.Classes.Contains("workspace-error") || block.Classes.Contains("error")).ToArray();
        return new GroupSlot
        {
            Id = WorkViewElement.GetId(expander)!, Expander = expander, Body = body, Parent = parent, Shortcuts = shortcuts,
            Inputs = inputs, Errors = errors, Count = shortcuts != null ? CommandCatalog.All.Count() : inputs.Length
        };
    }

    /// <summary>
    /// Applies a work view (the window tells it when the setting is applied). On a view change a collapsed group closes, a direct
    /// one shows its content flat, and a group with a changed value or an error opens whatever the view says (R6).
    /// </summary>
    internal void ApplyWorkView(WorkView view, bool viewChanged = true)
    {
        if (_isClosed) return;
        _currentWorkView = view;
        if (_groupSlots == null) return;
        bool structure = false;
        foreach (GroupSlot slot in _groupSlots)
        {
            structure |= SetFlat(slot, SettingsGroupRules.IsFlat(slot.Id, view));
            bool nonDefault = SettingsGroupRules.IsNonDefault(ChangedCount(slot), HasGroupError(slot));
            WorkViewPresenter.ApplyExpander(slot.Expander, view, viewChanged, nonDefault);
            slot.LastChanged = ChangedCount(slot);
            slot.HadError = HasGroupError(slot);
        }
        // The search reads the page as it is laid out: a group that moved in or out of its expander changes what a hit opens.
        if (structure) InvalidateSearchIndex();
        RefreshGroupHeaders();
    }

    // Direct groups put their content straight into the page; collapsed ones wrap it in the expander again.
    private static bool SetFlat(GroupSlot slot, bool flat)
    {
        if (slot.Flat == flat) return false;
        if (flat)
        {
            int index = slot.Parent.Children.IndexOf(slot.Expander);
            if (index < 0) return false;
            slot.Expander.Content = null;
            slot.Parent.Children[index] = slot.Body;
        }
        else
        {
            int index = slot.Parent.Children.IndexOf(slot.Body);
            if (index < 0) return false;
            slot.Parent.Children[index] = slot.Expander;
            slot.Expander.Content = slot.Body;
        }
        slot.Flat = flat;
        return true;
    }

    private void RefreshGroupHeaders()
    {
        if (_groupSlots == null) return;
        string more = Texts.T("workview.moreSettings");
        foreach (GroupSlot slot in _groupSlots)
            slot.Expander.Header = SettingsGroupRules.Header(more, slot.Count, ChangedCount(slot), count => Texts.T("workview.collapsedChanged", count));
    }

    // The loaded values are the reference of "changed": at construction and after a discard or a save.
    private void ResetGroupBaselines()
    {
        if (_groupSlots == null) return;
        foreach (GroupSlot slot in _groupSlots)
        {
            slot.Baseline = slot.Inputs.Select(ValueOf).ToArray();
            slot.LastChanged = 0;
            slot.HadError = HasGroupError(slot);
        }
        RefreshGroupHeaders();
    }

    private static int ChangedCount(GroupSlot slot)
    {
        if (slot.Shortcuts != null) return slot.Shortcuts.IsDirty ? 1 : 0;
        int changed = 0;
        for (int index = 0; index < slot.Inputs.Length && index < slot.Baseline.Length; index++)
            if (!string.Equals(ValueOf(slot.Inputs[index]), slot.Baseline[index], StringComparison.Ordinal)) changed++;
        return changed;
    }

    private static bool HasGroupError(GroupSlot slot) => slot.Shortcuts != null ? slot.Shortcuts.HasIssues
        : slot.Errors.Any(error => error.IsVisible && !string.IsNullOrEmpty(error.Text))
          || slot.Inputs.Any(input => Avalonia.Controls.DataValidationErrors.GetHasErrors(input));

    private static string ValueOf(Control input) => input switch
    {
        NumericUpDown number => number.Value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        TextBox text => text.Text ?? string.Empty,
        ComboBox box => box.SelectedIndex.ToString(CultureInfo.InvariantCulture),
        ToggleButton toggle => toggle.IsChecked switch { true => "1", false => "0", _ => "?" },
        Slider slider => slider.Value.ToString(CultureInfo.InvariantCulture),
        _ => string.Empty
    };

    // Draft properties change in bursts (a reset sets dozens): decide once afterwards, against the fresh baseline.
    private void QueueGroupSweep()
    {
        if (_groupSlots == null || _groupSweepQueued || _isClosed) return;
        _groupSweepQueued = true;
        Dispatcher.UIThread.Post(() => { _groupSweepQueued = false; SweepGroups(); }, DispatcherPriority.Background);
    }

    /// <summary>R6: opens every group that got a new change or a new error; keeps a group the user closed again closed.</summary>
    private void SweepGroups()
    {
        if (_groupSlots == null || _isClosed) return;
        foreach (GroupSlot slot in _groupSlots)
        {
            int changed = ChangedCount(slot);
            bool error = HasGroupError(slot);
            if (SettingsGroupRules.ShouldOpen(slot.LastChanged, changed, slot.HadError, error)) slot.Expander.IsExpanded = true;
            slot.LastChanged = changed;
            slot.HadError = error;
        }
        RefreshGroupHeaders();
    }
}
