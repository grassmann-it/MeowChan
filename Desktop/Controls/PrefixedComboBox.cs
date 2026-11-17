using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// A toolbar combo box that names what it chooses inside its closed box, as a muted prefix before the value: "Sort  By thread ▾" (UX review
/// 17.11.2026: a detached label in front of the box read as its own control and crowded the tool before it). The prefix is muted like the scope
/// label inside the search field (<see cref="SearchScopeChip"/>), the value keeps the normal text, both on one line in the standard control
/// height; the open list shows the values only (the items' own text or <see cref="ItemsControl.ItemTemplate"/>). Keyboard and focus are the
/// combo box's own (Tab stop, F4/Alt+Down, arrow keys).
/// The prefix yields before the value: while <see cref="ShowsPrefix"/> is off, or when the box is laid out narrower than prefix, gap and value
/// need, the value gets the whole box and the prefix is not drawn; the value is only ever cut when it does not fit on its own. An owner that
/// keeps a minimum width for the value adds <see cref="PrefixRoom"/> to it while the prefix shows (<see cref="MinWidthFor"/>).
/// Accessibility: the prefix is not in the accessibility tree; the owner gives the box the same text as its accessible name, so a screen reader
/// hears the name once and the value.
/// </summary>
public sealed class PrefixedComboBox : ComboBox
{
    /// <summary>The muted text in front of the value (the owner localizes it).</summary>
    public static readonly StyledProperty<string?> PrefixProperty =
        AvaloniaProperty.Register<PrefixedComboBox, string?>(nameof(Prefix));

    /// <summary>Whether the prefix is shown at all (an owner's narrow stage turns it off; the value then has the box alone).</summary>
    public static readonly StyledProperty<bool> ShowsPrefixProperty =
        AvaloniaProperty.Register<PrefixedComboBox, bool>(nameof(ShowsPrefix), true);

    /// <summary>The gap between the prefix and the value, in dip (two spaces of the body text).</summary>
    internal const double PrefixGap = 6;

    protected override Type StyleKeyOverride => typeof(ComboBox);

    public PrefixedComboBox()
    {
        // The closed box only: the open list keeps the items' own presentation.
        SelectionBoxItemTemplate = new FuncDataTemplate<object?>((_, _) => new SelectionBox(this), supportsRecycling: false);
    }

    /// <inheritdoc cref="PrefixProperty"/>
    public string? Prefix
    {
        get => GetValue(PrefixProperty);
        set => SetValue(PrefixProperty, value);
    }

    /// <inheritdoc cref="ShowsPrefixProperty"/>
    public bool ShowsPrefix
    {
        get => GetValue(ShowsPrefixProperty);
        set => SetValue(ShowsPrefixProperty, value);
    }

    /// <summary>Whether the prefix is drawn now: switched on, not empty and with room beside the whole value (tests and smokes).</summary>
    internal bool PrefixDrawn { get; private set; }

    /// <summary>
    /// The width the prefix and its gap take in front of the value, in dip, measured in the box's own font; 0 without a prefix. An owner that sizes
    /// the box for its value adds it while the prefix shows, so the prefix never costs the value any room.
    /// </summary>
    public double PrefixRoom
    {
        get
        {
            // Text is measured with the platform's text shaping, which a box outside a window (a page built before it is shown) may not have yet;
            // the owner asks again once the box is laid out.
            if (string.IsNullOrEmpty(Prefix) || !this.IsAttachedToVisualTree()) return 0;
            TextBlock measure = new() { Text = Prefix, FontFamily = FontFamily, FontSize = FontSize, FontStyle = FontStyle, FontStretch = FontStretch };
            measure.Measure(Size.Infinity);
            return Math.Ceiling(measure.DesiredSize.Width) + PrefixGap;
        }
    }

    /// <summary>The minimum width of a box whose value area keeps <paramref name="valueMinimum"/>: the prefix's room is added while it shows (pure, tests).</summary>
    internal static double MinWidthFor(double valueMinimum, bool showsPrefix, double prefixRoom) =>
        Math.Max(0, valueMinimum) + (showsPrefix ? Math.Max(0, prefixRoom) : 0);

    /// <summary>Whether the prefix fits beside a value of <paramref name="value"/> dip in <paramref name="available"/> dip (pure, tests).</summary>
    internal static bool PrefixFits(double available, double prefix, double value) =>
        !double.IsFinite(available) || available + 0.5 >= prefix + PrefixGap + value;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PrefixProperty || change.Property == ShowsPrefixProperty || change.Property == FontSizeProperty || change.Property == FontFamilyProperty)
            foreach (SelectionBox box in this.GetVisualDescendants().OfType<SelectionBox>())
                box.Refresh();
    }

    // The content of the closed box: the muted prefix, then the value; a panel of its own so the prefix can yield in measure and arrange.
    private sealed class SelectionBox : Panel
    {
        private readonly PrefixedComboBox _owner;
        private readonly TextBlock _prefix = new() { Name = "PrefixedComboPrefix", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, ClipToBounds = true };
        private readonly TextBlock _value = new() { Name = "PrefixedComboValue", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        private bool _drawn;

        internal SelectionBox(PrefixedComboBox owner)
        {
            _owner = owner;
            _prefix[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Muted");
            AutomationProperties.SetAccessibilityView(_prefix, AccessibilityView.Raw);
            Children.Add(_prefix);
            Children.Add(_value);
            DataContextChanged += (_, _) => Refresh();
            Refresh();
        }

        internal void Refresh()
        {
            _prefix.Text = _owner.Prefix;
            _value.Text = DataContext?.ToString();
            InvalidateMeasure();
        }

        private bool Wanted => _owner.ShowsPrefix && !string.IsNullOrEmpty(_owner.Prefix);

        protected override Size MeasureOverride(Size availableSize)
        {
            Size open = new(double.PositiveInfinity, availableSize.Height);
            _value.Measure(open);
            double value = _value.DesiredSize.Width;
            _prefix.Measure(open);
            double prefix = _prefix.DesiredSize.Width;
            _drawn = Wanted && PrefixFits(availableSize.Width, prefix, value);
            if (!_drawn) _prefix.Measure(new Size(0, availableSize.Height));
            double width = _drawn ? prefix + PrefixGap + value : value;
            if (double.IsFinite(availableSize.Width) && width > availableSize.Width) _value.Measure(new Size(Math.Max(0, availableSize.Width - (_drawn ? prefix + PrefixGap : 0)), availableSize.Height));
            double height = Math.Max(_value.DesiredSize.Height, _drawn ? _prefix.DesiredSize.Height : 0);
            return new Size(double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double prefix = _prefix.DesiredSize.Width, value = _value.DesiredSize.Width;
            // A box arranged narrower than it measured (the row ran out of room) drops the prefix before it cuts the value.
            bool drawn = _drawn && PrefixFits(finalSize.Width, prefix, value);
            double x = 0;
            if (drawn)
            {
                _prefix.Arrange(new Rect(0, 0, prefix, finalSize.Height));
                x = prefix + PrefixGap;
            }
            else _prefix.Arrange(new Rect(0, 0, 0, finalSize.Height));
            _value.Arrange(new Rect(x, 0, Math.Max(0, finalSize.Width - x), finalSize.Height));
            _owner.PrefixDrawn = drawn;
            return finalSize;
        }
    }
}
