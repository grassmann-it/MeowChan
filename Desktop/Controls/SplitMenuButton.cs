using System;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The menu part of a split button (G2: the ▾ beside the slideshow toggle). It is a Button that opens its <see cref="Button.Flyout"/>, and it
/// tells a screen reader whether that flyout is open (expanded or collapsed), which a plain Button with a flyout does not do for sure.
/// </summary>
internal sealed class SplitMenuButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>The flyout opened or closed: the screen reader hears the new state.</summary>
    internal void RaiseExpandedChanged(bool expanded) => (ControlAutomationPeer.FromElement(this) as Peer)?.Changed(expanded);

    private sealed class Peer(SplitMenuButton owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public bool ShowsMenu => true;
        public ExpandCollapseState ExpandCollapseState => owner.Flyout?.IsOpen == true ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public void Expand() => owner.Flyout?.ShowAt(owner);
        public void Collapse() => owner.Flyout?.Hide();
        internal void Changed(bool expanded) => RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
            expanded ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded, expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }
}

/// <summary>
/// A panel that sits in a home panel and is lent to a host (a flyout's content) for a while: <see cref="Lend"/> takes it out of its home and
/// into the host, <see cref="Return"/> puts it back at the same place. Both are safe to call twice. One panel, two ways to reach it (G2: the
/// slideshow settings under "⋯" and in the flyout of the split button).
/// </summary>
internal sealed class PanelLoan(Avalonia.Controls.Panel home, Avalonia.Controls.Panel panel, ContentControl host)
{
    private int _index = -1;

    internal bool IsLent => ReferenceEquals(host.Content, panel);

    internal void Lend()
    {
        if (IsLent) return;
        if (panel.Parent is Avalonia.Controls.Panel parent)
        {
            _index = parent.Children.IndexOf(panel);
            parent.Children.Remove(panel);
        }
        host.Content = panel;
    }

    internal void Return()
    {
        if (IsLent) host.Content = null;
        if (panel.Parent != null) return;
        int index = _index < 0 ? home.Children.Count : Math.Min(_index, home.Children.Count);
        home.Children.Insert(index, panel);
        _index = -1;
    }
}
