using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// S2: the toggle of a filter surface, the same on Archive and Media. A <see cref="ToggleButton"/> (not an Expander) with the
/// filters icon, a text and, while at least one filter is active, a count badge in the chip language (<c>chip-badge chip-compact</c>).
/// It has the frame, height and corner radius of the combo box beside it (<c>filter-toggle</c> in WorkspaceStyles.axaml);
/// <see cref="ToggleButton.IsChecked"/> says whether the surface is open. The owner sets what it shows through <see cref="Show"/>;
/// the accessible name is the owner's sentence ("Filters, 3 active"), because the badge alone is not read as a name.
/// The icon is created when the button is shown: it needs the platform's asset loader, which a unit test does not have.
/// </summary>
public sealed class FilterToggleButton : ToggleButton
{
    protected override Type StyleKeyOverride => typeof(ToggleButton);

    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
    private readonly TextBlock _badgeText = new() { Classes = { "chip-label" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _badge;
    private readonly StackPanel _content = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private Control? _icon;

    public FilterToggleButton()
    {
        Classes.Add("filter-toggle");
        _badge = new Border { Classes = { "chip-badge", "chip-compact" }, Child = _badgeText, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, IsHitTestVisible = false };
        AutomationProperties.SetAccessibilityView(_badge, AccessibilityView.Raw);
        _content.Children.Add(_label);
        _content.Children.Add(_badge);
        Content = _content;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Left;
    }

    /// <summary>The visible text.</summary>
    public string Label => _label.Text ?? string.Empty;

    /// <summary>Whether the text shows (F10: a narrow toolbar keeps only the icon and the badge; the accessible name and tooltip stay).</summary>
    public bool LabelVisible { get => _label.IsVisible; set { if (_label.IsVisible != value) _label.IsVisible = value; } }

    /// <summary>How many filters are active; the badge shows it while it is above zero.</summary>
    public int Count { get; private set; }

    public bool IsBadgeVisible => _badge.IsVisible;

    /// <summary>The number in the badge, or an empty string while there is none.</summary>
    public string BadgeText => _badgeText.Text ?? string.Empty;

    /// <summary>The badge (test and capture seam).</summary>
    internal Border Badge => _badge;

    /// <summary>
    /// Sets the text, the count and the accessible name. <paramref name="hint"/> is a tooltip and help text (the "unapplied draft"
    /// explanation of the archive); null removes it.
    /// </summary>
    public void Show(string label, int count, string accessibleName, string? hint = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        count = Math.Max(0, count);
        if (_label.Text != label) _label.Text = label;
        Count = count;
        string badge = count > 0 ? count.ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty;
        if (_badgeText.Text != badge) _badgeText.Text = badge;
        _badge.IsVisible = count > 0;
        AutomationProperties.SetName(this, accessibleName);
        ToolTip.SetTip(this, hint);
        AutomationProperties.SetHelpText(this, hint ?? string.Empty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_icon != null) return;
        _icon = CommandBindings.NamedIcon("sliders");
        _icon.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(_icon, AccessibilityView.Raw);
        _content.Children.Insert(0, _icon);
    }
}
