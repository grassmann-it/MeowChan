using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeowChan.Desktop.Controls;

/// <summary>What a child of a <see cref="PageHeader"/> is: the title block, the action row or a short statement under the actions.</summary>
public enum PageHeaderRole
{
    /// <summary>The page's title and subtitle (normally one <c>workspace-page-header</c> stack). Always at the start edge.</summary>
    Title,
    /// <summary>The page's actions (a WrapPanel of buttons, a status line): top right beside the title, or under it when they do not fit.</summary>
    Actions,
    /// <summary>A short statement about the page (a count): right-aligned under the actions, or between the title and the actions when stacked.</summary>
    Aside
}

/// <summary>
/// The one head row of a workspace page (narrow-window sweep, Tom 16.11.2026: "headings and texts must never vanish into nowhere in small
/// windows"): the title block at the start edge, the page's actions at the end edge. When the actions' natural single-row width leaves the
/// title less than <see cref="TitleMinWidth"/> (or the row is narrower than <see cref="StackBelowWidth"/>), the header stacks: title on the
/// full width, then the aside, then the actions at the start edge, where a WrapPanel of actions wraps onto further lines.
/// </summary>
/// <remarks>
/// <para>The bug it replaces: a plain <c>Grid ColumnDefinitions="*,Auto"</c> measures the Auto action column at infinity, so a long action row
/// (Sources: "Show [Sources ▾] [+ Add plugin…] [Refresh status] [Open plugin folder] [Review bundled repairs] [?]", longer in German) took the
/// whole width; the star column of the title became 0 dip wide, and a title block with a MinWidth was then centred on that empty column, i.e.
/// pushed out of the page to the left with the start of every line cut off. A fixed page breakpoint (900 dip) cannot know the length of the
/// row in twelve languages and four work views; the header measures it.</para>
/// <para>The header is a <see cref="Grid"/> so the placement stays readable for callers and smokes (<c>Grid.GetRow(actions) &gt;= 1</c> means
/// stacked), but it owns its definitions and its children's row, column, span, the actions' top margin and the start/end alignment of
/// actions and aside: do not set them in XAML. The decision depends only on the available width and on natural widths that do not depend on
/// the decision, so it cannot flip back and forth between two layout passes.</para>
/// </remarks>
public sealed class PageHeader : Grid
{
    /// <summary>
    /// The one distance from the top of the page area to the top of every page title, in dip (review of 17.11.2026, LY-03: the titles started
    /// at 60, 64, 65 and 77 dip). It is the top of the page margin <c>Workspace.Inset.Page</c> (a test reads both, so they cannot drift apart):
    /// a page whose head sits in the page margin needs nothing of its own; a page that builds its own head (a code page, a centred column)
    /// puts exactly this above the title and nothing else.
    /// </summary>
    public const double PageTopOffset = 12;

    /// <summary>The least room the title block keeps beside the actions before the header stacks, in dip.</summary>
    public const double DefaultTitleMinWidth = 280;

    /// <summary>The room between the title block and the actions in the side-by-side layout, in dip.</summary>
    public const double DefaultSpacing = 12;

    /// <summary>The room above the actions when they stand under the title, in dip.</summary>
    public const double StackedActionsGap = 8;

    /// <summary>The least room the title keeps beside the actions; less, and the actions go under it.</summary>
    public static readonly StyledProperty<double> TitleMinWidthProperty =
        AvaloniaProperty.Register<PageHeader, double>(nameof(TitleMinWidth), DefaultTitleMinWidth);

    /// <summary>A row narrower than this (dip) always stacks, whatever fits; 0 (the default) leaves the decision to the fit alone.</summary>
    public static readonly StyledProperty<double> StackBelowWidthProperty =
        AvaloniaProperty.Register<PageHeader, double>(nameof(StackBelowWidth));

    /// <summary>The room between the title block and the actions side by side.</summary>
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<PageHeader, double>(nameof(Spacing), DefaultSpacing);

    /// <summary>The role of a child (default <see cref="PageHeaderRole.Title"/>).</summary>
    public static readonly AttachedProperty<PageHeaderRole> RoleProperty =
        AvaloniaProperty.RegisterAttached<PageHeader, Control, PageHeaderRole>("Role");

    static PageHeader()
    {
        AffectsMeasure<PageHeader>(TitleMinWidthProperty, StackBelowWidthProperty, SpacingProperty);
        RoleProperty.Changed.AddClassHandler<Control>((control, _) => (control.Parent as PageHeader)?.InvalidateMeasure());
    }

