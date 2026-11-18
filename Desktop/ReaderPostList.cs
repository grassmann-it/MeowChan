using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;

namespace MeowChan.Desktop;

/// <summary>
/// The reader's post list: a <see cref="ListBox"/> whose cards keep their place in the viewport when one of them changes its height.
/// <para>
/// The virtualizing stack panel trusts the positions of its realized cards only while none of them changed its size outside the panel's
/// own measure (Avalonia's <c>RealizedStackElements.ValidateStartU</c>). A card that is measured on its own first (its selection enlarges
/// the media, a decoded picture replaces the reserved one, a frame cap follows the viewport) makes the panel discard those positions and
/// estimate the card at the top of the viewport from the average realized height instead: deep in a thread with posts of very different
/// heights that puts the cards thousands of dip away from where they were without an offset change, and each correction of the offset
/// meets the next estimate (the list flails).
/// </para>
/// <para>
/// Here a card whose measure is invalidated invalidates the panel as well, so the layout measures the panel first (it still sees the
/// card's previous size and keeps its positions) and the card again inside the panel's measure: the first card in the viewport stays
/// where it is and the others are laid out around it. A card that grows or shrinks therefore holds its top edge (or the top edge of the
/// card above it in the viewport), at any depth. Virtualization stays as it is: only the realized cards are measured.
/// </para>
/// </summary>
public sealed class ReaderPostList : ListBox
{
    // A local value: the list theme sets its own panel, which would win over a default.
    public ReaderPostList() => ItemsPanel = NewPanelTemplate();

    /// <summary>A new panel template: assigned for each page, so a page starts without its predecessor's realized positions.</summary>
    internal static FuncTemplate<Panel?> NewPanelTemplate() => new(() => new ReaderPostsPanel());

    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new ReaderPostItem();
}

/// <summary>A card of the <see cref="ReaderPostList"/>: a size change of its content is laid out by the panel (see there).</summary>
public sealed class ReaderPostItem : ListBoxItem
{
    protected override Type StyleKeyOverride => typeof(ListBoxItem);

    protected override void OnMeasureInvalidated()
    {
        base.OnMeasureInvalidated();
        (this.GetVisualParent() as ReaderPostsPanel)?.ItemMeasureInvalidated();
    }
}

/// <summary>The panel of the <see cref="ReaderPostList"/>: measured before a realized card that changes its size (see there).</summary>
public sealed class ReaderPostsPanel : VirtualizingStackPanel
{
    private bool _measuring;

    /// <summary>
    /// A realized card's measure was invalidated: outside the panel's own measure the panel is invalidated too, so the layout measures it
    /// before the card. A card the panel's measure itself invalidates (a recycled card taking its new post) is measured by that pass.
    /// </summary>
    internal void ItemMeasureInvalidated()
    {
        if (!_measuring) InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _measuring = true;
        try { return base.MeasureOverride(availableSize); }
        finally { _measuring = false; }
    }
}
