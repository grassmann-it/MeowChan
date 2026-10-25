using System;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Collections.Generic;
using System.Linq;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop;

namespace MeowChan;

public partial class SettingsEditor
{
    private SectionNavigationController? _sectionNavigation;
    private long _sectionFocusTransferRevision;
    private (TopLevel Owner, object? Focus, string? SectionId)? _pendingSectionFocusTransfer;

    private void QueueSectionNavigationFocus(TopLevel owner, Control? origin, bool categories, string? sectionId)
    {
        if (!categories) SettingsSections.IsExpanded = true;
        object? focusAtRequest = owner.FocusManager?.GetFocusedElement();
        long request = _sectionFocusTransferRevision;
        _pendingSectionFocusTransfer = (owner, focusAtRequest, sectionId);
        bool superseded = false;
        // This single deferred transfer must not overtake a newer keyboard,
        // pointer or focus decision, even when the hidden origin cleared focus.
        void Supersede()
        {
            superseded = true;
            if (request != _sectionFocusTransferRevision) return;
            _sectionFocusTransferRevision++;
            _pendingSectionFocusTransfer = null;
        }
        EventHandler<KeyEventArgs> onKey = (_, _) => Supersede();
        EventHandler<PointerPressedEventArgs> onPointer = (_, _) => Supersede();
        EventHandler<FocusChangedEventArgs> onFocus = (_, _) => Supersede();
        EventHandler onDeactivate = (_, _) => Supersede();
        Window? ownerWindow = owner as Window;
        void StopObserving()
        {
            owner.RemoveHandler(KeyDownEvent, onKey);
            owner.RemoveHandler(PointerPressedEvent, onPointer);
            owner.RemoveHandler(GotFocusEvent, onFocus);
            if (ownerWindow != null) ownerWindow.Deactivated -= onDeactivate;
        }
        owner.AddHandler(KeyDownEvent, onKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.AddHandler(PointerPressedEvent, onPointer, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.AddHandler(GotFocusEvent, onFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        if (ownerWindow != null) ownerWindow.Deactivated += onDeactivate;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                object? currentFocus = owner.FocusManager?.GetFocusedElement();
                bool sameFocus = ReferenceEquals(currentFocus, focusAtRequest)
                    || currentFocus == null && origin?.IsEffectivelyVisible == false;
                if (superseded || request != _sectionFocusTransferRevision || !sameFocus || _isClosed
                    || !ReferenceEquals(TopLevel.GetTopLevel(this), owner) || !IsEffectivelyVisible || !IsEditable
                    || owner is Window { IsActive: false } || SettingsCategories.IsVisible != categories) return;
                // Do not classify this bounded transfer as a newer user focus.
                StopObserving();
                // The new presentation now has a real layout/focus target.
                if (categories)
                    (SettingsCategoryLinks.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, sectionId))
                        ?? SettingsCategoryLinks.Children.OfType<Button>().FirstOrDefault())?.Focus();
                else SettingsSections.OpenNavigation();
            }
            finally
            {
                StopObserving();
                if (request == _sectionFocusTransferRevision) _pendingSectionFocusTransfer = null;
            }
        }, DispatcherPriority.Loaded);
    }
    public bool CanOpenSectionNavigation => IsEditable && _sectionNavigation?.IsAvailable == true;
    public string? SelectedSectionId => SettingsCategoryLinks.Children.OfType<Button>()
        .FirstOrDefault(button => button.IsKeyboardFocusWithin)?.Tag as string ?? SettingsSections.SelectedSectionId;
    public void OpenSectionNavigation()
    {
        if (!CanOpenSectionNavigation) return;
        SettingsSections.OpenNavigation();
        if (SettingsCategories.IsVisible)
            SettingsCategoryLinks.Children.OfType<Button>().FirstOrDefault()?.Focus();
    }
    public bool CanGoToSection(string id) => IsEditable
        && (SettingsSections.IsExpanded || SettingsCategories.IsVisible && SettingsCategories.IsKeyboardFocusWithin)
        && _sectionNavigation?.CanNavigate(id) == true;
    public void GoToSection(string id) { if (CanGoToSection(id)) SettingsSections.Navigate(id); }

    private void InitializeSectionNavigation()
    {
        _sectionNavigation = new SectionNavigationController(SettingsScrollViewer, () => !_isClosed && IsEditable);
        _sectionNavigation.Navigating += () => { _suppressPassiveErrorReveal = true; _errorRevealRevision++; };
        SettingsSections.Connect(_sectionNavigation);
        RefreshSectionNavigation();
        AttachedToVisualTree += (_, _) => RefreshSectionNavigation();
        DetachedFromVisualTree += (_, _) => { _sectionFocusTransferRevision++; _pendingSectionFocusTransfer = null; };
    }

    private void RefreshCategoryLinks(IReadOnlyList<SectionDescriptor> sections)
    {
        // Both presentations share the controller, targets and explicit navigation.
        // A refresh updates labels only; it never materializes or replaces editors.
        string? focused = SettingsCategoryLinks.Children.OfType<Button>()
            .FirstOrDefault(button => button.IsKeyboardFocusWithin)?.Tag as string;
        SettingsCategoryLinks.Children.Clear();
        foreach (SectionDescriptor section in sections)
        {
            var button = new Button { Tag = section.Id, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new TextBlock { Text = section.Title, TextWrapping = Avalonia.Media.TextWrapping.Wrap } };
            button.Classes.Add("subtle");
            AutomationProperties.SetName(button, section.Title);
            CommandBindings.BindExisting(button, "sections.go-to");
            // A later Tab/focus back into navigation supersedes an older queued
            // section-target reveal. Normal activation focuses before Click.
            button.GotFocus += (_, _) => _sectionNavigation?.CancelPendingNavigation();
            button.Click += (_, _) => SettingsSections.Navigate(section.Id);
            SettingsCategoryLinks.Children.Add(button);
        }
        if (focused != null && SettingsCategories.IsVisible)
            SettingsCategoryLinks.Children.OfType<Button>().FirstOrDefault(button => Equals(button.Tag, focused))?.Focus();
    }

    private void RefreshSectionNavigation()
    {
        if (_sectionNavigation == null || _isClosed) return;
        var sections = new List<SectionDescriptor>
        {
            new("settings.interface", InterfaceTitle, () => SettingsInterfaceHeading),
            new("settings.reading-media", ReadingMediaTitle, () => SettingsReadingMediaHeading),
            new("settings.downloads", DownloadsTitle, () => SettingsDownloadsHeading),
            new("settings.worker-budgets", AdvancedBudgetsLabel, () => SettingsWorkerBudgetsHeading,
                () => SettingsWorkerBudgetsHeading.IsExpanded = true),
            new("settings.updates", UpdatesTitle, () => SettingsUpdatesHeading)
        };
        AddOptional("settings.notifications", "SettingsNotificationsHeading");
        sections.Add(new("settings.behavior", BehaviorTitle, () => SettingsBehaviorHeading));
        AddOptional("settings.shortcuts", "ShortcutsEditor");
        _sectionNavigation.SetSections(sections);
        SettingsSections.Refresh();
        SettingsSections.IsVisible = !SettingsCategories.IsVisible;
        RefreshCategoryLinks(sections);

        void AddOptional(string id, string name)
        {
            Control? target = this.FindControl<Control>(name);
            if (target == null) return;
            string title = target is TextBlock text ? text.Text ?? id
                : MeowChan.Desktop.Localization.Texts.T("shortcuts.title");
            sections.Add(new SectionDescriptor(id, title, () =>
            {
                Control? current = this.FindControl<Control>(name);
                return name == "ShortcutsEditor"
                    ? (current as MeowChan.Desktop.Commands.ShortcutEditor)?.SectionHeading ?? current
                    : current;
            }));
        }
    }
}