    public PageHeader()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
        RowSpacing = 0;
        ColumnSpacing = DefaultSpacing;
    }

    public double TitleMinWidth { get => GetValue(TitleMinWidthProperty); set => SetValue(TitleMinWidthProperty, value); }

    public double StackBelowWidth { get => GetValue(StackBelowWidthProperty); set => SetValue(StackBelowWidthProperty, value); }

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    public static PageHeaderRole GetRole(Control control) => control.GetValue(RoleProperty);

    public static void SetRole(Control control, PageHeaderRole value) => control.SetValue(RoleProperty, value);

    /// <summary>True while the actions stand under the title (pseudo-class <c>:stacked</c>).</summary>
    internal bool IsStacked { get; private set; }

    /// <summary>
    /// The pure decision: whether a header <paramref name="available"/> dip wide stacks, given the actions' natural single-row width, the
    /// title block's natural width (a short title alone needs no more than itself) and the title's minimum room.
    /// </summary>
    internal static bool ShouldStack(double available, double actionsWidth, double titleNaturalWidth, double titleMinWidth, double spacing,
        double stackBelowWidth = 0)
    {
        if (actionsWidth <= 0 || double.IsInfinity(available) || double.IsNaN(available)) return false;
        if (available < stackBelowWidth) return true;
        double titleNeed = Math.Min(Math.Max(0, titleNaturalWidth), Math.Max(0, titleMinWidth));
        return available - actionsWidth - spacing < titleNeed;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        double actionsWidth = 0, titleWidth = 0;
        Size natural = new(double.PositiveInfinity, constraint.Height);
        foreach (Control child in Children.Where(child => child.IsVisible))
        {
            child.Measure(natural);
            if (GetRole(child) == PageHeaderRole.Title) titleWidth = Math.Max(titleWidth, child.DesiredSize.Width);
            else actionsWidth = Math.Max(actionsWidth, child.DesiredSize.Width);
        }
        Apply(ShouldStack(constraint.Width, actionsWidth, titleWidth, TitleMinWidth, Spacing, StackBelowWidth), actionsWidth > 0);
        return base.MeasureOverride(constraint);
    }

    private void Apply(bool stacked, bool hasEnd)
    {
        if (IsStacked != stacked) { IsStacked = stacked; PseudoClasses.Set(":stacked", stacked); }
        // No gap before an empty end column (all actions hidden): the title then reaches the row's end edge.
        double spacing = stacked || !hasEnd ? 0 : Spacing;
        if (ColumnSpacing != spacing) ColumnSpacing = spacing;
        foreach (Control child in Children)
        {
            PageHeaderRole role = GetRole(child);
            // Wide: title in the star column over two rows; actions top right, the aside right-aligned under them.
            // Stacked: title, aside and actions one under the other on the full width, all at the start edge.
            (int row, int column, int columnSpan, int rowSpan) = (role, stacked) switch
            {
                (PageHeaderRole.Title, false) => (0, 0, 1, 2),
                (PageHeaderRole.Title, true) => (0, 0, 2, 1),
                (PageHeaderRole.Aside, false) => (1, 1, 1, 1),
                (PageHeaderRole.Aside, true) => (1, 0, 2, 1),
                (_, false) => (0, 1, 1, 1),
                (_, true) => (2, 0, 2, 1)
            };
            if (GetRow(child) != row) SetRow(child, row);
            if (GetColumn(child) != column) SetColumn(child, column);
            if (GetColumnSpan(child) != columnSpan) SetColumnSpan(child, columnSpan);
            if (GetRowSpan(child) != rowSpan) SetRowSpan(child, rowSpan);
            if (role == PageHeaderRole.Title) continue;
            HorizontalAlignment alignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            if (child.HorizontalAlignment != alignment) child.HorizontalAlignment = alignment;
            if (child is TextBlock text)
            {
                TextAlignment textAlignment = stacked ? TextAlignment.Left : TextAlignment.Right;
                if (text.TextAlignment != textAlignment) text.TextAlignment = textAlignment;
            }
            if (role == PageHeaderRole.Actions)
            {
                Thickness margin = stacked ? new Thickness(0, StackedActionsGap, 0, 0) : default;
                if (child.Margin != margin) child.Margin = margin;
            }
        }
    }
}
