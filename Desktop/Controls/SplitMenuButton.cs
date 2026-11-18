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
/// The panel has exactly one owner at any time: <see cref="Return"/> only gives back what <see cref="Lend"/> took (a panel that was never
/// lent is the owner's to place, with <see cref="PlaceAtHome"/>), and every placement takes the panel from its old parent first.
/// </summary>
internal sealed class PanelLoan(Avalonia.Controls.Panel home, Avalonia.Controls.Panel panel, ContentControl host)
{
    private int _index = -1;
    private bool _lent;

    internal bool IsLent => ReferenceEquals(host.Content, panel);

    internal void Lend()
    {
        if (IsLent) return;
        if (panel.Parent is Avalonia.Controls.Panel parent)
        {
            _index = ReferenceEquals(parent, home) ? parent.Children.IndexOf(panel) : -1;
            parent.Children.Remove(panel);
        }
        else Detach();
        host.Content = panel;
        _lent = true;
    }

    internal void Return()
    {
        if (!_lent) { if (IsLent) host.Content = null; return; } // never lent (FP1: the viewer's constructor places the panel itself)
        _lent = false;
        Settle(_index);
        _index = -1;
    }

    /// <summary>The owner puts the panel into its home (at the end, or where it is already): idempotent, ends a loan, never a second insert.</summary>
    internal void PlaceAtHome()
    {
        _lent = false;
        _index = -1;
        Settle(-1);
    }

    private void Settle(int index)
    {
        if (ReferenceEquals(panel.Parent, home)) { if (IsLent) host.Content = null; return; }
        Detach();
        int at = index < 0 ? home.Children.Count : Math.Min(index, home.Children.Count);
        home.Children.Insert(at, panel);
    }

    // Takes the panel from whatever holds it (the host, another panel or content control), so the next insert is its only parent.
    private void Detach()
    {
        if (IsLent) host.Content = null;
        switch (panel.Parent)
        {
            case Avalonia.Controls.Panel parent: parent.Children.Remove(panel); break;
            case ContentControl content when ReferenceEquals(content.Content, panel): content.Content = null; break;
            case Decorator decorator when ReferenceEquals(decorator.Child, panel): decorator.Child = null; break;
        }
    }
}
