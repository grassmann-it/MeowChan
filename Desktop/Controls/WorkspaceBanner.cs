using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Media;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// DS-P3: the one in-page message surface. It replaces the Ursa banner: a plain <see cref="Border"/> that takes its colours, edge,
/// radius and inset from the workspace tokens through the <c>workspace-error</c>, <c>workspace-warning</c>, <c>workspace-info</c>
/// and <c>workspace-success</c> classes (WorkspaceStyles.axaml), so the surface follows the same system as the cards and dialogs.
/// The content, the live regions on its text and every action stay with the page that owns the banner; the control only maps the
/// severity to the class.
/// <para>A11y review A11: a banner whose severity changes at run time (the shell feedback banner) sets
/// <see cref="ShowSeverityIcon"/>: a small stroke icon in a different shape per severity (circle with a cross, triangle, circle with
/// an "i", circle with a tick), so colour is not the only carrier. The icon is drawn in the edge colour of the class and has no
/// automation peer; the severity is spoken through <see cref="SeverityLabel"/>, which the owner puts in front of the live text.</para>
/// </summary>
internal sealed class WorkspaceBanner : Border
{
    internal const string ErrorClass = "workspace-error";
    internal const string WarningClass = "workspace-warning";
    internal const string InfoClass = "workspace-info";
    internal const string SuccessClass = "workspace-success";

    /// <summary>Width of the icon and the gap before the content, in dip.</summary>
    internal const double IconSize = 16, IconGap = 8;

    private NotificationType _type = NotificationType.Error;
    private bool _showSeverityIcon;
    private SeverityIconVisual? _icon;

    public WorkspaceBanner() => Apply();

    // Styled like the border it is: the type selectors of the workspace styles (Border.workspace-error) must match it.
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The severity; the default (and the page errors) is <see cref="NotificationType.Error"/>.</summary>
    internal NotificationType Type
    {
        get => _type;
        set { if (_type == value) return; _type = value; Apply(); }
    }

    /// <summary>Draws the severity icon in front of the content (off by default: most banners are always errors and say so in words).</summary>
    public bool ShowSeverityIcon
    {
        get => _showSeverityIcon;
        set
        {
            if (_showSeverityIcon == value) return;
            _showSeverityIcon = value;
            // A visual-only child (no logical parent, no peer): Border.Render is sealed, so the icon draws itself.
            if (value) VisualChildren.Add(_icon = new SeverityIconVisual(this));
            else if (_icon != null) { VisualChildren.Remove(_icon); _icon = null; }
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>The style class a severity maps to.</summary>
    internal static string ClassFor(NotificationType type) => type switch
    {
        NotificationType.Warning => WarningClass,
        NotificationType.Success => SuccessClass,
        NotificationType.Information => InfoClass,
        _ => ErrorClass
    };

    /// <summary>The icon key of a severity (a different shape each).</summary>
    internal static string IconFor(NotificationType type) => type switch
    {
        NotificationType.Warning => WorkspaceIcons.SeverityWarning,
        NotificationType.Success => WorkspaceIcons.SeveritySuccess,
        NotificationType.Information => WorkspaceIcons.SeverityInfo,
        _ => WorkspaceIcons.SeverityError
    };

    /// <summary>The word for a severity ("Error", "Warning", "Information", "Success") in the interface language.</summary>
    internal static string SeverityLabel(NotificationType type) => Texts.T(type switch
    {
        NotificationType.Warning => "feedback.warning",
        NotificationType.Success => "feedback.severity.success",
        NotificationType.Information => "feedback.severity.information",
        _ => "notification.error"
    });

    /// <summary>The text a screen reader gets for a message of this severity: the severity word first, then the message.</summary>
    internal static string AnnouncedText(NotificationType type, string message) => Texts.T("feedback.severity.announce", SeverityLabel(type), message);

    private void Apply()
    {
        string chosen = ClassFor(_type);
        foreach (string name in new[] { ErrorClass, WarningClass, InfoClass, SuccessClass })
            Classes.Set(name, name == chosen);
        _icon?.InvalidateVisual();
    }

    private Thickness ContentInset()
    {
        Thickness inset = Padding + BorderThickness;
        return _showSeverityIcon ? new Thickness(inset.Left + IconSize + IconGap, inset.Top, inset.Right, inset.Bottom) : inset;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_showSeverityIcon) return base.MeasureOverride(availableSize);
        Thickness inset = ContentInset();
        Size content = default;
        _icon?.Measure(new Size(IconSize, IconSize));
        if (Child is { } child)
        {
            child.Measure(availableSize.Deflate(inset));
            content = child.DesiredSize;
        }
        return new Size(content.Width + inset.Left + inset.Right, Math.Max(content.Height, IconSize) + inset.Top + inset.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_showSeverityIcon) return base.ArrangeOverride(finalSize);
        Child?.Arrange(new Rect(finalSize).Deflate(ContentInset()));
        Thickness inset = Padding + BorderThickness;
        _icon?.Arrange(new Rect(inset.Left, inset.Top, IconSize, IconSize));
        return finalSize;
    }

    /// <summary>The severity icon: drawn with the banner's edge colour, which the severity class already sets.</summary>
    private sealed class SeverityIconVisual : Control
    {
        private readonly WorkspaceBanner _owner;

        internal SeverityIconVisual(WorkspaceBanner owner)
        {
            _owner = owner;
            IsHitTestVisible = false;
            Focusable = false;
            AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        }

        protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_owner.BorderBrush is not { } brush) return;
            Pen pen = new(brush, 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, WorkspaceIcons.GeometryOf(IconFor(_owner._type)));
        }
    }
}
