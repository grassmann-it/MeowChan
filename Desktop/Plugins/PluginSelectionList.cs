using System;
using Avalonia.Automation.Peers;
using Ursa.Controls;

namespace MeowChan.Desktop.Plugins;

// Ursa supplies the selection behavior and visuals. Its generic controls do not
// supply list/selection automation peers, so expose the existing Avalonia pattern.
public sealed class PluginSelectionList : SelectionList
{
    protected override Type StyleKeyOverride => typeof(SelectionList);
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);
    private sealed class Peer : SelectingItemsControlAutomationPeer
    {
        public Peer(PluginSelectionList owner) : base(owner) { }
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
    }
}

internal sealed class PluginSelectionItem : SelectionListItem
{
    protected override Type StyleKeyOverride => typeof(SelectionListItem);
    protected override AutomationPeer OnCreateAutomationPeer() => new ListItemAutomationPeer(this);
}
