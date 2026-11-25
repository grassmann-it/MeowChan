using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace MeowChan.Desktop.Controls;

/// <summary>A toggle that expands a region outside the button.</summary>
internal sealed class DisclosureButton : ToggleButton
{
    private readonly Control _chevron = WorkspaceIcons.Create(WorkspaceIcons.ChevronDown);

    public DisclosureButton()
    {
        _chevron.Classes.Add("glyph-small");
        _chevron.RenderTransformOrigin = RelativePoint.Center;
    }

    protected override Type StyleKeyOverride => typeof(ToggleButton);
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty && Content is StackPanel label)
        {
            (_chevron.Parent as Panel)?.Children.Remove(_chevron);
            label.Children.Add(_chevron);
        }
        if (change.Property != IsCheckedProperty || (change.OldValue is true) == (IsChecked == true)) return;
        bool expanded = IsChecked == true;
        _chevron.RenderTransform = expanded ? new ScaleTransform(1, -1) : null;
        (ControlAutomationPeer.FromElement(this) as Peer)?.NotifyStateChanged(expanded);
    }

    private sealed class Peer(DisclosureButton owner) : ToggleButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public bool ShowsMenu => false;
        public ExpandCollapseState ExpandCollapseState => owner.IsChecked == true
            ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public void Expand() => Set(true);
        public void Collapse() => Set(false);

        private void Set(bool expanded)
        {
            if (owner.IsEffectivelyEnabled && (owner.IsChecked == true) != expanded) owner.OnClick();
        }

        internal void NotifyStateChanged(bool expanded) =>
            RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                expanded ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded, ExpandCollapseState);
    }
}
