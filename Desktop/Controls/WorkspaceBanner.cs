using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// DS-P3: the one in-page message surface. It replaces the Ursa banner: a plain <see cref="Border"/> that takes its colours, edge,
/// radius and inset from the workspace tokens through the <c>workspace-error</c>, <c>workspace-warning</c>, <c>workspace-info</c>
/// and <c>workspace-success</c> classes (WorkspaceStyles.axaml), so the surface follows the same system as the cards and dialogs.
/// The content, the live regions on its text and every action stay with the page that owns the banner; the control only maps the
/// severity to the class.
/// </summary>
internal sealed class WorkspaceBanner : Border
{
    internal const string ErrorClass = "workspace-error";
    internal const string WarningClass = "workspace-warning";
    internal const string InfoClass = "workspace-info";
    internal const string SuccessClass = "workspace-success";

    private NotificationType _type = NotificationType.Error;

    public WorkspaceBanner() => Apply();

    // Styled like the border it is: the type selectors of the workspace styles (Border.workspace-error) must match it.
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The severity; the default (and the page errors) is <see cref="NotificationType.Error"/>.</summary>
    internal NotificationType Type
    {
        get => _type;
        set { if (_type == value) return; _type = value; Apply(); }
    }

    /// <summary>The style class a severity maps to.</summary>
    internal static string ClassFor(NotificationType type) => type switch
    {
        NotificationType.Warning => WarningClass,
        NotificationType.Success => SuccessClass,
        NotificationType.Information => InfoClass,
        _ => ErrorClass
    };

    private void Apply()
    {
        string chosen = ClassFor(_type);
        foreach (string name in new[] { ErrorClass, WarningClass, InfoClass, SuccessClass })
            Classes.Set(name, name == chosen);
    }
}
